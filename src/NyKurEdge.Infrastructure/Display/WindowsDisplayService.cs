using System.Runtime.InteropServices;
using NyKurEdge.Core.Display;

namespace NyKurEdge.Infrastructure.Display;

public sealed class WindowsDisplayService : IDisplayService
{
    private const uint MonitorInfoPrimary = 0x00000001;

    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var displays = new List<DisplayInfo>();
        MonitorEnumProcedure callback = (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx
            {
                Size = (uint)Marshal.SizeOf<MonitorInfoEx>(),
                DeviceName = string.Empty,
            };

            if (!GetMonitorInfo(monitor, ref info))
            {
                return true;
            }

            var dpi = GetMonitorDpi(monitor);
            var id = string.IsNullOrWhiteSpace(info.DeviceName)
                ? $"monitor-{monitor.ToInt64():X}"
                : info.DeviceName;
            displays.Add(new DisplayInfo(
                id,
                id,
                ToDisplayRect(info.Monitor),
                ToDisplayRect(info.WorkArea),
                dpi,
                (info.Flags & MonitorInfoPrimary) != 0));
            return true;
        };

        _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return displays;
    }

    public DisplayInfo GetPrimaryDisplay()
    {
        var displays = GetDisplays();
        return displays.FirstOrDefault(display => display.IsPrimary)
               ?? (displays.Count > 0
                   ? displays[0]
                   : new DisplayInfo(
                   "primary",
                   "Primary display",
                   new DisplayRect(0, 0, 1920, 1080),
                   new DisplayRect(0, 0, 1920, 1040),
                   96,
                   true));
    }

    private static uint GetMonitorDpi(IntPtr monitor)
    {
        try
        {
            return GetDpiForMonitor(monitor, MonitorDpiType.Effective, out var dpiX, out _) == 0
                ? dpiX
                : 96;
        }
        catch (DllNotFoundException)
        {
            return 96;
        }
        catch (EntryPointNotFoundException)
        {
            return 96;
        }
    }

    private static DisplayRect ToDisplayRect(NativeRect rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    private delegate bool MonitorEnumProcedure(
        IntPtr monitor,
        IntPtr deviceContext,
        IntPtr monitorRectangle,
        IntPtr data);

    private enum MonitorDpiType
    {
        Effective = 0,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr deviceContext,
        IntPtr clipRectangle,
        MonitorEnumProcedure callback,
        IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx monitorInfo);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        IntPtr monitor,
        MonitorDpiType dpiType,
        out uint dpiX,
        out uint dpiY);
}
