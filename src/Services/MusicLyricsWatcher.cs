using System.IO;
using System.Windows.Threading;

namespace BeeX.DeskNest;

/// <summary>
/// Watches a local player's lyrics directory for changes and fires a callback when a new .lrc file
/// matching the current song appears. Lets the app pick up lyrics written to disk by the player
/// without hooking into the player process.
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

    /// <summary>Sets the currently playing song and starts watching. The callback fires on the UI thread with the full path of the matched .lrc file.</summary>
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

            // Record the .lrc files already present in the directory to avoid triggering on existing files at startup
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

    /// <summary>Stops watching (keeps the song metadata for an easy restart).</summary>
    public void Pause()
    {
        enabled = false;
        StopWatchers();
    }

    /// <summary>Stops completely and clears state.</summary>
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
        // Debounce: the player may write the same file multiple times; merge repeated triggers within a short window
        pendingFile = path;
        debounceTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(600) };
        debounceTimer.Stop();
        debounceTimer.Tick += (_, _) =>
        {
            debounceTimer!.Stop();
            if (!enabled || pendingFile == null) return;
            var file = pendingFile;
            pendingFile = null;
            // Switch back to the UI thread to fire the callback
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
