namespace DesktopGuard;

/// 单个显示器的描述。
/// 借鉴 PecoFence `crates/platform/src/monitors.rs` 的 MonitorInfo：
/// 用稳定的设备名（\\.\DISPLAY1）作为身份，每台显示器单独记录 DPI。
internal sealed record MonitorDesc(
    string DeviceId,
    int X, int Y, int Width, int Height,
    uint Dpi,
    bool Primary)
{
    public double Scale => Dpi / 96.0;
    public int Right => X + Width;
    public int Bottom => Y + Height;

    /// 归一化：把绝对像素换成 0..1 的相对比例。
    /// 屏幕位置变了，比例不变 —— 这是跨屏幕布局还原的关键。
    public double RelX => Width > 0 ? (double)X / Width : 0;
    public double RelY => Height > 0 ? (double)Y / Height : 0;

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    /// 稳定标识：设备名 + 分辨率 + 缩放。显示器换位置不影响它。
    public string Identity => $"{DeviceId}|{Width}x{Height}|{Scale:0.##}";
}

internal static class ScreenLayout
{
    /// 枚举当前所有显示器。
    public static List<MonitorDesc> Capture()
    {
        var result = new List<MonitorDesc>();

        Interop.MonitorEnumProc callback = (IntPtr hMonitor, IntPtr _, ref Interop.RECT _, IntPtr _) =>
        {
            var info = new Interop.MONITORINFOEX { cbSize = 0 };
            info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Interop.MONITORINFOEX>();

            if (Interop.GetMonitorInfo(hMonitor, ref info))
            {
                uint dpiX = 96, dpiY = 96;
                try
                {
                    Interop.GetDpiForMonitor(hMonitor, Interop.MDT_EFFECTIVE_DPI, out dpiX, out dpiY);
                }
                catch (DllNotFoundException)
                {
                    dpiX = 96; // 极老系统没有 shcore，退回 96
                }
                if (dpiX == 0) dpiX = 96;

                result.Add(new MonitorDesc(
                    info.szDevice,
                    info.rcMonitor.Left,
                    info.rcMonitor.Top,
                    info.rcMonitor.Width,
                    info.rcMonitor.Height,
                    dpiX,
                    (info.dwFlags & Interop.MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        };

        Interop.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return result;
    }

    /// 布局指纹：只包含身份，不含位置。
    /// 屏幕换位置 → 指纹不变；换分辨率/缩放/插拔屏 → 指纹变化。
    public static string Fingerprint(IEnumerable<MonitorDesc> monitors) =>
        string.Join(" ; ", monitors
            .OrderBy(m => m.DeviceId, StringComparer.Ordinal)
            .Select(m => m.Identity));

    /// 虚拟桌面总边界。
    public static (int Left, int Top, int Right, int Bottom) VirtualBounds(IEnumerable<MonitorDesc> monitors)
    {
        var list = monitors.ToList();
        if (list.Count == 0) return (0, 0, 0, 0);
        return (
            list.Min(m => m.X),
            list.Min(m => m.Y),
            list.Max(m => m.Right),
            list.Max(m => m.Bottom));
    }

    /// 找出包含指定点的显示器；找不到就返回距离最近的。
    /// 借鉴 PecoFence `monitor_from_point(..., MONITOR_DEFAULTTONEAREST)` 的语义。
    public static MonitorDesc? Nearest(List<MonitorDesc> monitors, int x, int y)
    {
        if (monitors.Count == 0) return null;

        var hit = monitors.FirstOrDefault(m => m.Contains(x, y));
        if (hit is not null) return hit;

        return monitors
            .OrderBy(m => DistanceSquared(m, x, y))
            .First();
    }

    private static long DistanceSquared(MonitorDesc m, int x, int y)
    {
        int cx = Math.Clamp(x, m.X, m.Right);
        int cy = Math.Clamp(y, m.Y, m.Bottom);
        long dx = x - cx;
        long dy = y - cy;
        return dx * dx + dy * dy;
    }
}
