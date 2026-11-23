using System.Diagnostics;
using System.Runtime.InteropServices;
using SpiritNest.Models;

namespace SpiritNest.Services;

public sealed class ShellLaunchService
{
    public bool TryLaunch(ItemInfo item, out string? error)
    {
        error = null;
        try
        {
            var path = ResolveLaunchPath(item);
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "路径无效";
                return false;
            }

            if (IsHttpUrl(path))
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                return true;
            }

            if (ShellPlaces.IsShell(item))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + item.TargetPath + "\"",
                    UseShellExecute = true
                });
                return true;
            }

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                error = "目标不存在";
                return false;
            }

            // .lnk / .url must go through the shell shortcut path. Process.Start sets
            // SEE_MASK_NOCLOSEPROCESS and a working directory, which drops the shortcut's
            // AppUserModelID and icon. Edge/Chrome then open with a blank taskbar button
            // and a blank icon in Task Manager.
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
            {
                ShellOpen(path);
                return true;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? ""
            });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// If the target already has a visible window, bring it forward and return true
    /// (caller should not start another instance). A background-only process
    /// (Edge / Chrome startup boost, no window) returns false so the caller can open one.
    /// </summary>
    public bool TryActivateExisting(ItemInfo item)
    {
        // Browser URLs: always open (do not steal an arbitrary browser window)
        if (string.Equals(item.ItemKind, "Url", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.StorageMode, "Url", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var exe = ResolveExecutablePath(item);
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                return false;

            var hwnd = FindBestWindow(exe);
            if (hwnd == IntPtr.Zero)
                return false;

            ActivateWindow(hwnd);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Wait until a visible window for this item appears (or timeout). Returns true if found.</summary>
    public async Task<bool> WaitUntilRunningAsync(ItemInfo item, TimeSpan timeout, CancellationToken ct = default)
    {
        var exe = ResolveExecutablePath(item);
        if (string.IsNullOrWhiteSpace(exe))
        {
            // URL / folder / unknown — brief debounce only
            try { await Task.Delay(600, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* ignore */ }
            return false;
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var hwnd = FindBestWindow(exe);
            if (hwnd != IntPtr.Zero)
            {
                ActivateWindow(hwnd);
                return true;
            }

            try { await Task.Delay(150, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }

        return false;
    }

    private const uint SeeMaskFlagDdeWait = 0x00000100;
    private const int SwNormal = 1;

    /// <summary>Open a shortcut the way Explorer does, keeping its icon and taskbar identity.</summary>
    private static void ShellOpen(string path)
    {
        var sei = new ShellExecuteInfo
        {
            cbSize = Marshal.SizeOf<ShellExecuteInfo>(),
            fMask = SeeMaskFlagDdeWait,
            lpVerb = "open",
            lpFile = path,
            nShow = SwNormal
        };
        if (!ShellExecuteEx(ref sei))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    public void OpenInExplorer(ItemInfo item)
    {
        var path = item.StoredPath ?? item.TargetPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        if (File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path))
            Process.Start("explorer.exe", path);
    }

    public void ShowProperties(ItemInfo item)
    {
        var path = item.StoredPath ?? item.TargetPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var sei = new ShellExecuteInfo
        {
            cbSize = Marshal.SizeOf<ShellExecuteInfo>(),
            lpVerb = "properties",
            lpFile = path,
            nShow = 5,
            fMask = 0x0000000C
        };
        ShellExecuteEx(ref sei);
    }

    public static string? ResolveLaunchPath(ItemInfo item)
    {
        if (string.Equals(item.ItemKind, "Url", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.StorageMode, "Url", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(item.TargetPath) ? null : item.TargetPath;

        var path = item.StorageMode == "MovedFile" && !string.IsNullOrEmpty(item.StoredPath)
            ? item.StoredPath
            : item.TargetPath;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    /// <summary>Resolve .lnk/.url to a local .exe when possible (for process matching).</summary>
    public static string? ResolveExecutablePath(ItemInfo item)
    {
        var path = ResolveLaunchPath(item);
        if (string.IsNullOrWhiteSpace(path) || IsHttpUrl(path))
            return null;

        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
        {
            var target = ShortcutHelper.ResolveTarget(path);
            if (string.IsNullOrWhiteSpace(target) || IsHttpUrl(target))
                return null;
            path = target;
        }

        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            return path;

        return null;
    }

    private static bool IsHttpUrl(string path) =>
        path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Largest real top-level window owned by any process of this exe.
    /// Chromium (Edge) keeps windowless background processes and often leaves
    /// <see cref="Process.MainWindowHandle"/> at zero, so enumerate instead.
    /// </summary>
    private static IntPtr FindBestWindow(string exePath)
    {
        var pids = CollectProcessIds(exePath);
        if (pids.Count == 0) return IntPtr.Zero;

        var best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows((hWnd, _) =>
        {
            try
            {
                GetWindowThreadProcessId(hWnd, out var windowPid);
                if (!pids.Contains(windowPid)) return true;
                if (!IsUserWindow(hWnd)) return true;
                var area = WindowArea(hWnd);
                if (area > bestArea)
                {
                    bestArea = area;
                    best = hWnd;
                }
            }
            catch
            {
                /* skip this window */
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    private static HashSet<uint> CollectProcessIds(string exePath)
    {
        var name = Path.GetFileNameWithoutExtension(exePath);
        var ids = new HashSet<uint>();
        if (string.IsNullOrEmpty(name)) return ids;

        foreach (var proc in Process.GetProcessesByName(name))
        {
            try
            {
                string? fileName = null;
                try { fileName = proc.MainModule?.FileName; } catch { /* access denied */ }
                if (fileName is null
                    || string.Equals(fileName, exePath, StringComparison.OrdinalIgnoreCase))
                    ids.Add((uint)proc.Id);
            }
            catch
            {
                /* dead process */
            }
            finally
            {
                proc.Dispose();
            }
        }

        return ids;
    }

    private static bool IsUserWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd)) return false;
        if (GetWindow(hwnd, GwOwner) != IntPtr.Zero) return false;
        if (IsCloaked(hwnd)) return false;
        if ((GetWindowExStyle(hwnd) & WsExToolWindow) != 0) return false;
        if (GetWindowTextLength(hwnd) <= 0) return false;
        return WindowArea(hwnd) >= MinWindowArea;
    }

    private static long WindowArea(IntPtr hwnd)
    {
        var placement = new WindowPlacement { length = Marshal.SizeOf<WindowPlacement>() };
        if (GetWindowPlacement(hwnd, ref placement))
        {
            var width = Math.Max(0, placement.rcNormal.Right - placement.rcNormal.Left);
            var height = Math.Max(0, placement.rcNormal.Bottom - placement.rcNormal.Top);
            var normal = (long)width * height;
            if (normal > 0) return normal;
        }

        if (!GetWindowRect(hwnd, out var rect)) return 0;
        return (long)Math.Max(0, rect.Right - rect.Left) * Math.Max(0, rect.Bottom - rect.Top);
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        var hr = DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int));
        return hr == 0 && cloaked != 0;
    }

    private static int GetWindowExStyle(IntPtr hwnd) =>
        GetWindowLongW(hwnd, GwlExStyle);

    private static void ActivateWindow(IntPtr hwnd)
    {
        var foreground = GetForegroundWindow();
        var foreThread = GetWindowThreadProcessId(foreground, out _);
        var targetThread = GetWindowThreadProcessId(hwnd, out _);
        var current = GetCurrentThreadId();
        var attachedFore = false;
        var attachedTarget = false;
        try
        {
            if (foreThread != 0 && foreThread != current)
                attachedFore = AttachThreadInput(current, foreThread, true);
            if (targetThread != 0 && targetThread != current && targetThread != foreThread)
                attachedTarget = AttachThreadInput(current, targetThread, true);

            ShowWindow(hwnd, IsIconic(hwnd) ? SwRestore : SwShow);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        catch
        {
            /* window gone */
        }
        finally
        {
            if (attachedTarget)
                AttachThreadInput(current, targetThread, false);
            if (attachedFore)
                AttachThreadInput(current, foreThread, false);
        }
    }

    private const int SwRestore = 9;
    private const int SwShow = 5;
    private const uint GwOwner = 4;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int DwmwaCloaked = 14;
    private const long MinWindowArea = 80 * 80;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", ExactSpelling = true)]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out WinRect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement lpwndpl);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int length;
        public int flags;
        public int showCmd;
        public WinPoint minPosition;
        public WinPoint maxPosition;
        public WinRect rcNormal;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo lpExecInfo);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string lpVerb;
        public string lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }
}
