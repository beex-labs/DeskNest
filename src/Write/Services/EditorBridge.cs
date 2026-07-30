using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using BeexWrite.Models;
using Microsoft.Web.WebView2.Core;

namespace BeexWrite.Services;

/// <summary>
/// Typed message channel between the WPF shell and the CodeMirror editor
/// running inside WebView2. Outgoing messages are posted as JSON; incoming
/// ones are parsed and surfaced as .NET events.
/// </summary>
public sealed class EditorBridge
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private CoreWebView2? _core;
    private int _requestId;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pending = new();

    public event EventHandler? Ready;
    public event EventHandler<bool>? DirtyChanged;
    public event EventHandler<DocStats>? StatsChanged;
    public event EventHandler<List<OutlineEntry>>? OutlineChanged;
    public event EventHandler<CursorContext>? CursorContextChanged;
    public event EventHandler? SaveRequested;
    public event EventHandler<string>? HostCommandRequested;
    public event EventHandler<string>? OpenUrlRequested;
    public event EventHandler<string>? FileDropped;
    public event EventHandler<(string Data, string Name)>? ImagePasted;

    public bool IsAttached => _core != null;

    public void Attach(CoreWebView2 core)
    {
        _core = core;
        _core.WebMessageReceived += OnWebMessageReceived;
    }

    // ---- outgoing -----------------------------------------------------------

    public void SetContent(string content, string? path = null) =>
        Post(new { type = "setContent", content, path });

    public void MarkSaved() => Post(new { type = "markSaved" });

    public void GoToLine(int line) => Exec("goToLine", new { line });

    public void SetSourceMode(bool enabled) => Post(new { type = "setSourceMode", enabled });

    public void SetTheme(string theme) => Post(new { type = "setTheme", theme });

    public void SetFocusMode(bool enabled) => Post(new { type = "setFocusMode", enabled });

    public void SetTypewriterMode(bool enabled) => Post(new { type = "setTypewriterMode", enabled });

    public void SetZoom(double factor) => Post(new { type = "setZoom", factor });

    public void SetEditorWidth(int width) => Post(new { type = "setEditorWidth", width });

    public void SetCustomCss(string css) => Post(new { type = "setCustomCss", css });

    public void SetShortcuts(Dictionary<string, string> map) => Post(new { type = "setShortcuts", map });

    public void Focus() => Post(new { type = "focus" });

    public void Exec(string command, object? payload = null) =>
        Post(new { type = "exec", command, payload });

    public Task<string> RequestContentAsync()
    {
        if (_core is null) return Task.FromResult(string.Empty);
        var id = System.Threading.Interlocked.Increment(ref _requestId);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        Post(new { type = "requestContent", requestId = id });
        return tcs.Task;
    }

    private void Post(object message)
    {
        if (_core is null) return;
        var json = JsonSerializer.Serialize(message);
        _core.PostWebMessageAsJson(json);
    }

    // ---- incoming -----------------------------------------------------------

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try
        {
            json = e.WebMessageAsJson;
        }
        catch
        {
            return;
        }

        // File drop with full paths (postMessageWithAdditionalObjects)
        try
        {
            if (e.AdditionalObjects is { Count: > 0 })
            {
                foreach (var obj in e.AdditionalObjects)
                {
                    if (obj is CoreWebView2File file && !string.IsNullOrEmpty(file.Path))
                    {
                        FileDropped?.Invoke(this, file.Path);
                        return; // open first file only
                    }
                }
            }
        }
        catch { /* AdditionalObjects unsupported — ignore */ }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)) return;
            var type = typeProp.GetString();

            switch (type)
            {
                case "ready":
                    Ready?.Invoke(this, EventArgs.Empty);
                    break;
                case "docChanged":
                    DirtyChanged?.Invoke(this, root.TryGetProperty("dirty", out var d) && d.GetBoolean());
                    break;
                case "stats":
                    var stats = JsonSerializer.Deserialize<DocStats>(json, ReadOptions);
                    if (stats != null) StatsChanged?.Invoke(this, stats);
                    break;
                case "outline":
                    var payload = JsonSerializer.Deserialize<OutlinePayload>(json, ReadOptions);
                    OutlineChanged?.Invoke(this, payload?.Items ?? new List<OutlineEntry>());
                    break;
                case "cursorContext":
                    if (root.TryGetProperty("context", out var ctxEl))
                    {
                        var ctx = JsonSerializer.Deserialize<CursorContext>(ctxEl.GetRawText(), ReadOptions);
                        if (ctx != null) CursorContextChanged?.Invoke(this, ctx);
                    }
                    break;
                case "requestSave":
                    SaveRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case "hostCommand":
                    if (root.TryGetProperty("command", out var cmd))
                        HostCommandRequested?.Invoke(this, cmd.GetString() ?? string.Empty);
                    break;
                case "openUrl":
                    if (root.TryGetProperty("url", out var urlProp))
                    {
                        var url = urlProp.GetString();
                        if (!string.IsNullOrEmpty(url))
                            OpenUrlRequested?.Invoke(this, url);
                    }
                    break;
                case "pasteImage":
                    var imgData = root.TryGetProperty("data", out var dp) ? dp.GetString() ?? "" : "";
                    var imgName = root.TryGetProperty("name", out var np) ? np.GetString() ?? "image.png" : "image.png";
                    if (imgData.Length > 0)
                        ImagePasted?.Invoke(this, (imgData, imgName));
                    break;
                case "content":
                    var reqId = root.TryGetProperty("requestId", out var r) ? r.GetInt32() : -1;
                    var content = root.TryGetProperty("content", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                    if (_pending.TryRemove(reqId, out var tcs)) tcs.TrySetResult(content);
                    break;
            }
        }
        catch
        {
            // Malformed messages are ignored to keep the editor responsive.
        }
    }
}
