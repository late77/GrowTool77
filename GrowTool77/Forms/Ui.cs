// lang: C#, file: Ui.cs, target: .NET 8 Windows WinForms, proj: GrowTool77
// UI components — v2: gradient cards, modern buttons, better tabs, depth.
using System.Drawing.Drawing2D;
using GrowTool77.Modules;

namespace GrowTool77.Forms;

public static class Ui
{
    public static Label Lbl(string text, Rectangle bounds, Font font, Color color,
                            ContentAlignment align = ContentAlignment.MiddleLeft) => new()
    {
        Text         = text,
        Bounds       = bounds,
        Font         = font,
        ForeColor    = color,
        BackColor    = Color.Transparent,
        AutoSize     = false,
        AutoEllipsis = true,
        TextAlign    = align,
        UseMnemonic  = false,
    };

    public static Panel Divider(Rectangle bounds) => new() { Bounds = bounds, BackColor = Theme.Divider };

    public static void SetText(Control c, string text)
    {
        if (c.Text != text) c.Text = text;
    }
}

// Rounded card with top-to-bottom gradient and vivid rim — gives depth without clutter.
public class Card : Panel
{
    public Card()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g   = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int rim = Math.Max(1, Theme.S(2));
        var r   = new Rectangle(rim / 2, rim / 2, Width - rim - 1, Height - rim - 1);

        using var path = Theme.Round(r, Theme.S(14));

        // Subtle top→bottom gradient for depth
        using (var grad = new LinearGradientBrush(
                   new Point(0, 0), new Point(0, Math.Max(1, Height)),
                   Theme.CardTop, Theme.Card))
            g.FillPath(grad, path);

        // Soft inner top-highlight (simulates light hitting the card)
        using (var highlight = new Pen(Color.FromArgb(45, Color.White), 1))
            g.DrawPath(highlight, Theme.Round(
                new Rectangle(r.X + 1, r.Y + 1, r.Width - 2, r.Height / 2), Theme.S(13)));

        // Vivid rim
        using (var pen = new Pen(Theme.CardRim, rim))
            g.DrawPath(pen, path);
    }
}

public enum ButtonKind { Primary, Secondary, Danger }

public class GtButton : Control
{
    private bool _hover, _down;
    public ButtonKind Kind { get; set; }

