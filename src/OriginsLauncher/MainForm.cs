using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OriginsLauncher
{
    internal sealed class MainForm : Form
    {
        private readonly Config m_config;
        private readonly Updater m_updater;
        private Image m_background; // game art from the site (not shipped in the exe); null: plain dark backdrop
        private readonly Icon m_logo;

        private readonly GoldButton m_play = new GoldButton {Primary = true};
        private readonly GoldButton m_site = new GoldButton();
        private readonly GoldButton m_check = new GoldButton();
        private readonly GoldButton m_language = new GoldButton();
        private readonly ContextMenuStrip m_languageMenu = new ContextMenuStrip();
        private readonly Settings m_settings;
        private bool m_recheckAfterBusy;
        private readonly GoldButton m_close = new GoldButton {Text = "✕"};
        private readonly GoldButton m_minimize = new GoldButton {Text = "—"};
        private readonly GoldProgress m_progress = new GoldProgress();

        private Manifest m_manifest;
        private List<NewsItem> m_news = new List<NewsItem>();
        private string m_status = L.S.Starting;
        private string m_detail = "";
        private Color m_statusColor = Theme.Text;
        private readonly CancellationTokenSource m_cts = new CancellationTokenSource();
        private bool m_busy;
        private Bitmap m_backdrop; // background image + gradients, rendered once per size

        public MainForm(Config config)
        {
            m_config = config;
            m_updater = new Updater(config);
            m_settings = Settings.Load(config);
            L.S = Strings.For(m_settings.Language ?? Strings.SystemLanguage());
            Log.Init(config);
            Log.Write($"launcher {Updater.CurrentVersion} dir={config.InstallDir} updates={config.UpdateBaseUrl}");
            m_background = m_updater.LoadCachedBackground();
            m_logo = new Icon(typeof(MainForm).Assembly.GetManifestResourceStream("OriginsLauncher.logo.ico"), 64, 64);

            SuspendLayout();
            Text = "Origins Conquer";
            Icon = new Icon(typeof(MainForm).Assembly.GetManifestResourceStream("OriginsLauncher.logo.ico"));
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(880, 540);
            BackColor = Theme.Background;
            DoubleBuffered = true;
            Font = new Font("Segoe UI", 9.5f);

            m_play.Font = new Font("Georgia", 15f, FontStyle.Bold);
            m_play.SetBounds(680, 456, 176, 56);
            m_site.SetBounds(24, 492, 86, 30);
            m_check.SetBounds(118, 492, 150, 30);
            m_progress.SetBounds(24, 462, 636, 14);
            m_close.SetBounds(836, 12, 32, 28);
            m_minimize.SetBounds(798, 12, 32, 28);
            m_language.SetBounds(668, 12, 124, 28);
            m_language.Font = new Font("Segoe UI", 9f);
            foreach (var strings in Strings.All)
            {
                var item = new ToolStripMenuItem(strings.Name) {Tag = strings.Code};
                item.Click += (s, e) => ChangeLanguage((string) ((ToolStripMenuItem) s).Tag);
                m_languageMenu.Items.Add(item);
            }

            m_language.Click += (s, e) => m_languageMenu.Show(m_language, new Point(0, m_language.Height));
            ApplyTexts();
            m_close.Font = m_minimize.Font = new Font("Segoe UI", 10f);
            m_play.Enabled = false;
            Controls.AddRange(new Control[] {m_play, m_site, m_check, m_progress, m_close, m_minimize, m_language});

            m_play.Click += (s, e) => PlayGame();
            m_site.Click += (s, e) => OpenUrl(m_config.SiteUrl);
            m_check.Click += async (s, e) => await RunUpdateAsync(true);
            m_close.Click += (s, e) => Close();
            m_minimize.Click += (s, e) => WindowState = FormWindowState.Minimized;
            MouseDown += DragWindow;
            ResumeLayout(false);
        }

        /// <summary>Button texts and menu checks for the current language.</summary>
        private void ApplyTexts()
        {
            m_play.Text = L.S.Play;
            m_site.Text = L.S.Site;
            m_check.Text = L.S.Check;
            m_language.Text = $"{L.S.Language}: {L.S.Code.ToUpperInvariant()}";
            foreach (ToolStripMenuItem item in m_languageMenu.Items)
                item.Checked = (string) item.Tag == L.S.Code;
            Invalidate();
        }

        /// <summary>Saves the choice and installs that language's files (translated texts + login port).</summary>
        private async void ChangeLanguage(string code)
        {
            if (code == L.S.Code) return;
            L.S = Strings.For(code);
            m_settings.Language = code;
            try { m_settings.Save(m_config); }
            catch (Exception ex) { Log.Write($"settings not saved: {ex.Message}"); }
            Log.Write($"language -> {code}");
            ApplyTexts();
            _ = LoadNewsAsync();
            if (m_busy) m_recheckAfterBusy = true;
            else await RunUpdateAsync(false);
        }

        protected override void OnResize(EventArgs e)
        {
            m_backdrop?.Dispose();
            m_backdrop = null;
            base.OnResize(e);
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _ = LoadNewsAsync();
            _ = LoadBackgroundAsync();
            await RunUpdateAsync(false);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            m_cts.Cancel(); // partial downloads resume on the next start
            base.OnFormClosing(e);
        }

        // ------------------------------------------------------------------ update flow

        private async Task RunUpdateAsync(bool fullCheck)
        {
            if (m_busy) return;
            m_busy = true;
            m_play.Enabled = false;
            m_check.Enabled = false;
            var ct = m_cts.Token;
            var progress = new Progress<UpdateProgress>(p => SetStatus(p.Status, p.Detail, p.Fraction));

            try
            {
                if (!m_updater.InstallDirIsWritable())
                {
                    SetError(L.S.NoWrite, L.S.NoWriteDetail);
                    return;
                }

                SetStatus(L.S.LookingForUpdates, "", -1);
                try
                {
                    m_manifest = await m_updater.FetchManifestAsync(ct);
                    Log.Write($"manifest v{m_manifest.Version}: {m_manifest.Files.Count} files, launcher {m_manifest.LauncherVersion}");
                }
                catch (Exception ex) when (!(ex is OperationCanceledException) || !ct.IsCancellationRequested)
                {
                    // HttpClient reports its own timeout as a cancellation; only a real close is silent.
                    Log.Write($"manifest failed: {ex.GetType().Name}: {ex.Message}");
                    m_manifest = m_updater.LoadLocalManifest();
                    if (m_updater.GameFilesPresent(m_manifest?.GameExe ?? "Conquer.exe"))
                    {
                        SetStatus(L.S.ServerDown, L.S.ServerDownPlay, 1, Theme.Error);
                        m_play.Enabled = true;
                    }
                    else
                    {
                        SetError(L.S.CannotConnect, L.S.CannotConnectDetail);
                    }

                    return;
                }

                if (Updater.IsLauncherOutdated(m_manifest) && m_config.AfterUpdate)
                {
                    Log.Write($"still {Updater.CurrentVersion} after a self-update to {m_manifest.LauncherVersion}; continuing");
                }
                else if (Updater.IsLauncherOutdated(m_manifest))
                {
                    Log.Write($"self-update to {m_manifest.LauncherVersion}");
                    await m_updater.StartSelfUpdateAsync(m_manifest, progress, ct);
                    Close();
                    return;
                }

                var wanted = m_manifest.FilesFor(L.S.Code);
                var outdated = await m_updater.FindOutdatedAsync(wanted, fullCheck, progress, ct);
                int updated = outdated.Count;
                Log.Write($"check lang={L.S.Code} full={fullCheck}: {outdated.Count} outdated, {outdated.Sum(f => f.GzSize)} bytes to download");
                if (Updater.ShouldUsePack(m_manifest, outdated))
                {
                    try
                    {
                        await m_updater.InstallPackAsync(m_manifest.Pack, outdated, progress, ct);
                    }
                    catch (Exception ex) when (!(ex is GameRunningException) && !(ex is OperationCanceledException && ct.IsCancellationRequested))
                    {
                        // e.g. the pack was replaced on the server mid-download: single files still work
                        Log.Write($"pack failed, downloading single files: {ex.GetType().Name}: {ex.Message}");
                    }

                    outdated = await m_updater.FindOutdatedAsync(wanted, false, progress, ct);
                    Log.Write($"after pack: {outdated.Count} outdated, {outdated.Sum(f => f.GzSize)} bytes to download");
                }

                if (outdated.Count > 0)
                    await m_updater.DownloadAsync(outdated, progress, ct);
                Log.Write("up to date");
                m_updater.SaveLocalManifest(m_manifest);

                SetStatus(L.S.Ready, updated > 0 ? string.Format(L.S.FilesUpdated, updated) : L.S.UpToDate, 1, Theme.Ok);
                m_play.Enabled = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // closing
            }
            catch (GameRunningException ex)
            {
                Log.Write(ex.Message);
                SetError(L.S.GameOpen, ex.Message);
            }
            catch (Exception ex)
            {
                Log.Write($"update failed: {ex}");
                string reason = ex is OperationCanceledException ? L.S.ServerSlow : ex.Message;
                SetError(L.S.UpdateError, reason + "  " + L.S.TryAgain);
            }
            finally
            {
                m_busy = false;
                if (!IsDisposed) m_check.Enabled = true;
            }

            if (m_recheckAfterBusy && !IsDisposed && !ct.IsCancellationRequested)
            {
                m_recheckAfterBusy = false; // the language changed during this run
                await RunUpdateAsync(false);
            }
        }

        private async Task LoadNewsAsync()
        {
            try
            {
                m_news = (await m_updater.FetchNewsAsync(m_cts.Token)).Take(3).ToList();
            }
            catch (Exception)
            {
                m_news = new List<NewsItem>();
            }

            if (!IsDisposed) Invalidate();
        }

        /// <summary>Refreshes the background art from the site (kept in the state folder for the next start).</summary>
        private async Task LoadBackgroundAsync()
        {
            var image = await m_updater.FetchBackgroundAsync(m_cts.Token);
            if (image == null || IsDisposed) return;
            var old = m_background;
            m_background = image;
            old?.Dispose();
            m_backdrop?.Dispose();
            m_backdrop = null;
            Invalidate();
        }

        private void PlayGame()
        {
            if (m_manifest == null && !m_updater.GameFilesPresent()) return;
            try
            {
                try
                {
                    m_updater.EnsureFlashRegistered();
                }
                catch (Exception)
                {
                    // the game registers Flash itself when it can; never block playing for this
                }

                var game = m_updater.StartGame(m_manifest ?? new Manifest {GameExe = "Conquer.exe", GameArgs = "blacknull"});
                Log.Write($"game started pid={game?.Id}");
                Close();
            }
            catch (Exception ex)
            {
                SetError(L.S.CannotStart, ex.Message);
            }
        }

        private void SetStatus(string status, string detail, double fraction, Color? color = null)
        {
            if (IsDisposed) return; // progress posted after the window closed
            m_status = status ?? "";
            m_detail = detail ?? "";
            m_statusColor = color ?? Theme.Text;
            m_progress.Value = fraction;
            Invalidate(new Rectangle(0, (int) (400 * DeviceDpi / 96f), ClientSize.Width, ClientSize.Height));
        }

        private void SetError(string status, string detail) => SetStatus(status, detail, 0, Theme.Error);

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) {UseShellExecute = true}); }
            catch (Exception) { /* no browser registered */ }
        }

        // ------------------------------------------------------------------ painting

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float s = DeviceDpi / 96f;
            if (m_backdrop == null)
                m_backdrop = RenderBackdrop(s);
            g.DrawImageUnscaled(m_backdrop, 0, 0);
            Theme.Quality(g);

            // Header
            g.DrawIcon(m_logo, new Rectangle((int) (24 * s), (int) (20 * s), (int) (48 * s), (int) (48 * s)));
            using (var title = new Font("Georgia", 24f, FontStyle.Bold))
            using (var gold = new SolidBrush(Theme.GoldLight))
                g.DrawString("ORIGINS", title, gold, 82 * s, 16 * s);
            using (var small = new Font("Segoe UI", 8.5f, FontStyle.Bold))
            using (var muted = new SolidBrush(Theme.Muted))
                g.DrawString(L.S.Subtitle, small, muted, 86 * s, 52 * s);

            DrawNews(g, s);

            // Status
            using (var statusFont = new Font("Segoe UI Semibold", 11f))
            using (var statusBrush = new SolidBrush(m_statusColor))
                g.DrawString(m_status, statusFont, statusBrush, 22 * s, 410 * s);
            using (var detailFont = new Font("Segoe UI", 9f))
                TextRenderer.DrawText(g, m_detail, detailFont,
                    new Rectangle((int) (24 * s), (int) (434 * s), (int) (636 * s), (int) (22 * s)), Theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            using (var tiny = new Font("Segoe UI", 8f))
                TextRenderer.DrawText(g,
                    $"Launcher v{Updater.CurrentVersion.ToString(3)}" + (m_manifest != null ? "  ·  " + string.Format(L.S.ClientVersion, m_manifest.Version) : ""),
                    tiny, new Rectangle((int) (280 * s), (int) (497 * s), (int) (380 * s), (int) (20 * s)), Theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // OnPaint covers everything with the backdrop
        }

        /// <summary>Background image (cover) with dark gradients for legibility.</summary>
        private Bitmap RenderBackdrop(float s)
        {
            var client = ClientRectangle;
            var bitmap = new Bitmap(Math.Max(1, client.Width), Math.Max(1, client.Height));
            using (var g = Graphics.FromImage(bitmap))
            {
                Theme.Quality(g);
                g.Clear(Theme.Background);
                if (m_background != null)
                {
                    float scale = Math.Max((float) client.Width / m_background.Width, (float) client.Height / m_background.Height);
                    float w = m_background.Width * scale, h = m_background.Height * scale;
                    g.DrawImage(m_background, (client.Width - w) / 2, (client.Height - h) / 2, w, h);
                }
                using (var shade = new LinearGradientBrush(client, Color.FromArgb(245, 11, 12, 16), Color.FromArgb(150, 11, 12, 16), LinearGradientMode.Horizontal))
                    g.FillRectangle(shade, client);
                var lower = new Rectangle(0, (int) (380 * s), client.Width, Math.Max(1, client.Height - (int) (380 * s)));
                using (var bottom = new LinearGradientBrush(new Rectangle(lower.X, lower.Y - 1, lower.Width, lower.Height + 2),
                           Color.FromArgb(0, 11, 12, 16), Color.FromArgb(250, 11, 12, 16), LinearGradientMode.Vertical))
                    g.FillRectangle(bottom, lower);
                using (var border = new Pen(Theme.Line))
                    g.DrawRectangle(border, 0, 0, client.Width - 1, client.Height - 1);
            }

            return bitmap;
        }

        private void DrawNews(Graphics g, float s)
        {
            var panel = new RectangleF(24 * s, 96 * s, 520 * s, 290 * s);
            using (var path = Theme.RoundedRect(panel, 10 * s))
            using (var brush = new SolidBrush(Theme.Panel))
            using (var pen = new Pen(Theme.Line))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            using (var eyebrow = new Font("Segoe UI", 8f, FontStyle.Bold))
            using (var gold = new SolidBrush(Theme.Gold))
                g.DrawString(L.S.News, eyebrow, gold, panel.X + 18 * s, panel.Y + 14 * s);

            float y = panel.Y + 38 * s;
            if (m_news.Count == 0)
            {
                using (var font = new Font("Segoe UI", 9.5f))
                    TextRenderer.DrawText(g, L.S.Welcome, font,
                        new Rectangle((int) (panel.X + 18 * s), (int) y, (int) (panel.Width - 36 * s), (int) (60 * s)),
                        Theme.Text, TextFormatFlags.WordBreak);
                return;
            }

            using (var titleFont = new Font("Georgia", 11f, FontStyle.Bold))
            using (var dateFont = new Font("Segoe UI", 8f))
            using (var textFont = new Font("Segoe UI", 9f))
            {
                foreach (var item in m_news)
                {
                    if (y > panel.Bottom - 40 * s) break;
                    TextRenderer.DrawText(g, item.Title ?? "", titleFont,
                        new Rectangle((int) (panel.X + 18 * s), (int) y, (int) (panel.Width - 130 * s), (int) (22 * s)),
                        Theme.GoldLight, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                    TextRenderer.DrawText(g, FormatDate(item.Date), dateFont,
                        new Rectangle((int) (panel.Right - 110 * s), (int) y, (int) (92 * s), (int) (22 * s)),
                        Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                    y += 24 * s;

                    var textRect = new Rectangle((int) (panel.X + 18 * s), (int) y, (int) (panel.Width - 36 * s), (int) (54 * s));
                    TextRenderer.DrawText(g, item.Text ?? "", textFont, textRect, Theme.Text,
                        TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.TextBoxControl);
                    y += 62 * s;
                }
            }
        }

        private static string FormatDate(string isoDate) =>
            DateTime.TryParse(isoDate, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)
                ? date.ToString(L.S.DateFormat, System.Globalization.CultureInfo.InvariantCulture)
                : isoDate ?? "";

        // ------------------------------------------------------------------ borderless window drag

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        private void DragWindow(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, 2 /* HTCAPTION */, 0);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                cp.Style |= 0x20000; // WS_MINIMIZEBOX: taskbar click minimizes/restores
                return cp;
            }
        }
    }
}
