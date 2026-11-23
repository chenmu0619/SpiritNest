using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace SpiritNest.Services;

public sealed class IconService
{
    /// <summary>Cache suffix bump: regenerate icons without shortcut overlay arrows.</summary>
    private const string CacheSuffix = "_na";

    public BitmapImage? GetIndexedIcon(string file, int index, string cacheKey)
    {
        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(cacheKey)) return null;
        try
        {
            AppPaths.EnsureCreated();
            var cacheFile = Path.Combine(AppPaths.IconCacheRoot, cacheKey + CacheSuffix + ".png");
            if (!File.Exists(cacheFile))
            {
                using var icon = ExtractByIndex(file, index);
                if (icon is null) return null;
                using var bmp = icon.ToBitmap();
                bmp.Save(cacheFile, ImageFormat.Png);
            }

            return LoadPng(cacheFile);
        }
        catch
        {
            return null;
        }
    }

    public BitmapImage? GetIcon(string? path, string? cacheKey = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            AppPaths.EnsureCreated();
            var key = cacheKey ?? Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..16];
            var cacheFile = Path.Combine(AppPaths.IconCacheRoot, key + CacheSuffix + ".png");

            if (!File.Exists(cacheFile) || IsStale(cacheFile, path))
            {
                using var icon = ExtractIconNoOverlay(path);
                if (icon is null) return null;
                using var bmp = icon.ToBitmap();
                bmp.Save(cacheFile, ImageFormat.Png);
            }

            return LoadPng(cacheFile);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extract icon without the Windows shortcut overlay arrow.
    /// For .lnk, prefer IconLocation / TargetPath instead of the .lnk file itself.
    /// </summary>
    private static Icon? ExtractIconNoOverlay(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var fromLnk = ExtractFromShortcut(path);
            if (fromLnk is not null) return fromLnk;
        }

        if (ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            // .url has no overlay via associated icon in most cases; still try target icon first
            var url = ShortcutHelper.ResolveTarget(path);
            if (!string.IsNullOrWhiteSpace(url) && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                // keep associated (browser) icon
            }
        }

        if (path.StartsWith("::", StringComparison.Ordinal) ||
            path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            return ExtractViaShell(path);

        return SafeExtractAssociated(path);
    }

    private static Icon? ExtractFromShortcut(string linkPath)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            var sc = shell.CreateShortcut(linkPath);

            string? iconLoc = null;
            try { iconLoc = sc.IconLocation as string; } catch { /* ignore */ }

            if (!string.IsNullOrWhiteSpace(iconLoc) &&
                TryParseIconLocation(iconLoc, out var iconPath, out var index))
            {
                iconPath = Environment.ExpandEnvironmentVariables(iconPath);
                var byIndex = ExtractByIndex(iconPath, index);
                if (byIndex is not null) return byIndex;
                var assoc = SafeExtractAssociated(iconPath);
                if (assoc is not null) return assoc;
            }

            string? target = null;
            try { target = sc.TargetPath as string; } catch { /* ignore */ }
            if (!string.IsNullOrWhiteSpace(target))
            {
                target = Environment.ExpandEnvironmentVariables(target);
                if (File.Exists(target))
                {
                    var fromTarget = SafeExtractAssociated(target);
                    if (fromTarget is not null) return fromTarget;
                }
                if (Directory.Exists(target))
                {
                    var fromDir = ExtractViaShell(target);
                    if (fromDir is not null) return fromDir;
                }
            }
        }
        catch
        {
            /* fall through */
        }

        // Last resort: shell icon for the .lnk (may include overlay on some systems)
        return ExtractViaShell(linkPath) ?? SafeExtractAssociated(linkPath);
    }

    private static bool TryParseIconLocation(string iconLoc, out string path, out int index)
    {
        path = iconLoc.Trim();
        index = 0;
        if (path.Length == 0) return false;

        // WScript format: "C:\path\app.exe,0" — split on last comma when trailing token is int
        var comma = path.LastIndexOf(',');
        if (comma > 0 && comma < path.Length - 1)
        {
            var idxPart = path[(comma + 1)..].Trim();
            if (int.TryParse(idxPart, out var idx))
            {
                index = idx;
                path = path[..comma].Trim().Trim('"');
                return path.Length > 0;
            }
        }

        path = path.Trim('"');
        return path.Length > 0;
    }

    private static Icon? ExtractByIndex(string file, int index)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        if (!File.Exists(file) && !Directory.Exists(file)) return null;

        var large = new IntPtr[1];
        var small = new IntPtr[1];
        try
        {
            var count = ExtractIconEx(file, index, large, small, 1);
            if (count == 0)
                return null;

            Icon? result = null;
            if (large[0] != IntPtr.Zero)
            {
                using var tmp = Icon.FromHandle(large[0]);
                result = (Icon)tmp.Clone();
            }
            else if (small[0] != IntPtr.Zero)
            {
                using var tmp = Icon.FromHandle(small[0]);
                result = (Icon)tmp.Clone();
            }

            return result;
        }
        finally
        {
            if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
            if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
        }
    }

    private static Icon? ExtractViaShell(string path)
    {
        var shinfo = new SHFILEINFO();
        // Do not request overlays — SHGFI_ICON alone still often adds shortcut arrow for .lnk.
        // Prefer large icon from the item when we already resolved target; used mainly for folders.
        var flags = SHGFI_ICON | SHGFI_LARGEICON;
        var h = SHGetFileInfo(path, 0, ref shinfo, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (h == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero) return null;
        try
        {
            using var tmp = Icon.FromHandle(shinfo.hIcon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(shinfo.hIcon);
        }
    }

    private static Icon? SafeExtractAssociated(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return Icon.ExtractAssociatedIcon(path);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapImage LoadPng(string cacheFile)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.UriSource = new Uri(cacheFile);
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    private static bool IsStale(string cacheFile, string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath)) return false;
            return File.GetLastWriteTimeUtc(sourcePath) > File.GetLastWriteTimeUtc(cacheFile);
        }
        catch { return true; }
    }

    public static string UrlIconCachePath(string cacheKey)
    {
        AppPaths.EnsureCreated();
        return Path.Combine(AppPaths.IconCacheRoot, cacheKey + "_nav.png");
    }

    public BitmapImage? GetUrlIcon(string? cacheKey)
    {
        if (string.IsNullOrWhiteSpace(cacheKey)) return GetUrlPlaceholder();
        var file = UrlIconCachePath(cacheKey);
        if (!File.Exists(file)) return GetUrlPlaceholder();
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(file);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            return GetUrlPlaceholder();
        }
    }

    public void SaveUrlIcon(string cacheKey, byte[] pngBytes)
    {
        if (string.IsNullOrWhiteSpace(cacheKey) || pngBytes.Length == 0) return;
        AppPaths.EnsureCreated();
        File.WriteAllBytes(UrlIconCachePath(cacheKey), pngBytes);
    }

    public void SaveUrlIconFromFile(string cacheKey, string imagePath)
    {
        var bytes = File.ReadAllBytes(imagePath);
        var png = TryNormalizeToPng(bytes) ?? bytes;
        SaveUrlIcon(cacheKey, png);
    }

    public void ClearUrlIcon(string cacheKey)
    {
        if (string.IsNullOrWhiteSpace(cacheKey)) return;
        var file = UrlIconCachePath(cacheKey);
        try { if (File.Exists(file)) File.Delete(file); } catch { /* ignore */ }
    }

    private static BitmapImage? _urlPlaceholder;

    public BitmapImage GetUrlPlaceholder()
    {
        if (_urlPlaceholder is not null) return _urlPlaceholder;

        // High-res flat globe — stays crisp when downscaled to panel / dialog sizes
        const int s = 128;
        var bmp = new System.Drawing.Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;

            var pad = 8;
            var disk = new System.Drawing.Rectangle(pad, pad, s - pad * 2, s - pad * 2);
            using (var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x3D, 0x8B, 0x63)))
                g.FillEllipse(fill, disk);

            using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(0xF5, 0xF7, 0xF6), 3.5f)
            {
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round
            };
            // Meridian
            g.DrawEllipse(pen, pad + 28, pad, s - pad * 2 - 56, s - pad * 2);
            // Equator + tropics
            g.DrawLine(pen, pad + 4, s / 2, s - pad - 4, s / 2);
            g.DrawArc(pen, pad + 8, pad + 28, s - pad * 2 - 16, s / 2 - 20, 200, 140);
            g.DrawArc(pen, pad + 8, s / 2 - 8, s - pad * 2 - 16, s / 2 - 20, 20, 140);
            // Outer rim for definition
            using var rim = new System.Drawing.Pen(System.Drawing.Color.FromArgb(0x2A, 0x6B, 0x4A), 2f);
            g.DrawEllipse(rim, disk);
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        bmp.Dispose();
        _urlPlaceholder = bi;
        return bi;
    }

    /// <summary>Accept PNG/JPEG/GIF/ICO bytes; return PNG bytes or null.</summary>
    public static byte[]? TryNormalizeToPng(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var img = System.Drawing.Image.FromStream(ms);
            using var outMs = new MemoryStream();
            img.Save(outMs, ImageFormat.Png);
            return outMs.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("Shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string lpszFile, int nIconIndex, IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
