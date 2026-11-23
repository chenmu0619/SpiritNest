using SpiritNest.Models;

namespace SpiritNest.Services;

public sealed class CollectService
{
    private static readonly HashSet<string> SupportedExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lnk", ".url", ".exe", ".bat", ".cmd"
    };

    private readonly LibraryStore _library;

    public CollectService(LibraryStore library) => _library = library;

    public IReadOnlyList<FileInfo> ScanUserDesktop()
    {
        var desktop = DesktopPaths.GetUserDesktop();
        if (!Directory.Exists(desktop)) return [];
        return Directory.EnumerateFiles(desktop)
            .Where(f => SupportedExt.Contains(Path.GetExtension(f)))
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.Name)
            .ToList();
    }

    public CollectResult CollectFile(string sourcePath, string categoryId, bool preferMoveFromDesktop = true)
    {
        try
        {
            if (!File.Exists(sourcePath))
                return CollectResult.Fail(sourcePath, "文件不存在");

            // Already inside nest storage — never re-collect (avoids duplicates from panel reorder)
            try
            {
                var full = Path.GetFullPath(sourcePath);
                var itemsRoot = Path.GetFullPath(AppPaths.ItemsRoot)
                    .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (full.StartsWith(itemsRoot, StringComparison.OrdinalIgnoreCase))
                    return CollectResult.Fail(sourcePath, "已在灵栖中");

                if (_library.Current.Items.Any(i =>
                        string.Equals(i.StoredPath, full, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(i.TargetPath, full, StringComparison.OrdinalIgnoreCase)))
                    return CollectResult.Fail(sourcePath, "已在灵栖中");
            }
            catch { /* continue */ }

            var ext = Path.GetExtension(sourcePath);
            if (!SupportedExt.Contains(ext))
                return CollectResult.Fail(sourcePath, "不支持的类型");

            var originalDir = Path.GetDirectoryName(sourcePath) ?? "";
            var originalName = Path.GetFileName(sourcePath);
            var displayName = Path.GetFileNameWithoutExtension(sourcePath);
            var onDesktop = DesktopPaths.IsUnderUserDesktop(sourcePath);
            var isExe = ext.Equals(".exe", StringComparison.OrdinalIgnoreCase);
            var isSystemish = IsInstallDirectory(sourcePath);

            string itemKind = ext.ToLowerInvariant() switch
            {
                ".lnk" => "Lnk",
                ".url" => "Url",
                ".exe" => "Exe",
                _ => "Other"
            };

            AppPaths.EnsureCreated();
            var catDir = Path.Combine(AppPaths.ItemsRoot, categoryId);
            Directory.CreateDirectory(catDir);

            ItemInfo item;
            if (onDesktop && preferMoveFromDesktop && (!isExe || !isSystemish))
            {
                var dest = UniquePath(Path.Combine(catDir, originalName));
                File.Move(sourcePath, dest);
                var target = ShortcutHelper.ResolveTarget(dest) ?? dest;
                item = new ItemInfo
                {
                    CategoryId = categoryId,
                    DisplayName = displayName,
                    ItemKind = itemKind,
                    StorageMode = "MovedFile",
                    StoredPath = dest,
                    TargetPath = target,
                    OriginalDirectory = originalDir,
                    OriginalFileName = originalName,
                    Source = "UserDesktop",
                    SortOrder = NextSort(categoryId),
                    IconCacheKey = null
                };
            }
            else if (isSystemish || (!onDesktop && isExe))
            {
                // Reference only — create nest .lnk pointing to target
                var linkName = displayName + ".lnk";
                var dest = UniquePath(Path.Combine(catDir, linkName));
                ShortcutHelper.CreateShortcut(dest, sourcePath, displayName);
                item = new ItemInfo
                {
                    CategoryId = categoryId,
                    DisplayName = displayName,
                    ItemKind = "ReferenceLnk",
                    StorageMode = "PathReference",
                    StoredPath = dest,
                    TargetPath = sourcePath,
                    OriginalDirectory = originalDir,
                    OriginalFileName = originalName,
                    Source = onDesktop ? "UserDesktop" : "Explorer",
                    SortOrder = NextSort(categoryId)
                };
            }
            else
            {
                var dest = UniquePath(Path.Combine(catDir, originalName));
                File.Copy(sourcePath, dest);
                var target = ShortcutHelper.ResolveTarget(dest) ?? dest;
                item = new ItemInfo
                {
                    CategoryId = categoryId,
                    DisplayName = displayName,
                    ItemKind = itemKind,
                    StorageMode = "MovedFile",
                    StoredPath = dest,
                    TargetPath = target,
                    OriginalDirectory = originalDir,
                    OriginalFileName = originalName,
                    Source = onDesktop ? "UserDesktop" : "Explorer",
                    SortOrder = NextSort(categoryId)
                };
                if (onDesktop)
                {
                    try { File.Delete(sourcePath); } catch { /* keep copy if delete fails */ }
                }
            }

            RefreshBroken(item);
            _library.Current.Items.Add(item);
            _library.Save();
            return CollectResult.Ok(item);
        }
        catch (Exception ex)
        {
            return CollectResult.Fail(sourcePath, ex.Message);
        }
    }

    public RestoreResult RestoreToOriginal(ItemInfo item)
    {
        try
        {
            var dir = item.OriginalDirectory;
            var name = item.OriginalFileName ?? (item.DisplayName + Path.GetExtension(item.StoredPath ?? ".lnk"));

            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                dir = DesktopPaths.GetUserDesktop();

            Directory.CreateDirectory(dir!);
            var dest = UniquePath(Path.Combine(dir!, name!));

            if (item.StorageMode == "MovedFile" && !string.IsNullOrEmpty(item.StoredPath) && File.Exists(item.StoredPath))
            {
                File.Move(item.StoredPath, dest);
            }
            else
            {
                var target = item.TargetPath;
                if (string.IsNullOrWhiteSpace(target))
                    return RestoreResult.Fail(item, "无目标路径");
                var linkPath = Path.ChangeExtension(dest, ".lnk") ?? dest + ".lnk";
                linkPath = UniquePath(linkPath);
                ShortcutHelper.CreateShortcut(linkPath, target, item.DisplayName);
            }

            _library.Current.Items.RemoveAll(i => i.Id == item.Id);
            _library.Save();
            return RestoreResult.Ok(item);
        }
        catch (Exception ex)
        {
            return RestoreResult.Fail(item, ex.Message);
        }
    }

    public void RemoveFromNest(ItemInfo item, bool deleteStoredFile)
    {
        if (deleteStoredFile && !string.IsNullOrEmpty(item.StoredPath) && File.Exists(item.StoredPath))
        {
            try { File.Delete(item.StoredPath); } catch { /* ignore */ }
        }
        _library.Current.Items.RemoveAll(i => i.Id == item.Id);
        _library.Save();
    }

    public int RefreshAllBroken()
    {
        var n = 0;
        foreach (var item in _library.Current.Items)
        {
            var before = item.IsBroken;
            RefreshBroken(item);
            if (item.IsBroken != before) n++;
        }
        _library.Save();
        return n;
    }

    public static void RefreshBroken(ItemInfo item)
    {
        try
        {
            if (string.Equals(item.ItemKind, "Widget", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.StorageMode, "Widget", StringComparison.OrdinalIgnoreCase)
                || ShellPlaces.IsShell(item))
            {
                item.IsBroken = false;
                item.BrokenReason = null;
                return;
            }

            if (string.Equals(item.ItemKind, "Url", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.StorageMode, "Url", StringComparison.OrdinalIgnoreCase))
            {
                var u = item.TargetPath ?? "";
                if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    item.IsBroken = false;
                    item.BrokenReason = null;
                }
                else
                {
                    item.IsBroken = true;
                    item.BrokenReason = "InvalidUrl";
                }
                return;
            }

            if (item.StorageMode == "MovedFile")
            {
                if (string.IsNullOrEmpty(item.StoredPath) || !File.Exists(item.StoredPath))
                {
                    item.IsBroken = true;
                    item.BrokenReason = "TargetMissing";
                    return;
                }
                var resolved = ShortcutHelper.ResolveTarget(item.StoredPath);
                if (item.ItemKind is "Lnk" or "ReferenceLnk")
                {
                    if (string.IsNullOrEmpty(resolved) ||
                        (!resolved.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                         && !File.Exists(resolved) && !Directory.Exists(resolved)))
                    {
                        item.IsBroken = true;
                        item.BrokenReason = "TargetMissing";
                        return;
                    }
                }
            }
            else
            {
                var t = item.TargetPath;
                if (string.IsNullOrEmpty(t) ||
                    (!t.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                     && !File.Exists(t) && !Directory.Exists(t)))
                {
                    item.IsBroken = true;
                    item.BrokenReason = "TargetMissing";
                    return;
                }
            }

            item.IsBroken = false;
            item.BrokenReason = null;
        }
        catch
        {
            item.IsBroken = true;
            item.BrokenReason = "InvalidLnk";
        }
    }

    public int CleanupBroken(bool deleteStoredFiles)
    {
        var broken = _library.Current.Items.Where(i => i.IsBroken).ToList();
        foreach (var item in broken)
            RemoveFromNest(item, deleteStoredFiles);
        return broken.Count;
    }

    private int NextSort(string categoryId) =>
        _library.Current.Items.Where(i => i.CategoryId == categoryId).Select(i => i.SortOrder).DefaultIfEmpty(-1).Max() + 1;

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{name}_{Guid.NewGuid():N}{ext}");
    }

    private static bool IsInstallDirectory(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            return full.StartsWith(pf + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   || full.StartsWith(pf86 + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   || full.Contains(@"\Windows\System32\", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

public readonly record struct CollectResult(bool Success, ItemInfo? Item, string Path, string? Error)
{
    public static CollectResult Ok(ItemInfo item) => new(true, item, item.StoredPath ?? "", null);
    public static CollectResult Fail(string path, string error) => new(false, null, path, error);
}

public readonly record struct RestoreResult(bool Success, ItemInfo Item, string? Error)
{
    public static RestoreResult Ok(ItemInfo item) => new(true, item, null);
    public static RestoreResult Fail(ItemInfo item, string error) => new(false, item, error);
}
