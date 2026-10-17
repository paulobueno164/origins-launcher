using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;

namespace OriginsLauncher
{
    /// <summary>
    ///     Login against the site: POST the account and password to /api/account/launcher-token over HTTPS and
    ///     get a single-use token the game client sends in place of the password. The 6609 client's own password
    ///     cipher cannot be read by the server, so the account and password are checked here, by the site, and the
    ///     client only carries the resulting token (docs\migracao-6609.md).
    /// </summary>
    internal static class LoginClient
    {
        private static readonly HttpClient Http = Create();

        /// <summary>
        ///     Asks the site for a login token. Returns the token on success; throws <see cref="LoginException" />
        ///     with a message to show the player on a wrong account/password or a connection problem.
        /// </summary>
        public static async Task<string> RequestTokenAsync(Config config, string username, string password)
        {
            string url = config.SiteUrl + "api/account/launcher-token";
            using var content = new ByteArrayContent(Json.Serialize(new TokenRequest {Username = username, Password = password}));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            HttpResponseMessage res;
            try
            {
                res = await Http.PostAsync(url, content);
            }
            catch (Exception ex)
            {
                Log.Write($"login request failed: {ex.GetType().Name}: {ex.Message}");
                throw new LoginException(L.S.LoginConnError, ex);
            }

            byte[] bytes;
            try { bytes = await res.Content.ReadAsByteArrayAsync(); }
            catch { bytes = Array.Empty<byte>(); }

            TokenResponse parsed = null;
            try { if (bytes.Length > 0) parsed = Json.Parse<TokenResponse>(bytes); }
            catch { /* the body was not the JSON we expect */ }

            if (res.IsSuccessStatusCode && parsed?.ok == true && !string.IsNullOrEmpty(parsed.token))
                return parsed.token;

            // The site answers a bad login with { ok:false, message } and a 4xx; show its message when there is one.
            if (!string.IsNullOrEmpty(parsed?.message))
                throw new LoginException(parsed.message);
            if ((int) res.StatusCode >= 500)
                throw new LoginException(L.S.LoginConnError);
            throw new LoginException(L.S.LoginInvalid);
        }

        /// <summary>
        ///     Writes the token the patched client reads at login: ini\origins.login, the 32 token characters as
        ///     ASCII, no newline. The client copies it into the login packet and deletes the file (single-use).
        /// </summary>
        public static void WriteTokenFile(Config config, string token)
        {
            string dir = Path.Combine(config.InstallDir, "ini");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "origins.login"), Encoding.ASCII.GetBytes(token));
        }

        private static HttpClient Create()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new HttpClient {Timeout = TimeSpan.FromSeconds(30)};
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"OriginsLauncher/{Updater.CurrentVersion}");
            return client;
        }
    }

    internal sealed class LoginException : Exception
    {
        public LoginException(string message, Exception inner = null) : base(message, inner)
        {
        }
    }

    [DataContract]
    internal sealed class TokenRequest
    {
        [DataMember(Name = "Username")] public string Username { get; set; }
        [DataMember(Name = "Password")] public string Password { get; set; }
    }

    [DataContract]
    internal sealed class TokenResponse
    {
        [DataMember(Name = "ok")] public bool ok { get; set; }
        [DataMember(Name = "token")] public string token { get; set; }
        [DataMember(Name = "message")] public string message { get; set; }
    }
}
