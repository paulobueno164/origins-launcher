using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace OriginsLauncher
{
    /// <summary>
    ///     One play session. The 5095 client only renders 1024x768 (or 800x600) and its own "Full Screen"
    ///     switched the desktop to 16-bit colour, which Windows 8+ refuses. Full screen is done here
    ///     instead: when the saved mode (ini\GameSetUp.ini) is a full screen one, Magpie (GPL-3, shipped
    ///     in the game's Magpie folder) scales the game window to the monitor with an FSR filter, keeping
    ///     4:3. The game's Window/Full Screen buttons save the new mode and close the game; the session
    ///     then starts it again in that mode.
    /// </summary>
    internal sealed class GameSession
    {
        private const string MagpieFolder = "Magpie";
        private const string MagpieTemplate = "origins-config.json";

        private readonly Config m_config;
        private readonly Func<Process> m_startGame;
        private readonly Func<bool> m_fillScreen;

        /// <param name="fillScreen">the player's "fill the whole monitor" option, read each time Magpie starts</param>
        public GameSession(Config config, Func<Process> startGame, Func<bool> fillScreen = null)
        {
            m_config = config;
            m_startGame = startGame;
            m_fillScreen = fillScreen ?? (() => false);
        }

        private string MagpieDir => Path.Combine(m_config.InstallDir, MagpieFolder);
        private string SetupIni => Path.Combine(m_config.InstallDir, "ini", "GameSetUp.ini");

        /// <summary>Plays until the game closes without having changed the screen mode.</summary>
        public async Task RunAsync()
        {
            for (int run = 0; run < 10; run++) // a mode change restarts the game; never loop forever
            {
                int mode = ReadScreenMode();
                var game = m_startGame();
                if (game == null) return;
                Log.Write($"game started pid={game.Id} screenMode={mode}");

                if (IsFullScreen(mode))
                {
                    if (CanScale(out string why))
                        await StartScalerAsync(game);
                    else
                        Log.Write($"full screen unavailable: {why}");
                }

                await Task.Run(() => game.WaitForExit());
                StopScalerIfIdle();

                int after = ReadScreenMode();
                if (after == mode) return;
                Log.Write($"screen mode {mode} -> {after}: starting the game again");
            }
        }

        /// <summary>ScreenModeRecord: 0 800x600 window, 1 800x600 full, 2 1024x768 window, 3 1024x768 full.</summary>
        private int ReadScreenMode() => (int) GetPrivateProfileInt("ScreenMode", "ScreenModeRecord", 2, SetupIni);

        private static bool IsFullScreen(int mode) => mode == 1 || mode == 3;

        private bool CanScale(out string why)
        {
            why = null;
            if (!File.Exists(Path.Combine(MagpieDir, "Magpie.exe")) || !File.Exists(Path.Combine(MagpieDir, MagpieTemplate)))
                why = "Magpie is not installed";
            else if (!Environment.Is64BitOperatingSystem)
                why = "32-bit Windows";
            else if (Environment.OSVersion.Version.Major < 10 || Environment.OSVersion.Version.Build < 18362)
                why = $"Windows {Environment.OSVersion.Version} (Magpie needs Windows 10 1903+)";
            return why == null;
        }

        /// <summary>
        ///     Waits for the game window, then points Magpie's profile at it (exact exe path and window
        ///     class, as Magpie matches them) and starts Magpie in the tray. Magpie scales the window
        ///     whenever it is in the foreground (also right after it starts).
        /// </summary>
        private async Task StartScalerAsync(Process game)
        {
            try
            {
                IntPtr window = IntPtr.Zero;
                for (int i = 0; i < 240 && window == IntPtr.Zero; i++)
                {
                    if (game.HasExited) return;
                    window = FindGameWindow(game.Id);
                    if (window == IntPtr.Zero) await Task.Delay(500);
                }

                if (window == IntPtr.Zero)
                {
                    Log.Write("full screen: game window not found");
                    return;
                }

                string path = ProcessPath(game);
                string className = ClassName(window);
                string template = File.ReadAllText(Path.Combine(MagpieDir, MagpieTemplate), Encoding.UTF8);
                string json = template.Replace("{{GAME_PATH}}", JsonEscape(path)).Replace("{{GAME_CLASS}}", JsonEscape(className));
                // Magpie's scaling types: 1 = Fit (keeps 4:3, black bars on wide monitors), 3 = Fill (stretches)
                bool fill = m_fillScreen();
                if (fill)
                    json = json.Replace("\"scalingType\": 1", "\"scalingType\": 3");
                Directory.CreateDirectory(Path.Combine(MagpieDir, "config"));
                File.WriteAllText(Path.Combine(MagpieDir, "config", "config.json"), json, new UTF8Encoding(false));

                if (MagpieProcesses().Any())
                {
                    // another client of this folder is already scaled; Magpie reads its profile at start
                    Log.Write("full screen: Magpie already running");
                    return;
                }

                var magpie = Process.Start(new ProcessStartInfo(Path.Combine(MagpieDir, "Magpie.exe"), "-t")
                {
                    WorkingDirectory = MagpieDir,
                    UseShellExecute = false
                });
                Log.Write($"full screen: Magpie pid={magpie?.Id} window class={className} fill={fill}");
            }
            catch (Exception ex)
            {
                Log.Write($"full screen failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Closes Magpie once no game from this folder is left running (players run several clients).</summary>
        private void StopScalerIfIdle()
        {
            try
            {
                string gameExe = Path.Combine(m_config.InstallDir, "Conquer.exe");
                if (Process.GetProcessesByName("Conquer").Any(p => SamePath(SafePath(p), gameExe)))
                    return;

                uint quit = RegisterWindowMessage("WM_MAGPIE_QUIT");
                foreach (var magpie in MagpieProcesses())
                {
                    // Magpie's message loop quits on WM_MAGPIE_QUIT (and removes its tray icon)
                    foreach (var hwnd in TopLevelWindows(magpie.Id))
                        PostMessage(hwnd, quit, IntPtr.Zero, IntPtr.Zero);
                    if (!magpie.WaitForExit(5000))
                        magpie.Kill();
                    Log.Write($"full screen: Magpie pid={magpie.Id} closed");
                }
            }
            catch (Exception ex)
            {
                Log.Write($"closing Magpie failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private IEnumerable<Process> MagpieProcesses()
        {
            string exe = Path.Combine(MagpieDir, "Magpie.exe");
            return Process.GetProcessesByName("Magpie").Where(p => SamePath(SafePath(p), exe)).ToList();
        }

        private static bool SamePath(string a, string b) =>
            a != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        private static string SafePath(Process process)
        {
            try { return ProcessPath(process); }
            catch (Exception) { return null; } // exited, or a process of another user
        }

        /// <summary>The image path as Windows reports it (what Magpie compares with its profile).</summary>
        private static string ProcessPath(Process process)
        {
            IntPtr handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, process.Id);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var buffer = new StringBuilder(1024);
                int size = buffer.Capacity;
                return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        /// <summary>The client's main window: the visible MFC frame ("Afx:...") of at least 640x480.</summary>
        private static IntPtr FindGameWindow(int pid)
        {
            foreach (var hwnd in TopLevelWindows(pid))
            {
                if (!IsWindowVisible(hwnd) || !ClassName(hwnd).StartsWith("Afx:", StringComparison.Ordinal)) continue;
                if (GetWindowRect(hwnd, out var r) && r.Right - r.Left >= 640 && r.Bottom - r.Top >= 480)
                    return hwnd;
            }

            return IntPtr.Zero;
        }

        private static List<IntPtr> TopLevelWindows(int pid)
        {
            var result = new List<IntPtr>();
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out uint owner);
                if (owner == pid) result.Add(hwnd);
                return true;
            }, IntPtr.Zero);
            return result;
        }

        private static string ClassName(IntPtr hwnd)
        {
            var name = new StringBuilder(256);
            GetClassName(hwnd, name, name.Capacity);
            return name.ToString();
        }

        private static string JsonEscape(string value)
        {
            var sb = new StringBuilder();
            foreach (char c in value ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int) c);
                else sb.Append(c);
            }

            return sb.ToString();
        }

        // ------------------------------------------------------------------ Win32

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string name);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetPrivateProfileInt(string section, string key, int defaultValue, string file);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
