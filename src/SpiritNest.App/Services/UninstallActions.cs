using SpiritNest.Models;

namespace SpiritNest.Services;

/// <summary>
/// Runs only when the uninstaller passes <c>--uninstall-finalize</c>.
/// The uninstall page records the choices first; this performs them.
/// </summary>
public static class UninstallActions
{
    public const string FinalizeSwitch = "--uninstall-finalize";
    public const string RestoreSwitch = "--restore-shortcuts";
    public const string PurgeSwitch = "--purge-user-data";

    public static bool TryHandle(string[] args)
    {
        if (args.All(a => !string.Equals(a, FinalizeSwitch, StringComparison.OrdinalIgnoreCase)))
            return false;

        var restoreShortcuts = args.Any(a => string.Equals(a, RestoreSwitch, StringComparison.OrdinalIgnoreCase));
        var purge = args.Any(a => string.Equals(a, PurgeSwitch, StringComparison.OrdinalIgnoreCase));

        if (restoreShortcuts)
            RestoreCollectedFiles();
        if (purge)
            PurgeUserData();
        return true;
    }

    /// <summary>Added in Settings. These have no original file to put back.</summary>
    private static bool AddedInSettings(ItemInfo item) =>
        string.Equals(item.Source, "UrlNav", StringComparison.OrdinalIgnoreCase)
        || (string.Equals(item.StorageMode, "Url", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(item.OriginalDirectory));

    private static void RestoreCollectedFiles()
    {
        try
        {
            if (!File.Exists(AppPaths.LibraryFile))
                return;
            var library = new LibraryStore();
            library.Load();
            var collect = new CollectService(library);
            foreach (var item in library.Current.Items.ToList())
            {
                if (AddedInSettings(item))
                    continue;
                if (string.Equals(item.ItemKind, "Widget", StringComparison.OrdinalIgnoreCase))
                    continue;
                collect.RestoreToOriginal(item);
            }
        }
        catch { /* uninstall continues */ }
    }

    private static void PurgeUserData()
    {
        try
        {
            if (Directory.Exists(AppPaths.Root))
                Directory.Delete(AppPaths.Root, recursive: true);
        }
        catch { /* uninstall continues */ }
    }
}
