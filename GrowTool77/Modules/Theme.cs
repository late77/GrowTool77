using System.Drawing.Drawing2D;

namespace GrowTool77.Modules;

public static class Theme
{
    // Layout is designed at 96 DPI and scaled by S()/R() to the screen's DPI.
    public const int DesignW = 880;
    public const int DesignH = 560;

    public static float Scale { get; private set; } = 1f;

    public static Font Title     { get; private set; } = null!;
    public static Font Heading   { get; private set; } = null!;
    public static Font Section   { get; private set; } = null!;
    public static Font Body      { get; private set; } = null!;
    public static Font BodyBold  { get; private set; } = null!;
    public static Font Small     { get; private set; } = null!;
    public static Font SmallBold { get; private set; } = null!;
    public static Font Big       { get; private set; } = null!;
    public static Font Button    { get; private set; } = null!;
    public static Font Symbol    { get; private set; } = null!;

    public static void Init()
    {
        using (var g = Graphics.FromHwnd(IntPtr.Zero))
            Scale = Math.Max(1f, g.DpiX / 96f);

        Title     = F(22, FontStyle.Bold);
        Heading   = F(18, FontStyle.Bold);
        Section   = F(13, FontStyle.Bold);
        Body      = F(14);
        BodyBold  = F(14, FontStyle.Bold);
        Small     = F(12);
        SmallBold = F(12, FontStyle.Bold);
        Big       = F(30, FontStyle.Bold);
        Button    = F(14, FontStyle.Bold);
        Symbol    = new Font("Segoe UI Symbol", 32 * Scale, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    // Fonts in pixels so text grows exactly as much as the layout does.
    private static Font F(float px, FontStyle style = FontStyle.Regular) =>
        new("Segoe UI", px * Scale, style, GraphicsUnit.Pixel);

    public static int S(int v) => (int)Math.Round(v * Scale);
    public static Rectangle R(int x, int y, int w, int h) => new(S(x), S(y), S(w), S(h));

    // St. Patrick's palette — bright clover green, gold, white
    public static readonly Color Bg             = Color.FromArgb(62, 160, 62);
    public static readonly Color Header         = Color.FromArgb(34, 104, 36);
    public static readonly Color HeaderDeep     = Color.FromArgb(24, 80, 26);
    public static readonly Color Card           = Color.FromArgb(40, 120, 42);
    public static readonly Color CardAlt        = Color.FromArgb(46, 131, 48);
    public static readonly Color CardRim        = Color.FromArgb(120, 205, 110);
    public static readonly Color Input          = Color.FromArgb(26, 88, 28);
    public static readonly Color InputRim       = Color.FromArgb(90, 170, 86);
    public static readonly Color Divider        = Color.FromArgb(72, 152, 70);
    public static readonly Color Gold           = Color.FromArgb(255, 210, 40);
    public static readonly Color GoldHover      = Color.FromArgb(255, 226, 110);
    public static readonly Color GoldPress      = Color.FromArgb(225, 178, 20);
    public static readonly Color GoldRim        = Color.FromArgb(170, 120, 0);
    public static readonly Color OnGold         = Color.FromArgb(40, 60, 10);
    public static readonly Color Text           = Color.White;
    public static readonly Color TextDim        = Color.FromArgb(205, 238, 196);
    public static readonly Color Good           = Color.FromArgb(140, 255, 140);
    public static readonly Color Bad            = Color.FromArgb(255, 110, 100);
    public static readonly Color Warn           = Color.FromArgb(255, 220, 90);
    public static readonly Color Disabled       = Color.FromArgb(70, 122, 70);
    public static readonly Color DisabledText   = Color.FromArgb(160, 196, 155);
    public static readonly Color Secondary      = Color.FromArgb(30, 96, 32);
    public static readonly Color SecondaryHover = Color.FromArgb(38, 114, 40);
    public static readonly Color Danger         = Color.FromArgb(190, 60, 50);
    public static readonly Color DangerHover    = Color.FromArgb(212, 76, 64);
    public static readonly Color Selection      = Color.FromArgb(78, 158, 66);

    public static GraphicsPath Round(Rectangle r, int radius)
    {
        int d = Math.Max(2, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static GraphicsPath RoundTop(Rectangle r, int radius)
    {
        int d = Math.Max(2, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddLine(r.Right, r.Y + d / 2, r.Right, r.Bottom);
        p.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
        p.CloseFigure();
        return p;
    }
}
