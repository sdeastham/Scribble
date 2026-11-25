using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Scribble.Services;

public sealed record MonitorBounds(
    int LeftPx,
    int TopPx,
    int WidthPx,
    int HeightPx,
    int WorkLeftPx,
    int WorkTopPx,
    int WorkWidthPx,
    int WorkHeightPx,
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
                        info.rcWork.Left,
                        info.rcWork.Top,
                        info.rcWork.Right - info.rcWork.Left,
                        info.rcWork.Bottom - info.rcWork.Top,
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

    public static MonitorBounds? GetMonitorForWindow(Window window)
    {
        var monitors = GetMonitors();
        if (monitors.Count == 0) return null;

        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            IntPtr hMon = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref info))
            {
                var match = monitors.FirstOrDefault(m =>
                    string.Equals(m.DeviceName, info.szDevice, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
        }

        return monitors[0];
    }

    public static MonitorBounds? GetSecondaryMonitor(Window presenterWindow)
    {
        var monitors = GetMonitors();
        if (monitors.Count < 2)
        {
            return null;
        }

        MonitorBounds? current = GetMonitorForWindow(presenterWindow);
        if (current != null)
        {
            var other = monitors.FirstOrDefault(m =>
                !string.Equals(m.DeviceName, current.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (other != null)
            {
                return other;
            }
        }

        return monitors.FirstOrDefault(m => !m.IsPrimary) ?? monitors[1];
    }

    public static void MoveWindowToMonitor(Window window, MonitorBounds target, bool maximize)
    {
        bool wasMaximized = window.WindowState == WindowState.Maximized;
        if (wasMaximized)
        {
            window.WindowState = WindowState.Normal;
        }

        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        int width = Math.Min(target.WorkWidthPx, Math.Max(800, (int)(target.WorkWidthPx * 0.85)));
        int height = Math.Min(target.WorkHeightPx, Math.Max(600, (int)(target.WorkHeightPx * 0.85)));
        int x = target.WorkLeftPx + (target.WorkWidthPx - width) / 2;
        int y = target.WorkTopPx + (target.WorkHeightPx - height) / 2;

        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SwpNoZOrder | SwpShowWindow);

        if (maximize || wasMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }
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
