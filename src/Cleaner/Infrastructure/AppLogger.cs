using System.IO;
using System.Text;

namespace BeeXCleaner.Infrastructure;

/// <summary>
/// Lightweight internal logging: Written to Logs\app.log. The user interface typically does not display this information,
/// However, information such as exceptions and scan failures is logged for future reference, preventing issues from being silently swallowed by an empty `catch` block. Thread-safe.
/// </summary>
public static class AppLogger
{
    private static readonly object _gate = new();
    private const long MaxBytes = 2 * 1024 * 1024; // Scroll once every 2 MB

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
        catch { /* Logging failures must never affect the main process. */ }
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
        catch { /* Ignore Scroll Failure */ }
    }
}
