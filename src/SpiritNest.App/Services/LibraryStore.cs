using System.IO;
using System.Text.Json;
using SpiritNest.Models;

namespace SpiritNest.Services;

public sealed class LibraryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public LibraryData Current { get; private set; } = new();

    public void Load()
    {
        AppPaths.EnsureCreated();
        if (!File.Exists(AppPaths.LibraryFile))
        {
            Current = CreateDefault();
            Save();
            return;
        }

        try
        {
            var json = File.ReadAllText(AppPaths.LibraryFile);
            Current = JsonSerializer.Deserialize<LibraryData>(json, JsonOptions) ?? CreateDefault();
            if (Current.Categories.Count == 0)
            {
                Current = CreateDefault();
                Save();
            }
        }
        catch
        {
            Current = CreateDefault();
        }
    }

    public void Save()
    {
        AppPaths.EnsureCreated();
        File.WriteAllText(AppPaths.LibraryFile, JsonSerializer.Serialize(Current, JsonOptions));
    }

    private static LibraryData CreateDefault() => new()
    {
        Categories = [new CategoryInfo { Name = "默认", SortOrder = 0 }],
        Items = []
    };

    public CategoryInfo? FindCategory(string categoryId) =>
        Current.Categories.FirstOrDefault(c => c.Id == categoryId);

    public int CountItems(string categoryId) =>
        Current.Items.Count(i => i.CategoryId == categoryId);

    public bool RenameCategory(string categoryId, string newName)
    {
        var name = newName.Trim();
        if (string.IsNullOrWhiteSpace(name)) return false;
        var cat = FindCategory(categoryId);
        if (cat is null) return false;
        if (Current.Categories.Any(c =>
                c.Id != categoryId &&
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            return false;
        cat.Name = name;
        Save();
        return true;
    }

    /// <summary>
    /// Reorder a category relative to another: insert before or after <paramref name="relativeToId"/>.
    /// </summary>
    public bool ReorderCategory(string categoryId, string relativeToId, bool insertBefore)
    {
        if (categoryId == relativeToId) return false;
        var ordered = Current.Categories.OrderBy(c => c.SortOrder).ToList();
        var moving = ordered.FirstOrDefault(c => c.Id == categoryId);
        if (moving is null || ordered.All(c => c.Id != relativeToId)) return false;

        ordered.Remove(moving);
        var idx = ordered.FindIndex(c => c.Id == relativeToId);
        if (idx < 0) return false;
        if (!insertBefore) idx++;
        ordered.Insert(Math.Clamp(idx, 0, ordered.Count), moving);

        for (var i = 0; i < ordered.Count; i++)
            ordered[i].SortOrder = i;
        Save();
        return true;
    }

    /// <summary>
    /// Move every item in <paramref name="fromCategoryId"/> to <paramref name="toCategoryId"/>.
    /// Returns how many items were moved.
    /// </summary>
    public int MoveAllItems(string fromCategoryId, string toCategoryId)
    {
        if (fromCategoryId == toCategoryId) return 0;
        if (FindCategory(fromCategoryId) is null || FindCategory(toCategoryId) is null) return 0;
        var ids = Current.Items
            .Where(i => i.CategoryId == fromCategoryId)
            .OrderBy(i => i.SortOrder)
            .Select(i => i.Id)
            .ToList();
        return MoveItems(ids, toCategoryId);
    }

    /// <summary>Move specific items to a category (append). Returns how many moved or re-homed.</summary>
    public int MoveItems(IEnumerable<string> itemIds, string toCategoryId) =>
        MoveItemsAt(itemIds, toCategoryId, insertBeforeIndex: int.MaxValue);

    /// <summary>
    /// Move items into <paramref name="toCategoryId"/>, inserting before the given index
    /// among items that remain in that category (excluding the moved set).
    /// </summary>
    public int MoveItemsAt(IEnumerable<string> itemIds, string toCategoryId, int insertBeforeIndex)
    {
        if (FindCategory(toCategoryId) is null) return 0;

        var idSet = itemIds.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToHashSet();
        if (idSet.Count == 0) return 0;

        var moving = Current.Items
            .Where(i => idSet.Contains(i.Id))
            .OrderBy(i => i.SortOrder)
            .ToList();
        if (moving.Count == 0) return 0;

        var others = Current.Items
            .Where(i => i.CategoryId == toCategoryId && !idSet.Contains(i.Id))
            .OrderBy(i => i.SortOrder)
            .ToList();

        var at = Math.Clamp(insertBeforeIndex, 0, others.Count);
        others.InsertRange(at, moving);

        for (var i = 0; i < others.Count; i++)
        {
            others[i].CategoryId = toCategoryId;
            others[i].SortOrder = i;
        }

        Save();
        return moving.Count;
    }

    /// <summary>
    /// Delete a category. Items must already be empty, or pass <paramref name="moveItemsToCategoryId"/>.
    /// Cannot delete the last remaining category.
    /// </summary>
    public bool DeleteCategory(string categoryId, string? moveItemsToCategoryId, out string error)
    {
        error = "";
        var cat = FindCategory(categoryId);
        if (cat is null)
        {
            error = "标签不存在";
            return false;
        }
        if (Current.Categories.Count <= 1)
        {
            error = "至少保留一个标签";
            return false;
        }

        var count = CountItems(categoryId);
        if (count > 0)
        {
            if (string.IsNullOrEmpty(moveItemsToCategoryId) ||
                moveItemsToCategoryId == categoryId ||
                FindCategory(moveItemsToCategoryId) is null)
            {
                error = "请先将图标移到其他标签，或指定目标标签";
                return false;
            }
            MoveAllItems(categoryId, moveItemsToCategoryId);
        }

        Current.Categories.Remove(cat);
        // Compact sort orders
        var order = 0;
        foreach (var c in Current.Categories.OrderBy(c => c.SortOrder).ToList())
            c.SortOrder = order++;
        Save();

        try
        {
            var dir = Path.Combine(AppPaths.ItemsRoot, categoryId);
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir, false);
        }
        catch { /* ignore */ }

        return true;
    }
}
