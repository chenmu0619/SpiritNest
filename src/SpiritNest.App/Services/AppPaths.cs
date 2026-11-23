using System.IO;

namespace SpiritNest.Services;

public static class AppPaths
{
    private static readonly string LegacyRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpiritNest");

    /// <summary>Installed app data. Dev builds do not read or write this folder after the first copy.</summary>
    private static readonly string InstalledRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpiritPerch");

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
#if DEV_BUILD
        "SpiritPerchDev");
#else
        "SpiritPerch");
#endif

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LibraryFile => Path.Combine(Root, "library.json");
    public static string ItemsRoot => Path.Combine(Root, "items");
    public static string IconCacheRoot => Path.Combine(Root, "cache", "icons");

    public static void EnsureCreated()
    {
#if DEV_BUILD
        SeedFromInstalled();
#else
        MigrateLegacyRoot();
#endif
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ItemsRoot);
        Directory.CreateDirectory(IconCacheRoot);
    }

#if DEV_BUILD
    /// <summary>
    /// Copy the installed data folder once so the dev app starts with the current library,
    /// then leave the two folders independent.
    /// </summary>
    private static void SeedFromInstalled()
    {
        try
        {
            if (HasLibraryData(Root) || !HasLibraryData(InstalledRoot))
                return;

            CopyDirectory(InstalledRoot, Root);
            RewriteRootPaths(Root, InstalledRoot, Root);
        }
        catch
        {
            // Next launch tries again if the copy did not finish.
        }
    }

    private static bool HasLibraryData(string root) =>
        File.Exists(Path.Combine(root, "settings.json"))
        || File.Exists(Path.Combine(root, "library.json"));

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var target = Path.Combine(dest, Path.GetFileName(file));
            if (!File.Exists(target))
                File.Copy(file, target);
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    private static void RewriteRootPaths(string folder, string oldRoot, string newRoot)
    {
        var escapedOld = oldRoot.Replace("\\", "\\\\");
        var escapedNew = newRoot.Replace("\\", "\\\\");
        foreach (var file in Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var updated = text.Replace(escapedOld, escapedNew, StringComparison.OrdinalIgnoreCase)
                .Replace(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase);
            if (!string.Equals(text, updated, StringComparison.Ordinal))
                File.WriteAllText(file, updated);
        }
    }
#endif

    /// <summary>Move %AppData%\SpiritNest to SpiritPerch once, and fix absolute paths inside json.</summary>
    private static void MigrateLegacyRoot()
    {
        try
        {
            if (!Directory.Exists(LegacyRoot))
                return;

            if (Directory.Exists(Root))
            {
                var newHasData = File.Exists(SettingsFile) || File.Exists(LibraryFile);
                var oldHasData = File.Exists(Path.Combine(LegacyRoot, "settings.json"))
                                 || File.Exists(Path.Combine(LegacyRoot, "library.json"));
                if (newHasData || !oldHasData)
                    return;
                Directory.Delete(Root, recursive: true);
            }

            Directory.Move(LegacyRoot, Root);
            var escapedOld = LegacyRoot.Replace("\\", "\\\\");
            var escapedNew = Root.Replace("\\", "\\\\");
            foreach (var file in Directory.EnumerateFiles(Root, "*.json", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                var updated = text.Replace(escapedOld, escapedNew, StringComparison.OrdinalIgnoreCase)
                    .Replace(LegacyRoot, Root, StringComparison.OrdinalIgnoreCase);
                if (!string.Equals(text, updated, StringComparison.Ordinal))
                    File.WriteAllText(file, updated);
            }
        }
        catch
        {
            // Keep the old folder if it is still in use. The next launch tries again.
        }
    }
}
