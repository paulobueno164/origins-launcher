// Publishes a game client folder as a launcher update:
//   <out>/manifest.json                 every file with size + SHA-256 (written last, atomically)
//   <out>/files/<path>.gz               one gzip per file, reused when the file did not change
//   <out>/launcher/OriginsLauncher.exe  the current launcher, for self-update
//   <out>/pack/<id>.NN.bin + .index.json the files/*.gz of the client concatenated, for first installs
//                                        (--repack forces a new one, --pack-part-mb sets the part size)
//
// Usage: UpdateBuilder --source C:\conquer\client\5095 --out C:\conquer\site-data\updates
//                      --launcher ...\OriginsLauncher.exe [--lang-root C:\conquer\client-lang] [--game-args blacknull] [--dry-run]
//   <out>/files/lang/<code>/<path>.gz   language packs: translated files replacing <path> (manifest "languages")

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

var options = ParseArgs(args);
string source = Path.GetFullPath(Require(options, "source"));
string output = Path.GetFullPath(Require(options, "out"));
string launcher = Path.GetFullPath(Require(options, "launcher"));
string gameArgs = options.GetValueOrDefault("game-args", "blacknull");
bool dryRun = options.ContainsKey("dry-run");

if (!File.Exists(Path.Combine(source, "Conquer.exe"))) Fail($"{source} has no Conquer.exe");
if (!File.Exists(launcher)) Fail($"launcher not found: {launcher}");

string filesDir = Path.Combine(output, "files");
string manifestPath = Path.Combine(output, "manifest.json");
var json = new JsonSerializerOptions
{
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
};

Manifest? previous = File.Exists(manifestPath)
    ? JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(manifestPath), json)
    : null;
var previousFiles = previous?.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);

// ---------------------------------------------------------------- game files

var paths = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
    .Select(full => Path.GetRelativePath(source, full))
    .Where(rel => !Excluded(rel))
    .OrderBy(rel => rel, StringComparer.OrdinalIgnoreCase)
    .ToList();

int reused = 0, compressed = 0;
long rawBytes = 0, gzBytes = 0;
var stopwatch = Stopwatch.StartNew();

// Compresses every file of <dir> into files/<prefix><path>.gz, reusing the gz of unchanged files.
async Task<ManifestFile[]> PackAsync(string dir, List<string> rels, string prefix, Dictionary<string, ManifestFile> before, bool keepFlags)
{
    var result = new ManifestFile[rels.Count];
    await Parallel.ForEachAsync(Enumerable.Range(0, rels.Count),
        new ParallelOptions {MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount)},
        async (i, ct) =>
        {
            string rel = rels[i];
            string full = Path.Combine(dir, rel);
            string gz = Path.Combine(filesDir, prefix.Replace('/', Path.DirectorySeparatorChar) + rel + ".gz");
            string sha = await HashAsync(full, ct);
            long size = new FileInfo(full).Length;

            long gzSize;
            if (before.TryGetValue(rel, out var old) && old.Sha256 == sha && old.Size == size && old.GzSize > 0 &&
                File.Exists(gz) && new FileInfo(gz).Length == old.GzSize)
            {
                gzSize = old.GzSize;
                Interlocked.Increment(ref reused);
            }
            else if (dryRun)
            {
                gzSize = 0;
                Interlocked.Increment(ref compressed);
            }
            else
            {
                gzSize = await CompressAsync(full, gz, ct);
                Interlocked.Increment(ref compressed);
            }

            Interlocked.Add(ref rawBytes, size);
            Interlocked.Add(ref gzBytes, gzSize);
            result[i] = new ManifestFile
            {
                Path = rel, Size = size, Sha256 = sha, GzSize = gzSize, Keep = keepFlags && KeepFiles.Contains(rel),
                Url = prefix.Length > 0 ? prefix + rel.Replace('\\', '/') : null
            };
        });
    return result;
}

var entries = await PackAsync(source, paths, "", previousFiles, true);
var live = new HashSet<string>(paths.Select(p => Path.Combine(filesDir, p + ".gz")), StringComparer.OrdinalIgnoreCase);

// ---------------------------------------------------------------- language packs
// <lang-root>\<code>\<path>: translated files that replace the English <path> for players of that language.

