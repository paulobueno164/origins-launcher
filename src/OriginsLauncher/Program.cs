using System;
using System.Net;
using System.Linq;
using System.Windows.Forms;

namespace OriginsLauncher
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // Support/version diagnostics must not open a window or register an installation.
            if (args.Any(arg => arg.Equals("--version", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine(typeof(Program).Assembly.GetName().Version.ToString(3));
                Log.Init(Config.FromArgs(args));
                Log.Write("Launcher version: " + typeof(Program).Assembly.GetName().Version.ToString(3));
                return;
            }

            // Older Windows builds default to TLS 1.0; the update server only speaks TLS 1.2+.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            // .NET Framework allows 2 connections per host by default; downloads run 4 in parallel.
            ServicePointManager.DefaultConnectionLimit = 16; // 8 file downloads / 6 pack parts at once

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var config = Config.FromArgs(args);
            L.S = Strings.For(Settings.Load(config).Language ?? Strings.SystemLanguage());
            if (config.Uninstall)
            {
                Log.Init(config);
                Installer.Uninstall(config);
                return;
            }

            if (!config.ExplicitDir)
            {
                if (Installer.NeedsOwnFolder(config))
                {
                    using (var dialog = new InstallDialog(Installer.DefaultFolder()))
                        dialog.ShowDialog(); // on OK the launcher was restarted from the new folder
                    return;
                }

                if (Installer.IsFirstRun(config))
                    Installer.CreateShortcuts(Installer.LauncherPath);
                Installer.RegisterUninstall(config);
            }

            Application.Run(new MainForm(config));
        }
    }
}
