using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace SpiritNest.Services;

public sealed class FullscreenWatcher
{
    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _enabled;
    private bool _wasFullscreen;

    public event Action? EnteredFullscreen;
    public event Action? LeftFullscreen;

    public FullscreenWatcher(Func<bool> enabled)
    {
        _enabled = enabled;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private void Tick()
    {
        if (!_enabled())
        {
            if (_wasFullscreen)
            {
                _wasFullscreen = false;
                LeftFullscreen?.Invoke();
            }
            return;
        }

        var now = IsForegroundFullscreen();
        if (now && !_wasFullscreen)
        {
            _wasFullscreen = true;
            EnteredFullscreen?.Invoke();
        }
        else if (!now && _wasFullscreen)
        {
            _wasFullscreen = false;
            LeftFullscreen?.Invoke();
        }
    }

    private static bool IsForegroundFullscreen()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        // Ignore desktop / shell
        var className = new char[256];
        GetClassName(hwnd, className, className.Length);
        var cls = new string(className).TrimEnd('\0');
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow")
            return false;

        if (!GetWindowRect(hwnd, out var wr)) return false;
        var monitor = MonitorFromWindow(hwnd, 2);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref mi)) return false;

        var mw = mi.rcMonitor.Right - mi.rcMonitor.Left;
        var mh = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
        var ww = wr.Right - wr.Left;
        var wh = wr.Bottom - wr.Top;
        var scale = ScreenDpi.ScaleOfWindow(hwnd);

        // Cover nearly entire monitor (exclusive / borderless fullscreen), not ordinary maximized
        var covers = ww >= mw - 4 * scale && wh >= mh - 4 * scale;
        if (!covers) return false;

        return wr.Left <= mi.rcMonitor.Left + 2 * scale
               && wr.Top <= mi.rcMonitor.Top + 2 * scale
               && wr.Right >= mi.rcMonitor.Right - 2 * scale
               && wr.Bottom >= mi.rcMonitor.Bottom - 2 * scale;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }
}
