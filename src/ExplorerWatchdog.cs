using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DesktopGuard;

/// explorer.exe 生命周期看门狗。
///
/// 为什么需要它：360桌面助手是挂在 explorer 上的叠加层。
/// explorer 一重启，叠加层就失去宿主 —— 表现为"界面刷新/图标消失/自己重启"。
///
/// 借鉴 PecoFence `anchor.rs`：
///   - 用进程/桌面"代际(generation)"判断宿主是否换过
///   - 失败后用递增延迟重试，而不是立刻重试（避免和 Windows 抢时序）
internal sealed class ExplorerWatchdog : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// PecoFence 的重试节奏：50 → 150 → 350 → 750 → 1500 → 1200 ms。
    /// 前快后慢：既能在宿主就绪后立刻接上，又不会在长时间不可用时疯狂占用 CPU。
    private static readonly int[] RetryDelaysMs = { 50, 150, 350, 750, 1500, 1200 };

    private readonly System.Windows.Forms.Timer _timer;
    private int _lastGeneration = -1;
    private int _lastPid = -1;
    private int _retryStep;

    public event Action<int>? ShellRestarted;
    public event Action<string>? StateChanged;

    public ExplorerWatchdog()
    {
        // 2 秒一次：与 PecoFence 的 TIMER_ANCHOR_CHECK 节奏一致。
        // 轮询极轻（两个 Win32 调用），不会造成可感知的 CPU 占用。
        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start()
    {
        _lastPid = CurrentShellPid();
        _lastGeneration = CurrentGeneration();
        _timer.Start();
        StateChanged?.Invoke($"看门狗已启动，当前 explorer PID={_lastPid}");
    }

    public void Stop() => _timer.Stop();

    private void Poll()
    {
        int pid = CurrentShellPid();
        int gen = CurrentGeneration();

        bool changed = pid != _lastPid || gen != _lastGeneration;

        if (!changed)
        {
            _retryStep = 0;
            return;
        }

        // 宿主换了：先等一小段，让新 explorer 把桌面窗口建起来，再通知外部重新挂接。
        int delay = RetryDelaysMs[Math.Min(_retryStep, RetryDelaysMs.Length - 1)];
        _retryStep++;

        StateChanged?.Invoke(
            $"检测到 explorer 变更 (旧PID={_lastPid} 新PID={pid})，{delay}ms 后触发重挂");

        _lastPid = pid;
        _lastGeneration = gen;

        var t = new System.Windows.Forms.Timer { Interval = delay };
        t.Tick += (s, _) =>
        {
            ((System.Windows.Forms.Timer)s!).Stop();
            ((System.Windows.Forms.Timer)s!).Dispose();
            ShellRestarted?.Invoke(gen);
        };
        t.Start();
    }

    /// 当前 shell 窗口所属进程。
    private static int CurrentShellPid()
    {
        IntPtr shell = GetShellWindow();
        if (shell == IntPtr.Zero) return -1;
        GetWindowThreadProcessId(shell, out uint pid);
        return (int)pid;
    }

    /// 代际计数：宿主进程换一次，代际 +1。
    /// 比单纯比较 PID 更可靠（PID 可能被系统复用）。
    private static int CurrentGeneration()
    {
        try
        {
            var procs = Process.GetProcessesByName("explorer");
            if (procs.Length == 0) return 0;

            // 取启动最早的那个当作桌面宿主，其启动时间戳作为代际标识
            var oldest = procs
                .Where(p => { try { return true; } catch { return false; } })
                .OrderBy(p => { try { return p.StartTime; } catch { return DateTime.MaxValue; } })
                .First();

            return oldest.StartTime.GetHashCode();
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
