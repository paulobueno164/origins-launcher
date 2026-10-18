// Headless checks of the launcher's update and start code (the same source files as OriginsLauncher.exe, compiled in).
// Driven by run-6609-tests.ps1, which serves the update set with tools\DevUpdateServer (fault injection per phase).
//
//   LauncherTests update   --dir D --base-url U --lang pt [--full] [--cancel-at 0.3]
//                          UpdateGameAsync exactly as the launcher's window runs it (--cancel-at: cancel while the
//                          full-client pack is downloading, at that fraction, like closing the launcher)
//   LauncherTests verify   --dir D --lang pt --client C --lang-root R [--keep-changed ini\GameSetUp.ini]
//                          every installed file's SHA-256 = the client's (or the language pack's) one, nothing missing,
//                          nothing extra (besides .originsupdate and files the test made on purpose)
//   LauncherTests damage   --dir D       deletes, truncates and silently corrupts a few files, edits a keep file
//   LauncherTests settings --dir D       a 1.5.0 settings.json (keepAspect, closeOnExit, fillScreen) still loads
//   LauncherTests start    --dir D --base-url U   login token + ini\origins.server + what would be started, per language
//   LauncherTests migrate  --dir D --base-url U   a 5095 install (old local manifest) updated to the 6609 manifest
//   LauncherTests etag     --dir D --base-url U   an up-to-date folder fetches the manifest with If-None-Match (304)
// Exit code 0 = every check passed.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OriginsLauncher
{
    internal static class TestProgram
    {
        private static int s_failures;

        private static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 16;
            var o = Options(args.Skip(1));
            string dir = Path.GetFullPath(o["dir"]);
            var launcherArgs = new List<string> {"--dir=" + dir};
            if (o.TryGetValue("base-url", out string baseUrl)) launcherArgs.Add("--base-url=" + baseUrl);
            if (o.TryGetValue("site-url", out string siteUrl)) launcherArgs.Add("--site-url=" + siteUrl);
            var config = Config.FromArgs(launcherArgs.ToArray());
            Log.Init(config);
            string lang = o.TryGetValue("lang", out string l) ? l : "en";
            L.S = Strings.For(lang);
            var clock = Stopwatch.StartNew();
            try
            {
                switch (args[0])
                {
                    case "update": Update(config, lang, o.ContainsKey("full"), o.TryGetValue("cancel-at", out string c) ? double.Parse(c, System.Globalization.CultureInfo.InvariantCulture) : (double?) null,
                        o.TryGetValue("expect", out string e) ? int.Parse(e) : (int?) null).GetAwaiter().GetResult(); break;
                    case "verify": Verify(dir, lang, o["client"], o["lang-root"], o.TryGetValue("keep-changed", out string k) ? k : null); break;
                    case "damage": Damage(dir); break;
                    case "settings": SettingsCheck(config); break;
                    case "start": Start(config, o["base-url"]).GetAwaiter().GetResult(); break;
                    case "migrate": Migrate(config).GetAwaiter().GetResult(); break;
                    case "etag": Etag(config).GetAwaiter().GetResult(); break;
                    case "offline": Offline(config).GetAwaiter().GetResult(); break;
                    default: throw new ArgumentException("unknown command " + args[0]);
                }
            }
            catch (Exception ex)
            {
                Fail($"{args[0]} threw {ex.GetType().Name}: {ex.Message}");
            }

            Console.WriteLine($"{args[0]}: {(s_failures == 0 ? "PASS" : $"FAIL ({s_failures})")} in {clock.Elapsed.TotalSeconds:N0}s");
            return s_failures == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ update

        private static async Task Update(Config config, string lang, bool full, double? cancelAt, int? expect)
        {
            var updater = new Updater(config);
            var manifest = await updater.FetchManifestAsync(CancellationToken.None);
            Console.WriteLine($"manifest v{manifest.Version}: {manifest.Files.Count} files, exe {manifest.GameExe} {manifest.GameArgs} in {manifest.WorkingDir}, " +
                              $"pack {manifest.Pack?.Parts.Count} parts, languages {string.Join(",", (IEnumerable<string>) manifest.Languages?.Keys ?? new string[0])}");
            using var cts = new CancellationTokenSource();
            string lastStatus = null;
            var progress = new SyncProgress(p =>
            {
                string kind = System.Text.RegularExpressions.Regex.Replace(p.Status ?? "", @"\d+", "#");
                if (kind != lastStatus) Console.WriteLine($"  {p.Status}");
                lastStatus = kind;
                if (cancelAt != null && p.Status == L.S.DownloadingPack && p.Fraction >= cancelAt && !cts.IsCancellationRequested)
                {
                    Console.WriteLine($"  cancelling at {p.Fraction:P0} of the pack ({p.Detail})");
                    cts.Cancel();
                }
            });
            try
            {
                int updated = await updater.UpdateGameAsync(manifest, lang, full, progress, cts.Token);
                Console.WriteLine($"updated {updated} file(s), lang={lang} full={full}");
                if (cancelAt != null) Fail("expected a cancellation");
                if (expect != null) Check(updated == expect, $"{updated} file(s) found outdated (expected {expect})");
                var again = await updater.FindOutdatedAsync(manifest.FilesFor(lang), false, progress, CancellationToken.None);
                Check(again.Count == 0, $"nothing outdated afterwards ({again.Count})");
            }
            catch (OperationCanceledException) when (cancelAt != null)
            {
                string packDir = Path.Combine(config.StateDir, "pack");
                var left = Directory.Exists(packDir) ? Directory.GetFiles(packDir) : new string[0];
                long bytes = left.Sum(f => new FileInfo(f).Length);
                Console.WriteLine($"cancelled; {left.Length} pack file(s) kept for the resume ({bytes / 1048576.0:N0} MB: " +
                                  $"{left.Count(f => f.EndsWith(".bin"))} complete parts, {left.Count(f => f.EndsWith(".part"))} partial)");
                Check(left.Length > 0 && bytes > 0, "the partial pack stays on disk for the next start");
            }
        }

        // ------------------------------------------------------------------ verify

        private static void Verify(string dir, string lang, string client, string langRoot, string keepChanged)
        {
            var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // relative path -> source file
            foreach (string full in Directory.EnumerateFiles(client, "*", SearchOption.AllDirectories))
                expected[Rel(client, full)] = full;
            int fromPack = 0;
            if (lang != "en")
            {
                string packRoot = Path.Combine(langRoot, lang);
                foreach (string full in Directory.EnumerateFiles(packRoot, "*", SearchOption.AllDirectories))
                {
                    string rel = Rel(packRoot, full);
                    Check(expected.ContainsKey(rel), $"pack file {rel} replaces a client file");
                    expected[rel] = full;
                    fromPack++;
                }
            }

            var installed = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => Rel(dir, f))
                .Where(rel => !rel.StartsWith(".originsupdate\\", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var installedSet = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);
            var missing = expected.Keys.Where(rel => !installedSet.Contains(rel)).ToList();
            var extra = installed.Where(rel => !expected.ContainsKey(rel)).ToList();
            Check(missing.Count == 0, $"no file missing ({missing.Count}: {string.Join(", ", missing.Take(5))})");
            Check(extra.Count == 0, $"no extra file ({extra.Count}: {string.Join(", ", extra.Take(5))})");

            // the source side (client\6609 + packs) is hashed once and cached by size + time between verify runs
            string cachePath = Path.Combine(Path.GetTempPath(), "origins-6609-source-hashes.tsv");
            var cache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(cachePath))
                foreach (string line in File.ReadAllLines(cachePath))
                {
                    int tab = line.LastIndexOf('\t');
                    if (tab > 0) cache[line.Substring(0, tab)] = line.Substring(tab + 1);
                }

            string SourceHash(string path)
            {
                var info = new FileInfo(path);
                string key = $"{path}\t{info.Length}\t{info.LastWriteTimeUtc.Ticks}";
                return cache.GetOrAdd(key, _ => Hash(path));
            }

            var mismatch = new ConcurrentBag<string>();
            long bytes = 0;
            Parallel.ForEach(expected, new ParallelOptions {MaxDegreeOfParallelism = Environment.ProcessorCount}, pair =>
            {
                string target = Path.Combine(dir, pair.Key);
                if (!File.Exists(target)) return;
                Interlocked.Add(ref bytes, new FileInfo(target).Length);
                bool same = Hash(target) == SourceHash(pair.Value);
                bool shouldDiffer = keepChanged != null && pair.Key.Equals(keepChanged, StringComparison.OrdinalIgnoreCase);
                if (same == shouldDiffer) mismatch.Add(pair.Key);
            });
            File.WriteAllLines(cachePath, cache.Select(p => $"{p.Key}\t{p.Value}"));
            Check(mismatch.IsEmpty, $"every SHA-256 as expected ({mismatch.Count} wrong: {string.Join(", ", mismatch.Take(5))})");
            Console.WriteLine($"verified {expected.Count} files ({bytes / 1073741824.0:N2} GiB) in {dir}, {fromPack} from the {lang} pack" +
                              (keepChanged != null ? $", {keepChanged} kept as the player changed it" : ""));
        }

        // ------------------------------------------------------------------ damage (for the repair run)

        private static readonly string[] Deleted = {@"ini\StrRes.ini", @"data\main\flash\characterInAndOut.swf", @"Env_DX9\Conquer.exe"};
        private const string Truncated = @"Env_DX9\TQAnp.dll";
        private static readonly string[] Flipped = {@"ini\itemtype.dat", @"c3\texture\001000000.dds"}; // same size and time: only "Verify files" sees it
        public const string KeepFile = @"ini\GameSetUp.ini";

        private static void Damage(string dir)
        {
            foreach (string rel in Deleted) File.Delete(Path.Combine(dir, rel));
            using (var fs = new FileStream(Path.Combine(dir, Truncated), FileMode.Open)) fs.SetLength(fs.Length / 2);
            foreach (string rel in Flipped)
            {
                string path = Path.Combine(dir, rel);
                if (!File.Exists(path)) path = Directory.EnumerateFiles(Path.Combine(dir, Path.GetDirectoryName(rel))).First();
                var time = File.GetLastWriteTimeUtc(path);
                using (var fs = new FileStream(path, FileMode.Open))
                {
                    fs.Position = fs.Length / 3;
                    int b = fs.ReadByte();
                    fs.Position = fs.Length / 3;
                    fs.WriteByte((byte) (b ^ 0x5A));
                }

                File.SetLastWriteTimeUtc(path, time);
                Console.WriteLine($"flipped a byte in {Rel(dir, path)} (size and time unchanged)");
            }

            File.AppendAllText(Path.Combine(dir, KeepFile), "\r\n[OriginsTest]\r\nPlayerChanged=1\r\n");
            Console.WriteLine($"deleted {string.Join(", ", Deleted)}; truncated {Truncated}; edited the keep file {KeepFile}");
        }

        // ------------------------------------------------------------------ settings

        private static void SettingsCheck(Config config)
        {
            Directory.CreateDirectory(config.StateDir);
            string path = Path.Combine(config.StateDir, "settings.json");
            // byte for byte what 1.5.0 wrote (DataContractJsonSerializer: members in alphabetical order, no BOM)
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes("{\"closeOnExit\":true,\"keepAspect\":true,\"language\":\"fil\"}"));
            var settings = Settings.Load(config);
            Check(settings.Language == "fil", $"1.5.0 settings load, language kept ({settings.Language})");
            // 1.4.0's fillScreen, keys in another order, and a UTF-8 BOM (the file saved by Notepad)
            File.WriteAllText(path, "{\"fillScreen\":true,\"language\":\"pt\",\"keepAspect\":false}", new UTF8Encoding(true));
            Check(Settings.Load(config).Language == "pt", "1.4.0 keys, any order, with a BOM: language kept");
            settings.Language = "pt";
            settings.Save(config);
            string saved = File.ReadAllText(path);
            Check(saved == "{\"language\":\"pt\"}", $"saved without the removed keys: {saved}");
            File.WriteAllText(path, "{not json", Encoding.UTF8);
            Check(Settings.Load(config).Language == null, "an unreadable settings.json gives the defaults");
            File.Delete(path);
        }

        // ------------------------------------------------------------------ start

        private static async Task Start(Config updateConfig, string baseUrl)
        {
            // a stand-in for the site's POST /api/account/launcher-token
            const string token = "0123456789abcdef0123456789ABCDEF";
            var listener = new HttpListener();
            int port = 5098;
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
                    bool good = ctx.Request.HttpMethod == "POST" && ctx.Request.Url.AbsolutePath == "/api/account/launcher-token" &&
                                body.Contains("\"Username\":\"tester\"") && body.Contains("\"Password\":\"right\"");
                    ctx.Response.StatusCode = good ? 200 : 401;
                    byte[] answer = Encoding.UTF8.GetBytes(good
                        ? $"{{\"ok\":true,\"token\":\"{token}\"}}"
                        : "{\"ok\":false,\"message\":\"Wrong account or password (test).\"}");
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.OutputStream.Write(answer, 0, answer.Length);
                    ctx.Response.Close();
                }
            });

            try
            {
                var config = Config.FromArgs(new[] {"--dir=" + updateConfig.InstallDir, "--base-url=" + baseUrl, $"--site-url=http://127.0.0.1:{port}/"});
                var updater = new Updater(config);
                var manifest = await updater.FetchManifestAsync(CancellationToken.None);
                Check(manifest.GameExe == @"Env_DX9\Conquer.exe" && manifest.GameArgs == "blacknull" && manifest.WorkingDir == "Env_DX9",
                    $"manifest: {manifest.GameExe} {manifest.GameArgs} in {manifest.WorkingDir}");
                Console.WriteLine($"accountServers: {string.Join(", ", manifest.AccountServers.Select(p => $"{p.Key}={p.Value}"))}");

                // wrong password: the site's message reaches the dialog, nothing is written
                string loginFile = Path.Combine(config.InstallDir, "ini", "origins.login");
                if (File.Exists(loginFile)) File.Delete(loginFile);
                try
                {
                    await LoginClient.RequestTokenAsync(config, "tester", "wrong");
                    Fail("a wrong password must fail");
                }
                catch (LoginException ex)
                {
                    Check(ex.Message == "Wrong account or password (test).", $"wrong password -> \"{ex.Message}\"");
                }

                Check(!File.Exists(loginFile), "no token file after a failed login");

                var started = new List<ProcessStartInfo>();
                Updater.StartProcess = info =>
                {
                    started.Add(info);
                    return null; // the game is never run by the tests
                };

                string exe = Path.Combine(config.InstallDir, @"Env_DX9\Conquer.exe");
                string workDir = Path.Combine(config.InstallDir, "Env_DX9");
                var expectedPorts = new Dictionary<string, string> {["en"] = "9958", ["pt"] = "9959", ["fil"] = "9960"};
                string host = manifest.AccountServers["en"].Split(':')[0];
                foreach (var lang in new[] {"pt", "fil", "en"})
                {
                    // what MainForm.PlayGame does: login dialog -> token file, then StartGame(manifest, language)
                    string got = await LoginClient.RequestTokenAsync(config, "tester", "right");
                    LoginClient.WriteTokenFile(config, got);
                    var process = updater.StartGame(manifest, lang);
                    var info = started.Last();
                    Check(process == null && started.Count > 0, $"{lang}: start captured, nothing run");
                    Check(string.Equals(info.FileName, exe, StringComparison.OrdinalIgnoreCase), $"{lang}: exe {info.FileName}");
                    Check(info.Arguments == "blacknull", $"{lang}: args \"{info.Arguments}\"");
                    Check(string.Equals(info.WorkingDirectory.TrimEnd('\\'), workDir, StringComparison.OrdinalIgnoreCase), $"{lang}: working dir {info.WorkingDirectory}");
                    Check(File.Exists(info.FileName), $"{lang}: the exe is installed");
                    byte[] server = File.ReadAllBytes(Path.Combine(config.InstallDir, Updater.ServerFile));
                    string want = $"{host}:{expectedPorts[lang]}";
                    Check(server.SequenceEqual(Encoding.ASCII.GetBytes(want)), $"{lang}: ini\\origins.server = \"{Encoding.ASCII.GetString(server)}\" (want {want}, ASCII, no newline)");
                    byte[] login = File.ReadAllBytes(loginFile);
                    Check(login.SequenceEqual(Encoding.ASCII.GetBytes(token)), $"{lang}: ini\\origins.login = the 32-char token ({login.Length} bytes)");
                }

                // the account server per language comes only from the manifest; without it the file stays as installed
                var bare = Manifest.Fallback();
                File.WriteAllText(Path.Combine(config.InstallDir, Updater.ServerFile), "1.2.3.4:9958");
                updater.StartGame(bare, "pt");
                Check(File.ReadAllText(Path.Combine(config.InstallDir, Updater.ServerFile)) == "1.2.3.4:9958", "no accountServers: ini\\origins.server untouched");
                var fallback = started.Last();
                Check(string.Equals(fallback.FileName, exe, StringComparison.OrdinalIgnoreCase) && fallback.Arguments == "blacknull" &&
                      string.Equals(fallback.WorkingDirectory.TrimEnd('\\'), workDir, StringComparison.OrdinalIgnoreCase),
                    "offline fallback (no manifest saved) starts Env_DX9\\Conquer.exe blacknull in Env_DX9");
                updater.WriteServerFile(manifest, "en"); // leave the folder as an English start would
                File.Delete(loginFile); // the client deletes it after reading; the tests leave no token behind
            }
            finally
            {
                listener.Stop();
                await Task.WhenAny(serve, Task.Delay(1000));
            }
        }

        // ------------------------------------------------------------------ 5095 -> 6609

        private static async Task Migrate(Config config)
        {
            var updater = new Updater(config);
            var manifest = await updater.FetchManifestAsync(CancellationToken.None);
            string state = config.StateDir;
            // the folder is in Portuguese (run-6609-tests.ps1 leaves it so); pretend it was last updated by the 5095 launcher: its manifest (root Conquer.exe, Magpie, a
            // 5095-only file, a 5095 GameSetUp.ini kept for the player) and those files on disk
            var old = Json.Parse<Manifest>(File.ReadAllBytes(Path.Combine(state, "manifest.json")));
            old.GameExe = "Conquer.exe";
            old.WorkingDir = null;
            old.AccountServers = null;
            var oldOnly = new[] {@"Magpie\Magpie.exe", @"Magpie\origins-config.json", @"zftqat\krt.ini", "Flash9e.ocx"};
            foreach (string rel in oldOnly)
            {
                old.Files.Add(new ManifestFile {Path = rel, Size = 4, Sha256 = "x"});
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(config.InstallDir, rel)) ?? config.InstallDir);
                File.WriteAllText(Path.Combine(config.InstallDir, rel), "5095");
            }

            File.WriteAllBytes(Path.Combine(state, "manifest.json"), Json.Serialize(old));
            File.Delete(Path.Combine(state, "manifest.etag"));
            string setup = Path.Combine(config.InstallDir, KeepFile);
            File.WriteAllText(setup, "[ScreenMode]\r\nScreenModeRecord=3\r\n");
            string playerFile = Path.Combine(config.InstallDir, "screenshot", "mine.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(playerFile));
            File.WriteAllText(playerFile, "player's own file");

            Check(Updater.IsNewClient(old, manifest), "Conquer.exe -> Env_DX9\\Conquer.exe is a new client");
            int updated = await updater.UpdateGameAsync(manifest, "pt", false, new SyncProgress(_ => { }), CancellationToken.None);
            Check(updated == 1, $"only the 5095 GameSetUp.ini was replaced ({updated} updated)");
            foreach (string rel in oldOnly)
                Check(!File.Exists(Path.Combine(config.InstallDir, rel)), $"5095 leftover {rel} removed");
            Check(File.Exists(playerFile), "a file the player added stays");
            File.Delete(playerFile);
            Directory.Delete(Path.GetDirectoryName(playerFile));
            foreach (string folder in new[] {"Magpie", "zftqat"})
                if (Directory.Exists(Path.Combine(config.InstallDir, folder)) && !Directory.EnumerateFileSystemEntries(Path.Combine(config.InstallDir, folder)).Any())
                    Directory.Delete(Path.Combine(config.InstallDir, folder));
            var saved = updater.LoadLocalManifest();
            Check(saved.GameExe == manifest.GameExe && !Updater.IsNewClient(saved, manifest), "the 6609 manifest is now the installed one");

            // a second start: the keep file is the player's again
            File.AppendAllText(setup, "[OriginsTest]\r\nAfterMigration=1\r\n");
            updated = await updater.UpdateGameAsync(manifest, "pt", false, new SyncProgress(_ => { }), CancellationToken.None);
            Check(updated == 0 && File.ReadAllText(setup).Contains("AfterMigration"), $"after the switch, {KeepFile} is kept again ({updated} updated)");
        }

        // ------------------------------------------------------------------ conditional manifest

        private static async Task Etag(Config config)
        {
            string log = Path.Combine(config.StateDir, "launcher.log");
            long before = File.Exists(log) ? new FileInfo(log).Length : 0;
            string etag = File.Exists(Path.Combine(config.StateDir, "manifest.etag")) ? File.ReadAllText(Path.Combine(config.StateDir, "manifest.etag")) : null;
            Check(etag != null, $"the installed manifest's ETag was saved ({etag})");
            var updater = new Updater(config);
            var manifest = await updater.FetchManifestAsync(CancellationToken.None);
            int updated = await updater.UpdateGameAsync(manifest, "pt", false, new SyncProgress(_ => { }), CancellationToken.None);
            string tail = ReadFrom(log, before);
            Check(tail.Contains("manifest not modified (304)"), "an up-to-date folder gets 304 and uses the saved manifest");
            Check(updated == 0, $"nothing to update ({updated})");
            Check(manifest.Files.Count > 100000 && manifest.AccountServers?.Count == 3, $"the saved manifest is complete ({manifest.Files.Count} files)");
        }

        // ------------------------------------------------------------------ update server down

        private static async Task Offline(Config config)
        {
            var updater = new Updater(config);
            try
            {
                await updater.FetchManifestAsync(CancellationToken.None);
                Fail("the server is down (DevUpdateServer --down): the manifest must not load");
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                Check(true, $"manifest request fails: {ex.GetType().Name}: {ex.Message}");
            }

            // what MainForm does then: the saved manifest, PLAY enabled when the game is there
            var local = updater.LoadLocalManifest();
            Check(local != null && updater.GameFilesPresent(local.GameExe), "saved manifest + installed exe: PLAY stays enabled");
            var started = new List<ProcessStartInfo>();
            Updater.StartProcess = info => { started.Add(info); return null; };
            updater.StartGame(local, "pt");
            Check(started.Count == 1 && started[0].FileName.EndsWith(@"Env_DX9\Conquer.exe", StringComparison.OrdinalIgnoreCase) &&
                  started[0].WorkingDirectory.TrimEnd('\\').EndsWith(@"\Env_DX9", StringComparison.OrdinalIgnoreCase),
                $"offline start: {started.FirstOrDefault()?.FileName} in {started.FirstOrDefault()?.WorkingDirectory}");
            string server = File.ReadAllText(Path.Combine(config.InstallDir, Updater.ServerFile));
            Check(server.EndsWith(":9959"), $"offline start in pt still writes the pt port from the saved manifest ({server})");
            updater.WriteServerFile(local, "en");
        }

        // ------------------------------------------------------------------ helpers

        private static string ReadFrom(string path, long offset)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Position = Math.Min(offset, fs.Length);
            return new StreamReader(fs, Encoding.UTF8).ReadToEnd();
        }

        private static string Rel(string root, string full) => full.Substring(root.TrimEnd('\\').Length + 1);

        private static string Hash(string path)
        {
            using var sha = SHA256.Create();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
        }

        private static void Check(bool ok, string what)
        {
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
            if (!ok) s_failures++;
        }

        private static void Fail(string what) => Check(false, what);

        private static Dictionary<string, string> Options(IEnumerable<string> args)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var list = args.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                if (!list[i].StartsWith("--")) throw new ArgumentException("unexpected " + list[i]);
                string key = list[i].Substring(2);
                result[key] = i + 1 < list.Count && !list[i + 1].StartsWith("--") ? list[++i] : "";
            }

            return result;
        }

        /// <summary>Progress reported on the calling thread, in order (Progress&lt;T&gt; posts to the thread pool).</summary>
        private sealed class SyncProgress : IProgress<UpdateProgress>
        {
            private readonly Action<UpdateProgress> m_handler;
            private readonly object m_lock = new object();
            public SyncProgress(Action<UpdateProgress> handler) => m_handler = handler;

            public void Report(UpdateProgress value)
            {
                lock (m_lock) m_handler(value);
            }
        }
    }
}
