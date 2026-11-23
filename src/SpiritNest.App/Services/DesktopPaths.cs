using System.Runtime.InteropServices;

namespace SpiritNest.Services;

public static class DesktopPaths
{
    private static readonly Guid FolderIdDesktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");

    public static string GetUserDesktop()
    {
        var hr = SHGetKnownFolderPath(FolderIdDesktop, 0, IntPtr.Zero, out var pathPtr);
        if (hr != 0 || pathPtr == IntPtr.Zero)
            return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        try
        {
            return Marshal.PtrToStringUni(pathPtr) ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    public static bool IsUnderUserDesktop(string path)
    {
        try
        {
            var desktop = Path.GetFullPath(GetUserDesktop()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            return full.StartsWith(desktop, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);
}
