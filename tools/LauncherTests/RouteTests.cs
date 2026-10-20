// LauncherTests route --dir D [--gateway 127.0.0.1:25817 --login 29958 --game 25816] [--hold N]
//   2.4.0: GameRoute, the way the game reaches the servers. tunnel.json from a stand-in site (none, disabled, broken,
//   slow, down with and without a saved copy), the player's "direct", a gateway that never answers, a launcher closed
//   while the route opens, ini\origins.server as StartGame writes it, the probe reading the file again, the "route"
//   setting. With --gateway (the dev stack's): a real tunnel through a UDP relay the test can cut: the game's
//   connection through it, the keeper's login-screen hook writing the tunnel's address, then the account server's
//   own address while the tunnel is down, a restart that reopens the session at once, the background reconnect, and
//   the end of the session.
//   --hold N: opens the route to --gateway the way "Play" does, prints "ROUTE <address>" and keeps it N seconds (a
//   TestClient logs in through it, see run-6609-tests.ps1).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OriginsLauncher
{
    internal static partial class TestProgram
    {
        private const string FarHost = "203.0.113.7"; // documentation address: nothing ever connects to it

        private static async Task RouteCheck(Config updateConfig, Dictionary<string, string> o)
        {
            string dir = updateConfig.InstallDir;
            Directory.CreateDirectory(Path.Combine(dir, "ini"));
            IPEndPoint gateway = o.TryGetValue("gateway", out string g) ? ParseEndPoint(g) : null;
            int login = o.TryGetValue("login", out string lp) ? int.Parse(lp) : 29958;
            int game = o.TryGetValue("game", out string gp) ? int.Parse(gp) : 25816;
            string direct = $"{FarHost}:{login}";
            var manifest = Manifest.Fallback();
            manifest.AccountServers = new Dictionary<string, string> {["en"] = direct, ["pt"] = $"{FarHost}:{login + 1}"};

            using var site = new TunnelSite(5096);
            var config = Config.FromArgs(new[] {"--dir=" + dir, "--base-url=" + site.BaseUrl});
            var updater = new Updater(config);
            string serverFile = Path.Combine(dir, Updater.ServerFile);
            string savedTunnel = Path.Combine(config.StateDir, "tunnel.json");
            string before = File.Exists(serverFile) ? Server(serverFile) : null;
            var startProcess = Updater.StartProcess;
            var started = new List<ProcessStartInfo>();
            Updater.StartProcess = info =>
            {
                started.Add(info);
                return null; // the game is never run by the tests
            };

            try
            {
                if (o.TryGetValue("hold", out string hold))
                {
                    await Hold(updater, manifest, site, gateway, game, int.Parse(hold));
                    return;
                }

                await Directs(config, updater, manifest, site, direct, game, savedTunnel);
                SettingRoundTrip(config);
                if (gateway == null)
                    Console.WriteLine("  (no --gateway: the real tunnel's checks are skipped)");
                else
                    await Tunneled(config, updater, manifest, site, gateway, login, game, direct, started);
            }
            finally
            {
                Updater.StartProcess = startProcess;
                if (before != null) File.WriteAllText(serverFile, before);
                else if (File.Exists(serverFile)) File.Delete(serverFile);
                string token = LoginClient.TokenPath(config);
                if (File.Exists(token)) File.Delete(token);
            }

            string log = Path.Combine(config.StateDir, "launcher.log");
            if (File.Exists(log))
            {
                Console.WriteLine("  launcher.log (route/tunnel lines, last 16):");
                foreach (string line in File.ReadAllLines(log).Where(l => l.Contains("route") || l.Contains("tunnel")).Reverse().Take(16).Reverse())
                    Console.WriteLine("    " + line);
            }
        }

        // ------------------------------------------------------------------ every way to "direct"

        private static async Task Directs(Config config, Updater updater, Manifest manifest, TunnelSite site, string direct, int game, string savedTunnel)
        {
            async Task<(GameRoute route, long ms)> Open(string choice = null, Manifest m = null, CancellationToken ct = default)
            {
                var clock = Stopwatch.StartNew();
                var route = await GameRoute.OpenAsync(updater, m ?? manifest, "en", choice, ct);
                return (route, clock.ElapsedMilliseconds);
            }

            void IsDirect(GameRoute route, string what, string want = null)
            {
                want ??= direct;
                Check(!route.Tunneled && route.Address == want && route.Stats() == "direct", $"{what}: direct, address {route.Address ?? "(none)"}");
                route.Dispose();
            }

            // the player's choice: tunnel.json is not even read
            site.Serve(200, $"{{\"enabled\":true,\"host\":\"127.0.0.1\",\"port\":9,\"gamePort\":{game}}}");
            int asked = site.Requests;
            var (r, ms) = await Open(GameRoute.Direct);
            IsDirect(r, $"the player chose direct ({ms} ms)");
            Check(site.Requests == asked, "and the site is not asked");

            // no account server in the manifest: nothing to route (StartGame leaves the file as installed)
            (r, _) = await Open(m: Manifest.Fallback());
            Check(!r.Tunneled && r.Address == null && site.Requests == asked, "a manifest without accountServers: no address, the site not asked");
            r.Dispose();
            var broken = Manifest.Fallback();
            broken.AccountServers = new Dictionary<string, string> {["en"] = "not an address"};
            (r, _) = await Open(m: broken);
            Check(!r.Tunneled && r.Address == null, "a bad account server: no route (StartGame reports the manifest)");
            r.Dispose();

            // nothing published: 404, and the saved copy goes away
            Directory.CreateDirectory(config.StateDir);
            File.WriteAllText(savedTunnel, "{\"enabled\":true,\"host\":\"127.0.0.1\",\"port\":9,\"gamePort\":5816}");
            site.Serve(404, "");
            (r, ms) = await Open();
            IsDirect(r, $"no tunnel.json (404, {ms} ms)");
            Check(!File.Exists(savedTunnel), "the saved tunnel.json is deleted with the 404");

            // published but off: saved as read
            string off = $"{{\"enabled\":false,\"host\":\"127.0.0.1\",\"port\":9,\"gamePort\":{game}}}";
            site.Serve(200, off);
            (r, ms) = await Open();
            IsDirect(r, $"\"enabled\": false ({ms} ms)");
            Check(File.Exists(savedTunnel) && File.ReadAllText(savedTunnel) == off, "tunnel.json saved as read");

            // nonsense in it
            foreach (var (body, what) in new[]
                     {
                         ($"{{\"enabled\":true,\"host\":\"\",\"port\":5817,\"gamePort\":{game}}}", "no host"),
                         ($"{{\"enabled\":true,\"host\":\"127.0.0.1\",\"port\":0,\"gamePort\":{game}}}", "port 0"),
                         ("{\"enabled\":true,\"host\":\"127.0.0.1\",\"port\":5817,\"gamePort\":70000}", "game port 70000"),
                         ("{\"enabled\":true,\"host\":\"no-such-host.invalid\",\"port\":5817,\"gamePort\":5816}", "a host that does not resolve")
                     })
            {
                site.Serve(200, body);
                (r, ms) = await Open();
                IsDirect(r, $"{what} ({ms} ms)");
            }

            File.WriteAllText(savedTunnel, off);
            site.Serve(200, "<html>not json</html>");
            (r, ms) = await Open();
            IsDirect(r, $"a body that is not JSON: the saved copy (off) is used ({ms} ms)");
            Check(File.ReadAllText(savedTunnel) == off, "and stays as it was");

            // a gateway that never answers: three handshake attempts, then direct
            using (var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                int port = ((IPEndPoint) silent.Client.LocalEndPoint).Port;
                string on = $"{{\"enabled\":true,\"host\":\"127.0.0.1\",\"port\":{port},\"gamePort\":{game}}}";
                site.Serve(200, on);
                (r, ms) = await Open();
                IsDirect(r, $"a gateway that never answers: direct after {ms} ms");
                Check(ms >= 4000 && ms < 7000, $"after the three handshake attempts (~4.5 s): {ms} ms");
                Check(silent.Available > 0, $"the handshake did reach it ({silent.Available} bytes waiting)");

                // the site down: the saved copy (on, the silent gateway) is still tried
                site.Serve(500, "");
                (r, ms) = await Open();
                IsDirect(r, $"site 500 with a saved copy: its gateway tried, direct after {ms} ms");
                Check(ms >= 4000, "the saved gateway was tried (the handshake's time)");
                File.Delete(savedTunnel);
                (r, ms) = await Open();
                IsDirect(r, $"site 500, nothing saved ({ms} ms)");
                Check(ms < 2000, "at once");
            }

            // a site that never answers: 5 s at most
            site.Hang();
            (r, ms) = await Open();
            IsDirect(r, $"a site that never answers: direct after {ms} ms");
            Check(ms >= 4500 && ms < 8000, $"the 5 s cap on tunnel.json ({ms} ms)");

            // the launcher closes while the route opens: no exception, nothing left open
            using (var cts = new CancellationTokenSource(300))
            {
                (r, ms) = await Open(ct: cts.Token);
                IsDirect(r, $"closed while opening ({ms} ms)");
                Check(ms < 2000, "returns at once");
            }

            // what "Play" writes for a direct route
            site.Serve(404, "");
            (r, _) = await Open();
            updater.StartGame(manifest, "en", null, r.Address);
            Check(Server(Path.Combine(config.InstallDir, Updater.ServerFile)) == direct, $"StartGame with the direct route: ini\\origins.server = {direct}");
            r.Dispose();
        }

        private static void SettingRoundTrip(Config config)
        {
            string path = Path.Combine(config.StateDir, "settings.json");
            string saved = File.Exists(path) ? File.ReadAllText(path) : null;
            try
            {
                var settings = Settings.Load(config);
                settings.Route = null;
                settings.Save(config);
                Check(!File.ReadAllText(path).Contains("route") && Settings.Load(config).Route == null, "the default (tunnel) leaves no \"route\" key");
                settings.Route = GameRoute.Direct;
                settings.Save(config);
                Check(File.ReadAllText(path).Contains("\"route\":\"direct\"") && Settings.Load(config).Route == GameRoute.Direct,
                    $"\"direct\" is saved and read back ({File.ReadAllText(path)})");
            }
            finally
            {
                if (saved != null) File.WriteAllText(path, saved);
                else File.Delete(path);
            }
        }

        // ------------------------------------------------------------------ the real tunnel

        private static async Task Tunneled(Config config, Updater updater, Manifest manifest, TunnelSite site, IPEndPoint gateway, int login, int game,
            string direct, List<ProcessStartInfo> started)
        {
            using var relay = new UdpRelay(gateway);
            site.Serve(200, $"{{\"enabled\":true,\"host\":\"127.0.0.1\",\"port\":{relay.Port},\"gamePort\":{game}}}");
            var clock = Stopwatch.StartNew();
            var route = await GameRoute.OpenAsync(updater, manifest, "en", null, CancellationToken.None);
            long openMs = clock.ElapsedMilliseconds;
            string tunnel = route.Address;
            Check(route.Tunneled && tunnel != null && tunnel.StartsWith("127.0.0.1:") && tunnel != "127.0.0.1:0",
                $"the tunnel through {gateway} (relay :{relay.Port}): address {tunnel} in {openMs} ms; {route.Stats()}");
            if (!route.Tunneled)
            {
                route.Dispose();
                return;
            }

            int local = int.Parse(tunnel.Substring(tunnel.IndexOf(':') + 1));
            started.Clear();
            updater.StartGame(manifest, "en", null, route.Address);
            string serverFile = Path.Combine(config.InstallDir, Updater.ServerFile);
            Check(Server(serverFile) == tunnel && started.Count == 1, $"StartGame: ini\\origins.server = {Server(serverFile)}, the game started once");

            // the game's login connection through the tunnel
            var connection = new TcpClient {NoDelay = true};
            connection.Connect(IPAddress.Loopback, local);
            var stream = connection.GetStream();
            var first = new byte[256];
            var read = stream.ReadAsync(first, 0, first.Length);
            bool answered = await Task.WhenAny(read, Task.Delay(3000)) == read;
            int got = answered ? read.Result : -1;
            Check(got != 0, answered ? $"the account server answers through the tunnel ({got} bytes)" : "the connection through the tunnel stays open (3 s, nothing sent by the server)");

            // the probe: the game's connection to the tunnel's port counts, by the file (127.0.0.1) or by the port itself
            var me = Process.GetCurrentProcess();
            var probe = new GameProbe(me, config);
            Check(probe.Connected() == true, "probe: the connection to the tunnel's local port counts (ini\\origins.server is 127.0.0.1)");
            Put(serverFile, direct);
            Check(new GameProbe(me, config).Connected() == false, "probe, file direct, no tunnel ports: loopback connections don't count");
            Check(new GameProbe(me, config) {IsTunnelPort = route.IsLocalPort}.Connected() == true,
                "probe with the tunnel's ports: the connection counts whatever the file says");
            Check(route.IsLocalPort(local) && !route.IsLocalPort(0) && !route.IsLocalPort(login), $"IsLocalPort: {local} yes, 0 and {login} no");

            // the keeper's hook at the login screen (what MainForm sets when the route is tunneled)
            // (the file says direct: as if the tunnel had been down at the last look)
            var fakeSite = new FakeSite();
            var keeper = new ReloginKeeper(config, fakeSite.Mint, Fast());
            keeper.AtLoginScreen = () => route.WriteAddress(updater);
            keeper.IsTunnelPort = route.IsLocalPort;
            keeper.Start(NewToken(TimeSpan.FromSeconds(60)));
            var atLogin = new FakeGame();
            using (new Watching(keeper, atLogin))
            {
                Check(WaitFor(() => Server(serverFile) == tunnel, 2000), $"at the game's login screen the hook writes the tunnel ({Server(serverFile)})");
                atLogin.IsConnected = true;
                Thread.Sleep(600);
                Put(serverFile, direct);
                Thread.Sleep(300);
                Check(Server(serverFile) == direct, "while the game is logged in the hook leaves the file alone");
                atLogin.IsConnected = false;
                Check(WaitFor(() => Server(serverFile) == tunnel, 2000), "back at the login screen: the tunnel again");

                // the tunnel goes down (the relay drops everything): the game's connection closes, the account server's
                // own address goes in the file
                relay.Cut = true;
                clock.Restart();
                Check(WaitFor(() => route.Address == direct, 15000), $"cut: the route says direct after {clock.ElapsedMilliseconds} ms");
                long downMs = clock.ElapsedMilliseconds;
                Check(downMs >= 9000 && downMs < 13000, $"~10 s without an answer ({downMs} ms)");
                bool closed = await ClosedWithin(stream, read, answered, 2000);
                Check(closed, "the game's connection through the dead tunnel is closed (the game goes back to its login screen)");
                Check(WaitFor(() => Server(serverFile) == direct, 2000), $"the hook writes the account server itself ({Server(serverFile)})");

                // the probe reads the file again: loopback connections stop counting
                Thread.Sleep(2100);
                Check(probe.Connected() == false, "probe: with the direct address in the file, loopback connections no longer count");

                // a restart while it is down: PrepareAsync opens a session at once when the gateway is back
                relay.Cut = false;
                clock.Restart();
                await route.PrepareAsync();
                Check(route.Address == tunnel, $"restart: PrepareAsync brings the tunnel back in {clock.ElapsedMilliseconds} ms, same local port ({route.Address})");
                Check(WaitFor(() => Server(serverFile) == tunnel, 2000), "and the hook writes it");
                Console.WriteLine($"  {route.Stats()}");

                // down again; this time the background reconnect brings it back
                relay.Cut = true;
                Check(WaitFor(() => route.Address == direct, 15000), "cut again: direct");
                Check(WaitFor(() => Server(serverFile) == direct, 2000), "the file says direct");
                Thread.Sleep(2000);
                relay.Cut = false;
                clock.Restart();
                Check(WaitFor(() => route.Address == tunnel, 20000), $"restored: the background reconnect brings the tunnel back in {clock.ElapsedMilliseconds} ms");
                Check(WaitFor(() => Server(serverFile) == tunnel, 2000), "and the next login goes through it");

                // a new connection works through the new session
                using (var again = new TcpClient {NoDelay = true})
                {
                    again.Connect(IPAddress.Loopback, local);
                    var s2 = again.GetStream();
                    var r2 = s2.ReadAsync(new byte[256], 0, 256);
                    bool a2 = await Task.WhenAny(r2, Task.Delay(3000)) == r2;
                    Check(!a2 || r2.Result > 0, a2 ? $"a new login connection is answered ({r2.Result} bytes)" : "a new login connection stays open");
                }

                // the end of the play session
                route.Dispose();
                Check(route.Address == direct, $"after Dispose the route says direct ({route.Address})");
                Check(WaitFor(() => Server(serverFile) == direct, 2000), "and the hook writes it");
                bool refused;
                try
                {
                    using var late = new TcpClient();
                    late.Connect(IPAddress.Loopback, local);
                    refused = false;
                }
                catch (SocketException)
                {
                    refused = true;
                }

                Check(refused, "the tunnel's local port is closed");
            }

            connection.Close();
            Console.WriteLine($"  relay: {relay.Forwarded} datagrams forwarded, {relay.Dropped} dropped while cut");
        }

        /// <summary>ini\origins.server as it is now (null while it is being written).</summary>
        private static string Server(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return new StreamReader(fs, Encoding.ASCII).ReadToEnd();
            }
            catch (IOException)
            {
                return null;
            }
        }

        /// <summary>Writes ini\origins.server, waiting while the keeper's hook writes it.</summary>
        private static void Put(string path, string text)
        {
            for (int i = 0;; i++)
            {
                try
                {
                    File.WriteAllText(path, text);
                    return;
                }
                catch (IOException) when (i < 50)
                {
                    Thread.Sleep(10);
                }
            }
        }

        /// <summary>True when the server end of <paramref name="stream" /> closes within <paramref name="ms" />.</summary>
        private static async Task<bool> ClosedWithin(NetworkStream stream, Task<int> pending, bool consumed, int ms)
        {
            var clock = Stopwatch.StartNew();
            var buffer = new byte[4096];
            var read = consumed ? null : pending;
            try
            {
                while (clock.ElapsedMilliseconds < ms)
                {
                    read ??= stream.ReadAsync(buffer, 0, buffer.Length);
                    if (await Task.WhenAny(read, Task.Delay(ms)) != read) return false;
                    if (read.Result == 0) return true;
                    read = null; // data that was on its way; keep reading
                }

                return false;
            }
            catch (Exception)
            {
                return true; // reset
            }
        }

        // ------------------------------------------------------------------ hold (a TestClient logs in through it)

        private static async Task Hold(Updater updater, Manifest manifest, TunnelSite site, IPEndPoint gateway, int game, int seconds)
        {
            if (gateway == null) throw new ArgumentException("--hold needs --gateway");
            site.Serve(200, $"{{\"enabled\":true,\"host\":\"{gateway.Address}\",\"port\":{gateway.Port},\"gamePort\":{game}}}");
            using var route = await GameRoute.OpenAsync(updater, manifest, "en", null, CancellationToken.None);
            Check(route.Tunneled, $"the route opened through {gateway}");
            Console.WriteLine($"ROUTE {route.Address}");
            Console.Out.Flush();
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds)
                await Task.Delay(500);
            Console.WriteLine($"  after {seconds} s: address {route.Address}, {route.Stats()}");
            Check(route.Address != null && route.Address.StartsWith("127.0.0.1:"), "still tunneled at the end");
        }

        // ------------------------------------------------------------------ stand-ins

        private static IPEndPoint ParseEndPoint(string text)
        {
            int colon = text.LastIndexOf(':');
            return new IPEndPoint(IPAddress.Parse(text.Substring(0, colon)), int.Parse(text.Substring(colon + 1)));
        }

        /// <summary>The site's /updates/tunnel.json: an answer per test, or none at all.</summary>
        private sealed class TunnelSite : IDisposable
        {
            private readonly HttpListener m_listener = new HttpListener();
            private readonly CancellationTokenSource m_stop = new CancellationTokenSource();
            private readonly Task m_serve;
            private volatile int m_status = 404;
            private volatile string m_body = "";
            private volatile bool m_hang;
            private int m_requests;

            public TunnelSite(int port)
            {
                BaseUrl = $"http://127.0.0.1:{port}/updates/";
                m_listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                m_listener.Start();
                m_serve = Task.Run(ServeAsync);
            }

            public string BaseUrl { get; }
            public int Requests => Volatile.Read(ref m_requests);

            public void Serve(int status, string body)
            {
                m_hang = false;
                m_status = status;
                m_body = body;
            }

            public void Hang() => m_hang = true;

            private async Task ServeAsync()
            {
                while (m_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await m_listener.GetContextAsync(); }
                    catch (Exception) { return; }
                    Interlocked.Increment(ref m_requests);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (m_hang)
                            {
                                await Task.Delay(-1, m_stop.Token);
                                return;
                            }

                            bool known = ctx.Request.Url.AbsolutePath == "/updates/tunnel.json";
                            ctx.Response.StatusCode = known ? m_status : 404;
                            byte[] bytes = Encoding.UTF8.GetBytes(known ? m_body : "");
                            if (bytes.Length > 0) ctx.Response.ContentType = "application/json";
                            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                            ctx.Response.Close();
                        }
                        catch (Exception)
                        {
                            // the test moved on
                        }
                    });
                }
            }

            public void Dispose()
            {
                m_stop.Cancel();
                m_listener.Stop();
                m_serve.Wait(1000);
            }
        }

        /// <summary>
        ///     Between the launcher and the gateway: forwards datagrams both ways (one upstream socket per launcher socket,
        ///     like a home router) until <see cref="Cut" />, then drops them all.
        /// </summary>
        private sealed class UdpRelay : IDisposable
        {
            private const int SioUdpConnReset = -1744830452;
            private readonly IPEndPoint m_gateway;
            private readonly UdpClient m_front;
            private readonly Dictionary<IPEndPoint, UdpClient> m_upstream = new Dictionary<IPEndPoint, UdpClient>();
            private volatile bool m_disposed;
            private int m_forwarded, m_dropped;
            public volatile bool Cut;

            public UdpRelay(IPEndPoint gateway)
            {
                m_gateway = gateway;
                m_front = Open();
                _ = Task.Run(FrontLoop);
            }

            public int Port => ((IPEndPoint) m_front.Client.LocalEndPoint).Port;
            public int Forwarded => Volatile.Read(ref m_forwarded);
            public int Dropped => Volatile.Read(ref m_dropped);

            private static UdpClient Open()
            {
                var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                udp.Client.IOControl(SioUdpConnReset, new byte[4], null); // a closed launcher socket must not break Receive
                return udp;
            }

            private async Task FrontLoop()
            {
                while (!m_disposed)
                {
                    UdpReceiveResult r;
                    try { r = await m_front.ReceiveAsync(); }
                    catch (Exception)
                    {
                        if (m_disposed) return;
                        continue;
                    }

                    if (Cut)
                    {
                        Interlocked.Increment(ref m_dropped);
                        continue;
                    }

                    UdpClient up;
                    lock (m_upstream)
                    {
                        if (!m_upstream.TryGetValue(r.RemoteEndPoint, out up))
                        {
                            up = Open();
                            up.Connect(m_gateway);
                            m_upstream[r.RemoteEndPoint] = up;
                            var client = r.RemoteEndPoint;
                            _ = Task.Run(() => BackLoop(up, client));
                        }
                    }

                    try
                    {
                        await up.SendAsync(r.Buffer, r.Buffer.Length);
                        Interlocked.Increment(ref m_forwarded);
                    }
                    catch (Exception)
                    {
                        // the gateway's port closed for a moment
                    }
                }
            }

            private async Task BackLoop(UdpClient up, IPEndPoint client)
            {
                while (!m_disposed)
                {
                    UdpReceiveResult r;
                    try { r = await up.ReceiveAsync(); }
                    catch (Exception)
                    {
                        if (m_disposed) return;
                        continue;
                    }

                    if (Cut)
                    {
                        Interlocked.Increment(ref m_dropped);
                        continue;
                    }

                    try
                    {
                        await m_front.SendAsync(r.Buffer, r.Buffer.Length, client);
                        Interlocked.Increment(ref m_forwarded);
                    }
                    catch (Exception)
                    {
                        // the launcher's socket was replaced
                    }
                }
            }

            public void Dispose()
            {
                m_disposed = true;
                m_front.Close();
                lock (m_upstream)
                    foreach (var up in m_upstream.Values)
                        up.Close();
            }
        }
    }
}