    public GtButton(string text, ButtonKind kind = ButtonKind.Primary)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Text      = text;
        Kind      = kind;
        Font      = Theme.Button;
        Cursor    = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true;  Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true;  Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp  (MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged  (EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    private (Color fill, Color rim, Color fg) Colors()
    {
        if (!Enabled) return (Theme.Disabled, Theme.Disabled, Theme.DisabledText);
        return Kind switch
        {
            ButtonKind.Primary => (_down ? Theme.GoldPress : _hover ? Theme.GoldHover : Theme.Gold, Theme.GoldRim, Theme.OnGold),
            ButtonKind.Danger  => (_hover ? Theme.DangerHover : Theme.Danger, Color.FromArgb(135, 38, 30), Theme.Text),
            _                  => (_hover ? Theme.SecondaryHover : Theme.Secondary, Theme.CardRim, Theme.Text),
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var (fill, rim, fg) = Colors();
        int radius = Theme.S(10);
        using var path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), radius);

        // Gradient fill for primary buttons
        if (Kind == ButtonKind.Primary && Enabled)
        {
            var top = _down ? fill : Color.FromArgb(
                Math.Min(255, fill.R + 22),
                Math.Min(255, fill.G + 22),
                Math.Min(255, fill.B + 12));
            using var grad = new LinearGradientBrush(
                new Point(0, 0), new Point(0, Math.Max(1, Height)),
                top, fill);
            g.FillPath(grad, path);
        }
        else
        {
            using var b = new SolidBrush(fill);
            g.FillPath(b, path);
        }

        using (var p = new Pen(rim, Math.Max(1, Theme.S(1)))) g.DrawPath(p, path);

        var rect = _down && Enabled
            ? new Rectangle(0, Theme.S(1), Width, Height)
            : ClientRectangle;
        TextRenderer.DrawText(g, Text, Font, rect, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
}

public class TabButton : Control
{
    private bool _hover;
    private bool _selected;

    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    public TabButton(string text)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Text      = text;
        Font      = Theme.BodyBold;
        Cursor    = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true;  Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (_selected)
        {
            // Selected: filled with content-area colour + gold bottom accent
            using var path = Theme.RoundTop(new Rectangle(0, 0, Width - 1, Height), Theme.S(10));
            using (var b = new SolidBrush(Theme.Bg)) g.FillPath(b, path);
            // Gold accent line at very bottom ties selected tab to content
            using (var goldPen = new Pen(Theme.Gold, Theme.S(2)))
                g.DrawLine(goldPen, Theme.S(4), Height - 1, Width - Theme.S(4), Height - 1);
        }
        else if (_hover)
        {
            // Hover: subtle white overlay
            using var path = Theme.RoundTop(new Rectangle(1, 2, Width - 2, Height - 2), Theme.S(8));
            using var b = new SolidBrush(Color.FromArgb(28, Color.White));
            g.FillPath(b, path);
        }

        var fg = _selected ? Theme.Gold : _hover ? Theme.Text : Theme.TextDim;
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

public class Dot : Control
{
    private Color _color = Theme.Bad;

    public Color DotColor
    {
        get => _color;
        set { if (_color != value) { _color = value; Invalidate(); } }
    }

    public Dot()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Glow ring
        using (var glow = new SolidBrush(Color.FromArgb(40, _color)))
            g.FillEllipse(glow, -1, -1, Width + 1, Height + 1);
        using (var b = new SolidBrush(_color))
            g.FillEllipse(b, 1, 1, Width - 3, Height - 3);
    }
}

public class StatusPill : Control
{
    private bool _on;

    public StatusPill()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Font      = Theme.SmallBold;
    }

    public void SetState(bool on, string text)
    {
        if (_on == on && Text == text) return;
        _on  = on;
        Text = text;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Deep background with subtle rim
        using var path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2);
        using (var grad = new LinearGradientBrush(
                   new Point(0, 0), new Point(0, Height),
                   Color.FromArgb(20, 65, 22), Theme.HeaderDeep))
            g.FillPath(grad, path);
        using (var rim = new Pen(Color.FromArgb(60, Theme.CardRim), 1))
            g.DrawPath(rim, path);

        // Status dot with glow
        int d  = Theme.S(10);
        int dy = (Height - d) / 2;
        var dc = _on ? Theme.Good : Theme.Bad;
        using (var glow = new SolidBrush(Color.FromArgb(50, dc)))
            g.FillEllipse(glow, Theme.S(12), dy - 1, d + 2, d + 2);
        using (var dot = new SolidBrush(dc))
            g.FillEllipse(dot, Theme.S(13), dy, d, d);

        var textRect = new Rectangle(Theme.S(30), 0, Width - Theme.S(40), Height);
        TextRenderer.DrawText(g, Text, Font, textRect, Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
}

// Text input with rounded border and glow on focus.
public class InputBox : Panel
{
    public TextBox Box { get; }
    private bool _focused;

    public InputBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Input;
        Box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor   = Theme.Input,
            ForeColor   = Theme.Text,
            Font        = Theme.Body,
        };
        Box.Enter  += (_, _) => { _focused = true;  Invalidate(); };
        Box.Leave  += (_, _) => { _focused = false; Invalidate(); };
        Controls.Add(Box);
        Cursor = Cursors.IBeam;
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int pad = Theme.S(10);
        int h   = Box.PreferredHeight;
        Box.SetBounds(pad, Math.Max(0, (Height - h) / 2), Math.Max(0, Width - pad * 2), h);
    }

    protected override void OnClick(EventArgs e) { Box.Focus(); base.OnClick(e); }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), Theme.S(8));
        using (var b = new SolidBrush(Theme.Input)) g.FillPath(b, path);
        // Brighter rim when focused
        var rimColor = _focused ? Theme.Gold : Theme.InputRim;
        using (var p = new Pen(rimColor, _focused ? Math.Max(1, Theme.S(2)) : Math.Max(1, Theme.S(1))))
            g.DrawPath(p, path);
    }
}
