using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace SpiritNest.Services;

/// <summary>Loads engine logos from Assets/*.svg (file name may differ from engine id).</summary>
public static class SearchEngineIcons
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string CustomKey(string engineId) => "engine_" + engineId;

    public static void Invalidate(string engineId)
    {
        Cache.TryRemove(engineId, out _);
        Cache.TryRemove("png:" + engineId, out _);
    }

    public static ImageSource? Get(string? engineId)
    {
        if (string.IsNullOrWhiteSpace(engineId)) return null;
        var custom = IconService.UrlIconCachePath(CustomKey(engineId));
        if (File.Exists(custom))
            return Cache.GetOrAdd("png:" + engineId, _ => LoadBitmap(custom));
        return Cache.GetOrAdd(engineId, Load);
    }

    private static ImageSource? LoadBitmap(string path)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? Load(string engineId)
    {
        var file = engineId.ToLowerInvariant() switch
        {
            "bing" => "bing.svg",
            "baidu" => "baidu.svg",
            "google" => "google.svg",
            "sogou" or "sougou" => "sougou.svg",
            _ => null
        };
        if (file is null) return null;

        var path = Path.Combine(AppContext.BaseDirectory, "Assets", file);
        if (!File.Exists(path))
            path = Path.Combine(AppContext.BaseDirectory, file);
        if (!File.Exists(path)) return null;

        try
        {
            var settings = new WpfDrawingSettings
            {
                IncludeRuntime = false,
                TextAsGeometry = false
            };
            using var reader = new FileSvgReader(settings);
            var drawing = reader.Read(path);
            if (drawing is null) return null;
            var img = new DrawingImage(drawing);
            img.Freeze();
            return img;
        }
        catch
        {
            return null;
        }
    }
}
