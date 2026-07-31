using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Brushes = System.Windows.Media.Brushes;

namespace BeeX.DeskNest;

/// <summary>
/// Host window for the WebView2 scene editor (wwwroot\editor.html). The page owns the scene model and live preview;
/// this host answers its messages: picking media assets (copied into the wallpaper's folder so scenes stay portable),
/// saving scene.json, and closing. Saving also refreshes the library entry and the running engine.
/// </summary>
public sealed class SceneEditorWindow : Window
{
    readonly DeskNestService service;
    readonly WallpaperItem item;
    readonly string itemDir;
    readonly Microsoft.Web.WebView2.Wpf.WebView2 web = new() { DefaultBackgroundColor = System.Drawing.Color.FromArgb(13, 19, 33) };
    readonly WallpaperRuntimeBridge bridge = new();

    /// <summary>Raised after a successful save so the gallery can refresh its cards.</summary>
    public event Action? Saved;

    public SceneEditorWindow(DeskNestService service, WallpaperItem item)
    {
        this.service = service;
        this.item = item;
        itemDir = Path.GetDirectoryName(item.Path) ?? Path.Combine(BeeXPaths.WallpapersDir, item.Id.ToString("N"));
        Title = Localization.T("場景編輯器", service.State.Language);
        Width = 1080; Height = 680;
        MinWidth = 860; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Black;
        Content = web;
        Loaded += async (_, _) => await InitAsync();
        Closed += (_, _) => { try { web.Dispose(); } catch { } };
    }

    async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(BeeXPaths.DataDir, "WallpaperWV2"));
            await web.EnsureCoreWebView2Async(env);
            var core = web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            var assets = await Task.Run(WallpaperWebAssets.EnsureWebAssets);
            core.SetVirtualHostNameToFolderMapping("wallpaper.beex", assets, CoreWebView2HostResourceAccessKind.Allow);
            Directory.CreateDirectory(itemDir);
            core.SetVirtualHostNameToFolderMapping("item.beex", itemDir, CoreWebView2HostResourceAccessKind.Allow);
            bridge.Attach(core);
            bridge.Ready += () => Dispatcher.BeginInvoke(PushScene);
            bridge.MessageReceived += (type, root) => Dispatcher.BeginInvoke(() => OnMessage(type, root));
            core.Navigate("https://wallpaper.beex/editor.html");
        }
        catch { Close(); }
    }

    void PushScene()
    {
        try
        {
            var lang = service.State.Language;
            bridge.Post(new { type = "locale", value = lang == "zh-CN" ? "zh-CN" : lang == "en-US" ? "en" : "zh-TW" });
            if (File.Exists(item.Path))
                bridge.Post(new { type = "loadScene", json = File.ReadAllText(item.Path) });
        }
        catch { }
    }

    void OnMessage(string type, JsonElement root)
    {
        switch (type)
        {
            case "pickAsset": PickAsset(root); break;
            case "save": SaveScene(root); break;
            case "closeEditor": Close(); break;
        }
    }

    void PickAsset(JsonElement root)
    {
        var requestId = root.TryGetProperty("requestId", out var r) ? r.GetInt32() : 0;
        var kind = root.TryGetProperty("kind", out var k) ? k.GetString() : "image";
        var filter = kind == "video"
            ? "Video|*.mp4;*.webm;*.mkv;*.mov;*.avi;*.m4v"
            : "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp";
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = filter };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var name = Path.GetFileName(dialog.FileName);
            var dest = Path.Combine(itemDir, name);
            if (!string.Equals(dialog.FileName, dest, StringComparison.OrdinalIgnoreCase))
                File.Copy(dialog.FileName, dest, true);
            bridge.Post(new { type = "assetPicked", requestId, src = "https://item.beex/" + Uri.EscapeDataString(name), name });
        }
        catch { }
    }

    void SaveScene(JsonElement root)
    {
        try
        {
            var json = root.TryGetProperty("json", out var j) ? j.GetString() : null;
            if (string.IsNullOrWhiteSpace(json)) return;
            Directory.CreateDirectory(itemDir);
            File.WriteAllText(item.Path, json);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                item.Name = name;
            service.Save();
            service.Wallpaper?.ApplyPreferences();
            Saved?.Invoke();
        }
        catch { }
    }
}
