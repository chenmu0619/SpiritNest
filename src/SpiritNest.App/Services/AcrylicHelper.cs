using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SpiritNest.Services;

/// <summary>Win32 rounded window region for menus and dialogs.</summary>
public static class AcrylicHelper
{
    public const int CornerDoNotRound = 1;

    public static void TrySetCornerPreference(IntPtr hwnd, int preference)
    {
        try
        {
            const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            var corner = preference;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }
        catch { /* ignore */ }
    }

    /// <summary>Round a popup / context-menu HWND via Win32 region (reliable on layered popups).</summary>
    public static void ApplyRoundRegionToVisual(Visual visual, double radiusDip)
    {
        try
        {
            if (PresentationSource.FromVisual(visual) is not HwndSource { Handle: var hwnd })
                return;
            if (hwnd == IntPtr.Zero) return;
            ApplyRoundRegionHwnd(visual, hwnd, radiusDip);
        }
        catch { /* ignore */ }
    }

    private static void ApplyRoundRegionHwnd(Visual visual, IntPtr hwnd, double radiusDip)
    {
        var fe = visual as FrameworkElement;
        var wDip = fe?.ActualWidth > 1 ? fe.ActualWidth : (fe?.Width ?? 0);
        var hDip = fe?.ActualHeight > 1 ? fe.ActualHeight : (fe?.Height ?? 0);
        if ((wDip < 2 || hDip < 2) && fe is not null)
        {
            fe.UpdateLayout();
            wDip = fe.ActualWidth;
            hDip = fe.ActualHeight;
        }
        if (wDip < 2 || hDip < 2) return;

        var source = HwndSource.FromHwnd(hwnd);
        var m = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var w = Math.Max(1, (int)Math.Round(wDip * m.M11));
        var h = Math.Max(1, (int)Math.Round(hDip * m.M22));
        var ellipse = Math.Max(2, (int)Math.Round(radiusDip * 2 * Math.Max(m.M11, m.M22)));

        var rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, ellipse, ellipse);
        if (rgn == IntPtr.Zero) return;
        if (SetWindowRgn(hwnd, rgn, true) == 0)
            DeleteObject(rgn);
        TrySetCornerPreference(hwnd, CornerDoNotRound);
    }

    public static void ApplyUniformRoundRegion(Window window, double radiusDip = 8)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            if (hwnd == IntPtr.Zero) return;

            var source = PresentationSource.FromVisual(window) as HwndSource ?? HwndSource.FromHwnd(hwnd);
            var m = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            var wDip = window.ActualWidth > 1 ? window.ActualWidth : window.Width;
            var hDip = window.ActualHeight > 1 ? window.ActualHeight : window.Height;
            if (wDip < 2 || hDip < 2) return;

            var w = Math.Max(1, (int)Math.Round(wDip * m.M11));
            var h = Math.Max(1, (int)Math.Round(hDip * m.M22));
            var ellipse = Math.Max(2, (int)Math.Round(radiusDip * 2 * Math.Max(m.M11, m.M22)));

            var rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, ellipse, ellipse);
            if (rgn == IntPtr.Zero) return;
            if (SetWindowRgn(hwnd, rgn, true) == 0)
                DeleteObject(rgn);

            TrySetCornerPreference(hwnd, CornerDoNotRound);
        }
        catch { /* ignore */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
