namespace SpiritNest.Services;

public static class ShortcutHelper
{
    public static string? ResolveTarget(string linkPath)
    {
        try
        {
            if (!File.Exists(linkPath)) return null;
            var ext = Path.GetExtension(linkPath);
            if (ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
                return ParseUrlFile(linkPath);

            if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                return linkPath;

            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            var sc = shell.CreateShortcut(linkPath);
            string? target = sc.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : (string)target;
        }
        catch
        {
            return null;
        }
    }

    private static string? ParseUrlFile(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                return line[4..].Trim();
        }
        return null;
    }

    public static void CreateShortcut(string linkPath, string targetPath, string? description = null)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell")
                   ?? throw new InvalidOperationException("WScript.Shell unavailable");
        dynamic shell = Activator.CreateInstance(type)!;
        var sc = shell.CreateShortcut(linkPath);
        sc.TargetPath = targetPath;
        if (!string.IsNullOrEmpty(description))
            sc.Description = description;
        try
        {
            sc.WorkingDirectory = Path.GetDirectoryName(targetPath) ?? "";
        }
        catch { /* ignore */ }
        sc.Save();
    }
}
