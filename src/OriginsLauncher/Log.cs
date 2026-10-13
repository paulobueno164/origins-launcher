using System;
using System.IO;
using System.Text;

namespace OriginsLauncher
{
    /// <summary>
    ///     .originsupdate\launcher.log: what the launcher did and why it failed, so a player can
    ///     send it to support. Restarted when it grows past 512 KB.
    /// </summary>
    internal static class Log
    {
        private static readonly object Lock = new object();
        private static string s_path;

        public static void Init(Config config)
        {
            s_path = Path.Combine(config.StateDir, "launcher.log");
        }

        public static void Write(string message)
        {
            if (s_path == null) return;
            lock (Lock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(s_path));
                    var info = new FileInfo(s_path);
                    if (info.Exists && info.Length > 512 * 1024)
                        File.Delete(s_path);
                    File.AppendAllText(s_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}", Encoding.UTF8);
                }
                catch (Exception)
                {
                    // logging must never break the launcher
                }
            }
        }
    }
}
