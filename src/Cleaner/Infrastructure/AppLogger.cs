using System.IO;
using System.Text;

namespace BeeXCleaner.Infrastructure;

/// <summary>
/// 轻量内部日志：写入 Logs\app.log。用户界面通常不弹出这些内容，
/// 但异常、扫描失败等信息会落盘可查，避免问题被空 catch 悄悄吞掉。线程安全。
/// </summary>
public static class AppLogger
{
    private static readonly object _gate = new();
    private const long MaxBytes = 2 * 1024 * 1024; // 2MB 后滚动一次

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var path = Path.Combine(AppPaths.LogsRoot, "app.log");
            lock (_gate)
            {
                RollIfNeeded(path);
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                  .Append(" [").Append(level).Append("] ").Append(message);
                if (ex is not null)
                    sb.Append(" :: ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
                sb.AppendLine();
                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
        }
        catch { /* 日志失败绝不能影响主流程 */ }
    }

    private static void RollIfNeeded(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length <= MaxBytes) return;
            var bak = path + ".1";
            if (File.Exists(bak)) File.Delete(bak);
            File.Move(path, bak);
        }
        catch { /* 忽略滚动失败 */ }
    }
}
