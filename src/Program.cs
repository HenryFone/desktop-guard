namespace DesktopGuard;

/// DesktopGuard —— 360桌面助手 外部修复器。
///
/// 不改它的二进制（签名会被破坏，且它会自更新覆盖），
/// 而是在它外面加一层保护：布局变化时抢在它写坏数据之前把坐标修好。
///
/// 三个职责：
///   1. 看住 explorer 重启 —— 它一崩就会连累桌面助手
///   2. 看住屏幕布局 —— 插拔屏/改分辨率/改缩放时自动校准坐标
///   3. 看住布局数据 —— 每次变化都做一份带时间戳的异地备份
internal static class Program
{
    private static readonly bool Verbose =
        Environment.GetCommandLineArgs().Contains("--verbose", StringComparer.OrdinalIgnoreCase);

    [STAThread]
    private static void Main(string[] args)
    {
        LayoutStore.EnsureDirs();
        LayoutStore.Log("=== DesktopGuard 启动 ===");

        // 命令行一次性模式：跑完就退出，方便手动检查和定时任务调用
        if (args.Any(a => a.Equals("--once", StringComparison.OrdinalIgnoreCase)))
        {
            int code = RunOnce();
            Environment.Exit(code);
        }

        if (args.Any(a => a.Equals("--status", StringComparison.OrdinalIgnoreCase)))
        {
            PrintStatus();
            return;
        }

        RunTray();
    }

    /// 单次巡检：判断布局是否变化，变化就备份 + 修坐标。
    private static int RunOnce()
    {
        try
        {
            var monitors = ScreenLayout.Capture();
            string fp = ScreenLayout.Fingerprint(monitors);

            Console.WriteLine($"当前显示器 {monitors.Count} 台：");
            foreach (var m in monitors)
                Console.WriteLine($"  {m.DeviceId}  {m.Width}x{m.Height} @{m.Scale:0.##}x  " +
                                  $"pos=({m.X},{m.Y})  {(m.Primary ? "[主屏]" : "")}");
            Console.WriteLine();

            var prev = LayoutStore.Load();
            bool layoutChanged = prev is null || prev.Fingerprint != fp;

            if (prev is null)
            {
                var baseline = LayoutCalibrator.NewBaseline(monitors);
                LayoutStore.Save(baseline);
                LayoutStore.ArchiveIfChanged(baseline);
                Console.WriteLine("首次运行：已建立基线快照");
            }
            else if (layoutChanged)
            {
                Console.WriteLine($"布局已变化");
                Console.WriteLine($"  旧: {prev.Fingerprint}");
                Console.WriteLine($"  新: {fp}");

                // 先备份 360 的原始数据，再动任何东西
                DesktopLiteAdapter.BackupLayoutData();

                var result = LayoutCalibrator.Calibrate(prev, monitors, layoutChanged: true);
                prev.Fingerprint = fp;
                prev.Monitors = monitors;
                prev.CapturedAt = DateTime.Now;
                LayoutStore.Save(prev);
                LayoutStore.ArchiveIfChanged(prev);

                Console.WriteLine($"  {result.Summary}");
            }
            else
            {
                Console.WriteLine("布局未变化");
            }

            // 检查 360 的窗口坐标是否越界
            var pos = DesktopLiteAdapter.ReadWidgetPos();
            if (pos is not null)
            {
                Console.WriteLine();
                Console.WriteLine($"360 记录的小组件位置: {pos}");

                if (DesktopLiteAdapter.IsOffscreen(pos, monitors))
                {
                    Console.WriteLine("  [越界] 正在修复...");

                    if (!DesktopLiteAdapter.IsRunning())
                    {
                        Console.WriteLine("  360 进程未运行，现在写入会被它启动时覆盖。");
                        Console.WriteLine("  请先启动桌面助手再执行修复，或直接导入备份。");
                        return 2;
                    }

                    var fixedPos = DesktopLiteAdapter.ComputeValidPos(pos, monitors);
                    if (DesktopLiteAdapter.ApplyFix(fixedPos, pos))
                    {
                        Console.WriteLine($"  已修复 -> {fixedPos}");
                        Console.WriteLine("  重启桌面助手后生效");
                        return 1;
                    }
                    Console.WriteLine("  修复失败，详见日志");
                    return 3;
                }
                Console.WriteLine("  [正常] 坐标在可视区域内");
            }

            return 0;
        }
        catch (Exception ex)
        {
            LayoutStore.Log($"巡检异常: {ex}");
            Console.WriteLine($"异常: {ex.Message}");
            return 9;
        }
    }

