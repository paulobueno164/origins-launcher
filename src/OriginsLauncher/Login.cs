using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
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

        /// <summary>The file the patched client reads the token from (ASCII, no newline) and deletes.</summary>
        public const string TokenFile = @"ini\origins.login";

        /// <summary>How long a token lives when the site does not say (the site's INTERVAL 90 SECOND).</summary>
        public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(90);

        /// <summary>
        ///     Asks the site for a login token. Returns the token on success; throws <see cref="LoginException" />
        ///     with a message to show the player on a wrong account/password or a connection problem.
        /// </summary>
        public static async Task<string> RequestTokenAsync(Config config, string username, string password) =>
            (await IssueAsync(config, username, password).ConfigureAwait(false)).Value;

        /// <summary>
        ///     <see cref="RequestTokenAsync" /> with the token's lifetime. <paramref name="relogin" /> (since 2.3.0) marks
        ///     a token the launcher keeps ready for a login from the game's own login screen (after a disconnect): a
        ///     site that knows the flag gives it a longer life and says so in "expiresIn"; an older site ignores both.
        ///     <see cref="LoginException.Retryable" /> tells a passing failure (connection, server error, too many
        ///     requests) from a refusal of the account (wrong password, banned, blocked).
        /// </summary>
        public static async Task<IssuedToken> IssueAsync(Config config, string username, string password, bool relogin = false,
            CancellationToken ct = default)
        {
            string url = config.SiteUrl + "api/account/launcher-token";
            using var content = new ByteArrayContent(Json.Serialize(new TokenRequest {Username = username, Password = password, Relogin = relogin}));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            long asked = Stopwatch.GetTimestamp(); // the lifetime counts from the request, never from the answer

            HttpResponseMessage res;
            try
            {
                // ConfigureAwait(false): a caller that blocks on this from the window's thread must not deadlock
                res = await Http.PostAsync(url, content, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Write($"login request failed: {ex.GetType().Name}: {ex.Message}");
                throw new LoginException(L.S.LoginConnError, ex, retryable: true);
            }

            byte[] bytes;
            try { bytes = await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false); }
            catch { bytes = Array.Empty<byte>(); }

            TokenResponse parsed = null;
            try { if (bytes.Length > 0) parsed = Json.Parse<TokenResponse>(bytes); }
            catch { /* the body was not the JSON we expect */ }

            int status = (int) res.StatusCode;
            if (res.IsSuccessStatusCode && parsed?.ok == true && !string.IsNullOrEmpty(parsed.token))
            {
                var lifetime = parsed.expiresIn > 0
                    ? TimeSpan.FromSeconds(Math.Min(Math.Max(parsed.expiresIn, 5), 3600))
                    : DefaultLifetime;
                return new IssuedToken(parsed.token, lifetime, asked);
            }

            // 429: the site's per-IP limit (no body: until 2.2.0 this read "wrong password") or its per-account
            // lock after failed logins (with a message); both pass.
            if (status == 429)
                throw new LoginException(string.IsNullOrEmpty(parsed?.message) ? L.S.LoginBusy : parsed.message, retryable: true);
            if (status >= 500)
                throw new LoginException(L.S.LoginConnError, retryable: true);
            // The site answers a bad login with { ok:false, message } and a 4xx; show its message when there is one.
            if (!string.IsNullOrEmpty(parsed?.message))
                throw new LoginException(parsed.message);
            throw new LoginException(L.S.LoginInvalid);
        }

        public static string TokenPath(Config config) => Path.Combine(config.InstallDir, TokenFile);

        /// <summary>
        ///     Writes the token the patched client reads at login: ini\origins.login, the 32 token characters as
        ///     ASCII, no newline. The client copies it into the login packet and deletes the file (single-use).
        ///     Since 2.3.0 the file is written next to it and renamed into place, so the client never reads half a
        ///     token; while the client has it open (CreateFileA with FILE_SHARE_READ only) the rename waits.
        /// </summary>
        public static void WriteTokenFile(Config config, string token)
        {
            string path = TokenPath(config);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Process.GetCurrentProcess().Id + ".tmp";
            File.WriteAllBytes(temp, Encoding.ASCII.GetBytes(token));
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(path, FileAttributes.Normal);
            }
            catch (Exception)
            {
                // no file yet
            }

            try
            {
                for (int attempt = 0;; attempt++)
                {
                    // one rename over the old file (same folder, same volume): the client sees the old token or the new one
                    if (MoveFileEx(temp, path, MoveFileReplaceExisting))
                        return;
                    int error = Marshal.GetLastWin32Error();
                    if (attempt >= 40)
                        throw new IOException($"{TokenFile} not written (error {error})");
                    Thread.Sleep(25); // the client is reading the old token (a few milliseconds)
                }
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception) { /* left for the next write */ }
            }
        }

        /// <summary><see cref="ReadTokenFile" /> while the file is being replaced or deleted: not known this time.</summary>
        public const string Busy = "";

        /// <summary>
        ///     The token in ini\origins.login, null when there is none, <see cref="Busy" /> when it cannot be told now.
        ///     Opened with every sharing flag, so the client's own open (FILE_SHARE_READ) and its DeleteFileA never fail
        ///     because the launcher is looking.
        /// </summary>
        public static string ReadTokenFile(Config config)
        {
            try
            {
                using var fs = new FileStream(TokenPath(config), FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 64, FileOptions.None);
                var buffer = new byte[64];
                int read = 0, n;
                while (read < buffer.Length && (n = fs.Read(buffer, read, buffer.Length - read)) > 0)
                    read += n;
                return Encoding.ASCII.GetString(buffer, 0, read);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (IOException) { return Busy; }                 // being replaced: look again later
            catch (UnauthorizedAccessException) { return Busy; } // being deleted: look again later
        }

        /// <summary>Deletes ini\origins.login if it still holds <paramref name="token" />; true if it did.</summary>
        public static bool DeleteTokenFileIf(Config config, string token)
        {
            if (string.IsNullOrEmpty(token) || ReadTokenFile(config) != token)
                return false; // (a rename in between can only remove another launcher's token: it takes it as read)
            try
            {
                File.Delete(TokenPath(config));
                return true;
            }
            catch (Exception ex)
            {
                Log.Write($"login token not removed: {ex.Message}");
                return false;
            }
        }

        private const int MoveFileReplaceExisting = 1;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);

        private static HttpClient Create()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new HttpClient {Timeout = TimeSpan.FromSeconds(30)};
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"OriginsLauncher/{Updater.CurrentVersion}");
            return client;
        }
    }

    /// <summary>A login token and how long the site keeps it (counted from the moment it was asked for).</summary>
    internal sealed class IssuedToken
    {
        public IssuedToken(string value, TimeSpan lifetime, long askedAt)
        {
            Value = value;
            Lifetime = lifetime;
            AskedAt = askedAt;
        }

        public string Value { get; }
        public TimeSpan Lifetime { get; }

        /// <summary><see cref="Stopwatch.GetTimestamp" /> when the token was asked for.</summary>
        public long AskedAt { get; }

        public TimeSpan Remaining(long now) => Lifetime - TimeSpan.FromSeconds((now - AskedAt) / (double) Stopwatch.Frequency);
    }

    internal sealed class LoginException : Exception
    {
        public LoginException(string message, Exception inner = null, bool retryable = false) : base(message, inner)
        {
            Retryable = retryable;
        }

        /// <summary>A passing failure (connection, server error, too many requests), not a refusal of the account.</summary>
        public bool Retryable { get; }
    }

    [DataContract]
    internal sealed class TokenRequest
    {
        [DataMember(Name = "Username")] public string Username { get; set; }
        [DataMember(Name = "Password")] public string Password { get; set; }

        /// <summary>Since 2.3.0: a token kept ready for a login after a disconnect (sent only when true).</summary>
        [DataMember(Name = "Relogin", EmitDefaultValue = false)] public bool Relogin { get; set; }
    }

    [DataContract]
    internal sealed class TokenResponse
    {
        [DataMember(Name = "ok")] public bool ok { get; set; }
        [DataMember(Name = "token")] public string token { get; set; }
        [DataMember(Name = "message")] public string message { get; set; }

        /// <summary>Seconds the token lives, when the site says (0: <see cref="LoginClient.DefaultLifetime" />).</summary>
        [DataMember(Name = "expiresIn", IsRequired = false)] public int expiresIn { get; set; }
    }
}
