using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OriginsLauncher
{
    /// <summary>
    ///     The game installs next to the launcher. When a player runs the launcher straight from
    ///     Downloads or the Desktop, that would scatter 10k files there, so on the first run the
    ///     launcher offers a folder of its own, copies itself there and restarts from it.
    /// </summary>
    internal static class Installer
    {
        public const string FolderName = "Origins Conquer";
        public const string ShortcutName = "Origins Conquer.lnk";

        public static bool IsFirstRun(Config config) =>
            !HasGame(config.InstallDir) && !Directory.Exists(config.StateDir);

        /// <summary>A game folder: the 6609 client (Env_DX9\Conquer.exe) or an older install (Conquer.exe).</summary>
        public static bool HasGame(string dir) =>
            File.Exists(Path.Combine(dir, Manifest.DefaultGameExe)) || File.Exists(Path.Combine(dir, "Conquer.exe"));

        /// <summary>True when the launcher sits in a folder that holds other things than the game.</summary>
        public static bool NeedsOwnFolder(Config config)
        {
            if (!IsFirstRun(config)) return false;
            string self = Path.GetFileName(LauncherPath);
            return Directory.EnumerateFileSystemEntries(config.InstallDir)
                .Select(Path.GetFileName)
                .Any(name => !name.Equals(self, StringComparison.OrdinalIgnoreCase) &&
                             !name.StartsWith("OriginsLauncher", StringComparison.OrdinalIgnoreCase));
        }

        public static string DefaultFolder() =>
            Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", FolderName);

        /// <summary>A non-empty folder that is not a game folder gets an "Origins Conquer" subfolder.</summary>
        public static string ResolveTarget(string chosen)
        {
            chosen = Path.GetFullPath(chosen);
            if (Directory.Exists(chosen) && Directory.EnumerateFileSystemEntries(chosen).Any() &&
                !HasGame(chosen) &&
                !Path.GetFileName(chosen.TrimEnd('\\')).Equals(FolderName, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(chosen, FolderName);
            return chosen;
        }

        /// <summary>Copies the launcher into <paramref name="folder" />, adds the shortcuts and starts it there.</summary>
        public static void InstallTo(string folder)
        {
            Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, "OriginsLauncher.exe");
            if (!target.Equals(LauncherPath, StringComparison.OrdinalIgnoreCase))
                File.Copy(LauncherPath, target, true);
            CreateShortcuts(target);
            string args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(a => $"\"{a}\""));
            Process.Start(new ProcessStartInfo(target, args) {WorkingDirectory = folder, UseShellExecute = true});
        }

        /// <summary>Desktop and Start menu shortcuts; best effort, the game works without them.</summary>
        public static void CreateShortcuts(string launcher)
        {
            foreach (var folder in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                         Environment.GetFolderPath(Environment.SpecialFolder.Programs)
                     })
            {
                try
                {
                    if (string.IsNullOrEmpty(folder)) continue;
                    var shell = (dynamic) Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                    var link = shell.CreateShortcut(Path.Combine(folder, ShortcutName));
                    link.TargetPath = launcher;
                    link.WorkingDirectory = Path.GetDirectoryName(launcher);
                    link.IconLocation = launcher + ",0";
                    link.Description = "Origins Conquer";
                    link.Save();
                }
                catch (Exception)
                {
                    // no shell (server core) or blocked by policy
                }
            }
        }

        public static string LauncherPath => Process.GetCurrentProcess().MainModule.FileName;

        // ------------------------------------------------------------------ uninstall

        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\OriginsConquer";

        /// <summary>
        ///     Lists the game in Windows' installed apps (current user only, no admin) with an
        ///     Uninstall button that runs "OriginsLauncher.exe --uninstall". Refreshed on every start.
        /// </summary>
        public static void RegisterUninstall(Config config)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    key.SetValue("DisplayName", "Origins Conquer");
                    key.SetValue("DisplayIcon", LauncherPath + ",0");
                    key.SetValue("DisplayVersion", Updater.CurrentVersion.ToString(3));
                    key.SetValue("Publisher", "Origins Conquer");
                    key.SetValue("URLInfoAbout", config.SiteUrl);
                    key.SetValue("InstallLocation", config.InstallDir);
                    key.SetValue("UninstallString", $"\"{LauncherPath}\" {Config.UninstallArg}");
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    key.SetValue("EstimatedSize", 4140 * 1024, RegistryValueKind.DWord); // KB (the 6609 client: 4.0 GiB)
                }
            }
            catch (Exception ex)
            {
                Log.Write($"uninstall entry not written: {ex.Message}");
            }
        }

        /// <summary>
        ///     Removes what the launcher installed: the game files listed in the last manifest, its
        ///     state folder, the shortcuts, the installed-apps entry and the per-user Flash
        ///     registration it made. Files the player put in the folder stay; the launcher itself
        ///     and the emptied folders go once this process has exited.
        /// </summary>
        public static void Uninstall(Config config)
        {
            string dir = config.InstallDir;
            if (MessageBox.Show(string.Format(L.S.UninstallConfirm, dir.TrimEnd('\\')), L.S.UninstallTitle,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            if (Process.GetProcessesByName("Conquer").Any(p => IsUnder(SafePath(p), dir)))
            {
                MessageBox.Show(L.S.UninstallGameOpen, L.S.UninstallTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // the 5095 launcher's full screen scaler (a 5095 install updated to 6609 may still have it running)
            foreach (var magpie in Process.GetProcessesByName("Magpie").Where(p => IsUnder(SafePath(p), dir)))
                TryDelete(() => magpie.Kill());

            var manifest = new Updater(config).LoadLocalManifest();
            var files = manifest == null
                ? Enumerable.Empty<string>()
                : manifest.Files.Select(f => f.Path)
                    .Concat(manifest.Languages?.Values.SelectMany(p => p).Select(f => f.Path) ?? Enumerable.Empty<string>());
            foreach (string relative in files.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string full = Path.GetFullPath(Path.Combine(dir, relative));
                if (!IsUnder(full, dir)) continue;
                TryDelete(() => { if (File.Exists(full)) { File.SetAttributes(full, FileAttributes.Normal); File.Delete(full); } });
            }

            // written by the game or the launcher at run time (Magpie\*: 5095 installs updated to 6609)
            TryDelete(() => { string login = Path.Combine(dir, "ini", "origins.login"); if (File.Exists(login)) File.Delete(login); });
            TryDelete(() => { foreach (string temp in Directory.GetFiles(Path.Combine(dir, "ini"), "origins.login.*.tmp")) File.Delete(temp); });
            foreach (string sub in new[] {".originsupdate", "LOG", "debug", @"Magpie\config", @"Magpie\cache", @"Magpie\logs"})
                TryDelete(() => { if (Directory.Exists(Path.Combine(dir, sub))) Directory.Delete(Path.Combine(dir, sub), true); });

            foreach (var folder in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                         Environment.GetFolderPath(Environment.SpecialFolder.Programs)
                     })
            {
                if (string.IsNullOrEmpty(folder)) continue;
                string link = Path.Combine(folder, ShortcutName);
                TryDelete(() =>
                {
                    if (!File.Exists(link)) return;
                    var shell = (dynamic) Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                    string target = shell.CreateShortcut(link).TargetPath;
                    if (string.Equals(target, LauncherPath, StringComparison.OrdinalIgnoreCase))
                        File.Delete(link); // another install's shortcut stays
                });
            }

            string ocx = Path.Combine(dir, "Flash.ocx");
            foreach (var keyPath in new[] {$@"Software\Classes\CLSID\{Updater.FlashClsid}", $@"Software\Classes\Wow6432Node\CLSID\{Updater.FlashClsid}"})
            {
                TryDelete(() =>
                {
                    using (var server = Registry.CurrentUser.OpenSubKey(keyPath + @"\InprocServer32"))
                        if (!(server?.GetValue(null) is string path) || !path.Equals(ocx, StringComparison.OrdinalIgnoreCase))
                            return; // not ours
                    Registry.CurrentUser.DeleteSubKeyTree(keyPath, false);
                });
            }

            // the type library and the interfaces it registered (Updater.EnsureFlashRegistered)
            TryDelete(() =>
            {
                string libKey = $@"Software\Classes\TypeLib\{Updater.FlashLibId}";
                using (var lib = Registry.CurrentUser.OpenSubKey(libKey + @"\1.0\0\win32"))
                    if (!(lib?.GetValue(null) is string path) || !path.Equals(ocx, StringComparison.OrdinalIgnoreCase))
                        return; // not ours
                Registry.CurrentUser.DeleteSubKeyTree(libKey, false);
                foreach (string iid in new[] {"{D27CDB6C-AE6D-11CF-96B8-444553540000}", "{D27CDB6D-AE6D-11CF-96B8-444553540000}"})
                foreach (string view in new[] {@"Software\Classes\Interface\", @"Software\Classes\Wow6432Node\Interface\"})
                    Registry.CurrentUser.DeleteSubKeyTree(view + iid, false);
            });

            TryDelete(() => Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false));

            // the running exe and the (now empty) folders: removed by a script after this process exits
            string script = Path.Combine(Path.GetTempPath(), "origins-uninstall.cmd");
            int pid = Process.GetCurrentProcess().Id;
            File.WriteAllText(script, string.Join("\r\n",
                "@echo off",
                ":wait",
                $"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul && (ping 127.0.0.1 -n 2 >nul & goto wait)",
                $"del /f /q \"{LauncherPath}\" >nul 2>&1",
                $"for /f \"delims=\" %%d in ('dir /ad /b /s \"{dir.TrimEnd('\\')}\" ^| sort /r') do rd \"%%d\" >nul 2>&1",
                $"rd \"{dir.TrimEnd('\\')}\" >nul 2>&1",
                "del \"%~f0\"",
                ""), System.Text.Encoding.Default);
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
                {CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden});

            MessageBox.Show(L.S.UninstallDone, L.S.UninstallTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static bool IsUnder(string path, string dir) =>
            !string.IsNullOrEmpty(path) && Path.GetFullPath(path).StartsWith(dir, StringComparison.OrdinalIgnoreCase);

        private static string SafePath(Process process)
        {
            try { return process.MainModule?.FileName; }
            catch (Exception) { return null; }
        }

        private static void TryDelete(Action delete)
        {
            try { delete(); }
            catch (Exception ex) { Log.Write($"uninstall: {ex.Message}"); }
        }
    }

    /// <summary>First-run dialog: install folder with a default, a browse button and confirm.</summary>
    internal sealed class InstallDialog : Form
    {
        private readonly TextBox m_path = new TextBox();
        private readonly Label m_error = new Label();

        public InstallDialog(string folder)
        {
            SuspendLayout();
            Text = L.S.InstallTitle;
            Icon = new Icon(typeof(InstallDialog).Assembly.GetManifestResourceStream("OriginsLauncher.logo.ico"));
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(520, 250);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9.5f);

            var title = new Label
            {
                Text = L.S.InstallHeading, Font = new Font("Georgia", 14f, FontStyle.Bold), ForeColor = Theme.GoldLight,
                AutoSize = true, Location = new Point(20, 16)
            };
            var info = new Label
            {
                Text = L.S.InstallInfo,
                ForeColor = Theme.Muted, Location = new Point(22, 52), Size = new Size(480, 80)
            };
            m_path.SetBounds(22, 138, 380, 26);
            m_path.Text = folder;
            m_path.BackColor = Color.FromArgb(24, 26, 33);
            m_path.ForeColor = Theme.Text;
            m_path.BorderStyle = BorderStyle.FixedSingle;
            var browse = new GoldButton {Text = L.S.Browse};
            browse.SetBounds(410, 136, 90, 28);
            m_error.SetBounds(22, 170, 480, 20);
            m_error.ForeColor = Theme.Error;
            var install = new GoldButton {Text = L.S.Install, Primary = true, Font = new Font("Segoe UI Semibold", 10.5f)};
            install.SetBounds(300, 200, 120, 34);
            var cancel = new GoldButton {Text = L.S.Cancel};
            cancel.SetBounds(428, 200, 72, 34);
            Controls.AddRange(new Control[] {title, info, m_path, browse, m_error, install, cancel});

            browse.Click += (s, e) =>
            {
                using (var picker = new FolderBrowserDialog {Description = L.S.PickFolder, SelectedPath = m_path.Text})
                    if (picker.ShowDialog(this) == DialogResult.OK)
                        m_path.Text = Installer.ResolveTarget(picker.SelectedPath);
            };
            install.Click += (s, e) => Install();
            m_path.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                Install();
            };
            cancel.Click += (s, e) => DialogResult = DialogResult.Cancel;
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
            };
            ResumeLayout(false);
        }

        private void Install()
        {
            try
            {
                string target = Installer.ResolveTarget(m_path.Text.Trim());
                Installer.InstallTo(target);
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex)
            {
                m_error.Text = string.Format(L.S.InstallFailed, ex.Message);
            }
        }
    }
}
