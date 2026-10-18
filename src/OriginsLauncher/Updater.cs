using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace OriginsLauncher
{
    internal sealed class UpdateProgress
    {
        public string Status { get; set; }
        public string Detail { get; set; }

        /// <summary>0..1, or negative when the amount of work is unknown.</summary>
        public double Fraction { get; set; } = -1;
    }

    /// <summary>
    ///     Checks the game folder against the published manifest and downloads what is missing or
    ///     changed. Downloads are gzip files resumed with HTTP ranges; every file is verified with
    ///     SHA-256 before it replaces the old one.
    /// </summary>
    internal sealed class Updater
    {
        private const int MaxAttempts = 3;
        private const int ParallelDownloads = 8;
        private const long SmallFileBytes = 4 << 20;

        // Full-client pack: parts are tens of MB, so a few parallel connections fill the line.
        private const int PackParallel = 6;
        private const int PackAttempts = 5;

        /// <summary>Below this much to download, single files are faster than the pack.</summary>
        private const long PackThreshold = 64L << 20;
        private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

        private static readonly HttpClient Http = CreateHttpClient();

        private readonly Config m_config;
        private HashCache m_cache = new HashCache();

        public Updater(Config config)
        {
            m_config = config;
        }

        public static Version CurrentVersion => typeof(Updater).Assembly.GetName().Version;

        // ------------------------------------------------------------------ remote data

        /// <summary>ETag of the manifest last fetched with a 200 (saved with it by <see cref="SaveLocalManifest" />).</summary>
        private string m_fetchedEtag;

        /// <summary>
        ///     The published manifest. The 6609 client is ~110k files, so the manifest is a few MB even compressed:
        ///     when the game folder is up to date with a manifest of a known ETag, the request is conditional and a
        ///     304 reuses the saved copy.
        /// </summary>
        public async Task<Manifest> FetchManifestAsync(CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, m_config.ManifestUrl);
            string savedEtag = File.Exists(LocalManifestPath) && File.Exists(LocalEtagPath) ? ReadEtag() : null;
            if (savedEtag != null && EntityTagHeaderValue.TryParse(savedEtag, out var tag))
                request.Headers.IfNoneMatch.Add(tag);

            // headers first: the body (a few MB) is read with the stall timeout, not HttpClient's 60 s for the whole
            // response, so a slow line still gets it
            using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                Manifest manifest;
                if (response.StatusCode == HttpStatusCode.NotModified && savedEtag != null)
                {
                    manifest = LoadLocalManifest();
                    m_fetchedEtag = savedEtag;
                    Log.Write("manifest not modified (304): using the saved one");
                    if (manifest == null) throw new InvalidDataException(L.S.BadManifest);
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    manifest = Json.Parse<Manifest>(await ReadBodyAsync(response, ct).ConfigureAwait(false));
                    m_fetchedEtag = response.Headers.ETag?.ToString();
                }

                if (manifest?.Files == null || string.IsNullOrEmpty(manifest.GameExe))
                    throw new InvalidDataException(L.S.BadManifest);
                foreach (var file in manifest.Files.Concat(manifest.Languages?.Values.SelectMany(p => p ?? new List<ManifestFile>()) ?? Enumerable.Empty<ManifestFile>()))
                {
                    ResolveGamePath(file.Path); // reject unsafe paths up front
                    if (file.Url != null) ResolveGamePath(file.Url);
                }
                return manifest;
            }
        }

        /// <summary>The whole body of a response sent with ResponseHeadersRead, aborted only if it stops arriving.</summary>
        private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
        {
            using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var output = new MemoryStream())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await ReadWithStallTimeoutAsync(input, buffer, ct).ConfigureAwait(false)) > 0)
                    output.Write(buffer, 0, read);
                return output.ToArray();
            }
        }

        private string LocalEtagPath => Path.Combine(m_config.StateDir, "manifest.etag");

        private string ReadEtag()
        {
            try { return File.ReadAllText(LocalEtagPath, Encoding.ASCII).Trim(); }
            catch (Exception) { return null; }
        }

        /// <summary>Last manifest the game was fully updated to; lets the game start while offline.</summary>
        public Manifest LoadLocalManifest()
        {
            try
            {
                return File.Exists(LocalManifestPath) ? Json.Parse<Manifest>(File.ReadAllBytes(LocalManifestPath)) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void SaveLocalManifest(Manifest manifest)
        {
            Directory.CreateDirectory(m_config.StateDir);
            if (File.Exists(LocalEtagPath)) File.Delete(LocalEtagPath); // never an ETag next to another manifest
            File.WriteAllBytes(LocalManifestPath, Json.Serialize(manifest));
            if (m_fetchedEtag != null)
                File.WriteAllText(LocalEtagPath, m_fetchedEtag, Encoding.ASCII);
        }

        private string LocalManifestPath => Path.Combine(m_config.StateDir, "manifest.json");

        public async Task<List<NewsItem>> FetchNewsAsync(CancellationToken ct)
        {
            using (var response = await Http.GetAsync(m_config.NewsUrl + "?lang=" + L.S.Code, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                return Json.Parse<List<NewsItem>>(await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false))
                       ?? new List<NewsItem>();
            }
        }

        // The launcher's background is game art: it is served by the site and cached here instead
        // of being built into the (open source, signed) exe.
        private string BackgroundPath => Path.Combine(m_config.StateDir, "background.jpg");
        private const int MaxBackgroundBytes = 4 << 20;

        public System.Drawing.Image LoadCachedBackground()
        {
            try
            {
                return File.Exists(BackgroundPath) ? DecodeImage(File.ReadAllBytes(BackgroundPath)) : null;
            }
            catch (Exception)
            {
                return null; // damaged cache: plain backdrop until the next download
            }
        }

        /// <summary>The site's current background, or null when it is unchanged or unavailable.</summary>
        public async Task<System.Drawing.Image> FetchBackgroundAsync(CancellationToken ct)
        {
            try
            {
                using (var response = await Http.GetAsync(m_config.SiteUrl + "img/launcher-bg.jpg", ct).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) return null;
                    byte[] data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (data.Length == 0 || data.Length > MaxBackgroundBytes) return null;
                    if (File.Exists(BackgroundPath) && File.ReadAllBytes(BackgroundPath).SequenceEqual(data)) return null;
                    var image = DecodeImage(data);
                    Directory.CreateDirectory(m_config.StateDir);
                    File.WriteAllBytes(BackgroundPath, data);
                    return image;
                }
            }
            catch (Exception ex)
            {
                Log.Write($"background not loaded: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static System.Drawing.Image DecodeImage(byte[] data)
        {
            // decoded into memory: Image.FromStream needs its stream alive, and the file stays free
            using (var stream = new MemoryStream(data))
            using (var decoded = System.Drawing.Image.FromStream(stream))
                return new System.Drawing.Bitmap(decoded);
        }

        // ------------------------------------------------------------------ launcher self-update

        public static bool IsLauncherOutdated(Manifest manifest) =>
            Version.TryParse(manifest.LauncherVersion, out var latest) && latest > CurrentVersion;

        /// <summary>
        ///     Downloads the new launcher next to the state folder, verifies it, and starts a small
        ///     script that waits for this process to exit, swaps the exe and restarts it.
        /// </summary>
        public async Task StartSelfUpdateAsync(Manifest manifest, IProgress<UpdateProgress> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(m_config.StateDir);
            string newExe = Path.Combine(m_config.StateDir, "OriginsLauncher.new.exe");
            string url = m_config.UpdateBaseUrl + manifest.LauncherUrl;

            progress.Report(new UpdateProgress {Status = L.S.UpdatingLauncher, Fraction = -1});
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = File.Create(newExe))
                    await input.CopyToAsync(output, 81920, ct).ConfigureAwait(false);
            }

            if (!string.Equals(HashFile(newExe), manifest.LauncherSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(newExe);
                throw new InvalidDataException(L.S.BadLauncher);
            }

            string exe = Process.GetCurrentProcess().MainModule.FileName;
            int pid = Process.GetCurrentProcess().Id;
            // Keep the original arguments; --after-update stops a loop if the swap did not happen.
            string args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1)
                .Where(a => !a.Equals(Config.AfterUpdateArg, StringComparison.OrdinalIgnoreCase))
                .Select(a => $"\"{a.Replace("%", "%%")}\"")
                .Concat(new[] {Config.AfterUpdateArg}));
            string script = Path.Combine(m_config.StateDir, "update-launcher.cmd");
            File.WriteAllText(script, string.Join("\r\n",
                "@echo off",
                ":wait",
                $"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul && (ping 127.0.0.1 -n 2 >nul & goto wait)",
                $"move /Y \"{newExe}\" \"{exe}\" >nul",
                $"start \"\" \"{exe}\" {args}",
                "del \"%~f0\"",
                ""), Encoding.ASCII);

            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }

        // ------------------------------------------------------------------ update flow

        /// <summary>
        ///     Brings the game folder to <paramref name="manifest" /> in <paramref name="language" />: finds what is
        ///     missing or changed, takes it from the full-client pack when there is a lot (first install), downloads
        ///     the rest file by file, removes what the previous client had and this one has not, and saves the
        ///     manifest as the installed one. Returns how many files were outdated.
        /// </summary>
        public async Task<int> UpdateGameAsync(Manifest manifest, string language, bool fullCheck,
            IProgress<UpdateProgress> progress, CancellationToken ct)
        {
            var previous = LoadLocalManifest();
            bool newClient = IsNewClient(previous, manifest);
            if (newClient)
                Log.Write($"new client: {previous.GameExe} -> {manifest.GameExe}; its settings files are replaced once");

            var wanted = manifest.FilesFor(language);
            var outdated = await FindOutdatedAsync(wanted, fullCheck, progress, ct, newClient).ConfigureAwait(false);
            int updated = outdated.Count;
            Log.Write($"check lang={language} full={fullCheck}: {outdated.Count} outdated, {outdated.Sum(f => f.GzSize)} bytes to download");
            if (ShouldUsePack(manifest, outdated))
            {
                try
                {
                    await InstallPackAsync(manifest.Pack, outdated, progress, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is GameRunningException) && !(ex is OperationCanceledException && ct.IsCancellationRequested))
                {
                    // e.g. the pack was replaced on the server mid-download: single files still work
                    Log.Write($"pack failed, downloading single files: {ex.GetType().Name}: {ex.Message}");
                }

                outdated = await FindOutdatedAsync(wanted, false, progress, ct, newClient).ConfigureAwait(false);
                Log.Write($"after pack: {outdated.Count} outdated, {outdated.Sum(f => f.GzSize)} bytes to download");
            }

            if (outdated.Count > 0)
                await DownloadAsync(outdated, progress, ct).ConfigureAwait(false);
            RemoveStaleFiles(previous, manifest);
            Log.Write("up to date");
            SaveLocalManifest(manifest);
            return updated;
        }

        // ------------------------------------------------------------------ file check

        /// <summary>
        ///     Returns the files that must be downloaded. A normal check trusts the hash cache when
        ///     size and modification time are unchanged; <paramref name="full" /> re-hashes everything.
        /// </summary>
        /// <param name="ignoreKeep">
        ///     also replace the "keep" files (the player's settings) when they differ: only right after the manifest
        ///     switched to another client (<see cref="IsNewClient" />), whose settings files have another format
        /// </param>
        public async Task<List<ManifestFile>> FindOutdatedAsync(List<ManifestFile> files, bool full,
            IProgress<UpdateProgress> progress, CancellationToken ct, bool ignoreKeep = false)
        {
            LoadCache();
            var outdated = new List<ManifestFile>();
            var toHash = new List<(ManifestFile File, string FullPath, FileInfo Info)>();

            foreach (var file in files)
            {
                string fullPath = ResolveGamePath(file.Path);
                var info = new FileInfo(fullPath);
                if (!info.Exists)
                {
                    outdated.Add(file);
                    continue;
                }

                if (file.Keep && !ignoreKeep)
                    continue; // player-owned file: only installed when missing

                if (info.Length != file.Size)
                {
                    outdated.Add(file);
                    continue;
                }

                if (!full && m_cache.Entries.TryGetValue(Key(file.Path), out var cached) &&
                    cached.Size == info.Length && cached.LastWriteTicks == info.LastWriteTimeUtc.Ticks)
                {
                    if (!string.Equals(cached.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
                        outdated.Add(file);
                    continue;
                }

                toHash.Add((file, fullPath, info));
            }

            long totalBytes = toHash.Sum(x => x.Info.Length), doneBytes = 0;
            for (int i = 0; i < toHash.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (file, fullPath, info) = toHash[i];
                progress.Report(new UpdateProgress
                {
                    Status = L.S.VerifyingFiles,
                    Detail = string.Format(L.S.VerifyingDetail, i + 1, toHash.Count, file.Path),
                    Fraction = totalBytes == 0 ? 1 : (double) doneBytes / totalBytes
                });

                string sha = await Task.Run(() => HashFile(fullPath), ct).ConfigureAwait(false);
                m_cache.Entries[Key(file.Path)] = new CacheEntry
                    {Size = info.Length, LastWriteTicks = info.LastWriteTimeUtc.Ticks, Sha256 = sha};
                if (!string.Equals(sha, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    outdated.Add(file);
                doneBytes += info.Length;
            }

            SaveCache();
            return outdated;
        }

        // ------------------------------------------------------------------ download

        /// <summary>
        ///     Downloads with a few parallel connections: the client is ~10k files, mostly small,
        ///     so one connection would spend most of its time waiting on round trips.
        /// </summary>
        public async Task DownloadAsync(List<ManifestFile> files, IProgress<UpdateProgress> progress, CancellationToken ct)
        {
            var meter = new TransferMeter(files.Sum(f => f.GzSize));
            var lastSave = Stopwatch.StartNew();
            int next = -1, finished = 0;

            void Report(bool force = false)
            {
                if (meter.ShouldReport(force))
                    progress.Report(new UpdateProgress
                    {
                        Status = string.Format(L.S.Downloading, Volatile.Read(ref finished), files.Count),
                        Detail = meter.Describe(),
                        Fraction = meter.Fraction
                    });
            }

            async Task Worker(CancellationToken token)
            {
                int index;
                while ((index = Interlocked.Increment(ref next)) < files.Count)
                {
                    var file = files[index];
                    await RetryAsync(() => DownloadFileAsync(file, meter, () => Report(), token), file.Path, MaxAttempts,
                        meter, progress, token).ConfigureAwait(false);
                    Interlocked.Increment(ref finished);
                    Report();
                    lock (m_cache)
                    {
                        if (lastSave.Elapsed > TimeSpan.FromSeconds(15))
                        {
                            SaveCache(); // closing mid-download should not force a re-hash of what is done
                            lastSave.Restart();
                        }
                    }
                }
            }

            try
            {
                await RunWorkersAsync(Math.Min(ParallelDownloads, files.Count), Worker, ct).ConfigureAwait(false);
            }
            finally
            {
                lock (m_cache) SaveCache();
            }

            Report(true);
            RemoveEmptyFolders(Path.Combine(m_config.StateDir, "files"));
        }

        /// <summary>
        ///     Runs <paramref name="count" /> workers; the first real failure stops the others and is
        ///     rethrown (not the cancellations it caused).
        /// </summary>
        private static async Task RunWorkersAsync(int count, Func<CancellationToken, Task> worker, CancellationToken ct)
        {
            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var workers = Enumerable.Range(0, count).Select(_ => Task.Run(async () =>
                {
                    try
                    {
                        await worker(stop.Token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        stop.Cancel();
                        throw;
                    }
                })).ToList();

                try
                {
                    await Task.WhenAll(workers).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    var error = workers.Where(t => t.IsFaulted).SelectMany(t => t.Exception.InnerExceptions)
                        .FirstOrDefault(e => !(e is OperationCanceledException));
                    if (error != null && !ct.IsCancellationRequested)
                        ExceptionDispatchInfo.Capture(error).Throw();
                    throw;
                }
            }
        }

        private static void RemoveEmptyFolders(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var sub in Directory.GetDirectories(dir))
                    RemoveEmptyFolders(sub);
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception)
            {
                // leftovers are harmless
            }
        }

        private static async Task RetryAsync(Func<Task> attempt, string what, int maxAttempts, TransferMeter meter,
            IProgress<UpdateProgress> progress, CancellationToken ct)
        {
            for (int n = 1;; n++)
            {
                try
                {
                    await attempt().ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (n < maxAttempts && !(ex is OperationCanceledException) &&
                                           !(ex is GameRunningException))
                {
                    Log.Write($"retry {n} {what}: {ex.GetType().Name}: {ex.Message}");
                    progress.Report(new UpdateProgress
                    {
                        Status = L.S.Retry, Detail = $"{what}: {ex.Message}", Fraction = meter.Fraction
                    });
                    await Task.Delay(TimeSpan.FromSeconds(3 * n), ct).ConfigureAwait(false);
                }
            }
        }

        private async Task DownloadFileAsync(ManifestFile file, TransferMeter meter, Action report, CancellationToken ct)
        {
            string target = ResolveGamePath(file.Path);
            string part = Path.Combine(m_config.StateDir, "files", (file.Url ?? file.Path) + ".gz.part");
            Directory.CreateDirectory(Path.GetDirectoryName(target));

            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (have > file.GzSize)
            {
                File.Delete(part);
                have = 0;
            }

            // Most of the client is small files: those go through memory, so only the final
            // file is written (every file written is also scanned by the antivirus).
            bool inMemory = have == 0 && file.GzSize <= SmallFileBytes;
            MemoryStream memory = null;

            long counted = 0; // bytes of this file in the meter, taken back out if the attempt fails
            try
            {
                meter.AddExisting(have);
                counted = have;
                if (file.Size > 0 && have < file.GzSize)
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, FileUrl(file));
                    if (have > 0)
                        request.Headers.Range = new RangeHeaderValue(have, null);

                    using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        bool resumed = response.StatusCode == HttpStatusCode.PartialContent;
                        if (!resumed && have > 0)
                        {
                            meter.Remove(counted);
                            counted = 0;
                        }

                        if (!inMemory)
                            Directory.CreateDirectory(Path.GetDirectoryName(part));
                        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = inMemory
                                   ? memory = new MemoryStream((int) file.GzSize)
                                   : (Stream) new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                        {
                            var buffer = new byte[81920];
                            while (true)
                            {
                                int read = await ReadWithStallTimeoutAsync(input, buffer, ct).ConfigureAwait(false);
                                if (read == 0) break;
                                await output.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                                meter.Add(read);
                                counted += read;
                                report();
                            }

                            if (output.Length < file.GzSize) // connection closed early: keep the part and resume
                                throw new IOException(L.S.ConnInterrupted);
                        }
                    }
                }

                // Decompress next to the target, verify, then swap.
                string temp = target + ".originstmp";
                string sha = await Task.Run(() =>
                {
                    if (file.Size == 0) return Inflate(null, temp);
                    if (memory != null) return Inflate(new MemoryStream(memory.ToArray()), temp);
                    return Inflate(File.OpenRead(part), temp);
                }, ct).ConfigureAwait(false);
                if (!string.Equals(sha, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    DeleteIfExists(temp);
                    DeleteIfExists(part);
                    throw new InvalidDataException(L.S.Corrupted);
                }

                ReplaceFile(temp, target, file.Path);
                DeleteIfExists(part);
                var info = new FileInfo(target);
                lock (m_cache)
                    m_cache.Entries[Key(file.Path)] = new CacheEntry
                        {Size = info.Length, LastWriteTicks = info.LastWriteTimeUtc.Ticks, Sha256 = sha};
            }
            catch (Exception ex)
            {
                if (ex is InvalidDataException)
                {
                    // bad gzip or wrong hash: this file starts over
                    DeleteIfExists(part);
                    DeleteIfExists(target + ".originstmp");
                }

                meter.Remove(counted);
                throw;
            }
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path)) File.Delete(path); // File.Delete throws when the folder is missing
        }

        /// <summary>Puts the verified <paramref name="temp" /> in place of <paramref name="target" />.</summary>
        private static void ReplaceFile(string temp, string target, string path)
        {
            try
            {
                if (File.Exists(target))
                {
                    File.SetAttributes(target, FileAttributes.Normal);
                    File.Delete(target);
                }

                File.Move(temp, target);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                DeleteIfExists(temp);
                throw new GameRunningException(path);
            }
        }

        private static async Task<int> ReadWithStallTimeoutAsync(Stream input, byte[] buffer, CancellationToken ct)
        {
            var read = input.ReadAsync(buffer, 0, buffer.Length, ct);
            var winner = await Task.WhenAny(read, Task.Delay(StallTimeout, ct)).ConfigureAwait(false);
            if (winner != read)
            {
                ct.ThrowIfCancellationRequested();
                input.Dispose(); // abort the stalled connection; the download resumes on retry
                throw new IOException(L.S.ConnStalled);
            }

            return await read.ConfigureAwait(false);
        }

        /// <summary>Writes the decompressed <paramref name="gz" /> (null: an empty file) and returns its SHA-256.</summary>
        private static string Inflate(Stream gz, string outPath)
        {
            using (var sha = SHA256.Create())
            using (var input = gz == null ? Stream.Null : new GZipStream(gz, CompressionMode.Decompress))
            using (var output = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    output.Write(buffer, 0, read);
                }

                sha.TransformFinalBlock(buffer, 0, 0);
                return ToHex(sha.Hash);
            }
        }

        // ------------------------------------------------------------------ full-client pack

        /// <summary>A first install (or a broken one) downloads the pack instead of ~10k single files.</summary>
        public static bool ShouldUsePack(Manifest manifest, List<ManifestFile> outdated) =>
            manifest.Pack?.Parts?.Count > 0 && outdated.Sum(f => f.GzSize) >= PackThreshold;

        /// <summary>
        ///     Downloads the pack parts in parallel (resumable, each verified with SHA-256), then
        ///     installs the files of <paramref name="wanted" /> that the pack has with the same hash.
        ///     Anything else (files newer than the pack, language packs) is left for the per-file
        ///     download. Returns how many files were installed.
        /// </summary>
        public async Task<int> InstallPackAsync(PackInfo pack, List<ManifestFile> wanted, IProgress<UpdateProgress> progress,
            CancellationToken ct)
        {
            string dir = Path.Combine(m_config.StateDir, "pack");
            Directory.CreateDirectory(dir);
            foreach (var old in Directory.GetFiles(dir).Where(f => !Path.GetFileName(f).StartsWith(pack.Id + ".", StringComparison.OrdinalIgnoreCase)))
                DeleteIfExists(old); // parts of a pack the server no longer has

            progress.Report(new UpdateProgress {Status = L.S.DownloadingPack, Fraction = -1});
            PackIndex index;
            using (var response = await Http.GetAsync(m_config.UpdateBaseUrl + pack.Index, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                byte[] data = await ReadBodyAsync(response, ct).ConfigureAwait(false);
                if (!string.Equals(HashBytes(data), pack.IndexSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(L.S.Corrupted);
                index = Json.Parse<PackIndex>(data);
            }

            foreach (var file in index.Files)
                ResolveGamePath(file.Path); // reject unsafe paths up front

            // ---- download
            var meter = new TransferMeter(pack.Parts.Sum(p => p.Size));
            var parts = pack.Parts.Select((p, i) => Path.Combine(dir, $"{pack.Id}.{i:D2}.bin")).ToList();
            int next = -1;

            void Report(bool force = false)
            {
                if (meter.ShouldReport(force))
                    progress.Report(new UpdateProgress {Status = L.S.DownloadingPack, Detail = meter.Describe(), Fraction = meter.Fraction});
            }

            async Task Worker(CancellationToken token)
            {
                int i;
                while ((i = Interlocked.Increment(ref next)) < pack.Parts.Count)
                {
                    int n = i;
                    await RetryAsync(() => DownloadPartAsync(pack.Parts[n], parts[n], meter, () => Report(), token),
                        pack.Parts[n].Url, PackAttempts, meter, progress, token).ConfigureAwait(false);
                    Report();
                }
            }

            await RunWorkersAsync(Math.Min(PackParallel, pack.Parts.Count), Worker, ct).ConfigureAwait(false);
            Report(true);

            // ---- install
            var needed = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in wanted) needed[file.Path] = file;
            bool Matches(ManifestFile entry) =>
                needed.TryGetValue(entry.Path, out var want) && want.Size == entry.Size &&
                string.Equals(want.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase);

            var toInstall = index.Files.Where(Matches).ToList();
            long totalBytes = Math.Max(1, toInstall.Sum(f => f.Size)), doneBytes = 0;
            int installed = 0;
            var clock = Stopwatch.StartNew();

            await Task.Run(() =>
            {
                using (var blob = new PartsReader(parts))
                {
                    foreach (var entry in index.Files)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!Matches(entry))
                        {
                            blob.Skip(entry.GzSize);
                            continue;
                        }

                        string target = ResolveGamePath(entry.Path);
                        string temp = target + ".originstmp";
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        var slice = new LimitedStream(blob, entry.GzSize);
                        string sha = Inflate(entry.Size == 0 ? null : slice, temp);
                        slice.SkipRest();
                        if (!string.Equals(sha, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            // cannot happen with verified parts; leave the file to the per-file download
                            Log.Write($"pack: {entry.Path} hash mismatch, skipped");
                            DeleteIfExists(temp);
                            continue;
                        }

                        ReplaceFile(temp, target, entry.Path);
                        var info = new FileInfo(target);
                        lock (m_cache)
                            m_cache.Entries[Key(entry.Path)] = new CacheEntry
                                {Size = info.Length, LastWriteTicks = info.LastWriteTimeUtc.Ticks, Sha256 = sha};

                        installed++;
                        doneBytes += entry.Size;
                        if (clock.ElapsedMilliseconds >= 100 || installed == toInstall.Count)
                        {
                            clock.Restart();
                            progress.Report(new UpdateProgress
                            {
                                Status = string.Format(L.S.Unpacking, installed, toInstall.Count),
                                Detail = entry.Path,
                                Fraction = (double) doneBytes / totalBytes
                            });
                        }
                    }
                }
            }, ct).ConfigureAwait(false);

            lock (m_cache) SaveCache();
            foreach (var part in parts) DeleteIfExists(part);
            RemoveEmptyFolders(dir);
            Log.Write($"pack {pack.Id}: {installed} files installed");
            return installed;
        }

        private async Task DownloadPartAsync(PackPart part, string path, TransferMeter meter, Action report, CancellationToken ct)
        {
            if (File.Exists(path) && new FileInfo(path).Length == part.Size)
            {
                meter.AddExisting(part.Size); // finished in an earlier run (verified before the rename)
                return;
            }

            string temp = path + ".part";
            long have = File.Exists(temp) ? new FileInfo(temp).Length : 0;
            if (have > part.Size)
            {
                File.Delete(temp);
                have = 0;
            }

            long counted = 0;
            try
            {
                meter.AddExisting(have);
                counted = have;
                if (have < part.Size)
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, m_config.UpdateBaseUrl + part.Url);
                    if (have > 0)
                        request.Headers.Range = new RangeHeaderValue(have, null);

                    using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        bool resumed = response.StatusCode == HttpStatusCode.PartialContent;
                        if (!resumed && have > 0)
                        {
                            meter.Remove(counted);
                            counted = 0;
                        }

                        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = new FileStream(temp, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                        {
                            var buffer = new byte[1 << 16];
                            while (true)
                            {
                                int read = await ReadWithStallTimeoutAsync(input, buffer, ct).ConfigureAwait(false);
                                if (read == 0) break;
                                await output.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                                meter.Add(read);
                                counted += read;
                                report();
                            }

                            if (output.Length < part.Size) // connection closed early: keep the part and resume
                                throw new IOException(L.S.ConnInterrupted);
                        }
                    }
                }

                string sha = await Task.Run(() => HashFile(temp), ct).ConfigureAwait(false);
                if (!string.Equals(sha, part.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    DeleteIfExists(temp);
                    throw new InvalidDataException(L.S.Corrupted);
                }

                DeleteIfExists(path);
                File.Move(temp, path);
            }
            catch (Exception)
            {
                meter.Remove(counted);
                throw;
            }
        }

        // ------------------------------------------------------------------ game

        /// <summary>
        ///     The 6609 login screen is a Flash movie. The game registers its own Flash.ocx
        ///     machine-wide, which needs admin rights; when no Flash control is registered at all,
        ///     register the bundled one for the current user only (no admin needed).
        /// </summary>
        public void EnsureFlashRegistered()
        {
            const string clsid = "{D27CDB6E-AE6D-11cf-96B8-444553540000}";
            string ocx = Path.Combine(m_config.InstallDir, "Flash.ocx");
            if (!File.Exists(ocx))
                return;

            foreach (var view in new[] {RegistryView.Registry32, RegistryView.Registry64})
            {
                using (var root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view))
                using (var server = root.OpenSubKey($@"CLSID\{clsid}\InprocServer32"))
                {
                    if (server?.GetValue(null) is string path && File.Exists(path))
                        return; // a working Flash control is already registered
                }
            }

            foreach (var keyPath in new[] {$@"Software\Classes\CLSID\{clsid}", $@"Software\Classes\Wow6432Node\CLSID\{clsid}"})
            {
                using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
                {
                    key.SetValue(null, "Shockwave Flash Object");
                    using (var server = key.CreateSubKey("InprocServer32"))
                    {
                        server.SetValue(null, ocx);
                        server.SetValue("ThreadingModel", "Apartment");
                    }

                    using (var progId = key.CreateSubKey("ProgID"))
                        progId.SetValue(null, "ShockwaveFlash.ShockwaveFlash.7");
                }
            }
        }

        /// <summary>The file the patched 6609 client reads the account server from ("host:port", ASCII, no newline).</summary>
        public const string ServerFile = @"ini\origins.server";

        /// <summary>Starts a process; tests replace it to check what would be started without running the game.</summary>
        public static Func<ProcessStartInfo, Process> StartProcess = Process.Start;

        /// <summary>
        ///     Writes the account server of <paramref name="language" /> to ini\origins.server, then starts the game
        ///     (6609: Env_DX9\Conquer.exe blacknull in Env_DX9). The login token (ini\origins.login) is written
        ///     before this by the login dialog.
        /// </summary>
        public Process StartGame(Manifest manifest, string language)
        {
            WriteServerFile(manifest, language);
            var info = GameStartInfo(manifest);
            Log.Write($"start {info.FileName} {info.Arguments} in {info.WorkingDirectory}");
            return StartProcess(info);
        }

        public ProcessStartInfo GameStartInfo(Manifest manifest)
        {
            string exe = ResolveGamePath(string.IsNullOrEmpty(manifest.GameExe) ? Manifest.DefaultGameExe : manifest.GameExe);
            string workingDir = string.IsNullOrEmpty(manifest.WorkingDir)
                ? Path.GetDirectoryName(exe)
                : ResolveGamePath(manifest.WorkingDir);
            return new ProcessStartInfo(exe, manifest.GameArgs ?? "")
            {
                WorkingDirectory = workingDir,
                UseShellExecute = true
            };
        }

        /// <summary>
        ///     ini\origins.server = the manifest's account server for <paramref name="language" /> (English when the
        ///     language has none). Without addresses in the manifest the file is left as installed. Returns what was
        ///     written, or null.
        /// </summary>
        public string WriteServerFile(Manifest manifest, string language)
        {
            var servers = manifest.AccountServers;
            if (servers == null || servers.Count == 0)
            {
                Log.Write("manifest has no account servers; ini\\origins.server left as it is");
                return null;
            }

            if (!servers.TryGetValue(language ?? "", out string address) && !servers.TryGetValue("en", out address))
                return null;
            address = address?.Trim();
            if (!IsHostPort(address))
                throw new InvalidDataException(L.S.BadManifest);

            string path = ResolveGamePath(ServerFile);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            byte[] bytes = Encoding.ASCII.GetBytes(address);
            if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(bytes))
            {
                if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
                File.WriteAllBytes(path, bytes);
            }

            Log.Write($"account server ({language}) = {address}");
            return address;
        }

        private static bool IsHostPort(string address)
        {
            if (string.IsNullOrEmpty(address) || address.Length > 63) return false;
            int colon = address.LastIndexOf(':');
            return colon > 0 && ushort.TryParse(address.Substring(colon + 1), out ushort port) && port > 0 &&
                   address.Substring(0, colon).All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-') &&
                   address.All(c => c < 128);
        }

        public bool GameFilesPresent(string gameExe = Manifest.DefaultGameExe) =>
            File.Exists(Path.Combine(m_config.InstallDir, gameExe));

        // ------------------------------------------------------------------ client switch (5095 -> 6609)

        /// <summary>
        ///     The manifest starts another exe than the one the game folder was last updated to (the 5095 client's
        ///     Conquer.exe -> the 6609 client's Env_DX9\Conquer.exe): a different client, so the player's settings
        ///     files of the old one are replaced once and its leftover files are removed.
        /// </summary>
        public static bool IsNewClient(Manifest previous, Manifest manifest) =>
            previous != null && !string.IsNullOrEmpty(previous.GameExe) &&
            !string.Equals(previous.GameExe, manifest.GameExe, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        ///     Deletes the files the previous manifest installed that the new one no longer has (in any language).
        ///     Only files the launcher put there: anything the player added is not in a manifest and stays.
        ///     Returns how many were removed.
        /// </summary>
        public int RemoveStaleFiles(Manifest previous, Manifest manifest)
        {
            if (previous == null) return 0;
            var keep = manifest.AllPaths();
            int removed = 0;
            foreach (string path in previous.AllPaths())
            {
                if (keep.Contains(path)) continue;
                string full;
                try { full = ResolveGamePath(path); }
                catch (InvalidDataException) { continue; }

                try
                {
                    if (!File.Exists(full)) continue;
                    File.SetAttributes(full, FileAttributes.Normal);
                    File.Delete(full);
                    lock (m_cache) m_cache.Entries.Remove(Key(path));
                    removed++;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Write($"stale file not removed: {path}: {ex.Message}");
                }
            }

            if (removed > 0)
            {
                lock (m_cache) SaveCache();
                Log.Write($"{removed} file(s) of the previous client removed");
            }

            return removed;
        }

        // ------------------------------------------------------------------ helpers

        public bool InstallDirIsWritable()
        {
            try
            {
                Directory.CreateDirectory(m_config.StateDir);
                string probe = Path.Combine(m_config.StateDir, "write.test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private string ResolveGamePath(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
                relative.Split('\\', '/').Any(s => s == ".." || s == "."))
                throw new InvalidDataException($"Caminho inválido no manifesto: {relative}");

            string full = Path.GetFullPath(Path.Combine(m_config.InstallDir, relative));
            if (!full.StartsWith(m_config.InstallDir, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Caminho inválido no manifesto: {relative}");
            return full;
        }

        private string FileUrl(string relative) =>
            m_config.UpdateBaseUrl + "files/" +
            string.Join("/", relative.Split('\\', '/').Select(Uri.EscapeDataString)) + ".gz";

        private string FileUrl(ManifestFile file) => FileUrl(file.Url ?? file.Path);

        private static string Key(string path) => path.Replace('/', '\\').ToLowerInvariant();

        private string CachePath => Path.Combine(m_config.StateDir, "cache.json");

        private void LoadCache()
        {
            try
            {
                if (File.Exists(CachePath))
                    m_cache = Json.Parse<HashCache>(File.ReadAllBytes(CachePath)) ?? new HashCache();
            }
            catch (Exception)
            {
                m_cache = new HashCache(); // corrupt cache: just re-hash
            }

            if (m_cache.Entries == null)
                m_cache.Entries = new Dictionary<string, CacheEntry>();
        }

        private void SaveCache()
        {
            Directory.CreateDirectory(m_config.StateDir);
            string temp = CachePath + ".tmp";
            File.WriteAllBytes(temp, Json.Serialize(m_cache));
            if (File.Exists(CachePath)) File.Delete(CachePath);
            File.Move(temp, CachePath);
        }

        public static string HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20))
                return ToHex(sha.ComputeHash(stream));
        }

        private static string HashBytes(byte[] data)
        {
            using (var sha = SHA256.Create())
                return ToHex(sha.ComputeHash(data));
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static HttpClient CreateHttpClient()
        {
            // Decompression is for the JSON (the manifest is ~1.6 MB raw); the .gz downloads are
            // served without Content-Encoding, so their bytes and Range offsets are untouched.
            var handler = new HttpClientHandler {AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate};
            var client = new HttpClient(handler) {Timeout = TimeSpan.FromSeconds(60)};
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"OriginsLauncher/{CurrentVersion}");
            client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue {NoCache = true};
            return client;
        }
    }

    /// <summary>Reads the pack parts one after the other as a single stream (forward only).</summary>
    internal sealed class PartsReader : Stream
    {
        private readonly List<string> m_paths;
        private int m_index = -1;
        private FileStream m_current;

        public PartsReader(List<string> paths)
        {
            m_paths = paths;
        }

        private bool NextPart()
        {
            m_current?.Dispose();
            m_current = null;
            if (++m_index >= m_paths.Count) return false;
            m_current = new FileStream(m_paths[m_index], FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            return true;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (true)
            {
                if (m_current == null && !NextPart()) return 0;
                int read = m_current.Read(buffer, offset, count);
                if (read > 0) return read;
                m_current.Dispose();
                m_current = null;
            }
        }

        /// <summary>Moves forward <paramref name="bytes" /> without reading them.</summary>
        public void Skip(long bytes)
        {
            while (bytes > 0)
            {
                if (m_current == null && !NextPart()) throw new EndOfStreamException();
                long left = m_current.Length - m_current.Position;
                if (left <= 0)
                {
                    m_current.Dispose();
                    m_current = null;
                    continue;
                }

                long step = Math.Min(left, bytes);
                m_current.Seek(step, SeekOrigin.Current);
                bytes -= step;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) m_current?.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>One file's gzip inside the pack: at most <c>length</c> bytes of the parts reader.</summary>
    internal sealed class LimitedStream : Stream
    {
        private readonly PartsReader m_inner;
        private long m_left;

        public LimitedStream(PartsReader inner, long length)
        {
            m_inner = inner;
            m_left = length;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (m_left <= 0) return 0;
            int read = m_inner.Read(buffer, offset, (int) Math.Min(count, m_left));
            if (read == 0) throw new EndOfStreamException();
            m_left -= read;
            return read;
        }

        /// <summary>Skips what the decompressor did not read (the parts reader stays at the next file).</summary>
        public void SkipRest()
        {
            if (m_left > 0) m_inner.Skip(m_left);
            m_left = 0;
        }

        protected override void Dispose(bool disposing)
        {
            // the parts reader belongs to the caller
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal sealed class GameRunningException : IOException
    {
        public GameRunningException(string path)
            : base(string.Format(L.S.CannotReplace, path))
        {
        }
    }

    /// <summary>
    ///     Tracks bytes for the progress bar and a smoothed download speed. Shared by the
    ///     download workers, so every member locks.
    /// </summary>
    internal sealed class TransferMeter
    {
        private readonly object m_lock = new object();
        private readonly long m_total;
        private long m_done;
        private readonly Stopwatch m_clock = Stopwatch.StartNew();
        private long m_windowBytes;
        private double m_windowStart;
        private double m_speed;
        private double m_lastReport = -1;

        public TransferMeter(long total)
        {
            m_total = Math.Max(1, total);
        }

        public double Fraction
        {
            get { lock (m_lock) return Math.Max(0, Math.Min(1.0, (double) m_done / m_total)); }
        }

        /// <summary>Bytes of a partial download already on disk: progress, but not speed.</summary>
        public void AddExisting(long bytes)
        {
            lock (m_lock) m_done += bytes;
        }

        /// <summary>Takes back the bytes of a failed attempt (the part file is retried or discarded).</summary>
        public void Remove(long bytes)
        {
            lock (m_lock) m_done -= bytes;
        }

        public void Add(long bytes)
        {
            lock (m_lock)
            {
                m_done += bytes;
                m_windowBytes += bytes;
                double now = m_clock.Elapsed.TotalSeconds;
                if (now - m_windowStart >= 1.0)
                {
                    double current = m_windowBytes / (now - m_windowStart);
                    m_speed = m_speed <= 0 ? current : m_speed * 0.6 + current * 0.4;
                    m_windowBytes = 0;
                    m_windowStart = now;
                }
            }
        }

        /// <summary>Limits progress updates to ~10 per second so the UI thread is not flooded.</summary>
        public bool ShouldReport(bool force)
        {
            lock (m_lock)
            {
                double now = m_clock.Elapsed.TotalSeconds;
                if (!force && now - m_lastReport < 0.1) return false;
                m_lastReport = now;
                return true;
            }
        }

        public string Describe()
        {
            lock (m_lock)
            {
                string text = string.Format(L.S.Of, Format(m_done), Format(m_total));
                if (m_speed > 0)
                {
                    text += $"  •  {Format((long) m_speed)}/s";
                    double seconds = (m_total - m_done) / m_speed;
                    if (seconds > 1)
                        text += "  •  " + string.Format(L.S.Remaining, $"{(int) (seconds / 3600)}:{TimeSpan.FromSeconds(seconds):mm\\:ss}");
                }

                return text;
            }
        }

        public static string Format(long bytes)
        {
            if (bytes >= 1L << 30) return $"{bytes / (double) (1L << 30):0.00} GB";
            if (bytes >= 1L << 20) return $"{bytes / (double) (1L << 20):0.0} MB";
            if (bytes >= 1L << 10) return $"{bytes / 1024.0:0} KB";
            return $"{bytes} B";
        }
    }
}
