using System;
using System.IO;
using System.Text.Json;

namespace BeexWrite.Services;

/// <summary>
/// Crash / unsaved-draft recovery. While a document is dirty, the editor writes
/// the live content plus a small metadata record to the recovery folder. A
/// clean exit clears them; if they survive to the next launch, the previous
/// session ended unexpectedly and the draft can be offered for recovery.
/// </summary>
public sealed class RecoveryService
{
    private readonly string _dir;
    private readonly string _draftPath;
    private readonly string _metaPath;

    public RecoveryService()
    {
        _dir = Path.Combine(WriteHost.WriteDataDirectory, "recovery");
        _draftPath = Path.Combine(_dir, "draft.md");
        _metaPath = Path.Combine(_dir, "draft.json");
    }

    public sealed class DraftMeta
    {
        public string? OriginalPath { get; set; }
        public DateTime SavedAtUtc { get; set; }
        public int CursorLine { get; set; } = 1;
        /// <summary>SHA-256 of the on-disk file at draft time; used to detect external edits.</summary>
        public string? FileHash { get; set; }
    }

    /// <summary>Computes the SHA-256 of a file, or null when unavailable.</summary>
    public static string? HashFile(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(fs));
        }
        catch
        {
            return null;
        }
    }

    public void SaveDraft(string? originalPath, string content, int cursorLine = 1)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_draftPath, content);
            var meta = new DraftMeta
            {
                OriginalPath = originalPath,
                SavedAtUtc = DateTime.UtcNow,
                CursorLine = cursorLine,
                FileHash = HashFile(originalPath)
            };
            File.WriteAllText(_metaPath, JsonSerializer.Serialize(meta));
        }
        catch
        {
            // Recovery is best-effort.
        }
    }

    public void ClearDraft()
    {
        try
        {
            if (File.Exists(_draftPath)) File.Delete(_draftPath);
            if (File.Exists(_metaPath)) File.Delete(_metaPath);
        }
        catch
        {
            // Ignore.
        }
    }

    public bool TryGetDraft(out string content, out DraftMeta meta)
    {
        content = string.Empty;
        meta = new DraftMeta();
        try
        {
            if (!File.Exists(_draftPath) || !File.Exists(_metaPath)) return false;
            content = File.ReadAllText(_draftPath);
            var loaded = JsonSerializer.Deserialize<DraftMeta>(File.ReadAllText(_metaPath));
            if (loaded is not null) meta = loaded;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
