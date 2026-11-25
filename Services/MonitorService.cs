using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Scribble.Services;

public sealed record MonitorBounds(
    int LeftPx,
    int TopPx,
    int WidthPx,
    int HeightPx,
    bool IsPrimary,
    string DeviceName);

/// <summary>
/// DPI-safe Win32 monitor enumeration and pixel-exact window positioning
/// so Screen 2 (projector) aligns cleanly even with mixed DPI scaling (e.g. 200% Surface + 100% projector).
/// </summary>
public static class MonitorService
{
    private const uint MonitorInfoPrimary = 0x00000001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    public static IReadOnlyList<MonitorBounds> GetMonitors()
    {
        var monitors = new List<MonitorBounds>();

        EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
            {
                var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(hMonitor, ref info))
                {
                    monitors.Add(new MonitorBounds(
                        info.rcMonitor.Left,
                        info.rcMonitor.Top,
                        info.rcMonitor.Right - info.rcMonitor.Left,
                        info.rcMonitor.Bottom - info.rcMonitor.Top,
                        (info.dwFlags & MonitorInfoPrimary) != 0,
                        info.szDevice));
                }
                return true;
            },
            IntPtr.Zero);

        // Primary first, then left-to-right
        return monitors
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.LeftPx)
            .ThenBy(m => m.TopPx)
            .ToList();
    }

    public static MonitorBounds? GetSecondaryMonitor(Window presenterWindow)
    {
        var monitors = GetMonitors();
        if (monitors.Count < 2)
        {
            return null;
        }

        // Identify which monitor currently hosts the presenter window so we pick the OTHER one
        IntPtr hwnd = new WindowInteropHelper(presenterWindow).Handle;
        if (hwnd != IntPtr.Zero)
        {
            IntPtr presenterMonitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(presenterMonitor, ref info))
            {
                var other = monitors.FirstOrDefault(m =>
                    !string.Equals(m.DeviceName, info.szDevice, StringComparison.OrdinalIgnoreCase));
                if (other != null)
                {
                    return other;
                }
            }
        }

        return monitors.FirstOrDefault(m => !m.IsPrimary) ?? monitors[1];
    }

    public static void PlaceBorderlessFullscreen(Window window, MonitorBounds target, bool activate = false)
    {
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        uint flags = SwpNoZOrder | SwpShowWindow | (activate ? 0u : SwpNoActivate);
        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            target.LeftPx,
            target.TopPx,
            target.WidthPx,
            target.HeightPx,
            flags);
    }

    private const uint MonitorDefaultToNearest = 2;

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
}
