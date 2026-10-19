using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace OriginsLauncher
{
    /// <summary>
    ///     The game's screen, chosen in the launcher (owner 19/10: full screen without black bars, options for every
    ///     kind of monitor). Before each start the choice goes to ini\GameSetUp.ini [ScreenMode], which the 6609
    ///     client reads at startup (0x7A174F):
    ///     <list type="bullet">
    ///         <item>
    ///             full screen (default): ScreenModeRecord=4 (custom) at the monitor's size with FullScrType=1, the
    ///             client's borderless window over the whole monitor with no display-mode change (0x6E6B05), so no
    ///             black bars on a 16:9/16:10/21:9 monitor (TQ's "full screen" switches the monitor to 1024x768);
    ///         </item>
    ///         <item>window 1024x768: ScreenModeRecord=2, TQ's own mode;</item>
    ///         <item>window WxH: ScreenModeRecord=4 at that size, FullScrType=1 (a borderless window, centred).</item>
    ///     </list>
    ///     The client accepts mode 4 only up to the desktop size it saved at startup (EnumDisplaySettings), the
    ///     same call used here; the game runs DPI-aware (see <see cref="Updater.StartGame" />), so both see
    ///     physical pixels. Login is always a 1024x768 window; the chosen size applies once in the game.
    /// </summary>
    internal static class ScreenSetup
    {
        public const string Full = "full";

        /// <summary>Window sizes offered besides 1024x768, when they fit the monitor.</summary>
        public static readonly Size[] WindowSizes =
        {
            new Size(1280, 720), new Size(1280, 800), new Size(1366, 768), new Size(1440, 900), new Size(1600, 900),
            new Size(1680, 1050), new Size(1920, 1080), new Size(2560, 1440)
        };

        public static readonly Size Classic = new Size(1024, 768);

        /// <summary>Monitor size (primary display, physical pixels when the caller is DPI-aware).</summary>
        public static Size Desktop()
        {
            var mode = new DEVMODE {dmSize = (short) Marshal.SizeOf(typeof(DEVMODE))};
            if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref mode) && mode.dmPelsWidth > 0 && mode.dmPelsHeight > 0)
                return new Size(mode.dmPelsWidth, mode.dmPelsHeight);
            return Classic;
        }

        /// <summary>The primary display's work area (the desktop minus the taskbar).</summary>
        public static Rectangle WorkArea()
        {
            var r = new RECT();
            return SystemParametersInfo(SPI_GETWORKAREA, 0, ref r, 0)
                ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)
                : new Rectangle(Point.Empty, Desktop());
        }

        /// <summary>
        ///     The window sizes this monitor can show whole: narrower than the monitor and, with TQ's 20-pixel caption
        ///     strip above it, no taller than the work area. 1024x768 is always offered.
        /// </summary>
        public static List<Size> Windows(Size desktop, Rectangle work)
        {
            var list = new List<Size> {Classic};
            list.AddRange(WindowSizes.Where(s => s.Width < desktop.Width && s.Height + 20 <= work.Height));
            return list;
        }

        /// <summary>The window size of <paramref name="choice" /> when it is one this monitor offers; null = full screen
        /// (the choice is "full", missing, unreadable, or a size that no longer fits, e.g. another monitor).</summary>
        public static Size? WindowChoice(string choice, Size desktop, Rectangle work) =>
            TryParse(choice, out var size) && Windows(desktop, work).Contains(size) ? size : (Size?) null;

        /// <summary>"full" (or nothing) / "1024x768" / "WxH" -> the [ScreenMode] values; a size that no longer fits
        /// becomes full screen.</summary>
        public static (int Mode, int Width, int Height) Resolve(string choice, Size desktop, Rectangle work)
        {
            if (WindowChoice(choice, desktop, work) is Size size)
                return size == Classic ? (2, size.Width, size.Height) : (4, size.Width, size.Height);

            return desktop.Width >= Classic.Width && desktop.Height >= Classic.Height
                ? (4, desktop.Width, desktop.Height)
                : (2, Classic.Width, Classic.Height); // a monitor smaller than 1024x768: TQ's window
        }

        public static bool TryParse(string choice, out Size size)
        {
            size = Size.Empty;
            var parts = (choice ?? "").Split('x');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int w) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int h) || w <= 0 || h <= 0)
                return false;
            size = new Size(w, h);
            return true;
        }

        public static string Name(Size size) => $"{size.Width}x{size.Height}";

        /// <summary>Writes the choice to &lt;game&gt;\ini\GameSetUp.ini [ScreenMode]; returns what was written.</summary>
        public static (int Mode, int Width, int Height) Write(string installDir, string choice)
        {
            var r = Resolve(choice, Desktop(), WorkArea());
            string ini = Path.Combine(installDir, "ini", "GameSetUp.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(ini) ?? installDir);
            void Set(string key, int value)
            {
                if (!WritePrivateProfileString("ScreenMode", key, value.ToString(CultureInfo.InvariantCulture), ini))
                    throw new IOException($"{ini}: cannot write [ScreenMode] {key} ({Marshal.GetLastWin32Error()})");
            }

            Set("ScreenModeRecord", r.Mode);
            Set("ScrWidth", r.Width);
            Set("ScrHeight", r.Height);
            Set("FullScrType", 1);
            Log.Write($"screen {choice ?? Full} -> ScreenModeRecord={r.Mode} {r.Width}x{r.Height}");
            return r;
        }

        private const int ENUM_CURRENT_SETTINGS = -1;
        private const int SPI_GETWORKAREA = 0x0030;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(int action, int param, ref RECT rect, int winIni);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool WritePrivateProfileString(string section, string key, string value, string file);
    }
}
