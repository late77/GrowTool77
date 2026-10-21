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

// Rounded card with a light rim, like the in-game pack banners.
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
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int rim = Math.Max(1, Theme.S(2));
        var r = new Rectangle(rim / 2, rim / 2, Width - rim - 1, Height - rim - 1);
        using var path = Theme.Round(r, Theme.S(14));
        using (var fill = new SolidBrush(Theme.Card)) g.FillPath(fill, path);
        using (var pen = new Pen(Theme.CardRim, rim)) g.DrawPath(pen, path);
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

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    private (Color fill, Color rim, Color fg) Colors()
    {
        if (!Enabled) return (Theme.Disabled, Theme.Disabled, Theme.DisabledText);
        return Kind switch
        {
            ButtonKind.Primary => (_down ? Theme.GoldPress : _hover ? Theme.GoldHover : Theme.Gold, Theme.GoldRim, Theme.OnGold),
            ButtonKind.Danger  => (_hover ? Theme.DangerHover : Theme.Danger, Color.FromArgb(140, 40, 35), Theme.Text),
            _                  => (_hover ? Theme.SecondaryHover : Theme.Secondary, Theme.CardRim, Theme.Text),
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var (fill, rim, fg) = Colors();
        using var path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), Theme.S(10));
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        using (var p = new Pen(rim, Math.Max(1, Theme.S(1)))) g.DrawPath(p, path);
        var rect = _down && Enabled ? new Rectangle(0, Theme.S(1), Width, Height) : ClientRectangle;
        TextRenderer.DrawText(g, Text, Font, rect, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
}

public class TabButton : Control
{
    private bool _hover, _selected;

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

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_selected)
        {
            using var path = Theme.RoundTop(new Rectangle(0, 0, Width - 1, Height), Theme.S(10));
            using var b = new SolidBrush(Theme.Bg);
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
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(_color);
        e.Graphics.FillEllipse(b, 0, 0, Width - 1, Height - 1);
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
        using (var path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2))
        using (var b = new SolidBrush(Theme.HeaderDeep))
            g.FillPath(b, path);

        int d = Theme.S(10);
        using (var dot = new SolidBrush(_on ? Theme.Good : Theme.Bad))
            g.FillEllipse(dot, Theme.S(14), (Height - d) / 2, d, d);

        var textRect = new Rectangle(Theme.S(32), 0, Width - Theme.S(44), Height);
        TextRenderer.DrawText(g, Text, Font, textRect, Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
}

// Text input with rounded border; the real TextBox sits inside without its own border.
public class InputBox : Panel
{
    public TextBox Box { get; }

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
        using (var p = new Pen(Theme.InputRim, Math.Max(1, Theme.S(1)))) g.DrawPath(p, path);
    }
}
