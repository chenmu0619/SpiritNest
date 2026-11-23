using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace SpiritNest.Services;

/// <summary>
/// Foreground window is a maximized desktop browser. Electron apps share Chrome's
/// window class, so the match is the process file name (plus Firefox / IE classes).
/// </summary>
public sealed class BrowserMinimalWatcher
{
    private static readonly HashSet<string> BrowserExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe",
        "arc.exe", "chromium.exe", "iexplore.exe", "librewolf.exe", "waterfox.exe", "floorp.exe",
        "yandex.exe", "maxthon.exe", "liebao.exe", "quark.exe", "quarkbrowser.exe",
        "360chrome.exe", "360se.exe", "360chromex.exe", "qqbrowser.exe", "sogouexplorer.exe",
        "2345explorer.exe", "twinkstar.exe", "theworld.exe", "centbrowser.exe", "slimjet.exe",
        "huaweibrowser.exe", "slbrowser.exe"
    };

    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _enabled;
    private readonly Func<IntPtr> _ownWindow;
    private bool _active;

    public event Action? Entered;
    public event Action? Left;

    public BrowserMinimalWatcher(Func<bool> enabled, Func<IntPtr> ownWindow)
    {
        _enabled = enabled;
        _ownWindow = ownWindow;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private void Tick()
    {
        if (!_enabled())
        {
            if (_active)
            {
                _active = false;
                Left?.Invoke();
            }
            return;
        }

        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;

        // Hovering the island (or its settings) must not drop the minimal bar.
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == (uint)Environment.ProcessId)
            return;

        var now = IsMaximizedBrowser(hwnd);
        if (now && !_active)
        {
            _active = true;
            Entered?.Invoke();
        }
        else if (!now && _active)
        {
            _active = false;
            Left?.Invoke();
        }
    }

    private bool IsMaximizedBrowser(IntPtr hwnd)
    {
        if (!IsZoomed(hwnd)) return false;

        var own = _ownWindow();
        if (own != IntPtr.Zero)
        {
            var browserMon = MonitorFromWindow(hwnd, 2);
            var ownMon = MonitorFromWindow(own, 2);
            if (browserMon != ownMon) return false;
        }

        var className = new char[256];
        GetClassName(hwnd, className, className.Length);
        var cls = new string(className).TrimEnd('\0');
        if (cls is "MozillaWindowClass" or "IEFrame")
            return true;

        var exe = ProcessImageName(hwnd);
        return exe is not null && BrowserExecutables.Contains(exe);
    }

    private static string? ProcessImageName(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return null;
        var handle = OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buf = new char[1024];
            var size = buf.Length;
            if (!QueryFullProcessImageName(handle, 0, buf, ref size) || size <= 0)
                return null;
            return Path.GetFileName(new string(buf, 0, size));
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, char[] lpExeName, ref int lpdwSize);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr hObject);
}
