// lang: C#, file: ProxyTab.cs, target: .NET 8 Windows WinForms, proj: GrowTool77
// "Proxy" tab — top-level navigation between Auto features and Path Finding.
// Two glass-style buttons switch between sub-panels.
using System.Drawing.Drawing2D;
using GrowTool77.Modules;

namespace GrowTool77.Forms;

public class ProxyTab : Panel
{
    private readonly GlassNavButton _autoBtn;
    private readonly GlassNavButton _pathBtn;
    private readonly AutoTab        _autoPanel;
    private readonly Panel          _pathPanel;

    public AutoTab AutoFeatures => _autoPanel;

    public ProxyTab()
    {
        BackColor      = Theme.Bg;
        DoubleBuffered = true;

        // ── Glass nav buttons ─────────────────────────────────────────
        _autoBtn = new GlassNavButton("Auto",         isSelected: true)  { Bounds = Theme.R(24,  12, 200, 52) };
        _pathBtn = new GlassNavButton("Path Finding", isSelected: false) { Bounds = Theme.R(236, 12, 200, 52) };
        _autoBtn.Click += (_, _) => Switch(0);
        _pathBtn.Click += (_, _) => Switch(1);
        Controls.Add(_autoBtn);
        Controls.Add(_pathBtn);

        // ── Auto sub-panel ────────────────────────────────────────────
        _autoPanel = new AutoTab { Bounds = Theme.R(0, 76, 880, 348) };
        Controls.Add(_autoPanel);

        // ── Path Finding placeholder ──────────────────────────────────
        _pathPanel = new Panel { Bounds = Theme.R(0, 76, 880, 348), BackColor = Theme.Bg, Visible = false };
        var card = new Card { Bounds = Theme.R(24, 24, 832, 80) };
        card.Controls.Add(Ui.Lbl("Path Finding", Theme.R(20, 10, 400, 22), Theme.Section, Theme.Gold));
        card.Controls.Add(Ui.Lbl("Coming soon — configure waypoints and automated movement.", Theme.R(20, 36, 792, 28), Theme.Body, Theme.TextDim));
        _pathPanel.Controls.Add(card);
        Controls.Add(_pathPanel);
    }

    private void Switch(int idx)
    {
        _autoBtn.IsSelected  = idx == 0;
        _pathBtn.IsSelected  = idx == 1;
        _autoPanel.Visible   = idx == 0;
        _pathPanel.Visible   = idx == 1;
        _autoBtn.Invalidate();
        _pathBtn.Invalidate();
    }
}

// Green beer glass-style navigation button.
public class GlassNavButton : Control
{
    private bool _hover;
    private bool _selected;

    public bool IsSelected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    public GlassNavButton(string text, bool isSelected = false)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Text      = text;
        Font      = Theme.BodyBold;
        Cursor    = Cursors.Hand;
        _selected = isSelected;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true;  Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int r = Theme.S(14);
        using var path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), r);

        // Beer-glass fill: amber-green, semi-transparent
        var fillAlpha  = _selected ? 130 : _hover ? 80 : 45;
        var fillColor  = Color.FromArgb(fillAlpha, 210, 168, 18); // amber green-beer
        using (var b = new SolidBrush(fillColor)) g.FillPath(b, path);

        // Inner highlight (glass shimmer) — top 30% of button
        var shimmerRect = new RectangleF(2, 2, Width - 4, (Height - 4) * 0.35f);
        using (var shimmer = new LinearGradientBrush(
                   new PointF(0, 2), new PointF(0, shimmerRect.Bottom),
                   Color.FromArgb(50, Color.White), Color.Transparent))
        using (var shimmerPath = Theme.Round(
                   new Rectangle(2, 2, Width - 4, (int)shimmerRect.Height), r / 2))
            g.FillPath(shimmer, shimmerPath);

        // Frosted glass border
        var rimAlpha  = _selected ? 200 : 110;
        var rimColor  = Color.FromArgb(rimAlpha, 220, 240, 220);
        using (var pen = new Pen(rimColor, _selected ? 2f : 1.5f))
            g.DrawPath(pen, path);

        // Bottom accent line when selected (gold)
        if (_selected)
        {
            using var gold = new Pen(Color.FromArgb(200, Theme.Gold), Theme.S(2));
            g.DrawLine(gold, Theme.S(6), Height - 2, Width - Theme.S(6), Height - 2);
        }

        // Text
        var fg = _selected ? Color.White : Color.FromArgb(200, Color.White);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}
