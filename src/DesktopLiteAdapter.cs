using Microsoft.Win32;
using System.Text;

namespace DesktopGuard;

/// 360桌面助手 的修复适配器。
///
/// 它自己不修的问题，我们在外部替它修：
///   1. 它把窗口绝对坐标写进注册表和 reg.ini，屏幕布局一变就永远越界
///   2. 越界后它不自我纠正，只能等用户发现"桌面记忆丢了"
///
/// 这个类的职责：读出它写下的坐标 → 判断是否越界 → 回写一份合法坐标。
internal static class DesktopLiteAdapter
{
    /// <summary>
    /// 目标应用的数据目录。默认取当前用户的 Roaming 目录，因此换电脑/换用户名都不用改代码。
    /// 如需指向自定义位置，设环境变量 DESKTOP_GUARD_DATA_ROOT 即可。
    /// </summary>
    public static string DataRoot { get; } = ResolveDataRoot();

    public static string FenceDir { get; } = Path.Combine(DataRoot, "DTFence");
    public const string WidgetKey = @"HKEY_CURRENT_USER\Software\360desktoplite\DesktopWidgetPos";

    private static string ResolveDataRoot()
    {
        string? custom = Environment.GetEnvironmentVariable("DESKTOP_GUARD_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(custom)) return custom!;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "360DesktopLite");
    }

    internal sealed record WidgetPos(int Left, int Top, int Right, int Bottom, int Dpi)
    {
        public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) @{Dpi}dpi";
    }

    /// 读取它记录的桌面小组件位置。
    public static WidgetPos? ReadWidgetPos()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\360desktoplite\DesktopWidgetPos");
            if (key is null) return null;

            string? packed = key.GetValue("desktop_widget_pos") as string;
            int dpi = Convert.ToInt32(key.GetValue("last_dpi") ?? 96);

            if (!string.IsNullOrWhiteSpace(packed))
            {
                var parts = packed.Split(',');
                if (parts.Length == 4 &&
                    int.TryParse(parts[0], out int l) &&
                    int.TryParse(parts[1], out int t) &&
                    int.TryParse(parts[2], out int r) &&
                    int.TryParse(parts[3], out int b))
                {
                    return new WidgetPos(l, t, r, b, dpi);
                }
            }

