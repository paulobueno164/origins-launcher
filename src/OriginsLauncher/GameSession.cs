using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace OriginsLauncher
{
    /// <summary>
    ///     One play session. The 6609 client has its own native resolutions and full screen (no scaler: the 5095
    ///     launcher's Magpie full screen is gone since 2.0.0). After the game's graphics-mode change prompt the
    ///     client saves the new mode (ini\GameSetUp.ini [ScreenMode] ScreenModeRecord), closes and runs TQ's
    ///     play.exe, which Origins does not ship; the session starts the game again instead (each start writes a
    ///     fresh login token and the account server, see <see cref="Updater.StartGame" />).
    /// </summary>
    internal sealed class GameSession
    {
        private readonly Config m_config;
        private readonly Func<Process> m_startGame;

        public GameSession(Config config, Func<Process> startGame)
        {
            m_config = config;
            m_startGame = startGame;
        }

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

                await Task.Run(() => game.WaitForExit());

                int after = ReadScreenMode();
                if (after == mode) return;
                Log.Write($"screen mode {mode} -> {after}: starting the game again");
            }
        }

        private int ReadScreenMode() => (int) GetPrivateProfileInt("ScreenMode", "ScreenModeRecord", -1, SetupIni);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetPrivateProfileInt(string section, string key, int defaultValue, string file);
    }
}