var languages = new Dictionary<string, List<ManifestFile>>();
string? langRoot = options.GetValueOrDefault("lang-root");
if (langRoot != null && Directory.Exists(langRoot))
{
    foreach (var langDir in Directory.GetDirectories(langRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
    {
        string code = Path.GetFileName(langDir).ToLowerInvariant();
        var rels = Directory.EnumerateFiles(langDir, "*", SearchOption.AllDirectories)
            .Select(full => Path.GetRelativePath(langDir, full))
            .OrderBy(rel => rel, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var unknown = rels.Where(rel => !paths.Contains(rel, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0) Fail($"language {code} has files the client does not: {string.Join(", ", unknown)}");
        var before = previous?.Languages?.GetValueOrDefault(code)?.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase)
                     ?? new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
        languages[code] = (await PackAsync(langDir, rels, $"lang/{code}/", before, false)).ToList();
        foreach (var rel in rels) live.Add(Path.Combine(filesDir, "lang", code, rel + ".gz"));
    }
}

// Remove downloads of files that are no longer part of the client.
int removed = 0;
if (Directory.Exists(filesDir) && !dryRun)
{
    foreach (var stale in Directory.EnumerateFiles(filesDir, "*", SearchOption.AllDirectories).Where(f => !live.Contains(f)).ToList())
    {
        File.Delete(stale);
        removed++;
    }
}

// ---------------------------------------------------------------- full-client pack
// The .gz downloads of the English client concatenated and cut into parts: a first install gets a
// few big files instead of ~10k small ones (each small one costs round trips). Rebuilt with
// --repack, when there is none, or when more than 5% of the client changed since it was built; a
// stale pack still serves every unchanged file (the launcher checks each hash).

string packDir = Path.Combine(output, "pack");
PackInfo? pack = previous?.Pack;
long staleBytes = 0;
bool packValid = false;
if (pack != null)
{
    string indexFile = Path.Combine(output, pack.Index.Replace('/', Path.DirectorySeparatorChar));
    packValid = File.Exists(indexFile) &&
                pack.Parts.All(p => File.Exists(Path.Combine(output, p.Url.Replace('/', Path.DirectorySeparatorChar))) &&
                                    new FileInfo(Path.Combine(output, p.Url.Replace('/', Path.DirectorySeparatorChar))).Length == p.Size);
    if (packValid)
    {
        var packed = JsonSerializer.Deserialize<PackIndex>(File.ReadAllBytes(indexFile), json)!.Files
            .ToDictionary(f => f.Path, f => f.Sha256, StringComparer.OrdinalIgnoreCase);
        staleBytes = entries.Where(e => !packed.TryGetValue(e.Path, out var sha) || sha != e.Sha256).Sum(e => e.GzSize);
    }
}

bool repack = !dryRun && (options.ContainsKey("repack") || !packValid || staleBytes > gzBytes * 0.05);
if (repack)
{
    long partLimit = long.Parse(options.GetValueOrDefault("pack-part-mb", "32")) << 20;
    string id = "client-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
    Directory.CreateDirectory(packDir);
    var parts = new List<PackPart>();
    FileStream? part = null;
    IncrementalHash? partHash = null;

    void ClosePart()
    {
        if (part == null) return;
        long size = part.Length;
        part.Dispose();
        parts.Add(new PackPart
        {
            Url = $"pack/{id}.{parts.Count:D2}.bin", Size = size,
            Sha256 = Convert.ToHexStringLower(partHash!.GetHashAndReset())
        });
        part = null;
    }

    var buffer = new byte[1 << 20];
    foreach (var entry in entries)
    {
        using var input = File.OpenRead(Path.Combine(filesDir, entry.Path + ".gz"));
        if (input.Length != entry.GzSize) Fail($"{entry.Path}.gz changed while packing");
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            int offset = 0;
            while (offset < read)
            {
                if (part == null)
                {
                    part = File.Create(Path.Combine(packDir, $"{id}.{parts.Count:D2}.bin"));
                    partHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                }

                int take = (int) Math.Min(read - offset, partLimit - part.Length);
                part.Write(buffer, offset, take);
                partHash!.AppendData(buffer, offset, take);
                offset += take;
                if (part.Length == partLimit) ClosePart();
            }
        }
    }

    ClosePart();

    byte[] index = JsonSerializer.SerializeToUtf8Bytes(new PackIndex
    {
        Id = id,
        Files = entries.Select(e => new ManifestFile {Path = e.Path, Size = e.Size, Sha256 = e.Sha256, GzSize = e.GzSize}).ToList()
    }, json);
    File.WriteAllBytes(Path.Combine(packDir, $"{id}.index.json"), index);
    pack = new PackInfo
    {
        Id = id, Index = $"pack/{id}.index.json", IndexSha256 = Convert.ToHexStringLower(SHA256.HashData(index)),
        Size = parts.Sum(p => p.Size), Parts = parts
    };
    staleBytes = 0;
}

// ---------------------------------------------------------------- launcher

string launcherSha = await HashAsync(launcher, CancellationToken.None);
var launcherInfo = FileVersionInfo.GetVersionInfo(launcher);
string launcherVersion = new Version(launcherInfo.FileMajorPart, launcherInfo.FileMinorPart, launcherInfo.FileBuildPart).ToString();
string launcherTarget = Path.Combine(output, "launcher", "OriginsLauncher.exe");
if (!dryRun && (!File.Exists(launcherTarget) || await HashAsync(launcherTarget, CancellationToken.None) != launcherSha))
{
    Directory.CreateDirectory(Path.GetDirectoryName(launcherTarget)!);
    File.Copy(launcher, launcherTarget + ".tmp", true);
    File.Move(launcherTarget + ".tmp", launcherTarget, true);
}

// ---------------------------------------------------------------- manifest

var manifest = new Manifest
{
    Generated = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
    LauncherVersion = launcherVersion,
    LauncherSha256 = launcherSha,
    LauncherUrl = "launcher/OriginsLauncher.exe",
    GameExe = "Conquer.exe",
    GameArgs = gameArgs,
    Files = entries.ToList(),
    Languages = languages.Count > 0 ? languages : null,
    Pack = pack
};

bool changed = previous == null ||
               previous.LauncherSha256 != manifest.LauncherSha256 ||
               previous.GameExe != manifest.GameExe || previous.GameArgs != manifest.GameArgs ||
               previous.Files.Count != manifest.Files.Count ||
               previous.Files.Zip(manifest.Files).Any(pair =>
                   !string.Equals(pair.First.Path, pair.Second.Path, StringComparison.OrdinalIgnoreCase) ||
                   pair.First.Sha256 != pair.Second.Sha256 || pair.First.Keep != pair.Second.Keep) ||
               LanguagesKey(previous.Languages) != LanguagesKey(manifest.Languages);
manifest.Version = previous == null ? 1 : previous.Version + (changed ? 1 : 0);
if (!changed) manifest.Generated = previous!.Generated;

if (!dryRun)
{
    Directory.CreateDirectory(output);
    File.WriteAllBytes(manifestPath + ".tmp", JsonSerializer.SerializeToUtf8Bytes(manifest, json));
    File.Move(manifestPath + ".tmp", manifestPath, true);

    // the old pack goes only now that the manifest points to the new one (a launcher still
    // downloading it falls back to single files)
    if (pack != null && Directory.Exists(packDir))
        foreach (var old in Directory.GetFiles(packDir).Where(f => !Path.GetFileName(f).StartsWith(pack.Id + ".")))
            File.Delete(old);
}

Console.WriteLine($"{(dryRun ? "[dry-run] " : "")}client version {manifest.Version}{(changed ? " (new)" : " (unchanged)")}, launcher {launcherVersion}");
if (pack != null)
    Console.WriteLine($"pack {pack.Id}{(repack ? " (new)" : "")}: {pack.Parts.Count} parts, {pack.Size / 1048576.0:N1} MB, " +
                      $"{staleBytes / 1048576.0:N1} MB of the client changed since it was built");
Console.WriteLine($"{paths.Count} files, {rawBytes / 1048576.0:N1} MB -> {gzBytes / 1048576.0:N1} MB gzip; " +
                  $"{compressed} compressed, {reused} reused, {removed} stale removed, {stopwatch.Elapsed.TotalSeconds:N0}s");
return 0;

// ---------------------------------------------------------------- helpers

static string LanguagesKey(Dictionary<string, List<ManifestFile>>? packs) =>
    packs == null ? "" : string.Join("|", packs.OrderBy(p => p.Key).SelectMany(p => p.Value.OrderBy(f => f.Path).Select(f => $"{p.Key}:{f.Path}:{f.Sha256}")));


static bool Excluded(string rel)
{
    string top = rel.Split(Path.DirectorySeparatorChar)[0];
    if (ExcludedDirs.Contains(top) && rel.Contains(Path.DirectorySeparatorChar)) return true;
    string name = Path.GetFileName(rel);
    return ExcludedFiles.Contains(rel) ||
           name.EndsWith(".originstmp", StringComparison.OrdinalIgnoreCase) ||
           name.StartsWith("OriginsLauncher", StringComparison.OrdinalIgnoreCase);
}

static async Task<string> HashAsync(string path, CancellationToken ct)
{
    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
    return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
}

static async Task<long> CompressAsync(string source, string target, CancellationToken ct)
{
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    string temp = target + ".tmp";
    if (new FileInfo(source).Length == 0)
    {
        // GZipStream writes nothing at all for empty input; publish a real (20-byte) empty gzip.
        await File.WriteAllBytesAsync(temp, [0x1f, 0x8b, 8, 0, 0, 0, 0, 0, 0, 0xff, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0], ct);
    }
    else
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
        await using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
        await using var gzip = new GZipStream(output, CompressionLevel.SmallestSize);
        await input.CopyToAsync(gzip, ct);
    }

    File.Move(temp, target, true);
    return new FileInfo(target).Length;
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--")) Fail($"unexpected argument {args[i]}");
        string key = args[i][2..];
        result[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "";
    }

    return result;
}

static string Require(Dictionary<string, string> options, string key) =>
    options.TryGetValue(key, out var value) && value.Length > 0 ? value : Fail($"missing --{key}");

static string Fail(string message)
{
    Console.Error.WriteLine($"UpdateBuilder: {message}");
    Environment.Exit(1);
    return "";
}

partial class Program
{
    // Logs, TQ's own patcher and shortcuts to TQ sites never ship.
    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase) {"LOG", "debug", "AutoPatch"};

    private static readonly HashSet<string> ExcludedFiles = new(StringComparer.OrdinalIgnoreCase)
        {"AutoPatch.exe", "AutoPatch.dat", "1.url", "2.url", @"Help\credit.exe"}; // credit.exe: TQ's credits program, unused

    // Files the game writes with the player's own settings: installed once, never overwritten.
    private static readonly HashSet<string> KeepFiles = new(StringComparer.OrdinalIgnoreCase)
        {@"ini\GameSetUp.ini", @"zftqat\krt.ini"};
}

