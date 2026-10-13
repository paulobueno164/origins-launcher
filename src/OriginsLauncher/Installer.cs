using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

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
            !File.Exists(Path.Combine(config.InstallDir, "Conquer.exe")) && !Directory.Exists(config.StateDir);

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
                !File.Exists(Path.Combine(chosen, "Conquer.exe")) &&
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
            ClientSize = new Size(520, 210);
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
                ForeColor = Theme.Muted, Location = new Point(22, 52), Size = new Size(480, 40)
            };
            m_path.SetBounds(22, 98, 380, 26);
            m_path.Text = folder;
            m_path.BackColor = Color.FromArgb(24, 26, 33);
            m_path.ForeColor = Theme.Text;
            m_path.BorderStyle = BorderStyle.FixedSingle;
            var browse = new GoldButton {Text = L.S.Browse};
            browse.SetBounds(410, 96, 90, 28);
            m_error.SetBounds(22, 130, 480, 20);
            m_error.ForeColor = Theme.Error;
            var install = new GoldButton {Text = L.S.Install, Primary = true, Font = new Font("Segoe UI Semibold", 10.5f)};
            install.SetBounds(300, 160, 120, 34);
            var cancel = new GoldButton {Text = L.S.Cancel};
            cancel.SetBounds(428, 160, 72, 34);
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
