// LauncherTests relogin --dir D
//   2.3.0: the version text (5095), the site's answers (expiresIn, Relogin, 429, refusals), ini\origins.login written
//   and read the way the patched client opens it, the TCP table and the game probe on this process, and the
//   ReloginKeeper against a stand-in game (connected / at its login screen / window in front) and a stand-in site:
//   first login, disconnect and login again, a login going on, idle renewals and their cap, two game windows,
//   passing and final site failures, a probe that can't tell, the end of the game, and the GameSession hook.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OriginsLauncher
{
    internal static partial class TestProgram
    {
        private static async Task Relogin(Config updateConfig)
        {
            string dir = updateConfig.InstallDir;
            Directory.CreateDirectory(Path.Combine(dir, "ini"));
            string saved = L.S.Code;

            Texts();
            await SiteAnswers(dir);
            L.S = Strings.For(saved);

            var config = Config.FromArgs(new[] {"--dir=" + dir});
            TokenFile(config);
            Probe(config);
            await Keeper(config);
            await SessionHook(config);

            string log = Path.Combine(config.StateDir, "launcher.log");
            if (File.Exists(log))
            {
                Console.WriteLine("  launcher.log (relogin lines, last 12):");
                foreach (string line in File.ReadAllLines(log).Where(l => l.Contains("relogin:")).Reverse().Take(12).Reverse())
                    Console.WriteLine("    " + line);
            }
        }

        // ------------------------------------------------------------------ texts

        private static void Texts()
        {
            foreach (var s in Strings.All)
                Check(s.Subtitle.Contains("5095") && !s.Subtitle.Contains("6609"), $"{s.Code}: header \"{s.Subtitle}\"");
            Check(Strings.Pt.Subtitle == "CONQUER CLÁSSICO  ·  VERSÃO 5095", "pt header reads VERSÃO 5095");
            var fields = typeof(Strings).GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => f.FieldType == typeof(string)).ToList();
            foreach (var s in Strings.All)
            {
                var empty = fields.Where(f => string.IsNullOrEmpty((string) f.GetValue(s))).Select(f => f.Name).ToList();
                Check(empty.Count == 0, $"{s.Code}: every text set ({fields.Count}){(empty.Count > 0 ? ": missing " + string.Join(", ", empty) : "")}");
                var old = fields.Where(f => ((string) f.GetValue(s)).Contains("6609")).Select(f => f.Name).ToList();
                Check(old.Count == 0, $"{s.Code}: no text shows 6609{(old.Count > 0 ? ": " + string.Join(", ", old) : "")}");
            }
        }

        // ------------------------------------------------------------------ site answers

        private static async Task SiteAnswers(string dir)
        {
            const int port = 5097;
            var bodies = new List<string>();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            var serve = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await listener.GetContextAsync(); }
                    catch (Exception) { return; }
                    string body = new StreamReader(ctx.Request.InputStream).ReadToEnd();
                    lock (bodies) bodies.Add(body);
                    string user = body.Contains("\"Username\":\"") ? body.Split(new[] {"\"Username\":\""}, StringSplitOptions.None)[1].Split('"')[0] : "";
                    (int status, string answer) = user switch
                    {
                        "tester" => (200, body.Contains("\"Relogin\":true")
                            ? "{\"ok\":true,\"token\":\"0123456789abcdef0123456789abcdef\",\"expiresIn\":600}"
                            : "{\"ok\":true,\"token\":\"0123456789abcdef0123456789abcdef\"}"),
                        "busy" => (429, ""),
                        "locked" => (429, "{\"ok\":false,\"message\":\"Muitas tentativas para este usuário. Aguarde 15 minutos e tente novamente.\"}"),
                        "banned" => (403, "{\"ok\":false,\"message\":\"Esta conta está banida.\"}"),
                        "down" => (500, ""),
                        _ => (400, "{\"ok\":false,\"message\":\"Usuário ou senha incorretos.\"}")
                    };
                    ctx.Response.StatusCode = status;
                    byte[] bytes = Encoding.UTF8.GetBytes(answer);
                    if (bytes.Length > 0) ctx.Response.ContentType = "application/json";
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    ctx.Response.Close();
                }
            });

            try
            {
                var config = Config.FromArgs(new[] {"--dir=" + dir, $"--site-url=http://127.0.0.1:{port}/"});
                var kept = await LoginClient.IssueAsync(config, "tester", "right", true);
                Check(kept.Value.Length == 32 && kept.Lifetime == TimeSpan.FromSeconds(600), $"relogin token: {kept.Lifetime.TotalSeconds}s from expiresIn");
                Check(bodies.Last().Contains("\"Relogin\":true") && bodies.Last().Contains("\"Username\":\"tester\""), $"the request says Relogin: {bodies.Last().Replace("right", "***")}");
                var plain = await LoginClient.IssueAsync(config, "tester", "right");
                Check(plain.Lifetime == LoginClient.DefaultLifetime && !bodies.Last().Contains("Relogin"),
                    $"a site without expiresIn: {plain.Lifetime.TotalSeconds}s; no Relogin key unless asked ({bodies.Last().Replace("right", "***")})");
                Check(await LoginClient.RequestTokenAsync(config, "tester", "right") == "0123456789abcdef0123456789abcdef", "RequestTokenAsync still returns the token");

                foreach (string lang in new[] {"pt", "en", "fil"})
                {
                    L.S = Strings.For(lang);
                    await Refused(config, "busy", L.S.LoginBusy, true, $"{lang}: 429 without a body (the site's per-IP limit)");
                }

                L.S = Strings.Pt;
                await Refused(config, "locked", "Muitas tentativas para este usuário. Aguarde 15 minutos e tente novamente.", true, "429 with the site's message (per-account lock)");
                await Refused(config, "banned", "Esta conta está banida.", false, "403 banned");
                await Refused(config, "wrong", "Usuário ou senha incorretos.", false, "400 wrong password");
                await Refused(config, "down", L.S.LoginConnError, true, "500");
                var closed = Config.FromArgs(new[] {"--dir=" + dir, "--site-url=http://127.0.0.1:1/"});
                await Refused(closed, "tester", L.S.LoginConnError, true, "no site listening");

                // 2.2.0 fetched the restart's token with GetResult() on the window's thread: its continuation needed that
                // thread. Now the login code never needs its caller's thread.
                string got = null;
                var thread = new Thread(() =>
                {
                    var context = new PumpContext();
                    SynchronizationContext.SetSynchronizationContext(context);
                    got = LoginClient.RequestTokenAsync(config, "tester", "right").GetAwaiter().GetResult();
                }) {IsBackground = true};
                thread.Start();
                Check(thread.Join(TimeSpan.FromSeconds(15)) && got != null, "a blocking call from a single-thread context (the window's) does not deadlock");
            }
            finally
            {
                listener.Stop();
                await Task.WhenAny(serve, Task.Delay(1000));
            }
        }

        private static async Task Refused(Config config, string user, string message, bool retryable, string what)
        {
            try
            {
                await LoginClient.IssueAsync(config, user, "right", true);
                Fail($"{what}: must fail");
            }
            catch (LoginException ex)
            {
                Check(ex.Message == message && ex.Retryable == retryable, $"{what}: \"{ex.Message}\" retryable={ex.Retryable}");
            }
        }

        /// <summary>A synchronization context whose posts run only on a thread that is not pumping (the blocked window).</summary>
        private sealed class PumpContext : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object state)
            {
                // never run: the owning thread is blocked, like a window thread inside GetResult()
            }
        }

        // ------------------------------------------------------------------ the token file

        private static void TokenFile(Config config)
        {
            string path = LoginClient.TokenPath(config);
            if (File.Exists(path)) File.Delete(path);
            Check(LoginClient.ReadTokenFile(config) == null, "no file: null");
            LoginClient.WriteTokenFile(config, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            Check(File.ReadAllBytes(path).SequenceEqual(Encoding.ASCII.GetBytes("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")), "written: 32 ASCII characters, no newline");
            Check(!Directory.GetFiles(Path.GetDirectoryName(path), "origins.login.*.tmp").Any(), "no temporary file left");

            // the client has it open (CreateFileA GENERIC_READ, FILE_SHARE_READ): the new token waits, then lands whole
            var clock = Stopwatch.StartNew();
            using (var client = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var release = Task.Run(() =>
                {
                    Thread.Sleep(150);
                    client.Dispose();
                });
                LoginClient.WriteTokenFile(config, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
                release.Wait();
            }

            Check(LoginClient.ReadTokenFile(config) == "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" && clock.ElapsedMilliseconds >= 140,
                $"while the client reads the old one the new token waits ({clock.ElapsedMilliseconds} ms) and replaces it");

            // the launcher looking at the file never stops the client from opening and deleting it
            using (var looking = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                string read = ClientRead(config);
                Check(read == "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", $"the client opens (FILE_SHARE_READ), reads and deletes it while the launcher looks ({read})");
            }

            Check(!File.Exists(path), "deleted by the client");
            LoginClient.WriteTokenFile(config, "cccccccccccccccccccccccccccccccc");
            File.SetAttributes(path, FileAttributes.ReadOnly);
            LoginClient.WriteTokenFile(config, "dddddddddddddddddddddddddddddddd");
            Check(LoginClient.ReadTokenFile(config) == "dddddddddddddddddddddddddddddddd", "a read-only file is replaced too");
            Check(!LoginClient.DeleteTokenFileIf(config, "cccccccccccccccccccccccccccccccc") && File.Exists(path), "another token is not deleted");
            Check(LoginClient.DeleteTokenFileIf(config, "dddddddddddddddddddddddddddddddd") && !File.Exists(path), "our own token is deleted");
        }

        /// <summary>What the patched client does at "Log In" (cave 0xD69400): open, read 32 bytes, close, DeleteFileA.</summary>
        private static string ClientRead(Config config)
        {
            string path = LoginClient.TokenPath(config);
            try
            {
                string token;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var buffer = new byte[32];
                    int n = fs.Read(buffer, 0, 32);
                    token = Encoding.ASCII.GetString(buffer, 0, n);
                }

                File.Delete(path);
                return token;
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ TCP table and probe

        private static void Probe(Config config)
        {
            int self = Process.GetCurrentProcess().Id;
            var server = new TcpListener(IPAddress.Loopback, 0);
            server.Start();
            int port = ((IPEndPoint) server.LocalEndpoint).Port;
            var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            var accepted = server.AcceptTcpClient();
            try
            {
                var remotes = TcpTable.Established(self);
                Check(remotes != null, "the TCP table is readable without admin rights");
                Check(remotes != null && remotes.Any(r => r.Address.Equals(IPAddress.Loopback) && r.Port == port),
                    $"this process's connection to 127.0.0.1:{port} is listed ({string.Join(", ", remotes ?? new List<IPEndPoint>())})");
                Check(TcpTable.Established(999999)?.Count == 0, "a pid with no connections: none");

                Check(!GameProbe.IsServer(new IPEndPoint(IPAddress.Loopback, 5816), false), "loopback is not a server (server elsewhere)");
                Check(GameProbe.IsServer(new IPEndPoint(IPAddress.Loopback, 5816), true), "loopback is a server when the account server is on this machine");
                Check(GameProbe.IsServer(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 5816), false), "a remote game port is a server");
                Check(!GameProbe.IsServer(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 443), false) &&
                      !GameProbe.IsServer(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 80), false), "web ports are not");

                string serverFile = Path.Combine(config.InstallDir, Updater.ServerFile);
                string before = File.Exists(serverFile) ? File.ReadAllText(serverFile) : null;
                try
                {
                    var me = Process.GetCurrentProcess();
                    File.WriteAllText(serverFile, "127.0.0.1:9958");
                    Check(new GameProbe(me, config).Connected() == true, "probe: connected (account server on this machine, loopback counts)");
                    File.WriteAllText(serverFile, "203.0.113.7:9958");
                    Check(new GameProbe(me, config).Connected() == false, "probe: not connected (only a loopback connection, server elsewhere)");
                }
                finally
                {
                    if (before != null) File.WriteAllText(serverFile, before);
                    else File.Delete(serverFile);
                }
            }
            finally
            {
                client.Close();
                accepted.Close();
                server.Stop();
            }

            string image = GameProbe.ImagePath(self);
            Check(string.Equals(image, Process.GetCurrentProcess().MainModule.FileName, StringComparison.OrdinalIgnoreCase), $"exe path by pid: {image}");

            // other games from the folder: copies of ping.exe in the install folder stand in for Conquer.exe
            string games = Path.Combine(config.InstallDir, "Env_DX9");
            Directory.CreateDirectory(games);
            string ping = Path.Combine(games, "PING.EXE");
            File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), ping, true);
            var started = new List<Process>();
            Process Run(string exe) => Process.Start(new ProcessStartInfo(exe, "-n 6 127.0.0.1") {UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true});
            try
            {
                started.Add(Run(ping));
                var probe = new GameProbe(started[0], config, TimeSpan.Zero);
                Check(probe.OtherClients() == 0, "one game from the folder: alone");
                started.Add(Run(Path.Combine(Environment.SystemDirectory, "PING.EXE")));
                Check(probe.OtherClients() == 0, "the same exe name from another folder does not count");
                started.Add(Run(ping));
                Check(probe.OtherClients() == 1, "a second game from the folder counts");
                Check(!probe.IsForeground(), "a windowless process is not in front");
                Check(probe.Connected() == false && !probe.HasExited, "ping: running, no TCP connection");
            }
            finally
            {
                foreach (var p in started)
                {
                    try { if (!p.HasExited) p.Kill(); p.WaitForExit(2000); } catch (Exception) { /* gone */ }
                }

                try { File.Delete(ping); } catch (Exception) { /* still closing */ }
            }
        }

        // ------------------------------------------------------------------ the keeper

        private sealed class FakeGame : IGameProbe
        {
            public volatile bool IsConnected, Front, Unknown, Exited;
            public volatile int Others;
            public int Id { get; set; } = 4242;
            public bool HasExited => Exited;
            public bool? Connected() => Unknown ? (bool?) null : IsConnected;
            public bool IsForeground() => Front;
            public int OtherClients() => Others;
            public string Describe() => IsConnected ? "203.0.113.7:5816 (stand-in)" : "no connection";
        }

        private sealed class FakeSite
        {
            private readonly object m_lock = new object();
            public readonly List<long> Times = new List<long>();
            public readonly Queue<Exception> Failures = new Queue<Exception>();
            public TimeSpan Lifetime = TimeSpan.FromSeconds(30);

            public int Calls
            {
                get { lock (m_lock) return Times.Count; }
            }

            public Task<IssuedToken> Mint(CancellationToken ct)
            {
                lock (m_lock)
                {
                    Times.Add(Stopwatch.GetTimestamp());
                    if (Failures.Count > 0) throw Failures.Dequeue();
                }

                return Task.FromResult(NewToken(Lifetime));
            }
        }

        private static IssuedToken NewToken(TimeSpan lifetime) => new IssuedToken(Guid.NewGuid().ToString("N"), lifetime, Stopwatch.GetTimestamp());

        private static ReloginTimings Fast() => new ReloginTimings
        {
            Poll = TimeSpan.FromMilliseconds(20), LoginAfter = TimeSpan.FromMilliseconds(150), InGameAfter = TimeSpan.FromMilliseconds(400),
            MinGap = TimeSpan.Zero, RateLimit = 100, BackoffFirst = TimeSpan.FromMilliseconds(300), BackoffMax = TimeSpan.FromSeconds(1)
        };

        private static bool WaitFor(Func<bool> condition, int ms)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ms)
            {
                if (condition()) return true;
                Thread.Sleep(10);
            }

            return condition();
        }

        private sealed class Watching : IDisposable
        {
            private readonly CancellationTokenSource m_cts = new CancellationTokenSource();
            public readonly Task Task;
            public Watching(ReloginKeeper keeper, IGameProbe game) => Task = Task.Run(() => keeper.WatchAsync(game, m_cts.Token));

            public void Dispose()
            {
                m_cts.Cancel();
                Task.Wait(5000);
            }
        }

        private static async Task Keeper(Config config)
        {
            string path = LoginClient.TokenPath(config);
            string InFile() => LoginClient.ReadTokenFile(config);
            void Clean() { if (File.Exists(path)) File.Delete(path); }

            // 1. first login from the game's screen, a disconnect, the next login
            Clean();
            var site = new FakeSite();
            var keeper = new ReloginKeeper(config, site.Mint, Fast());
            var first = NewToken(TimeSpan.FromSeconds(30));
            keeper.Start(first);
            var game = new FakeGame();
            using (new Watching(keeper, game))
            {
                Check(InFile() == first.Value, "the dialog's token is in the file before the game starts");
                Thread.Sleep(400);
                Check(InFile() == first.Value && site.Calls == 0, "at the login screen with a fresh token: nothing asked");
                game.IsConnected = true; // Log In: the client connects, gets the seed, then reads the file
                Check(ClientRead(config) == first.Value, "Log In: the client reads the dialog's token");
                Thread.Sleep(700);
                Check(InFile() == null && site.Calls == 0, "logged in: no file, nothing asked");
                game.IsConnected = false; // disconnected: the client is back at its login screen
                Check(WaitFor(() => InFile() != null, 2000), "after the disconnect a token is in the file");
                string second = InFile();
                Check(second != first.Value && site.Calls == 1, $"a new one ({site.Calls} asked)");
                game.IsConnected = true;
                Check(ClientRead(config) == second, "Log In again: the client reads it");
                Thread.Sleep(700);
                Check(InFile() == null && site.Calls == 1, "logged in again: no file, nothing more asked");
            }

            // 2. a login going on: connected for a moment, the token must stay until the client reads it
            Clean();
            keeper = new ReloginKeeper(config, site.Mint, Fast());
            var racing = NewToken(TimeSpan.FromSeconds(30));
            keeper.Start(racing);
            game = new FakeGame();
            using (new Watching(keeper, game))
            {
                Thread.Sleep(300);
                game.IsConnected = true; // the account server connection, before the client reads the file
                bool stayed = true;
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 300)
                {
                    stayed &= InFile() == racing.Value;
                    Thread.Sleep(10);
                }

                Check(stayed, "connected for 300 ms (a login going on): the token stays in the file");
                Check(ClientRead(config) == racing.Value, "and the client reads it");
            }

            // 3. logged in with a token still in the file (written as the client read the old one): taken out, reused
            Clean();
            var calls = site.Calls;
            keeper = new ReloginKeeper(config, site.Mint, Fast());
            var left = NewToken(TimeSpan.FromSeconds(30));
            keeper.Start(left);
            game = new FakeGame {IsConnected = true};
            using (new Watching(keeper, game))
            {
                Check(WaitFor(() => InFile() == null, 2000), "logged in: a token left in the file is taken out");
                game.IsConnected = false;
                Check(WaitFor(() => InFile() == left.Value, 2000) && site.Calls == calls, "disconnected: the same unread token goes back, nothing asked");
            }

            // 4. idle at the login screen: an unread token is renewed before it ends, a few times; then the old one waits,
            // a click reads it (too old for the server) and a new one comes at once
            Clean();
            site = new FakeSite {Lifetime = TimeSpan.FromMilliseconds(400)};
            keeper = new ReloginKeeper(config, site.Mint, Fast());
            keeper.Start(NewToken(TimeSpan.FromMilliseconds(400)));
            game = new FakeGame();
            using (new Watching(keeper, game))
            {
                Thread.Sleep(3000);
                Check(site.Calls == 4, $"idle 3 s with 0.4 s tokens: renewed {site.Calls} times (cap 4)");
                string stale = InFile();
                Check(stale != null, "the last token stays in the file");
                game.IsConnected = true;
                Check(ClientRead(config) == stale, "Log In reads the old token (the server refuses it)");
                Thread.Sleep(60);
                game.IsConnected = false;
                Check(WaitFor(() => InFile() != null, 2000) && site.Calls == 5, $"a new token right after the refused login ({site.Calls} asked)");
                Thread.Sleep(1000);
                Check(site.Calls > 5, $"a read token restarts the renewals ({site.Calls} asked)");
            }

            // rate cap: the dialog's token counts; RateLimit 3 leaves 2 renewals
            Clean();
            site = new FakeSite {Lifetime = TimeSpan.FromMilliseconds(300)};
            var capped = Fast();
            capped.RateLimit = 3;
            capped.IdleRenewals = 50;
            keeper = new ReloginKeeper(config, site.Mint, capped);
            keeper.Start(NewToken(TimeSpan.FromMilliseconds(300)));
            using (new Watching(keeper, new FakeGame()))
            {
                Thread.Sleep(2000);
                Check(site.Calls == 2, $"rate cap 3 per window (the dialog's included): {site.Calls} asked");
            }

            // 5. two game windows from the folder share the file: the one in front has its token there
            Clean();
            site = new FakeSite();
            var siteB = new FakeSite();
            var a = new ReloginKeeper(config, site.Mint, Fast());
            var b = new ReloginKeeper(config, siteB.Mint, Fast());
            var ta = NewToken(TimeSpan.FromSeconds(30));
            var tb = NewToken(TimeSpan.FromSeconds(30));
            var ga = new FakeGame {Id = 1, Others = 1, Front = true};
            var gb = new FakeGame {Id = 2, Others = 1};
            a.Start(ta);
            b.Start(tb); // the second launcher writes its first token while the first game is in front
            using (new Watching(a, ga))
            using (new Watching(b, gb))
            {
                Check(WaitFor(() => InFile() == ta.Value, 2000), "game A in front: A's token in the file");
                ga.Front = false;
                gb.Front = true;
                Check(WaitFor(() => InFile() == tb.Value, 2000), "game B in front: B's token");
                gb.Front = false;
                Check(WaitFor(() => InFile() == null, 2000), "neither in front (browser): no token for a click on the wrong window");
                ga.Front = true;
                Check(WaitFor(() => InFile() == ta.Value, 2000), "A in front again: A's token back");
                Check(site.Calls == 0 && siteB.Calls == 0, "switching windows asks the site nothing");
                ga.IsConnected = true;
                Check(ClientRead(config) == ta.Value, "A logs in with A's token");
                ga.Front = false;
                gb.Front = true;
                Check(WaitFor(() => InFile() == tb.Value, 2000) && site.Calls == 0, "B in front: B's token, A logged in");
                gb.IsConnected = true;
                Check(ClientRead(config) == tb.Value, "B logs in with B's token");
                Thread.Sleep(700);
                Check(InFile() == null && site.Calls == 0 && siteB.Calls == 0, "both logged in: no file, nothing asked");
            }

            // 6. the site: passing failures wait longer each time; a refusal stops the keeper
            Clean();
            site = new FakeSite();
            site.Failures.Enqueue(new LoginException("down", retryable: true));
            site.Failures.Enqueue(new LoginException("down", retryable: true));
            keeper = new ReloginKeeper(config, site.Mint, Fast());
            var spent = NewToken(TimeSpan.FromSeconds(30));
            keeper.Start(spent);
            ClientRead(config); // read by a login that then failed: a new one is needed
            using (new Watching(keeper, new FakeGame()))
            {
                Check(WaitFor(() => InFile() != null, 4000), "after two passing failures a token comes");
                double Gap(int i) => (site.Times[i] - site.Times[i - 1]) / (double) Stopwatch.Frequency * 1000;
                Check(site.Calls == 3 && Gap(1) >= 280 && Gap(2) >= 560, $"asked {site.Calls} times, waits {Gap(1):N0} ms then {Gap(2):N0} ms (0.3 s, doubling)");
            }

            Clean();
            site = new FakeSite();
            site.Failures.Enqueue(new LoginException("Esta conta está banida."));
            keeper = new ReloginKeeper(config, site.Mint, Fast());
            keeper.Start(NewToken(TimeSpan.FromSeconds(30)));
            ClientRead(config);
            using (new Watching(keeper, new FakeGame()))
            {
                Thread.Sleep(1500);
                Check(site.Calls == 1 && keeper.Stopped && InFile() == null, $"a refused account: asked once ({site.Calls}), stopped, no file");
            }

            // 7. the TCP table can't be read: nothing at all (2.2.0 behaviour)
            Clean();
            site = new FakeSite();
            keeper = new ReloginKeeper(config, site.Mint, Fast());
            var unknown = NewToken(TimeSpan.FromMilliseconds(400));
            keeper.Start(unknown);
            using (new Watching(keeper, new FakeGame {Unknown = true, IsConnected = true}))
            {
                Thread.Sleep(1200);
                Check(site.Calls == 0 && InFile() == unknown.Value, "a probe that can't tell: nothing asked, nothing removed");
            }

            // 8. the game closes: its token leaves the file (unless another launcher's is there); a restart's renewal
            Clean();
            site = new FakeSite();
            keeper = new ReloginKeeper(config, site.Mint, Fast());
            var closing = NewToken(TimeSpan.FromSeconds(30));
            keeper.Start(closing);
            game = new FakeGame();
            var watch = new Watching(keeper, game);
            Thread.Sleep(300);
            game.Exited = true;
            Check(watch.Task.Wait(2000) && InFile() == null, "the game exits: its token is taken out");
            Check(await keeper.RenewAsync(CancellationToken.None) && InFile() == closing.Value && site.Calls == 0, "restart: the unread token is written again, nothing asked");
            ClientRead(config);
            Check(await keeper.RenewAsync(CancellationToken.None) && InFile() != null && InFile() != closing.Value && site.Calls == 1, "restart after a login: a new token");
            LoginClient.WriteTokenFile(config, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
            keeper.Forget();
            Check(InFile() == "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "the session ends: another launcher's token stays");
            Clean();
        }

        // ------------------------------------------------------------------ the session's hook

        private static async Task SessionHook(Config config)
        {
            var seen = new List<string>();
            CancellationToken watched = default;
            var session = new GameSession(config,
                () => Task.FromResult(Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 >nul") {UseShellExecute = false, CreateNoWindow = true})),
                async (game, ct) =>
                {
                    lock (seen) seen.Add($"pid {game.Id}");
                    watched = ct;
                    try { await Task.Delay(-1, ct); }
                    catch (OperationCanceledException) { lock (seen) seen.Add("stopped"); throw; }
                });
            var clock = Stopwatch.StartNew();
            await session.RunAsync();
            Check(seen.Count == 2 && seen[0].StartsWith("pid ") && seen[1] == "stopped" && watched.IsCancellationRequested,
                $"GameSession: the watch ran with the game and stopped when it exited ({string.Join(", ", seen)}, {clock.ElapsedMilliseconds} ms)");
        }
    }
}
