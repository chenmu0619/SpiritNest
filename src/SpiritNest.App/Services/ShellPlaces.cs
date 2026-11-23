using System.Runtime.InteropServices;
using Microsoft.Win32;
using SpiritNest.Models;

namespace SpiritNest.Services;

/// <summary>此电脑 and 回收站. They live in the page list, and are not desktop shortcuts.</summary>
public static class ShellPlaces
{
    public const string Kind = "Shell";
    public const string ThisPcId = "shell-this-pc";
    public const string RecycleId = "shell-recycle-bin";

    public const string ThisPcPath = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
    public const string RecyclePath = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    public static bool IsShell(ItemInfo item) =>
        string.Equals(item.ItemKind, Kind, StringComparison.OrdinalIgnoreCase)
        || item.Id is ThisPcId or RecycleId;

    public static bool IsThisPc(ItemInfo item) =>
        item.Id == ThisPcId || string.Equals(item.TargetPath, ThisPcPath, StringComparison.OrdinalIgnoreCase);

    public static bool IsRecycleBin(ItemInfo item) =>
        item.Id == RecycleId || string.Equals(item.TargetPath, RecyclePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Raised on the UI thread when the bin goes from empty to full, or the other way.</summary>
    public static event Action? RecycleStateChanged;

    private static bool _recycleKnown;
    private static bool _recycleHasItems;

    public static bool RecycleHasItems()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        var hr = SHQueryRecycleBin(null, ref info);
        return hr == 0 && info.i64NumItems > 0;
    }

    public static void PollRecycleState()
    {
        var has = RecycleHasItems();
        var changed = _recycleKnown && has != _recycleHasItems;
        _recycleKnown = true;
        _recycleHasItems = has;
        if (changed) RecycleStateChanged?.Invoke();
    }

    /// <summary>Cache key includes empty/full so the two recycle icons stay distinct.</summary>
    public static string IconCacheKey(ItemInfo item) =>
        IsRecycleBin(item)
            ? RecycleHasItems() ? RecycleId + "-full" : RecycleId + "-empty"
            : ThisPcId + "-win";

    public static bool TryGetIconLocation(ItemInfo item, out string file, out int index)
    {
        var fallback = IsRecycleBin(item)
            ? RecycleHasItems()
                ? @"%SystemRoot%\System32\imageres.dll,-54"
                : @"%SystemRoot%\System32\imageres.dll,-55"
            : @"%SystemRoot%\System32\imageres.dll,-109";
        var spec = IsRecycleBin(item)
            ? ReadRecycleIcon(RecycleHasItems()) ?? fallback
            : ReadDefaultIcon("{20D04FE0-3AEA-1069-A2D8-08002B30309D}") ?? fallback;
        return TryParseIconSpec(spec, out file, out index);
    }

    private static string? ReadRecycleIcon(bool full)
    {
        var name = full ? "full" : "empty";
        return ReadIconValue("{645FF040-5081-101B-9F08-00AA002F954E}", name);
    }

    private static string? ReadDefaultIcon(string clsid) => ReadIconValue(clsid, null);

    private static string? ReadIconValue(string clsid, string? valueName)
    {
        foreach (var path in new[]
        {
            $@"Software\Microsoft\Windows\CurrentVersion\Explorer\CLSID\{clsid}\DefaultIcon",
            $@"SOFTWARE\Classes\CLSID\{clsid}\DefaultIcon"
        })
        {
            var hive = path.StartsWith("SOFTWARE\\Classes", StringComparison.Ordinal)
                ? Registry.LocalMachine
                : Registry.CurrentUser;
            using var key = hive.OpenSubKey(path);
            var value = key?.GetValue(valueName) as string;
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static bool TryParseIconSpec(string spec, out string file, out int index)
    {
        file = "";
        index = 0;
        spec = Environment.ExpandEnvironmentVariables(spec.Trim().Trim('"'));
        var comma = spec.LastIndexOf(',');
        if (comma <= 0 || !int.TryParse(spec[(comma + 1)..].Trim(), out index))
            return false;
        file = spec[..comma].Trim().Trim('"');
        return file.Length > 0 && File.Exists(file);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    public static bool Apply(AppSettings settings, LibraryStore library)
    {
        var changed = false;
        changed |= Set(library, settings.ShowThisPc, ThisPcId, "此电脑", ThisPcPath);
        changed |= Set(library, settings.ShowRecycleBin, RecycleId, "回收站", RecyclePath);
        if (changed) library.Save();
        return changed;
    }

    private static bool Set(LibraryStore library, bool enabled, string id, string name, string path)
    {
        var existing = library.Current.Items.FirstOrDefault(i => i.Id == id);
        if (!enabled)
        {
            if (existing is null) return false;
            library.Current.Items.Remove(existing);
            return true;
        }

        if (existing is not null) return false;
        var cat = library.Current.Categories.OrderBy(c => c.SortOrder).FirstOrDefault();
        if (cat is null) return false;
        var order = library.Current.Items
            .Where(i => i.CategoryId == cat.Id)
            .Select(i => i.SortOrder)
            .DefaultIfEmpty(-1)
            .Max() + 1;
        library.Current.Items.Add(new ItemInfo
        {
            Id = id,
            CategoryId = cat.Id,
            DisplayName = name,
            ItemKind = Kind,
            StorageMode = Kind,
            TargetPath = path,
            Source = Kind,
            SortOrder = order
        });
        return true;
    }
}
