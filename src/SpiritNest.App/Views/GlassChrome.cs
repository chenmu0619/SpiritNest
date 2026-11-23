using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SpiritNest.Models;
using SpiritNest.Services;

namespace SpiritNest.Views;

/// <summary>
/// Shared frosted chrome. Uses layered round windows (no system acrylic HWND)
/// so corners never leak as squares.
/// </summary>
public static class GlassChrome
{
    public static readonly SolidColorBrush FallbackFill = Freeze(Color.FromRgb(0xFF, 0xF8, 0xF0));
    public static readonly SolidColorBrush OverlayFill = Freeze(Color.FromRgb(0xFF, 0xF8, 0xF0));
    public static readonly SolidColorBrush Edge = Freeze(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
    public static readonly SolidColorBrush Ink = Freeze(Color.FromRgb(0x1A, 0x24, 0x20));
    public static readonly SolidColorBrush Muted = Freeze(Color.FromRgb(0x6B, 0x7A, 0x72));
    public static readonly SolidColorBrush Line = Freeze(Color.FromRgb(0xC5, 0xCD, 0xC8));
    public static readonly SolidColorBrush FieldBg = Freeze(Color.FromRgb(0xFF, 0xFF, 0xFF));
    public static readonly SolidColorBrush Hover = Freeze(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
    public static readonly SolidColorBrush Press = Freeze(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    public static readonly SolidColorBrush PrimaryFill = Freeze(Color.FromArgb(0x66, 0xE8, 0xEC, 0xE9));

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public const double CornerRadiusDip = 12;

    public static void Attach(Window window, double radiusDip = CornerRadiusDip)
    {
        window.WindowStyle = WindowStyle.None;
        window.AllowsTransparency = true;
        window.Background = Brushes.Transparent;

        void Apply()
        {
            var live = (Application.Current as SpiritNest.App)?.CurrentSettings ?? new AppSettings();
            var palette = ThemeService.Resolve(live);
            ApplyTheme(window, palette, live, radiusDip);
        }

        if (window.IsInitialized)
            Apply();
        else
            window.SourceInitialized += (_, _) => Apply();

        window.Loaded += (_, _) => RoundWindow(window, radiusDip);
        window.ContentRendered += (_, _) => RoundWindow(window, radiusDip);
        window.SizeChanged += (_, _) => RoundWindow(window, radiusDip);
    }

    public static void ApplyTheme(Window window, ThemePalette palette, AppSettings settings,
        double radiusDip = CornerRadiusDip)
    {
        // Tint the root Border if present; opacity + blur % apply to all chrome surfaces
        if (window.Content is Border shell)
        {
            var a = ThemeService.SurfaceFillAlpha(settings);
            var rgb = palette.PanelRgb;
            shell.Background = new SolidColorBrush(Color.FromArgb(a, rgb.R, rgb.G, rgb.B));
            shell.BorderBrush = ThemeService.Brush(palette.Border);
            shell.CornerRadius = new CornerRadius(radiusDip);
            CornerClip.AttachBorder(shell);
        }

        RoundWindow(window, radiusDip);
    }

    private static void RoundWindow(Window window, double radiusDip)
    {
        if (window.Content is FrameworkElement fe)
            CornerClip.Attach(fe, radiusDip);
        AcrylicHelper.ApplyUniformRoundRegion(window, radiusDip);
    }

    public static void ApplyLocalBrushes(ResourceDictionary resources, ThemePalette palette)
    {
        SetBrush(resources, "Ink", palette.Text);
        SetBrush(resources, "Muted", palette.TextMuted);
        SetBrush(resources, "Line", palette.Border);
        SetBrush(resources, "Hover", palette.Hover);
        SetBrush(resources, "Press", palette.Press);
        // Opaque field / dropdown surface (translucent white washes out dark text contrast)
        SetBrush(resources, "FieldBg", palette.IsDark
            ? Color.FromRgb(
                (byte)Math.Min(255, palette.PanelRgb.R + 14),
                (byte)Math.Min(255, palette.PanelRgb.G + 14),
                (byte)Math.Min(255, palette.PanelRgb.B + 14))
            : Color.FromRgb(247, 248, 247));
        SetBrush(resources, "PrimaryFill", palette.AccentSoft);
        SetBrush(resources, "CheckFill", palette.Accent);
        SetBrush(resources, "SliderTrack", palette.IsDark
            ? Color.FromArgb(72, 255, 255, 255)
            : Color.FromArgb(48, 0, 0, 0));
        SetBrush(resources, "Surface", Color.FromArgb(0, 255, 255, 255));
    }

    private static void SetBrush(ResourceDictionary resources, string key, Color color)
    {
        // Keep unfrozen brushes so StaticResource/DynamicResource bindings update in place
        if (resources.Contains(key) && resources[key] is SolidColorBrush existing && !existing.IsFrozen)
        {
            existing.Color = color;
            return;
        }

        resources[key] = new SolidColorBrush(color);
    }

    public static void DragFromChrome(Window window, MouseButtonEventArgs e, DependencyObject? originalSource)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var d = originalSource;
        while (d is not null)
        {
            if (d is System.Windows.Controls.TextBox or System.Windows.Controls.Button
                or System.Windows.Controls.ComboBox or System.Windows.Controls.ListBox
                or System.Windows.Controls.Slider or System.Windows.Controls.CheckBox
                or System.Windows.Controls.ScrollViewer or System.Windows.Controls.Image)
                return;
            d = VisualTreeHelper.GetParent(d);
        }
        try { window.DragMove(); } catch { /* ignore */ }
    }
}
