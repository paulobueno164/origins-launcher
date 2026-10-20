using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Origins.Tunnel;

namespace OriginsLauncher
{
    /// <summary>/updates/tunnel.json: the UDP tunnel's gateway; "enabled": false (or no file) sends every launcher direct.</summary>
    [DataContract]
    internal sealed class TunnelInfo
    {
        [DataMember(Name = "enabled")] public bool Enabled { get; set; }
        [DataMember(Name = "host")] public string Host { get; set; }
        [DataMember(Name = "port")] public int Port { get; set; }

        /// <summary>The game server's port (the account server's port comes from the manifest, per language).</summary>
        [DataMember(Name = "gamePort")] public int GamePort { get; set; }
    }

    /// <summary>
    ///     Since 2.4.0: how the game reaches the servers. By default through the UDP tunnel (Tunnel\TunnelClient): the
    ///     game connects to 127.0.0.1 and the launcher carries its TCP connections over UDP to the gateway next to the
    ///     servers, where a lost packet costs a copy sent 20 ms later instead of a TCP retransmission (a few hundred ms
    ///     on a long route with loss). ini\origins.server gets the tunnel's local login port while the tunnel is up and
    ///     the account server's own address while it is not: the patched client reads the file at every login connect
    ///     (patch 1, GetServer), so a tunnel that dies costs one disconnect, and the next login (the keeper writes the
    ///     address at the game's login screen, see <see cref="ReloginKeeper.AtLoginScreen" />) goes direct until the
    ///     tunnel is back.
    ///     <para>
    ///         Direct for the whole session when the player chose it (settings "route": "direct"), when tunnel.json says
    ///         so (or neither it nor its saved copy can be read), or when the gateway does not answer the handshake.
    ///     </para>
    /// </summary>
    internal sealed class GameRoute : IDisposable
    {
        public const string Direct = "direct";

        private readonly string m_direct;
        private readonly TunnelClient m_tunnel;
        private readonly int m_loginTarget, m_gameTarget;

        private GameRoute(string direct, TunnelClient tunnel = null, int loginTarget = 0, int gameTarget = 0)
        {
            m_direct = direct;
            m_tunnel = tunnel;
            m_loginTarget = loginTarget;
            m_gameTarget = gameTarget;
        }

        /// <summary>A tunnel was opened for this session (it may be down for a moment).</summary>
        public bool Tunneled => m_tunnel != null;

        /// <summary>What ini\origins.server should say now; null when the manifest has no account server (left as it is).</summary>
        public string Address => m_tunnel != null && m_tunnel.IsUp ? "127.0.0.1:" + m_tunnel.LocalPort(m_loginTarget) : m_direct;

        public string Stats() => m_tunnel?.Stats() ?? "direct";

        /// <summary>One of the tunnel's 127.0.0.1 ports (login or game): the game's connection to it is its server's.</summary>
        public bool IsLocalPort(int port) =>
            m_tunnel != null && port != 0 && (port == m_tunnel.LocalPort(m_loginTarget) || port == m_tunnel.LocalPort(m_gameTarget));

        /// <summary>The route for this play session. Never throws: anything wrong with the tunnel means direct.</summary>
        public static async Task<GameRoute> OpenAsync(Updater updater, Manifest manifest, string language, string choice, CancellationToken ct)
        {
            string direct;
            try
            {
                direct = Updater.AccountServer(manifest, language);
            }
            catch (Exception)
            {
                return new GameRoute(null); // StartGame reports the bad manifest
            }

            if (direct == null) return new GameRoute(null);
            if (choice == Direct)
            {
                Log.Write("route: direct (the player's choice)");
                return new GameRoute(direct);
            }

            TunnelClient tunnel = null;
            try
            {
                var info = await updater.FetchTunnelAsync(ct).ConfigureAwait(false);
                if (info == null || !info.Enabled || string.IsNullOrEmpty(info.Host) || info.Port <= 0 || info.Port > 65535 ||
                    info.GamePort <= 0 || info.GamePort > 65535)
                {
                    Log.Write("route: direct (no tunnel published)");
                    return new GameRoute(direct);
                }

                int loginPort = int.Parse(direct.Substring(direct.LastIndexOf(':') + 1));
                var gateway = new IPEndPoint(await ResolveAsync(info.Host).ConfigureAwait(false), info.Port);
                tunnel = new TunnelClient(gateway, new[] {loginPort, info.GamePort}, info.GamePort) {Log = Log.Write};
                if (!await tunnel.StartAsync().ConfigureAwait(false))
                {
                    tunnel.Dispose();
                    Log.Write($"route: direct (the tunnel's gateway {gateway} did not answer)");
                    return new GameRoute(direct);
                }

                Log.Write($"route: tunnel via {gateway}: login {loginPort} -> 127.0.0.1:{tunnel.LocalPort(loginPort)}, " +
                          $"game {info.GamePort} -> 127.0.0.1:{tunnel.LocalPort(info.GamePort)}, rtt {tunnel.RttMs} ms");
                return new GameRoute(direct, tunnel, loginPort, info.GamePort);
            }
            catch (Exception ex)
            {
                tunnel?.Dispose();
                Log.Write($"route: direct (tunnel failed: {ex.GetType().Name}: {ex.Message})");
                return new GameRoute(direct);
            }
        }

        /// <summary>
        ///     The keeper's hook at the game's login screen (<see cref="ReloginKeeper.AtLoginScreen" />): ini\origins.server
        ///     = the tunnel while it is up, the account server itself while it is not. True if the file changed.
        /// </summary>
        public bool WriteAddress(Updater updater)
        {
            string address = Address;
            try
            {
                if (address == null || !updater.WriteServerAddress(address)) return false;
                Log.Write($"route: the game's next login goes to {address}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write($"route: ini\\origins.server not written: {ex.Message}");
                return false;
            }
        }

        /// <summary>Before the game starts again: a tunnel whose session died gets a new one now if it can (up to ~4.5 s).</summary>
        public async Task PrepareAsync()
        {
            if (m_tunnel == null || m_tunnel.IsUp) return;
            try
            {
                await m_tunnel.EnsureSessionAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // direct this time
            }
        }

        public void Dispose()
        {
            if (m_tunnel == null) return;
            Log.Write($"route: closing the tunnel: {m_tunnel.Stats()}");
            m_tunnel.Dispose();
        }

        private static async Task<IPAddress> ResolveAsync(string host)
        {
            if (IPAddress.TryParse(host, out var ip)) return ip;
            var all = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
            return all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ??
                   throw new SocketException((int) SocketError.HostNotFound);
        }
    }
}
