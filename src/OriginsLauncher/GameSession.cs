using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
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
    ///     Since 2.3.0 a watch runs next to each game process (<see cref="ReloginKeeper" />: a login token for the
    ///     game's own login screen after a disconnect), and the start can wait for the site without blocking the
    ///     window's thread (2.2.0 fetched the restart's token with a blocking call there: the restart never came).
    /// </summary>
    internal sealed class GameSession
    {
        private readonly Config m_config;
        private readonly Func<Task<Process>> m_startGame;
        private readonly Func<Process, CancellationToken, Task> m_watch;

        public GameSession(Config config, Func<Process> startGame) : this(config, () => Task.FromResult(startGame()), null)
        {
        }

        public GameSession(Config config, Func<Task<Process>> startGame, Func<Process, CancellationToken, Task> watch)
        {
            m_config = config;
            m_startGame = startGame;
            m_watch = watch;
        }

        private string SetupIni => Path.Combine(m_config.InstallDir, "ini", "GameSetUp.ini");

        /// <summary>Plays until the game closes without having switched the graphics mode.</summary>
        public async Task RunAsync()
        {
            for (int run = 0; run < 10; run++) // a mode switch restarts the game; never loop forever
            {
                int mode = ReadGameMode();
                var game = await m_startGame();
                if (game == null) return;
                Log.Write($"game started pid={game.Id} gameMode={mode}");

                using (var stop = new CancellationTokenSource())
                {
                    var watching = m_watch == null ? Task.CompletedTask : Task.Run(() => m_watch(game, stop.Token));
                    await Task.Run(() => game.WaitForExit());
                    stop.Cancel();
                    try
                    {
                        await watching;
                    }
                    catch (OperationCanceledException)
                    {
                        // stopped with the game
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"game watch failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }

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
