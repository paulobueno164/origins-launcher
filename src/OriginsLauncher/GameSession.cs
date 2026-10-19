using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace OriginsLauncher
{
    /// <summary>
    ///     One play session. The 6609 client changes resolution live (Settings window, no restart), but its "graphics
    ///     mode" switch (the NewLogin dialog's Restart button, handler 0x4DE013) writes ini\GameSetUp.ini
    ///     [GameMode] GameModeRecord, closes and runs TQ's play.exe, which Origins does not ship; the session starts the
    ///     game again instead (each start writes a fresh login token, the account server and the launcher's screen
    ///     choice, see <see cref="Updater.StartGame" />). Until 2.1.0 the session watched [ScreenMode]
    ///     ScreenModeRecord, which the live resolution change also writes: one unwanted start after quitting.
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

        /// <summary>Plays until the game closes without having switched the graphics mode.</summary>
        public async Task RunAsync()
        {
            for (int run = 0; run < 10; run++) // a mode switch restarts the game; never loop forever
            {
                int mode = ReadGameMode();
                var game = m_startGame();
                if (game == null) return;
                Log.Write($"game started pid={game.Id} gameMode={mode}");

                await Task.Run(() => game.WaitForExit());

                int after = ReadGameMode();
                if (after == mode) return;
                Log.Write($"graphics mode {mode} -> {after}: starting the game again");
            }
        }

        private int ReadGameMode() => (int) GetPrivateProfileInt("GameMode", "GameModeRecord", -1, SetupIni);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetPrivateProfileInt(string section, string key, int defaultValue, string file);
    }
}
