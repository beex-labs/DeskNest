using System.IO;
using System.Text;
using BeeXCleaner.Infrastructure;

namespace BeeXCleaner.Models;

/// <summary>Cleanup operation types. </summary>
public enum CleanupOperation
{
    /// <summary>Cleaning up leftover files after uninstallation.</summary>
    Residual,
    /// <summary>System-wide legacy cleanup. </summary>
    Orphan,
    /// <summary>Forced deletion.</summary>
    ForceRemove,
    /// <summary>Quickly delete files/folders.</summary>
    QuickDelete
}

/// <summary>
/// Clear Session: Each "clear operation" corresponds to a single session, uniformly assigning backup directories and log paths, and accumulating operation logs.
/// The backup directory is created later (it is not actually created until the first backup), to avoid generating a large number of empty directories.
/// </summary>
public sealed class CleanupSession
{
    private readonly StringBuilder _log = new();
    private bool _backupDirCreated;

    public CleanupSession(CleanupOperation op, IEnumerable<string>? targets = null)
    {
        OperationType = op;
        StartedAt = DateTime.Now;
        // Append a short random suffix: If two operations of the same type occur within the same second at the second-level granularity, they will conflict, causing logs to overwrite each other and backup directories to become mixed up.
        SessionId = $"{StartedAt:yyyyMMdd-HHmmss}-{op}-{Guid.NewGuid().ToString("N")[..6]}";
        TargetPrograms = (targets ?? Array.Empty<string>()).ToList();
        BackupFolder = Path.Combine(AppPaths.BackupsRoot, SessionId);
        LogPath = Path.Combine(AppPaths.LogsRoot, SessionId + ".log");
    }

    public string SessionId { get; }
    public DateTime StartedAt { get; }
    public CleanupOperation OperationType { get; }
    public IReadOnlyList<string> TargetPrograms { get; }

    /// <summary>The registry backup directory for this session (may not have been created yet; see <see cref="EnsureBackupFolder"/>).</summary>
    public string BackupFolder { get; }

    /// <summary>The path to the log file for this session. </summary>
    public string LogPath { get; }

    /// <summary>Whether any backups have been created (determines whether the results window displays the backup path). </summary>
    public bool HasBackups => _backupDirCreated;

    public string OperationTypeText => OperationType switch
    {
        CleanupOperation.Residual => "残留清理",
        CleanupOperation.Orphan => "遗留清理",
        CleanupOperation.ForceRemove => "强制删除",
        CleanupOperation.QuickDelete => "快速删除",
        _ => "清理"
    };

    /// <summary>Ensures that the backup directory exists (it is created on the first call). Returns the directory path. </summary>
    public string EnsureBackupFolder()
    {
        if (!_backupDirCreated)
        {
            try { Directory.CreateDirectory(BackupFolder); _backupDirCreated = true; }
            catch (Exception ex) { AppLogger.Warn($"创建备份目录失败: {BackupFolder}", ex); }
        }
        return BackupFolder;
    }

    /// <summary>Add a line to the operation log (with a timestamp). </summary>
    public void Log(string line) => _log.AppendLine($"{DateTime.Now:HH:mm:ss}  {line}");

    /// <summary>Text of the accumulated operation logs.</summary>
    public string LogText => _log.ToString();

    /// <summary> Writes the session log (including header metadata) to disk. Returns the path to the log file; returns null on failure. </summary>
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
