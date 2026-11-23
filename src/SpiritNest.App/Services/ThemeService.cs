using System.Windows;
using System.Windows.Media;
using SpiritNest.Models;

namespace SpiritNest.Services;

public sealed class ThemePalette
{
    public bool IsDark { get; init; }
    public Color PanelRgb { get; init; }
    public Color Text { get; init; }
    public Color TextMuted { get; init; }
    public Color Border { get; init; }
    public Color Hover { get; init; }
    public Color Press { get; init; }
    public Color Accent { get; init; }
    public Color AccentSoft { get; init; }
    public Color PillSelected { get; init; }
    public Color ScrollThumb { get; init; }
    public Color Danger { get; init; } = Color.FromRgb(0xE0, 0x5C, 0x5C);
}

/// <summary>
/// Fixed product colors: island (notch/panel) = carbon black; all other chrome = milk white.
/// Theme mode switch removed.
/// </summary>
public static class ThemeService
{
    /// <summary>奶白 — settings, menus, dialogs.</summary>
    public static readonly Color MilkWhite = Color.FromRgb(0xFF, 0xF8, 0xF0);

    /// <summary>碳黑 — 灵动岛 / 展开面板 / 极简指示条.</summary>
    public static readonly Color IslandBlack = Color.FromRgb(0x1C, 0x1C, 0x1C);

    public static readonly Color AccentDefault = Color.FromRgb(0x4F, 0x9D, 0xFF);

    public static bool IsDarkEffective(AppSettings s) => false;

    public static bool IsSystemDark() => false;

    public static Color ParseAccent(string? hex)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hex)) return AccentDefault;
            var h = hex.Trim().TrimStart('#');
            if (h.Length == 6)
                return Color.FromRgb(
                    Convert.ToByte(h[..2], 16),
                    Convert.ToByte(h[2..4], 16),
                    Convert.ToByte(h[4..6], 16));
        }
        catch { /* ignore */ }
        return AccentDefault;
    }

    /// <summary>奶白色表面（设置 / 托盘菜单 / 对话框等）。</summary>
    public static ThemePalette Resolve(AppSettings s) => Chrome(s);

    public static ThemePalette Chrome(AppSettings? s = null)
    {
        var accent = ParseAccent(s?.AccentColor);
        return new ThemePalette
        {
            IsDark = false,
            PanelRgb = MilkWhite,
            Text = Color.FromRgb(0x1A, 0x18, 0x16),
            TextMuted = Color.FromRgb(0x7A, 0x72, 0x68),
            Border = Color.FromArgb(40, 0x40, 0x34, 0x28),
            Hover = Color.FromArgb(22, 0x40, 0x34, 0x28),
            Press = Color.FromArgb(36, 0x40, 0x34, 0x28),
            Accent = accent,
            AccentSoft = Color.FromArgb(40, accent.R, accent.G, accent.B),
            PillSelected = Colors.White,
            ScrollThumb = Color.FromArgb(100, 0x40, 0x34, 0x28)
        };
    }

    /// <summary>碳黑灵动岛 / 面板。</summary>
    public static ThemePalette Island(AppSettings? s = null)
    {
        var accent = ParseAccent(s?.AccentColor);
        return new ThemePalette
        {
            IsDark = true,
            PanelRgb = IslandBlack,
            Text = Color.FromRgb(0xF5, 0xF2, 0xEC),
            TextMuted = Color.FromRgb(0xA8, 0xA0, 0x96),
            Border = Color.FromArgb(40, 255, 255, 255),
            Hover = Color.FromArgb(36, 255, 255, 255),
            Press = Color.FromArgb(52, 255, 255, 255),
            Accent = accent,
            AccentSoft = Color.FromArgb(60, accent.R, accent.G, accent.B),
            PillSelected = Color.FromArgb(44, 255, 255, 255),
            ScrollThumb = Color.FromArgb(110, 255, 255, 255)
        };
    }

    public static byte SurfaceFillAlpha(AppSettings s) => 255;

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static void Put(ResourceDictionary dict, string key, object value)
    {
        if (dict.Contains(key)) dict[key] = value;
        else dict.Add(key, value);
    }

    public static void ApplyResources(ResourceDictionary dict, ThemePalette p, AppSettings? settings = null)
    {
        PutMutable(dict, "Theme.Ink", p.Text);
        PutMutable(dict, "Theme.Muted", p.TextMuted);
        PutMutable(dict, "Theme.Border", p.Border);
        PutMutable(dict, "Theme.Hover", p.Hover);
        PutMutable(dict, "Theme.Press", p.Press);
        PutMutable(dict, "Theme.Accent", p.Accent);
        PutMutable(dict, "Theme.AccentSoft", p.AccentSoft);
        PutMutable(dict, "Theme.PillSelected", p.PillSelected);
        PutMutable(dict, "Theme.ScrollThumb", p.ScrollThumb);
        PutMutable(dict, "Theme.MenuBg", Color.FromArgb(255, p.PanelRgb.R, p.PanelRgb.G, p.PanelRgb.B));
        PutMutable(dict, "Theme.MenuBorder", p.Border);
        PutMutable(dict, "Theme.MenuDisabled", p.TextMuted);
        Put(dict, "Theme.PanelRgb", p.PanelRgb);
        Put(dict, "Theme.IsDark", p.IsDark);

        PutMutable(dict, "FlatScrollThumbBrush", p.ScrollThumb);
        PutMutable(dict, "FlatScrollThumbHoverBrush",
            Color.FromArgb((byte)Math.Min(255, p.ScrollThumb.A + 40), p.ScrollThumb.R, p.ScrollThumb.G, p.ScrollThumb.B));
        PutMutable(dict, "FlatScrollThumbDragBrush", p.Accent);
    }

    private static void PutMutable(ResourceDictionary dict, string key, Color color)
    {
        if (dict.Contains(key) && dict[key] is SolidColorBrush existing && !existing.IsFrozen)
        {
            existing.Color = color;
            return;
        }

        Put(dict, key, new SolidColorBrush(color));
    }
}
