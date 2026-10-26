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
    private NumericUpDown _tpX = null!;
    private NumericUpDown _tpY = null!;
    private GtButton      _tpBtn = null!;
    private Label         _tpStatusLbl = null!;
    private Action<byte[]>? _sendToServer;
    private Func<(bool, float, float)>? _getPlayer;
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

        // ── Path Finding — Teleport ───────────────────────────────────
        _pathPanel = new Panel { Bounds = Theme.R(0, 76, 880, 348), BackColor = Theme.Bg, Visible = false };

        var tpCard = new Card { Bounds = Theme.R(24, 24, 832, 160) };
        tpCard.Controls.Add(Ui.Lbl("Teleport", Theme.R(20, 10, 200, 22), Theme.BodyBold, Theme.Gold));
        tpCard.Controls.Add(Ui.Lbl(
            "Sends a position packet placing you at target tile. Use to reach drop-hacked items in inaccessible areas.",
            Theme.R(20, 32, 792, 20), Theme.Small, Theme.TextDim));

        tpCard.Controls.Add(Ui.Lbl("Tile X", Theme.R(20, 64, 50, 20), Theme.Small, Theme.Text));
        _tpX = new NumericUpDown { Bounds = Theme.R(70, 60, 80, 26), Minimum = 0, Maximum = 200, Value = 0,
            Font = Theme.Body, BackColor = Theme.Input, ForeColor = Theme.Text };

        tpCard.Controls.Add(Ui.Lbl("Tile Y", Theme.R(170, 64, 50, 20), Theme.Small, Theme.Text));
        _tpY = new NumericUpDown { Bounds = Theme.R(220, 60, 80, 26), Minimum = 0, Maximum = 120, Value = 0,
            Font = Theme.Body, BackColor = Theme.Input, ForeColor = Theme.Text };

        tpCard.Controls.Add(Ui.Lbl("↑ 0,0 = top-left corner of world", Theme.R(320, 64, 280, 20), Theme.Small, Theme.TextDim));

        _tpBtn = new GtButton("Teleport") { Bounds = Theme.R(20, 100, 120, 30) };
        _tpBtn.Click += (_, _) => TeleportToTile();
        _tpStatusLbl  = Ui.Lbl("", Theme.R(160, 106, 500, 22), Theme.Small, Theme.TextDim);

        tpCard.Controls.Add(_tpX);
        tpCard.Controls.Add(_tpY);
        tpCard.Controls.Add(_tpBtn);
        tpCard.Controls.Add(_tpStatusLbl);
        _pathPanel.Controls.Add(tpCard);
        Controls.Add(_pathPanel);
    }

    public void SetSendToServer(Action<byte[]> send, Func<(bool, float, float)> getPlayer)
    {
        _sendToServer = send;
        _getPlayer    = getPlayer;
    }

    private void TeleportToTile()
    {
        if (_sendToServer == null) { _tpStatusLbl.Text = "Start [AURA] first"; return; }

        int tileX = (int)_tpX.Value;
        int tileY = (int)_tpY.Value;
        float px  = tileX * 32f + 16f;
        float py  = tileY * 32f + 16f;

        // Send movement packet with spoofed position (inner type=3 = Player Movement)
        var p = new byte[4 + 56];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(p, 4); // NET_MESSAGE_GAME_PACKET
        p[4] = 0; // PACKET_STATE
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(4 + 16), px); // vecX
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(4 + 20), py); // vecY
        _sendToServer(p);

        _tpStatusLbl.Text      = $"Teleport sent → ({tileX}, {tileY})  pos=({px:F0}, {py:F0})";
        _tpStatusLbl.ForeColor = Theme.Good;
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
