using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace OriginsLauncher
{
    internal static class AtomicFile
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string source, string target, uint flags);

        public static byte[] ReadAllBytes(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                return memory.ToArray();
            }
        }

        public static void WriteAllBytes(string path, byte[] data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temp = path + "." + Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(data, 0, data.Length);
                    stream.Flush(true);
                }
                for (int attempt = 0; ; attempt++)
                {
                    if (MoveFileEx(temp, path, 1 | 8)) return; // REPLACE_EXISTING | WRITE_THROUGH
                    int error = Marshal.GetLastWin32Error();
                    if (attempt >= 49 || error != 32 && error != 33 && error != 5)
                        throw new IOException("Could not atomically save " + Path.GetFileName(path), new Win32Exception(error));
                    Thread.Sleep(20);
                }
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        // A file handle may be disposed after an await on a different thread.
        // The OS releases the handle if a launcher exits during an update.
        public static async Task<IDisposable> AcquireAsync(string path, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException ex) when ((ex.HResult & 0xffff) == 32 || (ex.HResult & 0xffff) == 33)
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
            }
        }
    }
}
