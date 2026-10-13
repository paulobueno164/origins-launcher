using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace OriginsLauncher
{
    internal static class Theme
    {
        public static readonly Color Background = Color.FromArgb(11, 12, 16);
        public static readonly Color Panel = Color.FromArgb(215, 18, 20, 27);
        public static readonly Color Line = Color.FromArgb(70, 226, 182, 92);
        public static readonly Color Text = Color.FromArgb(233, 228, 216);
        public static readonly Color Muted = Color.FromArgb(168, 162, 148);
        public static readonly Color Gold = Color.FromArgb(226, 182, 92);
        public static readonly Color GoldLight = Color.FromArgb(246, 217, 139);
        public static readonly Color GoldDark = Color.FromArgb(168, 120, 42);
        public static readonly Color Error = Color.FromArgb(224, 100, 100);
        public static readonly Color Ok = Color.FromArgb(87, 196, 122);

        public static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void Quality(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        }
    }

    /// <summary>Owner-drawn button: solid gold (primary) or gold outline (secondary).</summary>
    internal sealed class GoldButton : Control
    {
        private bool m_hover, m_down;

        public GoldButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            ForeColor = Theme.Text;
        }

        public bool Primary { get; set; }

        protected override void OnMouseEnter(EventArgs e) { m_hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { m_hover = false; m_down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { m_down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { m_down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.Quality(g);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.RoundedRect(r, Math.Min(8f, Height / 4f)))
            {
                if (Primary)
                {
                    Color top = Enabled ? (m_hover ? Color.FromArgb(255, 230, 160) : Theme.GoldLight) : Color.FromArgb(90, 86, 78);
                    Color bottom = Enabled ? (m_down ? Theme.GoldDark : Theme.Gold) : Color.FromArgb(60, 58, 52);
                    using (var brush = new LinearGradientBrush(r, top, bottom, LinearGradientMode.Vertical))
                        g.FillPath(brush, path);
                    using (var pen = new Pen(Enabled ? Theme.GoldDark : Color.FromArgb(70, 68, 62)))
                        g.DrawPath(pen, path);
                }
                else
                {
                    using (var brush = new SolidBrush(m_hover && Enabled ? Color.FromArgb(70, 226, 182, 92) : Color.FromArgb(150, 12, 13, 18)))
                        g.FillPath(brush, path);
                    using (var pen = new Pen(Enabled ? (m_hover ? Theme.GoldLight : Theme.Line) : Color.FromArgb(60, 60, 60)))
                        g.DrawPath(pen, path);
                }
            }

            Color text = Primary ? (Enabled ? Color.FromArgb(26, 18, 6) : Color.FromArgb(150, 146, 136))
                : (Enabled ? (m_hover ? Theme.GoldLight : Theme.Text) : Theme.Muted);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>Thin gold progress bar; negative Value draws an indeterminate shimmer.</summary>
    internal sealed class GoldProgress : Control
    {
        private double m_value;
        private readonly Timer m_timer = new Timer {Interval = 40};
        private float m_phase;

        public GoldProgress()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            m_timer.Tick += (s, e) => { m_phase = (m_phase + 0.02f) % 1f; Invalidate(); };
        }

        public double Value
        {
            get => m_value;
            set
            {
                m_value = value;
                m_timer.Enabled = value < 0;
                Invalidate();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) m_timer.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.Quality(g);
            var track = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float radius = Height / 2f;
            using (var path = Theme.RoundedRect(track, radius))
            using (var brush = new SolidBrush(Color.FromArgb(200, 8, 9, 12)))
            {
                g.FillPath(brush, path);
                using (var pen = new Pen(Theme.Line)) g.DrawPath(pen, path);
            }

            RectangleF fill;
            if (m_value < 0)
            {
                float w = track.Width * 0.25f;
                fill = new RectangleF(track.X + (track.Width + w) * m_phase - w, track.Y, w, track.Height);
                fill.Intersect(track);
            }
            else
            {
                fill = new RectangleF(track.X, track.Y, (float) (track.Width * Math.Min(1, m_value)), track.Height);
            }

            if (fill.Width > radius)
            {
                using (var path = Theme.RoundedRect(fill, radius))
                using (var brush = new LinearGradientBrush(fill, Theme.GoldLight, Theme.Gold, LinearGradientMode.Vertical))
                    g.FillPath(brush, path);
            }
        }
    }
}
