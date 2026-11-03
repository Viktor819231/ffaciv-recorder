using System.Drawing.Drawing2D;

namespace OutsideRecorderShell;

/// <summary>The icon by the clock: its look says the state (idle, waiting for Civ, recording, sending, needs attention).</summary>
static class TrayIcons
{
    public static readonly string[] Kinds = ["waiting", "recording", "sending", "attention", "paused"];
    static readonly Dictionary<(string, int), Icon> cache = [];

    /// <summary>A gold ring (the site's Civ 5 ring) with the state in the middle.</summary>
    public static Bitmap Draw(string kind, int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = size, pad = s * 0.04f, ring = s * (size <= 20 ? 0.09f : 0.13f);
        var outer = new RectangleF(pad, pad, s - 2 * pad, s - 2 * pad);
        using (var gold = new LinearGradientBrush(outer, Color.FromArgb(246, 214, 120), Color.FromArgb(150, 116, 52), 90f)) g.FillEllipse(gold, outer);
        var inner = RectangleF.Inflate(outer, -ring, -ring);
        using (var dark = new SolidBrush(Color.FromArgb(10, 22, 24))) g.FillEllipse(dark, inner);
        float shrink = size <= 20 ? 0.12f : 0.22f;
        var c = RectangleF.Inflate(inner, -inner.Width * shrink, -inner.Height * shrink);
        switch (kind)
        {
            case "recording":
                using (var b = new SolidBrush(Color.FromArgb(235, 72, 58))) g.FillEllipse(b, c);
                break;
            case "sending":
            {
                using var pen = new Pen(Color.FromArgb(94, 200, 188), Math.Max(1.6f, s * 0.11f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                float cx = c.X + c.Width / 2, top = c.Top, bot = c.Bottom, w = c.Width * 0.42f;
                g.DrawLine(pen, cx, bot, cx, top + 1);
                g.DrawLines(pen, [new PointF(cx - w, top + w), new PointF(cx, top), new PointF(cx + w, top + w)]);
                break;
            }
            case "attention":
            {
                using var b = new SolidBrush(Color.FromArgb(240, 160, 60));
                g.FillEllipse(b, c);
                using var dark = new SolidBrush(Color.FromArgb(30, 18, 4));
                float cx = c.X + c.Width / 2, bw = Math.Max(1.5f, c.Width * 0.16f);
                g.FillRectangle(dark, cx - bw / 2, c.Top + c.Height * 0.18f, bw, c.Height * 0.42f);
                g.FillRectangle(dark, cx - bw / 2, c.Top + c.Height * 0.70f, bw, bw);
                break;
            }
            case "paused":
            {
                using var b = new SolidBrush(Color.FromArgb(170, 182, 178));
                float bw = c.Width * 0.26f;
                g.FillRectangle(b, c.X + c.Width * 0.14f, c.Y + c.Height * 0.1f, bw, c.Height * 0.8f);
                g.FillRectangle(b, c.Right - c.Width * 0.14f - bw, c.Y + c.Height * 0.1f, bw, c.Height * 0.8f);
                break;
            }
            default: // waiting: a hollow dim ring
                using (var pen = new Pen(Color.FromArgb(120, 150, 152), Math.Max(1.2f, s * 0.08f))) g.DrawEllipse(pen, RectangleF.Inflate(c, -s * 0.03f, -s * 0.03f));
                break;
        }
        return bmp;
    }

    public static Icon Get(string kind, int size)
    {
        if (cache.TryGetValue((kind, size), out var i)) return i;
        using var bmp = Draw(kind, size);
        return cache[(kind, size)] = Icon.FromHandle(bmp.GetHicon());
    }
}

/// <summary>The tray menu in the window's colours.</summary>
sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    static readonly Color Bg = Color.FromArgb(12, 27, 30), Hover = Color.FromArgb(28, 52, 56), Line = Color.FromArgb(110, 90, 46), Text = Color.FromArgb(227, 230, 225), Muted = Color.FromArgb(147, 160, 155);
    public DarkMenuRenderer() : base(new Colors()) { RoundedEdges = false; }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = e.Item.Enabled ? Text : Muted; base.OnRenderItemText(e); }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { using var p = new Pen(Line); e.Graphics.DrawRectangle(p, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1); }
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle; e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var p = new Pen(Color.FromArgb(240, 200, 96), 2f);
        e.Graphics.DrawLines(p, [new Point(r.Left + 3, r.Top + r.Height / 2), new Point(r.Left + r.Width / 2 - 1, r.Bottom - 4), new Point(r.Right - 3, r.Top + 4)]);
    }
    sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemBorder => Hover;
        public override Color MenuBorder => Line;
        public override Color SeparatorDark => Color.FromArgb(33, 57, 62);
        public override Color SeparatorLight => Bg;
        public override Color CheckBackground => Bg;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}
