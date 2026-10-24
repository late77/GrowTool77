// lang: C#, file: Theme.cs, target: .NET 8 Windows WinForms, proj: GrowTool77
// St. Patrick's palette — v2: richer depth, modern contrast, Green Beer edition.
using System.Drawing.Drawing2D;

namespace GrowTool77.Modules;

public static class Theme
{
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

    private static Font F(float px, FontStyle style = FontStyle.Regular) =>
        new("Segoe UI", px * Scale, style, GraphicsUnit.Pixel);

    public static int S(int v) => (int)Math.Round(v * Scale);
    public static Rectangle R(int x, int y, int w, int h) => new(S(x), S(y), S(w), S(h));

    // ── Palette: St. Patrick's Green Beer ────────────────────────────
    // Background: vibrant clover green
    public static readonly Color Bg             = Color.FromArgb(54,  145, 54);
    // Header: deep forest, premium feel
    public static readonly Color Header         = Color.FromArgb(22,  78,  24);
    public static readonly Color HeaderTop      = Color.FromArgb(28,  90,  30); // lighter top for gradient
    public static readonly Color HeaderDeep     = Color.FromArgb(14,  56,  16);
    // Cards: layered depth
    public static readonly Color Card           = Color.FromArgb(36,  112, 38);
    public static readonly Color CardTop        = Color.FromArgb(46,  130, 48); // lighter for gradient
    public static readonly Color CardRim        = Color.FromArgb(100, 195, 92); // vivid rim
    public static readonly Color CardAlt        = Color.FromArgb(42,  122, 44);
    // Inputs: deep inset feel
    public static readonly Color Input          = Color.FromArgb(18,  68,  20);
    public static readonly Color InputRim       = Color.FromArgb(80,  160, 78);
    public static readonly Color Divider        = Color.FromArgb(68,  148, 66);
    // Gold: brass tap / Irish gold
    public static readonly Color Gold           = Color.FromArgb(255, 210, 40);
    public static readonly Color GoldHover      = Color.FromArgb(255, 228, 110);
    public static readonly Color GoldPress      = Color.FromArgb(220, 174, 16);
    public static readonly Color GoldRim        = Color.FromArgb(160, 115, 0);
    public static readonly Color OnGold         = Color.FromArgb(35,  55,  8);
    // Text
    public static readonly Color Text           = Color.White;
    public static readonly Color TextDim        = Color.FromArgb(200, 238, 192);
    // Status
    public static readonly Color Good           = Color.FromArgb(130, 255, 130);
    public static readonly Color Bad            = Color.FromArgb(255, 105, 95);
    public static readonly Color Warn           = Color.FromArgb(255, 220, 80);
    // Interactive states
    public static readonly Color Disabled       = Color.FromArgb(60,  118, 62);
    public static readonly Color DisabledText   = Color.FromArgb(150, 192, 148);
    public static readonly Color Secondary      = Color.FromArgb(28,  95,  30);
    public static readonly Color SecondaryHover = Color.FromArgb(36,  112, 38);
    public static readonly Color Danger         = Color.FromArgb(185, 55,  45);
    public static readonly Color DangerHover    = Color.FromArgb(208, 72,  60);
    public static readonly Color Selection      = Color.FromArgb(76,  156, 64);

    // ── Geometry helpers ─────────────────────────────────────────────
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
