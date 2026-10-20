using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OriginsLauncher
{
    /// <summary>
    ///     Since 2.3.0: logging in again from the game's own login screen. After a disconnect the 6609 client does not
    ///     close: it shows "disconnected from the game server" and goes back to its login screen (netmsg loop
    ///     0x741D34, message pushed at 0x741F97, login tip 0x507D07). The next "Log In" reads ini\origins.login again
    ///     (patch 4, cave 0xD69400 from the MsgAccount builder 0x93E838) and deletes it; until 2.2.0 nothing was
    ///     there any more, the server answered "wrong password" and the player had to close the game and log in
    ///     through the launcher again. The first token also died after 90 seconds at the login screen.
    ///     <para>
    ///         The launcher now stays with the game for the whole session and keeps a token in the file whenever the
    ///         game is at its login screen: with the account and password typed in the launcher (memory only, never on
    ///         disk) it asks the site for a new one when the last one was read, and renews an unread one before it
    ///         ends (a few times per wait, so an idle login screen does not ask the site forever). The game is "at the
    ///         login screen" when its process has had no TCP connection to a server for <see cref="ReloginTimings.LoginAfter" />;
    ///         it is logged in after <see cref="ReloginTimings.InGameAfter" /> connected (a login attempt connects for
    ///         well under a second, so a token is never taken away while the client is about to read it).
    ///     </para>
    ///     <para>
    ///         Several game windows (one launcher each) share the one file: the player clicks "Log In" in the window in
    ///         front, so with more than one game from this folder only the launcher of the window in front keeps its
    ///         token there, and the others take theirs out (kept in memory, written again when their window comes to
    ///         the front). A logged-in game's token never waits in the file for another window.
    ///     </para>
    ///     If any of this cannot be told (no TCP table), the launcher does nothing and the game behaves as in 2.2.0.
    /// </summary>
    internal sealed class ReloginKeeper
    {
        private readonly Config m_config;
        private readonly Func<CancellationToken, Task<IssuedToken>> m_mint;
        private readonly ReloginTimings m_t;
        private readonly object m_lock = new object();
        private readonly Queue<long> m_asked = new Queue<long>(); // when tokens were asked for (the rate cap)

        private IssuedToken m_token; // the newest token of this session
        private bool m_spent;        // a game read it (it left the file without the launcher taking it out)
        private bool m_inFile;       // written by this launcher and still there when last looked
        private bool m_stopped;      // the site refused the account: no more tokens this session
        private long m_retryAt;      // after a passing failure, no request before this
        private TimeSpan m_backoff;
        private int m_errors;

        public ReloginKeeper(Config config, Func<CancellationToken, Task<IssuedToken>> mint, ReloginTimings timings = null)
        {
            m_config = config;
            m_mint = mint;
            m_t = timings ?? new ReloginTimings();
        }

        /// <summary>Tokens asked for by the keeper (not the login dialog's).</summary>
        public int Minted { get; private set; }

        /// <summary>The site refused the account (password changed, banned): the keeper gave up for this session.</summary>
        public bool Stopped => m_stopped;

        /// <summary>
        ///     Since 2.4.0: called at every look while the game waits at its login screen in front (or alone), before its
        ///     token is put in the file: the launcher points ini\origins.server at the tunnel or, while the tunnel is
        ///     down, at the account server itself (<see cref="GameRoute" />). The client reads both files at the click.
        /// </summary>
        public Action AtLoginScreen;

        /// <summary>
        ///     Since 2.4.0: the UDP tunnel's local ports (<see cref="GameRoute.IsLocalPort" />); the game's connections to
        ///     them mean "logged in" whatever ini\origins.server says at that moment.
        /// </summary>
        public Func<int, bool> IsTunnelPort;

        /// <summary>The token the login dialog got: written for the game's first login. Throws if the file can't be written.</summary>
        public void Start(IssuedToken token)
        {
            lock (m_lock)
            {
                m_token = token;
                m_spent = false;
                m_inFile = false;
                m_asked.Enqueue(token.AskedAt);
            }

            WriteMine();
        }

        /// <summary>
        ///     Before the session starts the game again (graphics-mode switch): the token in memory if it is unread and
        ///     fresh, otherwise a new one. False if there is none (the game then can't log in, as in 2.2.0).
        /// </summary>
        public async Task<bool> RenewAsync(CancellationToken ct)
        {
            string inFile = LoginClient.ReadTokenFile(m_config);
            if (inFile != LoginClient.Busy) Observe(inFile); // read just before the game closed?
            if (!Fresh(Stopwatch.GetTimestamp()) && await MintAsync(ct, true).ConfigureAwait(false) == null)
                return false;
            try
            {
                WriteMine();
                return true;
            }
            catch (Exception ex)
            {
                Log.Write($"relogin: token not written: {ex.Message}");
                return false;
            }
        }

        /// <summary>End of the play session: this session's token leaves the file and memory.</summary>
        public void Forget()
        {
            RemoveMine("the session ended");
            lock (m_lock) m_token = null;
        }

        public Task WatchAsync(Process game, CancellationToken ct) =>
            WatchAsync(new GameProbe(game, m_config, m_t.OthersCache) {IsTunnelPort = IsTunnelPort}, ct);

        /// <summary>Keeps a token ready while <paramref name="game" /> runs; returns when it exits or on cancel.</summary>
        public async Task WatchAsync(IGameProbe game, CancellationToken ct)
        {
            var w = new WatchState();
            Log.Write($"relogin: watching the game (pid {game.Id})");
            try
            {
                while (!ct.IsCancellationRequested && !game.HasExited)
                {
                    try
                    {
                        await TickAsync(game, w, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        if (++m_errors <= 3) Log.Write($"relogin: {ex.GetType().Name}: {ex.Message}");
                    }

                    await Task.Delay(m_t.Poll, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // the game closed
            }
            finally
            {
                RemoveMine("the game closed"); // never left for the next start (a restart writes its own)
            }
        }

        private sealed class WatchState
        {
            public long? ConnectedSince, DisconnectedSince;
            public bool InGame, AtLogin, WasInGame;
            public int IdleRenewals;
            public string Front; // last logged "who may use the file" state
        }

        private async Task TickAsync(IGameProbe game, WatchState w, CancellationToken ct)
        {
            bool? connected = game.Connected();
            if (connected == null) return; // can't tell: do nothing (2.2.0 behaviour)
            long now = Stopwatch.GetTimestamp();
            if (connected.Value)
            {
                w.ConnectedSince ??= now;
                w.DisconnectedSince = null;
            }
            else
            {
                w.DisconnectedSince ??= now;
                w.ConnectedSince = null;
            }

            string inFile = LoginClient.ReadTokenFile(m_config);
            if (inFile == LoginClient.Busy) return; // the game or another launcher is on it right now
            if (Observe(inFile))
            {
                // a click on "Log In" read it: a login is going on (the next token, if one is needed, gets its own
                // renewals and waits until the game has been without a connection for LoginAfter since this read)
                w.IdleRenewals = 0;
                if (!connected.Value) w.DisconnectedSince = now;
            }

            if (w.ConnectedSince is long since && Elapsed(since) >= m_t.InGameAfter)
            {
                if (!w.InGame)
                {
                    w.InGame = w.WasInGame = true;
                    w.AtLogin = false;
                    Log.Write($"relogin: the game is logged in ({game.Describe()})");
                }

                // a token left in the file now would log the next "Log In" (this window after a disconnect, or another
                // window) into this account with a stale token; it stays in memory for the next login screen
                RemoveMine("the game is logged in");
                return;
            }

            if (!(w.DisconnectedSince is long gone) || Elapsed(gone) < m_t.LoginAfter)
                return; // a login going on (connected for a moment), or the connection just dropped

            if (!w.AtLogin)
            {
                w.AtLogin = true;
                w.InGame = false;
                w.IdleRenewals = 0;
                Log.Write(w.WasInGame ? "relogin: the game lost its connection; keeping a token for its login screen" : "relogin: the game is at its login screen");
            }

            // Several games from this folder share ini\origins.login: the player clicks "Log In" in the window in
            // front, so only that game's launcher keeps its token there.
            bool mayUseFile = game.IsForeground() || game.OtherClients() == 0;
            string front = mayUseFile ? "front" : "behind";
            if (front != w.Front && w.Front != null)
                Log.Write(mayUseFile ? "relogin: this game is in front: its token goes in the file" : "relogin: another game window is in front");
            w.Front = front;
            if (!mayUseFile)
            {
                RemoveMine("another game window is in front");
                return;
            }

            AtLoginScreen?.Invoke();
            if (m_stopped) return;
            IssuedToken token;
            bool spent;
            lock (m_lock)
            {
                token = m_token;
                spent = m_spent;
            }

            if (!Fresh(now))
            {
                bool idle = token != null && !spent; // never read, only getting old
                if (idle && w.IdleRenewals >= m_t.IdleRenewals)
                {
                    // Enough renewals for one wait. The old token stays in the file: a click on "Log In" reads it (the
                    // server refuses it, too old), and a token that was read is replaced at once.
                }
                else if (await MintAsync(ct, false).ConfigureAwait(false) != null && idle)
                {
                    w.IdleRenewals++;
                }
            }

            EnsureMineInFile();
        }

        /// <summary>The newest token is unread and has more than the renewal margin left.</summary>
        private bool Fresh(long now)
        {
            lock (m_lock)
                return m_token != null && !m_spent && m_token.Remaining(now) > Margin(m_token);
        }

        private TimeSpan Margin(IssuedToken token) =>
            TimeSpan.FromTicks(Math.Min(m_t.MaxMargin.Ticks, token.Lifetime.Ticks / 4));

        /// <summary>
        ///     Notices the game reading the token (the file is gone: true) or another launcher's token replacing it.
        /// </summary>
        private bool Observe(string inFile)
        {
            lock (m_lock)
            {
                if (m_token == null || !m_inFile || inFile == m_token.Value)
                    return false;
                m_inFile = false;
                if (inFile == null)
                {
                    m_spent = true;
                    Log.Write("relogin: the game read the login token");
                    return true;
                }

                Log.Write("relogin: another launcher's token is in the file; ours waits in memory");
                return false;
            }
        }

        private void EnsureMineInFile()
        {
            IssuedToken token;
            lock (m_lock)
            {
                token = m_token;
                if (token == null || m_spent) return;
            }

            if (LoginClient.ReadTokenFile(m_config) == token.Value)
            {
                lock (m_lock) if (m_token == token) m_inFile = true;
                return;
            }

            try
            {
                WriteMine();
            }
            catch (Exception ex)
            {
                if (++m_errors <= 3) Log.Write($"relogin: token not written: {ex.Message}");
            }
        }

        private void WriteMine()
        {
            IssuedToken token;
            lock (m_lock) token = m_token;
            if (token == null) return;
            LoginClient.WriteTokenFile(m_config, token.Value);
            lock (m_lock)
            {
                if (m_token == token)
                {
                    m_inFile = true;
                    m_spent = false;
                }
            }
        }

        /// <summary>Takes this launcher's token out of the file (kept in memory). True if it was there and removed.</summary>
        private bool RemoveMine(string why)
        {
            IssuedToken token;
            lock (m_lock)
            {
                token = m_token;
                if (token == null || !m_inFile) return false;
            }

            // if the file no longer holds it, the game read it or another launcher replaced it: Observe decides
            if (!LoginClient.DeleteTokenFileIf(m_config, token.Value))
                return false;
            lock (m_lock) if (m_token == token) m_inFile = false;
            Log.Write($"relogin: token taken out of the file ({why})");
            return true;
        }

        /// <summary>
        ///     Asks the site for a token unless a cap says no (<paramref name="force" />: the session's own restart).
        ///     A passing failure waits longer each time; a refusal of the account stops the keeper.
        /// </summary>
        private async Task<IssuedToken> MintAsync(CancellationToken ct, bool force)
        {
            long now = Stopwatch.GetTimestamp();
            lock (m_lock)
            {
                if (m_stopped) return null;
                while (m_asked.Count > 0 && Elapsed(m_asked.Peek()) > m_t.RateWindow)
                    m_asked.Dequeue();
                if (!force)
                {
                    if (now < m_retryAt) return null;
                    if (m_asked.Count >= m_t.RateLimit) return null;
                    if (m_asked.Count > 0 && Elapsed(m_asked.Last()) < m_t.MinGap) return null;
                }

                m_asked.Enqueue(now);
            }

            try
            {
                var token = await m_mint(ct).ConfigureAwait(false);
                lock (m_lock)
                {
                    m_token = token;
                    m_spent = false;
                    m_inFile = false;
                    m_backoff = TimeSpan.Zero;
                    Minted++;
                }

                Log.Write($"relogin: new login token (valid {token.Lifetime.TotalSeconds:N0}s)");
                return token;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (LoginException ex) when (!ex.Retryable)
            {
                m_stopped = true;
                Log.Write($"relogin: the site refused the account ({ex.Message}); no more tokens this session");
                return null;
            }
            catch (Exception ex)
            {
                lock (m_lock)
                {
                    m_backoff = m_backoff == TimeSpan.Zero
                        ? m_t.BackoffFirst
                        : TimeSpan.FromTicks(Math.Min(m_backoff.Ticks * 2, m_t.BackoffMax.Ticks));
                    m_retryAt = Stopwatch.GetTimestamp() + (long) (m_backoff.TotalSeconds * Stopwatch.Frequency);
                }

                Log.Write($"relogin: no token now ({ex.Message}); trying again in {m_backoff.TotalSeconds:N0}s");
                return null;
            }
        }

        private static TimeSpan Elapsed(long since) =>
            TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - since) / (double) Stopwatch.Frequency);
    }

    /// <summary>The keeper's timings; the tests shorten them.</summary>
    internal sealed class ReloginTimings
    {
        public TimeSpan Poll = TimeSpan.FromMilliseconds(250);

        /// <summary>No server connection this long: the game is at its login screen.</summary>
        public TimeSpan LoginAfter = TimeSpan.FromSeconds(2);

        /// <summary>Connected this long: logged in. A login attempt (account server, then game server) is far shorter.</summary>
        public TimeSpan InGameAfter = TimeSpan.FromSeconds(5);

        /// <summary>An unread token is renewed this long before it ends (at most a quarter of its life).</summary>
        public TimeSpan MaxMargin = TimeSpan.FromSeconds(20);

        /// <summary>Renewals of an unread token per wait at the login screen (90 s tokens: about 6 minutes).</summary>
        public int IdleRenewals = 4;

        /// <summary>The site allows 10 logins per 10 minutes per IP (its "auth" limit, shared with the website).</summary>
        public int RateLimit = 6;

        public TimeSpan RateWindow = TimeSpan.FromMinutes(10);
        public TimeSpan MinGap = TimeSpan.FromSeconds(5);
        public TimeSpan BackoffFirst = TimeSpan.FromSeconds(30);
        public TimeSpan BackoffMax = TimeSpan.FromMinutes(5);
        public TimeSpan OthersCache = TimeSpan.FromSeconds(1);
    }

    /// <summary>What the keeper needs to know about the game process (the tests use a stand-in).</summary>
    internal interface IGameProbe
    {
        int Id { get; }
        bool HasExited { get; }

        /// <summary>The game has a TCP connection to a server (true), none (false), or it can't be told (null).</summary>
        bool? Connected();

        /// <summary>The window in front belongs to the game.</summary>
        bool IsForeground();

        /// <summary>Other games running from this install folder.</summary>
        int OtherClients();

        string Describe();
    }

    /// <summary>The real game process: its TCP connections, the window in front, other games from the folder.</summary>
    internal sealed class GameProbe : IGameProbe
    {
        private readonly Process m_game;
        private readonly Config m_config;
        private readonly string m_dir;
        private readonly string m_name;
        private bool m_loopbackServer;
        private long m_loopbackAt;
        private readonly TimeSpan m_othersCache;
        private List<IPEndPoint> m_servers = new List<IPEndPoint>();
        private long m_othersAt;
        private int m_others;

        public GameProbe(Process game, Config config, TimeSpan? othersCache = null)
        {
            m_game = game;
            m_config = config;
            m_dir = config.InstallDir;
            m_othersCache = othersCache ?? TimeSpan.FromSeconds(1);
            try { m_name = game.ProcessName; }
            catch (Exception) { m_name = "Conquer"; }
            m_loopbackServer = ServerIsLoopback(config) ?? false;
            m_loopbackAt = Stopwatch.GetTimestamp();
        }

        /// <summary>Since 2.4.0: the UDP tunnel's local ports, which count as the server's (see <see cref="IsServer" />).</summary>
        public Func<int, bool> IsTunnelPort;

        public int Id => m_game.Id;

        public bool HasExited
        {
            get
            {
                try { return m_game.HasExited; }
                catch (Exception) { return true; }
            }
        }

        public bool? Connected()
        {
            var remotes = TcpTable.Established(m_game.Id);
            if (remotes == null) return null;
            if (Stopwatch.GetTimestamp() - m_loopbackAt > 2 * Stopwatch.Frequency)
            {
                // since 2.4.0 the file changes during the session: 127.0.0.1 while the UDP tunnel carries the game
                m_loopbackServer = ServerIsLoopback(m_config) ?? m_loopbackServer; // being written: as it was
                m_loopbackAt = Stopwatch.GetTimestamp();
            }

            var tunnel = IsTunnelPort;
            m_servers = remotes.Where(r => IsServer(r, m_loopbackServer || tunnel != null && tunnel(r.Port))).ToList();
            return m_servers.Count > 0;
        }

        /// <summary>
        ///     A connection that means "logged in": not a web one (80/443) and, unless the account server is on this
        ///     machine (the UDP tunnel's local port since 2.4.0, or a test setup), not to this machine.
        /// </summary>
        public static bool IsServer(IPEndPoint remote, bool loopbackServer) =>
            remote.Port != 80 && remote.Port != 443 && (loopbackServer || !IPAddress.IsLoopback(remote.Address));

        public bool IsForeground()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero) return false;
            GetWindowThreadProcessId(window, out uint pid);
            return pid == (uint) m_game.Id;
        }

        public int OtherClients()
        {
            if (m_othersAt != 0 && (Stopwatch.GetTimestamp() - m_othersAt) / (double) Stopwatch.Frequency < m_othersCache.TotalSeconds)
                return m_others;
            int count = 0;
            foreach (var process in Process.GetProcessesByName(m_name))
            {
                using (process)
                {
                    if (process.Id == m_game.Id) continue;
                    string path = ImagePath(process.Id);
                    if (path != null && path.StartsWith(m_dir, StringComparison.OrdinalIgnoreCase))
                        count++;
                }
            }

            m_others = count;
            m_othersAt = Stopwatch.GetTimestamp();
            return count;
        }

        public string Describe() => m_servers.Count == 0 ? "no connection" : string.Join(", ", m_servers);

        /// <summary>ini\origins.server names this machine; null when it can't be read.</summary>
        private static bool? ServerIsLoopback(Config config)
        {
            try
            {
                string server = File.ReadAllText(Path.Combine(config.InstallDir, Updater.ServerFile), Encoding.ASCII).Trim();
                int colon = server.LastIndexOf(':');
                string host = colon > 0 ? server.Substring(0, colon) : server;
                return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                       IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>A process's exe path without admin rights (works for the 32-bit game from a 64-bit launcher).</summary>
        public static string ImagePath(int pid)
        {
            IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var buffer = new StringBuilder(1024);
                int size = buffer.Capacity;
                return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private const int ProcessQueryLimitedInformation = 0x1000;

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    }

    /// <summary>The system's IPv4 TCP table (GetExtendedTcpTable, no admin rights needed).</summary>
    internal static class TcpTable
    {
        private const int AfInet = 2, TcpTableOwnerPidConnections = 4, MibTcpStateEstab = 5, RowSize = 24;
        private const uint ErrorInsufficientBuffer = 122;

        /// <summary>The remote ends of <paramref name="pid" />'s established connections; null if the table can't be read.</summary>
        public static List<IPEndPoint> Established(int pid)
        {
            int size = 0;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                IntPtr buffer = size > 0 ? Marshal.AllocHGlobal(size) : IntPtr.Zero;
                try
                {
                    uint result = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidConnections, 0);
                    if (result == ErrorInsufficientBuffer)
                    {
                        size += 16 * RowSize; // the table may grow before the next call
                        continue;
                    }

                    if (result != 0) return null;
                    int count = Marshal.ReadInt32(buffer);
                    var list = new List<IPEndPoint>();
                    for (int i = 0; i < count; i++)
                    {
                        // MIB_TCPROW_OWNER_PID: state, local addr, local port, remote addr, remote port, owning pid
                        IntPtr row = buffer + 4 + i * RowSize;
                        if (Marshal.ReadInt32(row) != MibTcpStateEstab || Marshal.ReadInt32(row + 20) != pid) continue;
                        long address = (uint) Marshal.ReadInt32(row + 12); // network order, as IPAddress(long) wants
                        int port = Marshal.ReadInt32(row + 16);
                        list.Add(new IPEndPoint(new IPAddress(address), ((port & 0xFF) << 8) | ((port >> 8) & 0xFF)));
                    }

                    return list;
                }
                finally
                {
                    if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                }
            }

            return null;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    }
}
