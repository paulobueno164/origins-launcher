using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace OriginsLauncher
{
    /// <summary>
    ///     Update manifest published at /updates/manifest.json by tools/UpdateBuilder.
    ///     Every game file is listed with its size and SHA-256; the download is files/&lt;path&gt;.gz.
    /// </summary>
    [DataContract]
    public sealed class Manifest
    {
        [DataMember(Name = "version")] public int Version { get; set; }
        [DataMember(Name = "generated")] public string Generated { get; set; }
        [DataMember(Name = "launcherVersion")] public string LauncherVersion { get; set; }
        [DataMember(Name = "launcherSha256")] public string LauncherSha256 { get; set; }
        [DataMember(Name = "launcherUrl")] public string LauncherUrl { get; set; }
        [DataMember(Name = "gameExe")] public string GameExe { get; set; }
        [DataMember(Name = "gameArgs")] public string GameArgs { get; set; }
        [DataMember(Name = "files")] public List<ManifestFile> Files { get; set; } = new List<ManifestFile>();

        /// <summary>
        ///     Language packs ("pt", "fil"): files that replace the English ones of the same path
        ///     (translated texts and a Server.dat that logs in through the language's port).
        /// </summary>
        [DataMember(Name = "languages")] public Dictionary<string, List<ManifestFile>> Languages { get; set; }

        /// <summary>
        ///     The whole English client in a few big parts, for a first install: thousands of small
        ///     downloads spend most of their time on round trips. May be older than the manifest;
        ///     only files whose hash still matches are taken from it.
        /// </summary>
        [DataMember(Name = "pack")] public PackInfo Pack { get; set; }

        /// <summary>The file list for <paramref name="language" />: English files with the pack's replacing them.</summary>
        public List<ManifestFile> FilesFor(string language)
        {
            if (Languages == null || !Languages.TryGetValue(language ?? "", out var pack) || pack == null)
                return Files;
            var byPath = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Files) byPath[file.Path] = file;
            foreach (var file in pack) byPath[file.Path] = file;
            return byPath.Values.ToList();
        }
    }

    [DataContract]
    public sealed class ManifestFile
    {
        /// <summary>Path relative to the game folder, with backslashes.</summary>
        [DataMember(Name = "path")] public string Path { get; set; }
        [DataMember(Name = "size")] public long Size { get; set; }
        [DataMember(Name = "sha256")] public string Sha256 { get; set; }
        [DataMember(Name = "gzSize")] public long GzSize { get; set; }

        /// <summary>
        ///     Files the game itself changes (e.g. ini\GameSetUp.ini with the player's settings):
        ///     installed when missing, never overwritten afterwards.
        /// </summary>
        [DataMember(Name = "keep")] public bool Keep { get; set; }

        /// <summary>Download path under files/ without ".gz" when it is not <see cref="Path" /> (language packs).</summary>
        [DataMember(Name = "url")] public string Url { get; set; }
    }

    /// <summary>
    ///     Full-client package: the per-file .gz downloads concatenated in <see cref="PackIndex" />
    ///     order and cut into parts of a few dozen MB (served as pack/&lt;id&gt;.NN.bin).
    /// </summary>
    [DataContract]
    public sealed class PackInfo
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "index")] public string Index { get; set; }
        [DataMember(Name = "indexSha256")] public string IndexSha256 { get; set; }
        [DataMember(Name = "size")] public long Size { get; set; }
        [DataMember(Name = "parts")] public List<PackPart> Parts { get; set; } = new List<PackPart>();
    }

    [DataContract]
    public sealed class PackPart
    {
        [DataMember(Name = "url")] public string Url { get; set; }
        [DataMember(Name = "size")] public long Size { get; set; }
        [DataMember(Name = "sha256")] public string Sha256 { get; set; }
    }

    /// <summary>pack/&lt;id&gt;.index.json: the files of the pack, in order (each is GzSize bytes of gzip).</summary>
    [DataContract]
    public sealed class PackIndex
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "files")] public List<ManifestFile> Files { get; set; } = new List<ManifestFile>();
    }

    [DataContract]
    public sealed class NewsItem
    {
        [DataMember(Name = "title")] public string Title { get; set; }
        [DataMember(Name = "date")] public string Date { get; set; }
        [DataMember(Name = "text")] public string Text { get; set; }
    }

    /// <summary>Per-file hash cache so a normal start does not re-hash 1.3 GB.</summary>
    [DataContract]
    public sealed class HashCache
    {
        [DataMember(Name = "entries")] public Dictionary<string, CacheEntry> Entries { get; set; } =
            new Dictionary<string, CacheEntry>();
    }

    [DataContract]
    public sealed class CacheEntry
    {
        [DataMember(Name = "size")] public long Size { get; set; }
        [DataMember(Name = "ticks")] public long LastWriteTicks { get; set; }
        [DataMember(Name = "sha256")] public string Sha256 { get; set; }
    }

    internal static class Json
    {
        private static readonly DataContractJsonSerializerSettings Settings =
            new DataContractJsonSerializerSettings {UseSimpleDictionaryFormat = true};

        public static T Parse<T>(byte[] utf8)
        {
            using (var ms = new MemoryStream(utf8))
                return (T) new DataContractJsonSerializer(typeof(T), Settings).ReadObject(ms);
        }

        public static byte[] Serialize<T>(T value)
        {
            using (var ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T), Settings).WriteObject(ms, value);
                return ms.ToArray();
            }
        }

        public static T Parse<T>(string text) => Parse<T>(Encoding.UTF8.GetBytes(text));
    }
}