            // 退回到分字段读取
            int fl = Convert.ToInt32(key.GetValue("desktop_wigit_left") ?? 0);
            int ft = Convert.ToInt32(key.GetValue("desktop_wigit_top") ?? 0);
            int fr = Convert.ToInt32(key.GetValue("desktop_wigit_right") ?? 0);
            int fb = Convert.ToInt32(key.GetValue("desktop_wigit_bottom") ?? 0);
            return new WidgetPos(fl, ft, fr, fb, dpi);
        }
        catch (Exception ex)
        {
            LayoutStore.Log($"读取 360 窗口坐标失败: {ex.Message}");
            return null;
        }
    }

    /// 判断坐标是否已经跑出可视区。
    public static bool IsOffscreen(WidgetPos pos, List<MonitorDesc> monitors)
    {
        if (monitors.Count == 0) return false;

        // 左上角所在显示器
        var host = ScreenLayout.Nearest(monitors, pos.Left, pos.Top);
        if (host is null) return false;

        // 完全跑到屏幕外，或可见部分少得几乎点不到
        var (vl, vt, vr, vb) = ScreenLayout.VirtualBounds(monitors);
        bool fullyOutside = pos.Right < vl || pos.Left > vr || pos.Bottom < vt || pos.Top > vb;
        if (fullyOutside) return true;

        int visibleW = Math.Min(pos.Right, host.Right) - Math.Max(pos.Left, host.X);
        int visibleH = Math.Min(pos.Bottom, host.Bottom) - Math.Max(pos.Top, host.Y);
        return visibleW < 48 || visibleH < 24;
    }

    /// 把越界坐标修回主屏右上角（贴近它原本的使用习惯）。
    public static WidgetPos ComputeValidPos(WidgetPos bad, List<MonitorDesc> monitors)
    {
        var target = monitorPrimary(monitors) ?? monitors[0];
        int w = Math.Clamp(bad.Right - bad.Left, 120, target.Width);
        int h = Math.Clamp(bad.Bottom - bad.Top, 80, target.Height);

        // 右上角，留 25px 边距
        int right = target.Right - 25;
        int left = right - w;
        int top = target.Y + 25;
        int bottom = top + h;

        // 再夹一次，防小屏
        if (left < target.X) { left = target.X + 10; right = left + w; }
        if (bottom > target.Bottom) { bottom = target.Bottom - 10; top = Math.Max(target.Y + 5, bottom - h); }

        return new WidgetPos(left, top, right, bottom, (int)target.Dpi);

        static MonitorDesc? monitorPrimary(List<MonitorDesc> ms) =>
            ms.FirstOrDefault(m => m.Primary);
    }

    /// 写回修复后的坐标。改之前先把原值存一份。
    public static bool ApplyFix(WidgetPos fixedPos, WidgetPos original)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\360desktoplite\DesktopWidgetPos", writable: true);
            if (key is null)
            {
                LayoutStore.Log("未找到 360 窗口位置注册表键，跳过修复");
                return false;
            }

            LayoutStore.Log($"修复前: {original} -> 修复后: {fixedPos}");

            key.SetValue("desktop_wigit_left", fixedPos.Left, RegistryValueKind.DWord);
            key.SetValue("desktop_wigit_top", fixedPos.Top, RegistryValueKind.DWord);
            key.SetValue("desktop_wigit_right", fixedPos.Right, RegistryValueKind.DWord);
            key.SetValue("desktop_wigit_bottom", fixedPos.Bottom, RegistryValueKind.DWord);
            key.SetValue("desktop_widget_pos",
                $"{fixedPos.Left},{fixedPos.Top},{fixedPos.Right},{fixedPos.Bottom}",
                RegistryValueKind.String);
            key.SetValue("last_dpi", fixedPos.Dpi, RegistryValueKind.DWord);

            return true;
        }
        catch (Exception ex)
        {
            LayoutStore.Log($"写回坐标失败: {ex.Message}");
            return false;
        }
    }

    /// 备份它的布局数据到 DesktopGuard 的历史目录。
    /// 它自己的备份和主数据在同一目录同一块盘上 —— 盘坏或目录被清就全没了。
    public static bool BackupLayoutData()
    {
        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string dest = Path.Combine(LayoutStore.HistoryDir, $"360-{stamp}");
            Directory.CreateDirectory(dest);

            string[] files = { "DTFenceData.dtf", "DTFenceData.dtf.bk", "Config.ini", "reg.ini" };
            int copied = 0;

            foreach (string name in files)
            {
                string src = Path.Combine(FenceDir, name);
                if (File.Exists(src))
                {
                    File.Copy(src, Path.Combine(dest, name), overwrite: true);
                    copied++;
                }
            }

            // 连带它自己的备份目录一起搬走
            string srcBackup = Path.Combine(FenceDir, "Backup");
            if (Directory.Exists(srcBackup))
            {
                string dstBackup = Path.Combine(dest, "Backup");
                CopyDir(srcBackup, dstBackup);
            }

            LayoutStore.Log($"已备份 360 布局数据 {copied} 个文件 -> {Path.GetFileName(dest)}");
            return copied > 0;
        }
        catch (Exception ex)
        {
            LayoutStore.Log($"备份 360 布局失败: {ex.Message}");
            return false;
        }
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
        foreach (string dir in Directory.GetDirectories(src))
            CopyDir(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }

    /// 360 进程是否在跑。不在跑的时候改坐标会被它启动时覆盖，所以要先确认。
    public static bool IsRunning()
    {
        try
        {
            return System.Diagnostics.Process
                .GetProcessesByName("360DesktopLite64").Length > 0
                || System.Diagnostics.Process
                .GetProcessesByName("360DesktopLite").Length > 0;
        }
        catch { return false; }
    }
}
