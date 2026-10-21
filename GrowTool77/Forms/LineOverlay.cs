using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using GrowTool77.Aura;
using GrowTool77.Modules;

namespace GrowTool77.Forms;

public enum PointerStyle { Line, Arrow }

// A transparent, click-through, always-on-top window over Growtopia's window
// that points from the player to target tiles — as lines from the eyes, or as
// GrowScan-style arrows next to the player.
//
// Where the game's camera is (and its zoom) is worked out from a picture of
// the screen under this window, matched against the world map: a full search
// once, then a light follow-up about 12 times a second (CameraSolver.Track).
// The picture is taken from the screen, so it is in exactly the pixels the
// pointers are drawn in, whatever the Windows display scaling. This window is
// excluded from screen capture, so its own drawing never appears in the
// picture. Drawing uses per-pixel alpha (UpdateLayeredWindow), so lines can be
// slightly see-through. The game itself is never touched.
public sealed class LineOverlay : Form
{
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
                                                   IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    [StructLayout(LayoutKind.Sequential)] private struct RECT  { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE  { public int Cx, Cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    private const int  WS_EX_LAYERED     = 0x00080000;
    private const int  WS_EX_TRANSPARENT = 0x00000020;
    private const int  WS_EX_TOOLWINDOW  = 0x00000080;
    private const int  WS_EX_NOACTIVATE  = 0x08000000;
    private const int  ULW_ALPHA         = 0x2;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;      // Windows 10 2004 and later
    private const byte LineAlpha = 204;                    // lines: ~80 % opaque

    private readonly Func<(bool Ok, float X, float Y)> _player;
    private readonly Action<string> _log;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ShiftyColor _shifty = new();

    private IReadOnlyList<TilePos> _targets = Array.Empty<TilePos>();
    private WorldView? _world;
    private volatile List<TilePos> _shiftyTiles = new();
    private IntPtr     _gameWindow;
    private DateTime   _lastLookup = DateTime.MinValue;
    private Bitmap?    _canvas;
    private Bitmap?    _shiftyTexture;
    private Rectangle  _shownArea;

    public PointerStyle Style { get; set; } = PointerStyle.Line;

    // Camera: written by the background loop, read when drawing.
    private sealed record Solution(CameraFix Fix, float PlayerCX, float PlayerCY);
    private volatile Solution? _solution;
    private CancellationTokenSource? _solving;
    private string _lastState = "";
    private int    _trackMisses;
    private DateTime _lastFull = DateTime.MinValue;

    private sealed record CaptureArea(Rectangle Screen);
    private volatile CaptureArea? _area;
    private bool _excludedFromCapture;
    private int  _errorsLogged;

    // Where the game keeps its camera centre relative to the player (world pixels).
    private float _biasX, _biasY;
    private bool  _biasKnown;

    private sealed record LastCheck(ScreenFrame Frame, int Scale, CameraFix Fix, float PlayerX, float PlayerY,
                                    WorldView World, IReadOnlyList<TilePos> Targets);
    private volatile LastCheck? _lastCheck;

    public LineOverlay(Func<(bool Ok, float X, float Y)> player, Action<string> log)
    {
        _player         = player;
        _log            = log;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        TopMost         = true;
        StartPosition   = FormStartPosition.Manual;
        Bounds          = new Rectangle(-32000, -32000, 10, 10);
        _timer.Tick    += (_, _) => Follow();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _excludedFromCapture = SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);
    }

    // The game's own Shifty Block texture (used to fill the arrows), if available.
    public void SetShiftyTexture(Bitmap? texture) => _shiftyTexture = texture;

    public void SetWorld(WorldView? world)
    {
        bool changed = world?.Name != _world?.Name || world?.Entered != _world?.Entered;
        _world = world;
        if (changed)
        {
            _solution    = null;          // the learned camera bias is kept: it belongs to the game, not the world
            _trackMisses = 0;
            var shifty = new List<TilePos>();                  // built first, then published (read by the background loop)
            if (world != null)
                for (int i = 0; i < world.Fg.Length; i++)
                    if (world.Fg[i] == ShiftyColor.ItemId) shifty.Add(new TilePos(i % world.Width, i / world.Width));
            _shiftyTiles = shifty;
        }
        if (world == null) SetTargets(Array.Empty<TilePos>());
    }

    public void SetTargets(IReadOnlyList<TilePos> targets)
    {
        _targets = targets;
        if (targets.Count == 0)
        {
            _timer.Stop();
            StopSolving();
            if (Visible) Hide();
            return;
        }
        _timer.Start();
        StartSolving();
        Follow();
    }

    // ── Window tracking (UI thread, ~30 times a second) ─────────────

    private void Follow()
    {
        var hwnd = FindGameWindow();
        if (hwnd == IntPtr.Zero || IsIconic(hwnd) || !IsWindowVisible(hwnd))
        {
            if (Visible) Hide();
            return;
        }

        GetClientRect(hwnd, out var rc);
        var origin = new POINT();
        ClientToScreen(hwnd, ref origin);
        var area = new Rectangle(origin.X, origin.Y, rc.Right - rc.Left, rc.Bottom - rc.Top);
        if (area.Width <= 0 || area.Height <= 0) return;

        _area = new CaptureArea(area);
        _shownArea = area;
        if (!Visible) Show();
        Render();
    }

    private IntPtr FindGameWindow()
    {
        if (_gameWindow != IntPtr.Zero && DateTime.UtcNow - _lastLookup < TimeSpan.FromSeconds(2)) return _gameWindow;
        _lastLookup = DateTime.UtcNow;
        IntPtr found = IntPtr.Zero;
        foreach (var p in Process.GetProcessesByName("Growtopia"))
        {
            using (p)
            {
                if (found == IntPtr.Zero && p.MainWindowHandle != IntPtr.Zero) found = p.MainWindowHandle;
            }
        }
        _gameWindow = found;
        return _gameWindow;
    }

    // ── Camera (background) ─────────────────────────────────────────

    private void StartSolving()
    {
        if (_solving != null) return;
        var cts = new CancellationTokenSource();
        _solving = cts;
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    Step();
                }
                catch (Exception ex)
                {
                    if (Interlocked.Increment(ref _errorsLogged) <= 3)
                        _log($"Lines: camera check failed ({ex.GetType().Name}: {ex.Message}); retrying.");
                }
                int wait = _solution?.Fix.Matched == true ? 80 : 300;      // light follow-up vs full search
                try { await Task.Delay(wait, cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    private void StopSolving()
    {
        _solving?.Cancel();
        _solving = null;
    }

    private void Step()
    {
        var world = _world;
        var hwnd  = _gameWindow;
        var area  = _area;
        if (world == null || area == null || hwnd == IntPtr.Zero || IsIconic(hwnd)) return;
        var (ok, px, py) = _player();
        if (!ok) return;

        var shot = CaptureScreen(area.Screen);
        if (shot == null) return;
        double captured = _clock.Elapsed.TotalSeconds;
        float pcx = px + 10, pcy = py + 15;
        int sc = shot.Value.Scale;
        var sol = _solution;

        CameraFix? fix = null;
        bool full = sol == null || !sol.Fix.Matched || (DateTime.UtcNow - _lastFull).TotalSeconds > 5;
        if (!full)
        {
            // Light follow-up: same zoom, camera near where it should be after the player's move.
            float predX = sol!.Fix.CamX + (pcx - sol.PlayerCX), predY = sol.Fix.CamY + (pcy - sol.PlayerCY);
            var t = CameraSolver.Track(shot.Value.Frame, world.Fg, world.Bg, world.Width, world.Height,
                                       sol.Fix.TilePixels / sc, predX, predY);
            if (t != null && t.Matched)
            {
                fix = t with { TilePixels = t.TilePixels * sc };
                _trackMisses = 0;
            }
            else if (++_trackMisses >= 2)
            {
                full = true;                                       // lost it (zoomed, teleported…): search again
            }
        }

        if (full)
        {
            var f2 = CameraSolver.Solve(shot.Value.Frame, world.Fg, world.Bg, world.Width, world.Height, pcx + _biasX, pcy + _biasY);
            _lastFull = DateTime.UtcNow;
            if (f2 == null)
            {
                Report("Lines: no tile grid visible on screen right now");
                return;
            }
            f2 = f2 with { TilePixels = f2.TilePixels * sc };
            // A routine re-check doesn't throw away a good lock for a weaker answer.
            bool keepLock = sol != null && sol.Fix.Matched && _trackMisses < 2 && (!f2.Matched || f2.Confidence < 0.3f);
            if (!keepLock)
            {
                fix = f2;
                if (f2.Matched) _trackMisses = 0;
            }
        }

        if (fix != null)
        {
            _solution = new Solution(fix, pcx, pcy);
            if (fix.Matched && fix.Confidence >= 0.25f) Learn(fix, pcx, pcy, area.Screen.Size, world);
            _lastCheck = new LastCheck(shot.Value.Frame, sc, fix, px, py, world, _targets);
            Report(fix.Matched
                ? $"Lines: camera locked to the game (tile {fix.TilePixels:0} px on screen)"
                : $"Lines: tile {fix.TilePixels:0} px on screen, but the world isn't recognisable right now; using an estimate");
        }

        SampleShifty(shot.Value.Frame, sc, (fix ?? sol?.Fix), captured);
    }

    // If a Shifty Block is on screen, measure its colour so ours matches the game's exactly.
    private void SampleShifty(ScreenFrame frame, int sc, CameraFix? fix, double time)
    {
        if (fix == null || !fix.Matched || _shiftyTiles.Count == 0) return;
        float tileF = fix.TilePixels / sc, k = tileF / 32f;
        foreach (var t in _shiftyTiles)
        {
            float x0 = (t.X * 32 - fix.CamX) * k, y0 = (t.Y * 32 - fix.CamY) * k;
            if (x0 < 0 || y0 < 0 || x0 + tileF >= frame.Width || y0 + tileF >= frame.Height) continue;
            long r = 0, g = 0, b = 0; int n = 0;
            for (int y = (int)(y0 + tileF * 0.3f); y < (int)(y0 + tileF * 0.7f); y++)
            for (int x = (int)(x0 + tileF * 0.3f); x < (int)(x0 + tileF * 0.7f); x++)
            {
                int p = y * frame.Stride + x * 4;                 // screen pictures are B, G, R, A
                b += frame.Pixels[p]; g += frame.Pixels[p + 1]; r += frame.Pixels[p + 2]; n++;
            }
            if (n == 0) continue;
            if (_shifty.Observe((byte)(r / n), (byte)(g / n), (byte)(b / n), time)) return;
        }
    }

    private void Learn(CameraFix fix, float pcx, float pcy, Size screen, WorldView world)
    {
        float viewW = screen.Width * 32f / fix.TilePixels, viewH = screen.Height * 32f / fix.TilePixels;
        bool freeX = fix.CamX > 32 && fix.CamX + viewW < world.Width * 32f - 32;
        bool freeY = fix.CamY > 32 && fix.CamY + viewH < world.Height * 32f - 32;
        float bx = fix.CamX + viewW / 2 - pcx, by = fix.CamY + viewH / 2 - pcy;
        if (freeX) _biasX = _biasKnown ? _biasX * 0.7f + bx * 0.3f : bx;
        if (freeY) _biasY = _biasKnown ? _biasY * 0.7f + by * 0.3f : by;
        if (freeX || freeY) _biasKnown = true;
    }

    private void Report(string state)
    {
        if (state == _lastState) return;
        _lastState = state;
        _log(state + (_excludedFromCapture ? "." : " (pointers may show up in the picture on this Windows version)."));
    }

    // Picture of the screen under this window: the same pixels the pointers are
    // drawn in. Large areas are halved to keep the check fast.
    private static (ScreenFrame Frame, int Scale)? CaptureScreen(Rectangle screen)
    {
        if (screen.Width < 200 || screen.Height < 150) return null;

        using var bmp = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(screen.Location, Point.Empty, screen.Size, CopyPixelOperation.SourceCopy);

        var data = bmp.LockBits(new Rectangle(Point.Empty, screen.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var pixels = new byte[data.Stride * screen.Height];
        Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        var frame = new ScreenFrame { Pixels = pixels, Width = screen.Width, Height = screen.Height, Stride = data.Stride };
        bmp.UnlockBits(data);

        return screen.Width > 1100 ? (Half(frame), 2) : (frame, 1);
    }

    private static ScreenFrame Half(ScreenFrame f)
    {
        int w = f.Width / 2, h = f.Height / 2;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int a = (2 * y) * f.Stride + (2 * x) * 4, b = a + f.Stride, d = (y * w + x) * 4;
            for (int c = 0; c < 4; c++)
                px[d + c] = (byte)((f.Pixels[a + c] + f.Pixels[a + 4 + c] + f.Pixels[b + c] + f.Pixels[b + 4 + c]) / 4);
        }
        return new ScreenFrame { Pixels = px, Width = w, Height = h, Stride = w * 4 };
    }

    // ── Drawing (UI thread) ─────────────────────────────────────────

    private void Render()
    {
        var area = _shownArea;
        if (area.Width <= 0 || area.Height <= 0 || !IsHandleCreated) return;
        if (_canvas == null || _canvas.Width != area.Width || _canvas.Height != area.Height)
        {
            _canvas?.Dispose();
            _canvas = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        }

        using (var g = Graphics.FromImage(_canvas))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode   = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            Draw(g, area.Size);
        }
        Push(_canvas, area.Location);
    }

    private void Draw(Graphics g, Size size)
    {
        var world = _world;
        var (ok, px, py) = _player();
        if (!ok || world == null || _targets.Count == 0 || world.Width <= 0 || world.Height <= 0) return;

        var sol = _solution;
        var geo = LineGeometry.Compute(sol?.Fix, sol?.PlayerCX ?? 0, sol?.PlayerCY ?? 0, px, py,
                                       world.Width, world.Height, size.Width, size.Height,
                                       _biasX, _biasY, _targets);
        var (r, gr, b) = _shifty.At(_clock.Elapsed.TotalSeconds);
        var colour = Color.FromArgb(r, gr, b);
        float s = geo.Tile / 32f;

        if (Style == PointerStyle.Line)
        {
            // Shifty-coloured, no outline, slightly see-through.
            using var pen  = new Pen(Color.FromArgb(LineAlpha, colour), Math.Max(3f, 3f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var fill = new SolidBrush(Color.FromArgb(LineAlpha, colour));
            float dot = Math.Max(4f, 5f * s);
            var eyes = new PointF(geo.Eyes.X, geo.Eyes.Y);
            foreach (var (ex, ey) in geo.Ends)
            {
                g.DrawLine(pen, eyes, new PointF(ex, ey));
                g.FillEllipse(fill, ex - dot, ey - dot, 2 * dot, 2 * dot);
            }
            return;
        }

        // Arrows: the Shifty Block's own texture tinted with the current colour, outlined, opaque.
        using var tinted = Tint(_shiftyTexture, colour);
        using var outline = new Pen(Color.FromArgb(255, 24, 24, 24), Math.Max(2f, geo.Tile / 18f)) { LineJoin = LineJoin.Round };
        foreach (var arrow in LineGeometry.Arrows(geo))
        {
            var points = LineGeometry.Outline(arrow).Select(p => new PointF(p.X, p.Y)).ToArray();
            using Brush brush = tinted != null ? ArrowTexture(tinted, arrow, geo.Tile) : new SolidBrush(colour);
            g.FillPolygon(brush, points);
            g.DrawPolygon(outline, points);
        }
    }

    private static TextureBrush ArrowTexture(Bitmap tinted, LineGeometry.Arrow arrow, float tile)
    {
        var brush = new TextureBrush(tinted, WrapMode.Tile);
        brush.TranslateTransform(arrow.Tip.X, arrow.Tip.Y);
        brush.RotateTransform(arrow.Angle * 180f / MathF.PI);
        brush.ScaleTransform(tile / tinted.Width, tile / tinted.Height);
        return brush;
    }

    // The texture's light and shade in the given colour (grey → tint), as the game tints the block.
    private static Bitmap? Tint(Bitmap? texture, Color colour)
    {
        if (texture == null) return null;
        float tr = colour.R / 255f * 1.25f, tg = colour.G / 255f * 1.25f, tb = colour.B / 255f * 1.25f;
        var matrix = new ColorMatrix(new[]
        {
            new[] { 0.30f * tr, 0.30f * tg, 0.30f * tb, 0f, 0f },
            new[] { 0.59f * tr, 0.59f * tg, 0.59f * tb, 0f, 0f },
            new[] { 0.11f * tr, 0.11f * tg, 0.11f * tb, 0f, 0f },
            new[] { 0f, 0f, 0f, 1f, 0f },
            new[] { 0f, 0f, 0f, 0f, 1f },
        });
        var result = new Bitmap(texture.Width, texture.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(matrix);
        g.DrawImage(texture, new Rectangle(0, 0, texture.Width, texture.Height), 0, 0, texture.Width, texture.Height, GraphicsUnit.Pixel, attributes);
        return result;
    }

    // Hands the picture (with its alpha) to Windows as this window's content.
    private void Push(Bitmap bitmap, Point location)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc    = CreateCompatibleDC(screenDc);
        IntPtr hBitmap  = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            old     = SelectObject(memDc, hBitmap);
            var size  = new SIZE { Cx = bitmap.Width, Cy = bitmap.Height };
            var src   = new POINT();
            var dst   = new POINT { X = location.X, Y = location.Y };
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memDc, old);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // ── Diagnostics ─────────────────────────────────────────────────

    // Writes the last checked picture as a PNG with the check's data inside it.
    public string? SaveSnapshot(string path)
    {
        var last = _lastCheck;
        if (last == null) return "No picture yet: show some pointers first, then try again.";

        var f = last.Frame;
        using var bmp = new Bitmap(f.Width, f.Height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, f.Width, f.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < f.Height; y++) Marshal.Copy(f.Pixels, y * f.Stride, data.Scan0 + y * data.Stride, f.Width * 4);
        bmp.UnlockBits(data);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);

        static string Pack(ushort[] v)
        {
            var raw = new byte[v.Length * 2];
            Buffer.BlockCopy(v, 0, raw, 0, raw.Length);
            using var outMs = new MemoryStream();
            using (var z = new System.IO.Compression.ZLibStream(outMs, System.IO.Compression.CompressionLevel.Optimal)) z.Write(raw);
            return Convert.ToBase64String(outMs.ToArray());
        }
        var info = System.Text.Json.JsonSerializer.Serialize(new
        {
            world = last.World.Name, width = last.World.Width, height = last.World.Height,
            pictureScale = last.Scale, tile = last.Fix.TilePixels, camX = last.Fix.CamX, camY = last.Fix.CamY,
            matched = last.Fix.Matched, confidence = last.Fix.Confidence,
            playerX = last.PlayerX, playerY = last.PlayerY, biasX = _biasX, biasY = _biasY,
            style = Style.ToString(), shiftySynced = _shifty.Synced, shiftySpeed = _shifty.DegreesPerSecond,
            screen = new { w = _shownArea.Width, h = _shownArea.Height },
            targets = last.Targets.Select(t => new[] { t.X, t.Y }),
            fg = Pack(last.World.Fg), bg = Pack(last.World.Bg),
        });
        File.WriteAllBytes(path, PngText.Insert(ms.ToArray(), "growtool77-lines", info));
        return null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopSolving();
            _timer.Dispose();
            _canvas?.Dispose();
        }
        base.Dispose(disposing);
    }
}
