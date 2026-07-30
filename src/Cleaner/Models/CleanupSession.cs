using System.IO;
using System.Text;
using BeeXCleaner.Infrastructure;

namespace BeeXCleaner.Models;

/// <summary>清理操作类型。</summary>
public enum CleanupOperation
{
    /// <summary>卸载后残留清理。</summary>
    Residual,
    /// <summary>全系统遗留清理。</summary>
    Orphan,
    /// <summary>强制删除。</summary>
    ForceRemove,
    /// <summary>快速删除文件/文件夹。</summary>
    QuickDelete
}

/// <summary>
/// 清理会话：一次“清理动作”对应一个会话，统一分配备份目录、日志路径，并累积操作日志。
/// 备份目录延迟创建（首次备份时才真正建立），避免产生大量空目录。
/// </summary>
public sealed class CleanupSession
{
    private readonly StringBuilder _log = new();
    private bool _backupDirCreated;

    public CleanupSession(CleanupOperation op, IEnumerable<string>? targets = null)
    {
        OperationType = op;
        StartedAt = DateTime.Now;
        // 附加短随机后缀：秒级粒度下同秒两次同类操作会碰撞，导致日志互相覆盖、备份目录混叠
        SessionId = $"{StartedAt:yyyyMMdd-HHmmss}-{op}-{Guid.NewGuid().ToString("N")[..6]}";
        TargetPrograms = (targets ?? Array.Empty<string>()).ToList();
        BackupFolder = Path.Combine(AppPaths.BackupsRoot, SessionId);
        LogPath = Path.Combine(AppPaths.LogsRoot, SessionId + ".log");
    }

    public string SessionId { get; }
    public DateTime StartedAt { get; }
    public CleanupOperation OperationType { get; }
    public IReadOnlyList<string> TargetPrograms { get; }

    /// <summary>本次会话的注册表备份目录（可能尚未创建，见 <see cref="EnsureBackupFolder"/>）。</summary>
    public string BackupFolder { get; }

    /// <summary>本次会话的日志文件路径。</summary>
    public string LogPath { get; }

    /// <summary>是否已产生任何备份（决定结果窗口是否显示备份路径）。</summary>
    public bool HasBackups => _backupDirCreated;

    public string OperationTypeText => OperationType switch
    {
        CleanupOperation.Residual => "残留清理",
        CleanupOperation.Orphan => "遗留清理",
        CleanupOperation.ForceRemove => "强制删除",
        CleanupOperation.QuickDelete => "快速删除",
        _ => "清理"
    };

    /// <summary>确保备份目录存在（首次调用时创建）。返回目录路径。</summary>
    public string EnsureBackupFolder()
    {
        if (!_backupDirCreated)
        {
            try { Directory.CreateDirectory(BackupFolder); _backupDirCreated = true; }
            catch (Exception ex) { AppLogger.Warn($"创建备份目录失败: {BackupFolder}", ex); }
        }
        return BackupFolder;
    }

    /// <summary>追加一行操作日志（带时间戳）。</summary>
    public void Log(string line) => _log.AppendLine($"{DateTime.Now:HH:mm:ss}  {line}");

    /// <summary>已累积的操作日志文本。</summary>
    public string LogText => _log.ToString();

    /// <summary>把会话日志（含头部元信息）写盘。返回日志文件路径，失败返回 null。</summary>
    public string? Flush(string? summary = null)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("BeeX Cleaner 清理日志");
            sb.AppendLine($"会话: {SessionId}");
            sb.AppendLine($"时间: {StartedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"操作: {OperationTypeText}");
            if (TargetPrograms.Count > 0)
                sb.AppendLine($"目标: {string.Join(", ", TargetPrograms)}");
            if (!string.IsNullOrWhiteSpace(summary))
                sb.AppendLine($"结果: {summary}");
            if (_backupDirCreated)
                sb.AppendLine($"注册表备份目录: {BackupFolder}");
            sb.AppendLine(new string('-', 60));
            sb.Append(_log);
            File.WriteAllText(LogPath, sb.ToString(), Encoding.UTF8);
            return LogPath;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("写入会话日志失败", ex);
            return null;
        }
    }
}
