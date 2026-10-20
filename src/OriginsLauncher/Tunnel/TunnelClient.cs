using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Origins.Tunnel
{
    /// <summary>
    ///     The launcher's side of the tunnel: one local TCP port on 127.0.0.1 per server port the game uses (the login
    ///     port of its language and the game port), carried over one UDP session to the gateway. The game's
    ///     ini\origins.server points at the local login port; the gateway tells the account server the local game port
    ///     (sent in CONNECT), which the client is then sent to instead of the game server's public address.
    ///     <para>
    ///         A session that dies (10 s without an answer, or the gateway closed it) closes the game's connections
    ///         (the game goes back to its login screen, see the launcher's ReloginKeeper) and is opened again in the
    ///         background; a connection that arrives while there is none waits for a new handshake (up to ~4.5 s) or is
    ///         closed. <see cref="IsUp" /> says which address the launcher should give the game.
    ///     </para>
    /// </summary>
    public sealed class TunnelClient : IDisposable
    {
        public const int HandshakeAttempts = 3, AttemptMs = 1500, ResendMs = 500;
        private const int SioUdpConnReset = -1744830452;

        private readonly IPEndPoint m_gateway;
        private readonly int[] m_targets;
        private readonly int m_gameTarget;
        private readonly object m_sync = new object();
        private readonly Dictionary<int, TcpListener> m_listeners = new Dictionary<int, TcpListener>();
        private readonly Dictionary<ushort, Bridge> m_bridges = new Dictionary<ushort, Bridge>();
        private readonly RandomNumberGenerator m_random = RandomNumberGenerator.Create();

        private Socket m_udp;
        private Channel m_channel;
        private uint m_session, m_token;
        private ushort m_nextStream;
        private bool m_up, m_reconnecting;
        private volatile bool m_disposed;
        private Task<bool> m_connecting;
        private Pending m_pending;
        private Thread m_receiver, m_timer;
        private long m_upSince;

        /// <summary>Log lines (the launcher's log; the tests' console).</summary>
        public Action<string> Log;

        /// <summary>Sessions opened so far (1 = never reconnected).</summary>
        public int Sessions { get; private set; }

        private sealed class Pending
        {
            public byte Expect;
            public ulong Nonce;
            /// <remarks>
            ///     Completed by the receive thread inside the lock: its awaiters (the handshake, then whoever waits for
            ///     the session) must run elsewhere, or they would keep the lock and the receive thread.
            /// </remarks>
            public TaskCompletionSource<byte[]> Reply;
        }

        /// <param name="gateway">The gateway's UDP address.</param>
        /// <param name="targets">The server ports the game connects to (login port, game port).</param>
        /// <param name="gameTarget">The game server's port, among <paramref name="targets" />.</param>
        public TunnelClient(IPEndPoint gateway, IEnumerable<int> targets, int gameTarget)
        {
            m_gateway = gateway;
            m_targets = targets.Distinct().ToArray();
            m_gameTarget = gameTarget;
        }

        public bool IsUp
        {
            get
            {
                lock (m_sync) return m_up;
            }
        }

        /// <summary>The 127.0.0.1 port that carries <paramref name="target" />, or 0.</summary>
        public int LocalPort(int target)
        {
            lock (m_sync)
                return m_listeners.TryGetValue(target, out var l) ? ((IPEndPoint) l.LocalEndpoint).Port : 0;
        }

        public int RttMs
        {
            get
            {
                Channel channel;
                lock (m_sync) channel = m_up ? m_channel : null;
                return channel?.RttMs ?? -1;
            }
        }

        public string Stats()
        {
            Channel channel;
            lock (m_sync) channel = m_channel;
            return channel == null ? "no session" : $"session {m_session:x8} ({Sessions} opened): {channel.Stats()}";
        }

        /// <summary>Opens the local ports and the session. True when the gateway accepted it.</summary>
        public Task<bool> StartAsync()
        {
            Clock.HighResolution();
            lock (m_sync)
            {
                foreach (int target in m_targets)
                {
                    var listener = new TcpListener(IPAddress.Loopback, 0);
                    listener.Start();
                    m_listeners[target] = listener;
                    _ = AcceptLoopAsync(listener, target);
                }
            }

            m_receiver = new Thread(ReceiveLoop) {IsBackground = true, Name = "tunnel receive"};
            m_receiver.Start();
            m_timer = new Thread(TimerLoop) {IsBackground = true, Name = "tunnel timer"};
            m_timer.Start();
            return EnsureSessionAsync();
        }

        /// <summary>The session, opened now if there is none (one handshake at a time, shared by every caller).</summary>
        public Task<bool> EnsureSessionAsync()
        {
            lock (m_sync)
            {
                if (m_disposed) return Task.FromResult(false);
                if (m_up) return Task.FromResult(true);
                if (m_connecting == null || m_connecting.IsCompleted)
                    m_connecting = HandshakeAsync();
                return m_connecting;
            }
        }

        public void Dispose()
        {
            List<Bridge> bridges;
            lock (m_sync)
            {
                if (m_disposed) return;
                m_disposed = true;
                if (m_up)
                {
                    var close = new byte[Wire.HeaderSize];
                    Wire.PutHeader(close, Wire.Close, m_session, m_token);
                    Transmit(close, close.Length);
                    Transmit(close, close.Length);
                }

                m_up = false;
                m_channel?.Shutdown();
                bridges = m_bridges.Values.ToList();
                m_bridges.Clear();
                foreach (var l in m_listeners.Values)
                {
                    try { l.Stop(); }
                    catch (Exception) { /* already stopped */ }
                }

                m_pending?.Reply.TrySetResult(null);
            }

            foreach (var b in bridges) b.Close(false);
            try { m_udp?.Close(); }
            catch (Exception) { /* already closed */ }
        }

        // ------------------------------------------------------------------ handshake

        private async Task<bool> HandshakeAsync()
        {
            NewSocket();
            for (int attempt = 0; attempt < HandshakeAttempts; attempt++)
            {
                ulong nonce = NewNonce();
                var hello = new byte[Wire.HandshakeSize];
                Wire.PutHeader(hello, Wire.Hello, 0, 0);
                Wire.PutU64(hello, 12, nonce);
                var challenge = await ExchangeAsync(hello, Wire.Challenge, nonce).ConfigureAwait(false);
                if (m_disposed) return false;
                if (challenge == null || challenge.Length < 40) continue;

                var connect = new byte[Wire.HandshakeSize];
                Wire.PutHeader(connect, Wire.Connect, 0, 0);
                Buffer.BlockCopy(challenge, 12, connect, 12, 8 + 4 + Wire.CookieSize); // nonce, time, cookie
                Wire.PutU16(connect, 40, LocalPort(m_gameTarget));
                var answer = await ExchangeAsync(connect, Wire.Accept, nonce).ConfigureAwait(false);
                if (m_disposed) return false;
                if (answer == null) continue;
                if (answer[1] == Wire.Reject)
                {
                    Log?.Invoke($"tunnel: the gateway refused the session (reason {(answer.Length > 20 ? answer[20] : 0)})");
                    return false;
                }

                lock (m_sync)
                {
                    if (m_disposed) return false;
                    m_session = Wire.U32(answer, 4);
                    m_token = Wire.U32(answer, 8);
                    var header = new byte[Wire.HeaderSize];
                    Wire.PutHeader(header, Wire.Data, m_session, m_token);
                    var channel = new Channel(m_sync, header, Transmit, Clock.NowMs);
                    channel.Received = (id, data) =>
                    {
                        if (m_bridges.TryGetValue(id, out var b)) b.Deliver(data);
                    };
                    channel.Closed = (id, reset) =>
                    {
                        if (m_bridges.TryGetValue(id, out var b)) b.PeerClosed(reset);
                    };
                    channel.Opened = (id, port) => channel.Reset(id, Clock.NowMs); // only the launcher opens streams
                    m_channel = channel;
                    m_nextStream = 0;
                    m_up = true;
                    m_upSince = Clock.NowMs;
                    Sessions++;
                }

                Log?.Invoke($"tunnel: session {m_session:x8} with {m_gateway} (attempt {attempt + 1})");
                return true;
            }

            Log?.Invoke($"tunnel: no answer from {m_gateway}");
            return false;
        }

        /// <summary>Sends <paramref name="datagram" /> (again every 500 ms) until the answer comes or 1.5 s pass.</summary>
        private async Task<byte[]> ExchangeAsync(byte[] datagram, byte expect, ulong nonce)
        {
            var pending = new Pending
            {
                Expect = expect, Nonce = nonce, Reply = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            lock (m_sync) m_pending = pending;
            try
            {
                for (int waited = 0; waited < AttemptMs; waited += ResendMs)
                {
                    Transmit(datagram, datagram.Length);
                    var done = await Task.WhenAny(pending.Reply.Task, Task.Delay(ResendMs)).ConfigureAwait(false);
                    if (done == pending.Reply.Task) return pending.Reply.Task.Result;
                }

                return null;
            }
            finally
            {
                lock (m_sync)
                    if (m_pending == pending)
                        m_pending = null;
            }
        }

        private void NewSocket()
        {
            var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                udp.IOControl(SioUdpConnReset, new byte[4], null); // an ICMP "port unreachable" must not break Receive
            }
            catch (Exception)
            {
                // not Windows
            }

            udp.Connect(m_gateway);
            Socket old;
            lock (m_sync)
            {
                old = m_udp;
                m_udp = udp;
            }

            try { old?.Close(); }
            catch (Exception) { /* already closed */ }
        }

        private ulong NewNonce()
        {
            var b = new byte[8];
            m_random.GetBytes(b);
            return Wire.U64(b, 0);
        }

        private void Transmit(byte[] datagram, int length)
        {
            try
            {
                m_udp?.Send(datagram, 0, length, SocketFlags.None);
            }
            catch (Exception)
            {
                // the network went away: the session times out and comes back when it can
            }
        }

        // ------------------------------------------------------------------ loops

        private void ReceiveLoop()
        {
            var buffer = new byte[2048];
            while (!m_disposed)
            {
                var udp = m_udp;
                int n;
                try
                {
                    n = udp.Receive(buffer);
                }
                catch (Exception)
                {
                    if (m_disposed) return;
                    if (udp == m_udp) Thread.Sleep(10); // a replaced socket throws once; anything else, don't spin
                    continue;
                }

                byte type = Wire.TypeOf(buffer, n);
                switch (type)
                {
                    case Wire.Challenge:
                    case Wire.Accept:
                    case Wire.Reject:
                        lock (m_sync)
                        {
                            var p = m_pending;
                            if (p != null && n >= 20 && Wire.U64(buffer, 12) == p.Nonce &&
                                (type == p.Expect || type == Wire.Reject && p.Expect == Wire.Accept))
                                p.Reply.TrySetResult(buffer.Take(n).ToArray());
                        }

                        break;
                    case Wire.Data:
                        lock (m_sync)
                        {
                            if (m_up && Wire.U32(buffer, 4) == m_session && Wire.U32(buffer, 8) == m_token)
                                m_channel.OnDatagram(buffer, n, Clock.NowMs);
                        }

                        break;
                    case Wire.Close:
                        lock (m_sync)
                        {
                            if (m_up && Wire.U32(buffer, 4) == m_session && Wire.U32(buffer, 8) == m_token)
                                SessionLost("the gateway closed it");
                        }

                        break;
                }
            }
        }

        private void TimerLoop()
        {
            while (!m_disposed)
            {
                Thread.Sleep(5);
                long now = Clock.NowMs;
                lock (m_sync)
                {
                    if (!m_up) continue;
                    m_channel.Tick(now);
                    if (m_channel.IsDead(now))
                        SessionLost($"no answer for {Channel.DeadMs / 1000} s");
                }
            }
        }

        /// <summary>Inside the lock: the game's connections close (they belonged to the session) and a new one is sought.</summary>
        private void SessionLost(string why)
        {
            m_up = false;
            Log?.Invoke($"tunnel: session {m_session:x8} lost after {(Clock.NowMs - m_upSince) / 1000} s ({why}): {m_channel.Stats()}");
            m_channel.Shutdown();
            foreach (var b in m_bridges.Values.ToList()) b.Close(false);
            m_bridges.Clear();
            if (!m_reconnecting)
            {
                m_reconnecting = true;
                _ = ReconnectAsync();
            }
        }

        private async Task ReconnectAsync()
        {
            try
            {
                int[] waits = {1000, 2000, 4000, 8000, 15000};
                for (int i = 0; !m_disposed; i++)
                {
                    await Task.Delay(waits[Math.Min(i, waits.Length - 1)]).ConfigureAwait(false);
                    if (await EnsureSessionAsync().ConfigureAwait(false)) return;
                }
            }
            finally
            {
                lock (m_sync) m_reconnecting = false;
            }
        }

        private async Task AcceptLoopAsync(TcpListener listener, int target)
        {
            while (!m_disposed)
            {
                Socket socket;
                try
                {
                    socket = await listener.AcceptSocketAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    if (m_disposed) return;
                    await Task.Delay(100).ConfigureAwait(false);
                    continue;
                }

                _ = OpenAsync(socket, target);
            }
        }

        private async Task OpenAsync(Socket socket, int target)
        {
            bool up = false;
            try
            {
                socket.NoDelay = true;
                up = await EnsureSessionAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // the game closed it meanwhile
            }

            lock (m_sync)
            {
                if (!up || !m_up || m_disposed)
                {
                    Log?.Invoke($"tunnel: no session, the game's connection to port {target} is closed");
                    try { socket.Close(); }
                    catch (Exception) { /* already closed */ }
                    return;
                }

                ushort id = NextStream();
                var bridge = new Bridge(m_channel, m_sync, id);
                bridge.Ended = b =>
                {
                    if (m_bridges.TryGetValue(b.Id, out var current) && current == b) m_bridges.Remove(b.Id);
                };
                m_bridges[id] = bridge;
                m_channel.Open(id, (ushort) target, Clock.NowMs);
                bridge.Attach(socket);
            }
        }

        private ushort NextStream()
        {
            do
                m_nextStream++;
            while (m_nextStream == 0 || m_bridges.ContainsKey(m_nextStream));
            return m_nextStream;
        }
    }
}
