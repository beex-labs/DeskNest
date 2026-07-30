using System.IO;
using System.Windows.Threading;

namespace BeeX.DeskNest;

/// <summary>
/// 监听本地播放器歌词目录的变化，当检测到匹配当前歌曲的新 .lrc 文件时触发回调。
/// 用于在不侵入播放器进程的前提下，实时获取原生软件（网易云/QQ音乐/汽水音乐等）落盘的歌词。
/// </summary>
internal sealed class MusicLyricsWatcher : IDisposable
{
    readonly Dispatcher dispatcher;
    readonly List<FileSystemWatcher> watchers = [];
    readonly HashSet<string> knownFiles = new(StringComparer.OrdinalIgnoreCase);
    string? currentTitle;
    string? currentArtist;
    Action<string>? onLyricsFile;
    bool enabled;
    DispatcherTimer? debounceTimer;
    string? pendingFile;

    public MusicLyricsWatcher(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
    }

    /// <summary>设定当前播放歌曲并启动监听。回调在 UI 线程触发，参数为匹配的 .lrc 文件完整路径。</summary>
    public void Start(string title, string artist, Action<string> onLyricsFile)
    {
        currentTitle = title;
        currentArtist = artist;
        this.onLyricsFile = onLyricsFile;
        StopWatchers();
        knownFiles.Clear();
        enabled = true;

        foreach (var dir in MusicLyricsService.GetWatchDirectories())
        {
            FileSystemWatcher watcher;
            try
            {
                watcher = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
            }
            catch { continue; }

            // 记录目录下已有的 .lrc 文件，避免启动时立即触发已有文件
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.lrc", SearchOption.AllDirectories))
                    knownFiles.Add(f);
            }
            catch { }

            watcher.Created += (_, e) => OnFileChanged(e.FullPath);
            watcher.Changed += (_, e) => OnFileChanged(e.FullPath);
            watcher.Renamed += (_, e) => OnFileChanged(e.FullPath);
            watchers.Add(watcher);
        }
    }

    /// <summary>停止监听（保留歌曲元数据，便于重启）。</summary>
    public void Pause()
    {
        enabled = false;
        StopWatchers();
    }

    /// <summary>完全停止并清空状态。</summary>
    public void Stop()
    {
        enabled = false;
        StopWatchers();
        knownFiles.Clear();
        currentTitle = null;
        currentArtist = null;
        onLyricsFile = null;
        pendingFile = null;
        debounceTimer?.Stop();
    }

    void OnFileChanged(string path)
    {
        if (!enabled || onLyricsFile == null) return;
        if (!path.EndsWith(".lrc", StringComparison.OrdinalIgnoreCase)) return;
        // 防抖：播放器可能多次写同一个文件，合并短时间内的多次触发
        pendingFile = path;
        debounceTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(600) };
        debounceTimer.Stop();
        debounceTimer.Tick += (_, _) =>
        {
            debounceTimer!.Stop();
            if (!enabled || pendingFile == null) return;
            var file = pendingFile;
            pendingFile = null;
            // 切回 UI 线程触发回调
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (!enabled || onLyricsFile == null || string.IsNullOrEmpty(currentTitle)) return;
                onLyricsFile(file);
            }));
        };
        debounceTimer.Start();
    }

    void StopWatchers()
    {
        foreach (var w in watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
        }
        watchers.Clear();
    }

    public void Dispose()
    {
        Stop();
        debounceTimer = null;
        GC.SuppressFinalize(this);
    }
}
