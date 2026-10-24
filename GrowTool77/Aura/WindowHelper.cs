// lang: C#, file: WindowHelper.cs, target: .NET 8 Windows, proj: GrowTool77
// DPI-aware window client bounds — separate from ScreenMap so existing code isn't touched.
// Fix: uses GetClientRect + ClientToScreen instead of GetWindowRect.
// GetWindowRect returns the outer frame (title bar + borders) which shifts all
// overlay coordinates whenever the window moves. ClientToScreen gives the exact
// top-left pixel of the drawable area.
using System.Runtime.InteropServices;

namespace GrowTool77.Aura;

public static class WindowHelper
{
    [StructLayout(LayoutKind.Sequential)] private struct RECT  { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct WPOINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref WPOINT p);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);

    /// <summary>
    /// Returns the screen-space bounds of the window's client area and DPI scale.
    /// Safe to call every frame — no allocations.
    /// </summary>
    public static bool TryGetClientScreenBounds(IntPtr hWnd, out Rectangle bounds, out float dpiScale)
    {
        bounds   = Rectangle.Empty;
        dpiScale = 1f;

        if (hWnd == IntPtr.Zero || !IsWindowVisible(hWnd) || IsIconic(hWnd)) return false;
        if (!GetClientRect(hWnd, out RECT rc)) return false;

        int w = rc.Right - rc.Left;
        int h = rc.Bottom - rc.Top;
        if (w < 32 || h < 32) return false;

        var pt = new WPOINT { X = 0, Y = 0 };
        if (!ClientToScreen(hWnd, ref pt)) return false;

        uint dpi = GetDpiForWindow(hWnd);
        if (dpi > 0) dpiScale = dpi / 96f;

        bounds = new Rectangle(pt.X, pt.Y, w, h);
        return true;
    }
}
