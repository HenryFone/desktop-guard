namespace DesktopGuard;

internal sealed record CalibrationResult(
    bool LayoutChanged,
    int Fixed,
    int Unreachable,
    string Summary);

/// 布局校准器 —— 全部逻辑集中在这里，不做任何窗口操作。
///
/// 输入：上一次的布局快照 + 当前显示器列表
/// 输出：修正后的坐标（以及哪些真的救不回来了）
///
/// 这样设计的原因：算坐标是纯函数，可以脱机测试、可重复验证；
/// 真正去移动窗口的部分单独隔离，出问题也不会破坏数据。
internal static class LayoutCalibrator
{
    public static CalibrationResult Calibrate(
        LayoutSnapshot previous,
        List<MonitorDesc> current,
        bool layoutChanged)
    {
        if (current.Count == 0)
            return new CalibrationResult(layoutChanged, 0, previous.Fences.Count, "当前无可用显示器");

        int fixedCount = 0;
        int unreachable = 0;

        foreach (var fence in previous.Fences)
        {
            var oldMonitor = previous.Monitors
                .FirstOrDefault(m => m.Identity == fence.MonitorIdentity);

            MonitorDesc? target = null;

            if (oldMonitor is not null)
            {
                // 优先找完全同款显示器（同设备名+同分辨率+同缩放）
                target = current.FirstOrDefault(m => m.Identity == oldMonitor.Identity);

                // 没有完全同款：退而找同设备名的（分辨率或缩放变了）
                target ??= current.FirstOrDefault(m =>
                    string.Equals(m.DeviceId, oldMonitor.DeviceId, StringComparison.OrdinalIgnoreCase));
            }

            if (target is null)
            {
                // 这个屏拔了：用主屏接管，坐标按比例换算过去，而不是直接丢掉
                target = current.FirstOrDefault(m => m.Primary) ?? current[0];
                unreachable++;
            }

            // 算出它现在的绝对像素坐标
            int newLeft, newTop, newRight, newBottom;

            if (oldMonitor is not null && oldMonitor.Identity == target.Identity)
            {
                // 显示器没变：保留原始像素，不缩放（避免 DPI 变化被二次放缩放大误差）
                newLeft = target.X + (fence.Left - oldMonitor.X);
                newTop = target.Y + (fence.Top - oldMonitor.Y);
                newRight = target.X + (fence.Right - oldMonitor.X);
                newBottom = target.Y + (fence.Bottom - oldMonitor.Y);
            }
            else
            {
                // 显示器变了：按记录时的比例还原尺寸和位置
                int w = (int)Math.Round(fence.RelWidth * target.Width);
                int h = (int)Math.Round(fence.RelHeight * target.Height);
                int l = target.X + (int)Math.Round(fence.RelLeft * target.Width);
                int t = target.Y + (int)Math.Round(fence.RelTop * target.Height);
                newLeft = l;
                newTop = t;
                newRight = l + w;
                newBottom = t + h;
            }

            // 夹回可视区域：绝不把窗口留在屏幕外
            var (cl, ct, cr, cb) = ClampInto(newLeft, newTop, newRight, newBottom, target);

            bool moved = cl != fence.Left || ct != fence.Top || cr != fence.Right || cb != fence.Bottom;
            if (moved)
            {
                fence.Left = cl;
                fence.Top = ct;
                fence.Right = cr;
                fence.Bottom = cb;
                fence.MonitorIdentity = target.Identity;
                fence.LastDpi = (int)target.Dpi;
                RecomputeRatios(fence, target);
                fixedCount++;
            }
        }

        string summary = layoutChanged
            ? $"布局已变化：修正 {fixedCount} 个位置，{unreachable} 个原屏不可用已迁移"
            : $"布局未变：校验 {previous.Fences.Count} 个位置，修正 {fixedCount} 个";

        return new CalibrationResult(layoutChanged, fixedCount, unreachable, summary);
    }

    /// 把矩形夹进目标显示器，并保证至少留一条边可见（否则用户拖不回来）。
    private static (int, int, int, int) ClampInto(int l, int t, int r, int b, MonitorDesc m)
    {
        const int MinVisible = 48; // 至少露出标题栏那一小条

        int w = Math.Max(1, r - l);
        int h = Math.Max(1, b - t);

        // 尺寸不能超过屏幕
        w = Math.Min(w, m.Width);
        h = Math.Min(h, m.Height);

        int maxL = m.Right - MinVisible;
        int minL = m.X - w + MinVisible;
        int maxT = m.Bottom - MinVisible;
        int minT = m.Y;

        int nl = Math.Clamp(l, minL, Math.Max(minL, maxL));
        int nt = Math.Clamp(t, minT, Math.Max(minT, maxT));

        return (nl, nt, nl + w, nt + h);
    }

    private static void RecomputeRatios(FenceRecord f, MonitorDesc m)
    {
        if (m.Width <= 0 || m.Height <= 0) return;
        f.RelLeft = (double)(f.Left - m.X) / m.Width;
        f.RelTop = (double)(f.Top - m.Y) / m.Height;
        f.RelWidth = (double)(f.Right - f.Left) / m.Width;
        f.RelHeight = (double)(f.Bottom - f.Top) / m.Height;
    }

    /// 由当前显示器列表生成空快照（首次运行时的基线）。
    public static LayoutSnapshot NewBaseline(List<MonitorDesc> monitors)
    {
        var snap = new LayoutSnapshot
        {
            CapturedAt = DateTime.Now,
            Monitors = monitors,
            Fingerprint = ScreenLayout.Fingerprint(monitors),
        };
        snap.Fences.AddRange(monitors.Select((m, i) => new FenceRecord
        {
            Id = $"monitor-{i}",
            Title = m.Primary ? $"主屏 {m.DeviceId}" : $"副屏 {m.DeviceId}",
            MonitorIdentity = m.Identity,
            Left = m.X,
            Top = m.Y,
            Right = m.Right,
            Bottom = m.Bottom,
            LastDpi = (int)m.Dpi,
            RelLeft = 0,
            RelTop = 0,
            RelWidth = 1,
            RelHeight = 1,
        }));
        return snap;
    }
}
