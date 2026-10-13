using System;
using System.IO;
using System.Linq;

namespace OriginsLauncher
{
    /// <summary>
    ///     Where to fetch updates from and where the game lives. Defaults point to the production
    ///     site and to the launcher's own folder; "--base-url=" and "--dir=" override them (tests).
    /// </summary>
    internal sealed class Config
    {
        public string SiteUrl { get; private set; } = "https://originsco.online/";
        public string UpdateBaseUrl { get; private set; } = "https://originsco.online/updates/";
        public string InstallDir { get; private set; } = AppDomain.CurrentDomain.BaseDirectory;

        /// <summary>Set by "--dir=": the folder was chosen on purpose, so no install prompt or shortcuts.</summary>
        public bool ExplicitDir { get; private set; }

        public const string AfterUpdateArg = "--after-update";

        /// <summary>Started by the self-update script: never self-update twice in a row.</summary>
        public bool AfterUpdate { get; private set; }

        public string NewsUrl => SiteUrl + "api/news";
        public string ManifestUrl => UpdateBaseUrl + "manifest.json";

        /// <summary>Hidden working folder for partial downloads and the hash cache.</summary>
        public string StateDir => Path.Combine(InstallDir, ".originsupdate");

        public static Config FromArgs(string[] args)
        {
            var config = new Config();
            foreach (var arg in args ?? Array.Empty<string>())
            {
                if (arg.StartsWith("--base-url=", StringComparison.OrdinalIgnoreCase))
                    config.UpdateBaseUrl = WithSlash(arg.Substring("--base-url=".Length));
                else if (arg.StartsWith("--site-url=", StringComparison.OrdinalIgnoreCase))
                    config.SiteUrl = WithSlash(arg.Substring("--site-url=".Length));
                else if (arg.Equals(AfterUpdateArg, StringComparison.OrdinalIgnoreCase))
                    config.AfterUpdate = true;
                else if (arg.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase))
                {
                    config.InstallDir = Path.GetFullPath(arg.Substring("--dir=".Length));
                    config.ExplicitDir = true;
                }
            }

            config.InstallDir = WithSeparator(config.InstallDir);
            return config;
        }

        private static string WithSlash(string url) => url.EndsWith("/") ? url : url + "/";

        private static string WithSeparator(string dir) =>
            new[] {Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar}.Contains(dir.Last())
                ? dir
                : dir + Path.DirectorySeparatorChar;
    }
}
