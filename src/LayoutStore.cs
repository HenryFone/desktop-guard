using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopGuard;

/// 一条收纳盒/小组件的位置记录。
/// 同时保存绝对像素与归一化比例，两种都留着才能既快又稳。
internal sealed class FenceRecord
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";

    /// 所在显示器的稳定身份（设备名|分辨率|缩放）。
    /// 用身份而不是坐标来记住"它在哪个屏上"。
    public string MonitorIdentity { get; set; } = "";

    // 绝对像素（原样保存，同布局时可原样恢复）
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }

    public int LastDpi { get; set; } = 96;

    // 归一化比例（跨布局恢复时才用）
    public double RelLeft { get; set; }
    public double RelTop { get; set; }
    public double RelWidth { get; set; }
    public double RelHeight { get; set; }
}

internal sealed class LayoutSnapshot
{
    public string Schema { get; set; } = "desktopguard/layout/1";
    public DateTime CapturedAt { get; set; } = DateTime.Now;
    public string Fingerprint { get; set; } = "";
    public List<MonitorDesc> Monitors { get; set; } = new();
    public List<FenceRecord> Fences { get; set; } = new();
}

internal static class LayoutStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopGuard");

    public static string CurrentFile => Path.Combine(DataDir, "layout.json");
    public static string BackupFile => Path.Combine(DataDir, "layout.json.bak");
    public static string HistoryDir => Path.Combine(DataDir, "history");
    public static string LogFile => Path.Combine(DataDir, "guard.log");

    public static void EnsureDirs()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(HistoryDir);
    }

    /// 原子写入。
    /// 借鉴 desktop_box JsonStoreService：
    /// 先写临时文件 → 把旧的换成 .bak → 再把临时文件顶上去。
    /// 任一步骤崩溃，磁盘上始终留有一份完整配置。
    public static void Save(LayoutSnapshot snapshot)
    {
        EnsureDirs();
        string json = JsonSerializer.Serialize(snapshot, JsonOpts);
        string tmp = CurrentFile + ".tmp";

        File.WriteAllText(tmp, json, new UTF8Encoding(false));

        if (File.Exists(CurrentFile))
        {
            try
            {
                File.Replace(tmp, CurrentFile, BackupFile, ignoreMetadataErrors: true);
            }
            catch (PlatformNotSupportedException)
            {
                // 某些文件系统不支持 Replace，退回复制覆盖
                File.Copy(CurrentFile, BackupFile, overwrite: true);
                File.Copy(tmp, CurrentFile, overwrite: true);
                File.Delete(tmp);
            }
            catch (IOException)
            {
                File.Copy(CurrentFile, BackupFile, overwrite: true);
                File.Copy(tmp, CurrentFile, overwrite: true);
                File.Delete(tmp);
            }
        }
        else
        {
            File.Move(tmp, CurrentFile);
        }
    }

    /// 读取，主文件坏了自动回退到 .bak，并保留损坏文件供排查。
    /// 借鉴 desktop_box：坏文件另存带时间戳的副本，绝不直接覆盖掉唯一的证据。
    public static LayoutSnapshot? Load()
    {
        var snap = TryRead(CurrentFile);
        if (snap is not null) return snap;

        if (File.Exists(CurrentFile))
        {
            string corrupt = CurrentFile + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Copy(CurrentFile, corrupt, overwrite: true); } catch { /* 尽力而为 */ }
            Log($"主配置损坏，已另存为 {Path.GetFileName(corrupt)}");
        }

        snap = TryRead(BackupFile);
        if (snap is not null)
        {
            Log("已从备份恢复配置");
            return snap;
        }

        return null;
    }

    private static LayoutSnapshot? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            string json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<LayoutSnapshot>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    /// 按布局指纹归档历史快照，便于回溯。
    public static void ArchiveIfChanged(LayoutSnapshot snapshot)
    {
        EnsureDirs();
        string tag = ShortHash(snapshot.Fingerprint);
        string target = Path.Combine(HistoryDir, $"{DateTime.Now:yyyyMMdd-HHmmss}_{tag}.json");

        string newest = Directory.Exists(HistoryDir)
            ? Directory.GetFiles(HistoryDir, "*.json").OrderByDescending(f => f).FirstOrDefault() ?? ""
            : "";

        if (newest.Length > 0)
        {
            var prev = TryRead(newest);
            if (prev is not null && prev.Fingerprint == snapshot.Fingerprint) return;
        }

        try
        {
            File.WriteAllText(target, JsonSerializer.Serialize(snapshot, JsonOpts), new UTF8Encoding(false));
            Log($"归档布局快照 {Path.GetFileName(target)}");
        }
        catch (Exception ex)
        {
            Log($"归档失败: {ex.Message}");
        }
    }

    private static string ShortHash(string s)
    {
        using var md5 = MD5.Create();
        byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(h, 0, 4).ToLowerInvariant();
    }

    public static void Log(string message)
    {
        try
        {
            EnsureDirs();
            File.AppendAllText(LogFile,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch { /* 日志失败不能影响主流程 */ }
    }
}