internal sealed class Manifest
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("generated")] public string Generated { get; set; } = "";
    [JsonPropertyName("launcherVersion")] public string LauncherVersion { get; set; } = "";
    [JsonPropertyName("launcherSha256")] public string LauncherSha256 { get; set; } = "";
    [JsonPropertyName("launcherUrl")] public string LauncherUrl { get; set; } = "";
    [JsonPropertyName("gameExe")] public string GameExe { get; set; } = "";
    [JsonPropertyName("gameArgs")] public string GameArgs { get; set; } = "";
    [JsonPropertyName("files")] public List<ManifestFile> Files { get; set; } = new();
    [JsonPropertyName("languages")] public Dictionary<string, List<ManifestFile>>? Languages { get; set; }
    [JsonPropertyName("pack")] public PackInfo? Pack { get; set; }
}

internal sealed class PackInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("index")] public string Index { get; set; } = "";
    [JsonPropertyName("indexSha256")] public string IndexSha256 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("parts")] public List<PackPart> Parts { get; set; } = new();
}

internal sealed class PackPart
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

internal sealed class PackIndex
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("files")] public List<ManifestFile> Files { get; set; } = new();
}

internal sealed class ManifestFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("gzSize")] public long GzSize { get; set; }
    [JsonPropertyName("keep")] public bool Keep { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}
