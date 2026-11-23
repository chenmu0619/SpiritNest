using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace SpiritNest.Services;

/// <summary>
/// Win32 coordinates are physical pixels. Layout uses DIPs (96 per inch) so a
/// small high-resolution screen does not shrink the UI.
/// </summary>
public static class ScreenDpi
{
    public static double ScaleOfWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 1;
        var dpi = GetDpiForWindow(hwnd);
        return dpi >= 96 ? dpi / 96.0 : 1;
    }

    public static double ScaleOfMonitor(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero || GetDpiForMonitor(monitor, 0, out var dpiX, out _) != 0 || dpiX < 96)
            return 1;
        return dpiX / 96.0;
    }

    /// <summary>
    /// Extra scale when the window is still laid out at 96 DPI while the monitor is denser.
    /// 1 when WPF has already applied the monitor DPI.
    /// </summary>
    public static double ExtraScale(Window window)
    {
        double wpf = 1;
        try
        {
            wpf = VisualTreeHelper.GetDpi(window).DpiScaleX;
        }
        catch
        {
            /* not in a visual tree yet */
        }
        if (wpf < 0.5) wpf = 1;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var extra = ScaleOfWindow(hwnd) / wpf;
        return extra < 1.05 ? 1 : extra;
    }

    public static Point DeviceToDip(Point devicePixels, double dpiScale)
    {
        var s = dpiScale < 0.5 ? 1 : dpiScale;
        return new Point(devicePixels.X / s, devicePixels.Y / s);
    }

    /// <summary>Scale a dialog once, from its design size, when WPF did not already apply DPI.</summary>
    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) => Apply(window);
        window.DpiChanged += (_, _) => Apply(window);
    }

    private static void Apply(Window window)
    {
        if (window.Content is not FrameworkElement root) return;

        if (window.ReadLocalValue(DesignWidthProperty) == DependencyProperty.UnsetValue)
        {
            window.SetValue(DesignWidthProperty, window.Width);
            window.SetValue(DesignHeightProperty, window.Height);
        }

        var extra = ExtraScale(window);
        root.LayoutTransform = extra > 1.01
            ? new ScaleTransform(extra, extra)
            : Transform.Identity;

        var designW = (double)window.GetValue(DesignWidthProperty);
        var designH = (double)window.GetValue(DesignHeightProperty);
        if (!double.IsNaN(designW) && designW > 1)
            window.Width = designW * extra;
        if (!double.IsNaN(designH) && designH > 1
            && window.SizeToContent is not (SizeToContent.Height or SizeToContent.WidthAndHeight))
            window.Height = designH * extra;
    }

    private static readonly DependencyProperty DesignWidthProperty =
        DependencyProperty.RegisterAttached("DesignWidth", typeof(double), typeof(ScreenDpi), new PropertyMetadata(double.NaN));

    private static readonly DependencyProperty DesignHeightProperty =
        DependencyProperty.RegisterAttached("DesignHeight", typeof(double), typeof(ScreenDpi), new PropertyMetadata(double.NaN));

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
