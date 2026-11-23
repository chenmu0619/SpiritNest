using System.Runtime.InteropServices;
using System.Windows;

namespace SpiritNest.Services;

public readonly record struct MonitorInfo(string Id, Rect WorkArea, Rect DeviceWorkArea, double DpiScale, bool IsPrimary);

public static class DisplayPlacement
{
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (hMonitor, _, _, _) =>
            {
                var info = new MONITORINFOEX();
                info.cbSize = Marshal.SizeOf<MONITORINFOEX>();
                if (GetMonitorInfo(hMonitor, ref info))
                {
                    var work = info.rcWork;
                    var device = new Rect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
                    var scale = ScreenDpi.ScaleOfMonitor(hMonitor);
                    var dip = new Rect(device.Left / scale, device.Top / scale, device.Width / scale, device.Height / scale);
                    var primary = (info.dwFlags & 1) != 0;
                    list.Add(new MonitorInfo(info.szDevice, dip, device, scale, primary));
                }
                return true;
            }, IntPtr.Zero);
        return list;
    }

    public static MonitorInfo GetPrimary() =>
        GetMonitors().FirstOrDefault(m => m.IsPrimary) is { Id: not null } m
            ? m
            : GetMonitors().FirstOrDefault() is { Id: not null } any
                ? any
                : new MonitorInfo("PRIMARY", new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.WorkArea.Height),
                    new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.WorkArea.Height), 1, true);

    /// <summary>Point is in DIPs (Window.Left / Top).</summary>
    public static MonitorInfo FromDip(Point dip)
    {
        foreach (var m in GetMonitors())
        {
            var px = new Point(dip.X * m.DpiScale, dip.Y * m.DpiScale);
            if (m.DeviceWorkArea.Contains(px) || m.WorkArea.Contains(dip)) return m;
        }
        return GetPrimary();
    }

    /// <summary>Point is in physical pixels (GetCursorPos).</summary>
    public static MonitorInfo FromDevicePoint(Point devicePixels)
    {
        foreach (var m in GetMonitors())
        {
            if (m.DeviceWorkArea.Contains(devicePixels)) return m;
        }
        return GetPrimary();
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
}
