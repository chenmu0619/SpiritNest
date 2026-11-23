using System.Runtime.InteropServices;
using System.Text;

namespace SpiritNest.Services;

/// <summary>Detects whether the cursor is over the Windows desktop shell surface.</summary>
public static class DesktopHitTest
{
    public static bool IsDesktopUnderCursor(IntPtr excludeRootHwnd = default)
    {
        if (!GetCursorPos(out var pt)) return false;
        var hwnd = WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return false;

        if (excludeRootHwnd != IntPtr.Zero)
        {
            var root = GetAncestor(hwnd, GA_ROOT);
            if (root == excludeRootHwnd || hwnd == excludeRootHwnd)
                return false;
        }

        for (var cur = hwnd; cur != IntPtr.Zero; cur = GetParent(cur))
        {
            var cls = GetWindowClass(cur);
            if (cls is "Progman" or "WorkerW" or "SHELLDLL_DefView")
                return true;
        }

        return false;
    }

    private static string GetWindowClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
}
