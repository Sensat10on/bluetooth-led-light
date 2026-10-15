using System.Runtime.InteropServices;

namespace OpenTuyaSync.Capture;

/// <summary>Сведения о мониторе для выпадающего списка в интерфейсе.</summary>
public sealed record MonitorInfo(int Index, string DeviceName, int X, int Y, int Width, int Height, bool IsPrimary)
{
    public string Caption =>
        $"{Index}: {Width}x{Height}{(IsPrimary ? " (основной)" : "")}";

    /// <summary>Полное имя устройства (нужно DXGI для сопоставления выхода).</summary>
    public override string ToString() => Caption;
}

/// <summary>
/// Перечисление мониторов через Win32. Обратите внимание: индексы в
/// интерфейсе начинаются с 1, как в Python-версии, а ноль зарезервирован
/// под «все мониторы вместе».
/// </summary>
public static class MonitorEnumerator
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    private const uint MONITORINFOF_PRIMARY = 0x1;

    /// <summary>
    /// Возвращает список физических мониторов. Первый элемент списка --
    /// виртуальный «все мониторы вместе» с индексом 0.
    /// </summary>
    public static List<MonitorInfo> Enumerate()
    {
        var result = new List<MonitorInfo>();
        var physical = new List<MonitorInfo>();

        MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var info = new MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<MONITORINFOEX>(),
                szDevice = "",
            };

            if (GetMonitorInfo(hMonitor, ref info))
            {
                physical.Add(new MonitorInfo(
                    physical.Count + 1,          // индекс 1..N
                    info.szDevice,
                    rect.Left,
                    rect.Top,
                    rect.Right - rect.Left,
                    rect.Bottom - rect.Top,
                    (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }

            return true;
        };

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

        if (physical.Count == 0)
        {
            // Сюда попадаем в редких случаях (например, запуск в сессии без
            // рабочего стола) -- отдаём хотя бы виртуальный монитор.
            physical.Add(new MonitorInfo(1, "\\\\.\\DISPLAY1", 0, 0, 1920, 1080, true));
        }

        if (physical.Count > 1)
        {
            int left = physical.Min(m => m.X);
            int top = physical.Min(m => m.Y);
            int right = physical.Max(m => m.X + m.Width);
            int bottom = physical.Max(m => m.Y + m.Height);
            result.Add(new MonitorInfo(0, "ALL", left, top, right - left, bottom - top, false));
        }

        result.AddRange(physical);
        return result;
    }
}