    private static void PrintStatus()
    {
        var monitors = ScreenLayout.Capture();
        Console.WriteLine("=== 显示器 ===");
        foreach (var m in monitors)
            Console.WriteLine($"  {m.DeviceId}  {m.Width}x{m.Height} @{m.Scale:0.##}x  ({m.X},{m.Y})");

        var snap = LayoutStore.Load();
        Console.WriteLine();
        Console.WriteLine("=== 上次快照 ===");
        if (snap is null)
        {
            Console.WriteLine("  (无)");
        }
        else
        {
            Console.WriteLine($"  时间: {snap.CapturedAt:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"  指纹: {snap.Fingerprint}");
            Console.WriteLine($"  记录: {snap.Fences.Count} 项");

            string fp = ScreenLayout.Fingerprint(monitors);
            Console.WriteLine($"  与当前: {(snap.Fingerprint == fp ? "一致" : "不一致")}");
        }

        Console.WriteLine();
        Console.WriteLine("=== 360 数据 ===");
        var pos = DesktopLiteAdapter.ReadWidgetPos();
        Console.WriteLine(pos is null ? "  (未读取到)" : $"  {pos}");
        Console.WriteLine($"  进程运行中: {(DesktopLiteAdapter.IsRunning() ? "是" : "否")}");

        Console.WriteLine();
        Console.WriteLine($"数据目录: {LayoutStore.DataDir}");
        int hist = Directory.Exists(LayoutStore.HistoryDir)
            ? Directory.GetDirectories(LayoutStore.HistoryDir).Length
            : 0;
        Console.WriteLine($"历史备份: {hist} 份");
    }

    /// 托盘常驻模式。
    private static void RunTray()
    {
        using var watchdog = new ExplorerWatchdog();

        // 每 30 秒检查一次屏幕布局；explorer 由看门狗以 2 秒节奏盯着
        var layoutTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        layoutTimer.Tick += (_, _) => CheckLayout();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("立即检查", null, (_, _) => CheckLayout());
        menu.Items.Add("备份 360 布局数据", null, (_, _) =>
            System.Windows.Forms.MessageBox.Show(
                DesktopLiteAdapter.BackupLayoutData() ? "备份完成" : "备份失败，详见日志"));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("打开数据目录", null, (_, _) =>
            System.Diagnostics.Process.Start("explorer.exe", LayoutStore.DataDir));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) =>
            System.Windows.Forms.Application.Exit());

        var tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Shield,
            Text = "DesktopGuard - 桌面布局守护",
            ContextMenuStrip = menu,
            Visible = true,
        };

        watchdog.ShellRestarted += gen =>
        {
            LayoutStore.Log($"explorer 已重启 (gen={gen})，重新校验布局");
            CheckLayout();
        };

        watchdog.StateChanged += msg =>
        {
            if (Verbose) LayoutStore.Log(msg);
        };

        watchdog.Start();
        layoutTimer.Start();

        CheckLayout();

        System.Windows.Forms.Application.Run();

        layoutTimer.Stop();
        layoutTimer.Dispose();
        tray.Visible = false;
        LayoutStore.Log("=== DesktopGuard 退出 ===");
    }

    private static void CheckLayout()
    {
        var monitors = ScreenLayout.Capture();
        string fp = ScreenLayout.Fingerprint(monitors);
        var prev = LayoutStore.Load();

        if (prev is null)
        {
            var baseline = LayoutCalibrator.NewBaseline(monitors);
            LayoutStore.Save(baseline);
            LayoutStore.ArchiveIfChanged(baseline);
            return;
        }

        if (prev.Fingerprint != fp)
        {
            LayoutStore.Log($"布局变化 detected\n  旧: {prev.Fingerprint}\n  新: {fp}");

            DesktopLiteAdapter.BackupLayoutData();

            var result = LayoutCalibrator.Calibrate(prev, monitors, layoutChanged: true);
            prev.Fingerprint = fp;
            prev.Monitors = monitors;
            prev.CapturedAt = DateTime.Now;
            LayoutStore.Save(prev);
            LayoutStore.ArchiveIfChanged(prev);

            LayoutStore.Log(result.Summary);
        }

        // 越界修复
        var pos = DesktopLiteAdapter.ReadWidgetPos();
        if (pos is not null && DesktopLiteAdapter.IsOffscreen(pos, monitors)
            && DesktopLiteAdapter.IsRunning())
        {
            var fixedPos = DesktopLiteAdapter.ComputeValidPos(pos, monitors);
            DesktopLiteAdapter.ApplyFix(fixedPos, pos);
        }
    }
}
