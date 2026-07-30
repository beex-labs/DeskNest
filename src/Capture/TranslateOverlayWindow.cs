using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shapes = System.Windows.Shapes;
using IOPath = System.IO.Path;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Clipboard = System.Windows.Clipboard;
using MouseButtonState = System.Windows.Input.MouseButtonState;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingColor = System.Drawing.Color;

namespace BeeX.DeskNest;

/// <summary>
/// OCR 文本块：描述原图中一个文本区域的坐标和内容。
/// </summary>
internal sealed class OcrTextBlock
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public string Text { get; init; } = "";
}

/// <summary>
/// 翻译后的文本块，携带译文和原始坐标。
/// </summary>
internal sealed record TranslatedBlock
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public string OriginalText { get; init; } = "";
    public string TranslatedText { get; init; } = "";
}

/// <summary>
/// 翻译覆盖窗口：在截图原图上叠加译文，实现原位翻译效果。
/// 纯代码 WPF Canvas 渲染，无 XAML。
/// </summary>
internal sealed class TranslateOverlayWindow : Window
{
    private readonly string _allTranslatedText;

    /* ── 静态工厂方法 ── */

    /// <summary>批量翻译所有文本块到指定目标语言（DeepL 单次多句，可取消）。</summary>
    public static async Task<List<TranslatedBlock>> TranslateBlocksAsync(List<OcrTextBlock> blocks, string targetLang, CancellationToken ct)
    {
        var results = new TranslatedBlock[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
            results[i] = new TranslatedBlock { X = blocks[i].X, Y = blocks[i].Y, Width = blocks[i].Width, Height = blocks[i].Height, OriginalText = blocks[i].Text, TranslatedText = blocks[i].Text };

        var idx = Enumerable.Range(0, blocks.Count).Where(i => !string.IsNullOrWhiteSpace(blocks[i].Text)).ToList();
        if (idx.Count == 0) return results.ToList();
        var texts = idx.Select(i => blocks[i].Text).ToList();

        string? deepLKey = GetDeepLKey();
        List<string> translated;
        try
        {
            translated = !string.IsNullOrEmpty(deepLKey)
                ? await TranslateBatchViaDeepL(texts, targetLang, deepLKey, ct)
                : await TranslateBatchFallback(texts, targetLang, ct);
        }
        catch { translated = texts; }

        for (int k = 0; k < idx.Count; k++)
            results[idx[k]] = results[idx[k]] with { TranslatedText = k < translated.Count && translated[k].Length > 0 ? translated[k] : blocks[idx[k]].Text };
        return results.ToList();
    }

    /// <summary>DeepL 批量翻译：一次请求提交多条 text（分块 ≤48），按顺序返回。</summary>
    private static async Task<List<string>> TranslateBatchViaDeepL(List<string> texts, string targetLang, string apiKey, CancellationToken ct)
    {
        var output = new List<string>(texts.Count);
        string tgt = MapDeepLLang(targetLang);
        for (int off = 0; off < texts.Count; off += 48)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = texts.Skip(off).Take(48).ToList();
            var body = new StringBuilder();
            body.Append("target_lang=").Append(Uri.EscapeDataString(tgt));
            foreach (var t in chunk) body.Append("&text=").Append(Uri.EscapeDataString(t));
            // DeepL 2025-11 起弃用 form body 里的 auth_key，必须用 Authorization 头
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api-free.deepl.com/v2/translate");
            req.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + apiKey);
            req.Content = new StringContent(body.ToString(), new UTF8Encoding(false), "application/x-www-form-urlencoded");
            using var resp = await Http.SendAsync(req, ct);
            if ((int)resp.StatusCode == 456) throw new InvalidOperationException("DeepL quota exceeded");
            resp.EnsureSuccessStatusCode();
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var arr = doc.RootElement.GetProperty("translations");
            for (int i = 0; i < chunk.Count; i++)
                output.Add(i < arr.GetArrayLength() ? arr[i].GetProperty("text").GetString()?.Trim() ?? chunk[i] : chunk[i]);
        }
        return output;
    }

    /// <summary>无 DeepL key 时的兜底：MyMemory 逐条并行（限流 6）。</summary>
    private static async Task<List<string>> TranslateBatchFallback(List<string> texts, string targetLang, CancellationToken ct)
    {
        using var throttle = new SemaphoreSlim(6);
        var tasks = texts.Select(async t =>
        {
            await throttle.WaitAsync(ct);
            try { return await TranslateInline(t, targetLang); }
            catch { return t; }
            finally { throttle.Release(); }
        }).ToList();
        return (await Task.WhenAll(tasks)).ToList();
    }

    /// <summary>加载原图、翻译每个 block、创建覆盖窗口并显示。</summary>
    public static async Task ShowAsync(
        string imagePath,
        List<OcrTextBlock> blocks,
        System.Drawing.Rectangle screenRect)
    {
        string target = TranslateResultWindow.InferTargetLanguage(string.Concat(blocks.Select(b => b.Text)));
        var translated = await TranslateBlocksAsync(blocks, target, CancellationToken.None);

        string allText = string.Join("\n", translated.Select(b => b.TranslatedText));

        var window = new TranslateOverlayWindow(imagePath, translated, screenRect, allText);
        window.Show();
        window.Activate();
    }

    /* ── 构造函数 ── */

    private TranslateOverlayWindow(
        string imagePath,
        List<TranslatedBlock> translatedBlocks,
        System.Drawing.Rectangle screenRect,
        string allTranslatedText)
    {
        _allTranslatedText = allTranslatedText;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;

        Width = screenRect.Width;
        Height = screenRect.Height;
        Left = screenRect.X;
        Top = screenRect.Y;

        // 用 System.Drawing.Bitmap 加载图片用于像素采样
        using var drawingBmp = new DrawingBitmap(imagePath);

        // 构建 Canvas
        var canvas = new Canvas
        {
            Width = screenRect.Width,
            Height = screenRect.Height
        };

        // 1. 底层：原图
        var bitmapImage = new BitmapImage(new Uri(imagePath, UriKind.Absolute));
        var image = new System.Windows.Controls.Image
        {
            Source = bitmapImage,
            Width = screenRect.Width,
            Height = screenRect.Height
        };
        canvas.Children.Add(image);

        // 2. 上层：对每个翻译块
        foreach (var block in translatedBlocks)
        {
            // 2a. 从原图采样背景色
            Color bgColor = SampleBackgroundColor(drawingBmp, block);

            // 2b. 背景色块遮盖原文
            var bgRect = new Shapes.Rectangle
            {
                Width = block.Width,
                Height = block.Height,
                Fill = new SolidColorBrush(bgColor)
            };
            Canvas.SetLeft(bgRect, block.X);
            Canvas.SetTop(bgRect, block.Y);
            canvas.Children.Add(bgRect);

            // 2c. 译文
            var textBlock = new TextBlock
            {
                Text = block.TranslatedText,
                Foreground = new SolidColorBrush(DetectTextColor(bgColor)),
                FontSize = EstimateFontSize(block.Height, block.OriginalText),
                TextWrapping = TextWrapping.Wrap,
                Width = block.Width,
                Padding = new Thickness(2)
            };
            Canvas.SetLeft(textBlock, block.X);
            Canvas.SetTop(textBlock, block.Y);
            canvas.Children.Add(textBlock);
        }

        Content = canvas;

        /* ── 交互事件 ── */
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { Close(); return; }
            if (e.LeftButton == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
        };

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        /* ── 右键菜单 ── */
        var menu = new WpfContextMenu
        {
            Background = new SolidColorBrush(Color.FromArgb(236, 13, 19, 33)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(160, 255, 138, 0)),
            BorderThickness = new Thickness(1)
        };
        var copyAll = new WpfMenuItem { Header = Localization.T("複製全部譯文", Localization.CurrentLanguage), Foreground = Brushes.White };
        copyAll.Click += (_, _) => { try { Clipboard.SetText(_allTranslatedText); } catch { } };
        var closeMenu = new WpfMenuItem { Header = Localization.T("關閉", Localization.CurrentLanguage), Foreground = Brushes.White };
        closeMenu.Click += (_, _) => Close();
        menu.Items.Add(copyAll);
        menu.Items.Add(closeMenu);
        ContextMenu = menu;
    }

    /* ── 辅助方法 ── */

    /// <summary>从文本框边缘外采样像素，取出现最多的颜色作为背景色。</summary>
    private static Color SampleBackgroundColor(DrawingBitmap bmp, TranslatedBlock block)
    {
        int bx = Math.Max(0, (int)block.X);
        int by = Math.Max(0, (int)block.Y);
        int bw = Math.Min((int)block.Width, bmp.Width - bx);
        int bh = Math.Min((int)block.Height, bmp.Height - by);

        var colorCounts = new Dictionary<int, int>(); // ARGB hash → count
        int bestArgb = 0;
        int bestCount = 0;

        void Sample(int x, int y)
        {
            if (x < 0 || y < 0 || x >= bmp.Width || y >= bmp.Height) return;
            var pixel = bmp.GetPixel(x, y);
            int key = pixel.ToArgb();
            colorCounts.TryGetValue(key, out int c);
            c++;
            colorCounts[key] = c;
            if (c > bestCount) { bestCount = c; bestArgb = key; }
        }

        // 采样框外 3 像素的边框区域（上方 + 下方）
        const int margin = 3;
        for (int dx = 0; dx < bw; dx += Math.Max(1, bw / 10))
        {
            for (int m = 1; m <= margin; m++)
            {
                Sample(bx + dx, by - m);         // 上方
                Sample(bx + dx, by + bh - 1 + m); // 下方
            }
        }

        // 左方 + 右方
        for (int dy = 0; dy < bh; dy += Math.Max(1, bh / 10))
        {
            for (int m = 1; m <= margin; m++)
            {
                Sample(bx - m, by + dy);          // 左方
                Sample(bx + bw - 1 + m, by + dy); // 右方
            }
        }

        if (bestCount == 0)
            return Colors.White;

        var dc = DrawingColor.FromArgb(
            (byte)((bestArgb >> 24) & 0xFF),
            (byte)((bestArgb >> 16) & 0xFF),
            (byte)((bestArgb >> 8) & 0xFF),
            (byte)(bestArgb & 0xFF));
        return Color.FromArgb(dc.A, dc.R, dc.G, dc.B);
    }

    /// <summary>根据背景亮度选择黑色或白色文字。</summary>
    private static Color DetectTextColor(Color bg)
    {
        double luminance = 0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B;
        return luminance > 128 ? Colors.Black : Colors.White;
    }

    /// <summary>根据框高度估算字号。</summary>
    private static double EstimateFontSize(double boxHeight, string originalText)
    {
        // 框高度 * 0.7 作为基础字号，最小 8
        return Math.Max(8, boxHeight * 0.7);
    }

    /* ── 内联翻译（复用 TranslateResultWindow 的 DeepL + MyMemory 逻辑） ── */

    private static async Task<string> TranslateInline(string text, string targetLangCode)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        // 直接复用 TranslateResultWindow 的静态翻译方法（通过反射不可行，
        // 所以这里内联一份简化版的翻译调用）
        string sourceLang = DetectSourceLanguage(text, targetLangCode);
        if (string.Equals(sourceLang, targetLangCode, StringComparison.OrdinalIgnoreCase))
            return text;

        // 尝试 DeepL
        string? deepLKey = GetDeepLKey();
        if (!string.IsNullOrEmpty(deepLKey))
        {
            try
            {
                return await TranslateViaDeepL(text, sourceLang, targetLangCode, deepLKey);
            }
            catch { /* 回退 MyMemory */ }
        }

        return await TranslateViaMyMemory(text, sourceLang, targetLangCode);
    }

    private static string? _deepLKey;
    private static bool _deepLKeyLoaded;
    private static readonly object Lock = new();

    private static string? GetDeepLKey()
    {
        if (_deepLKeyLoaded) return _deepLKey;
        lock (Lock)
        {
            if (_deepLKeyLoaded) return _deepLKey;
            // 用户 Key
            try
            {
                string configPath = BeeXPaths.ConfigFile;
                if (File.Exists(configPath))
                {
                    using var stream = File.OpenRead(configPath);
                    using var doc = System.Text.Json.JsonDocument.Parse(stream);
                    if (doc.RootElement.TryGetProperty("deepl_api_key", out var el))
                    {
                        string key = el.GetString()?.Trim() ?? "";
                        if (key.Length > 0) { _deepLKey = key; _deepLKeyLoaded = true; return _deepLKey; }
                    }
                }
            }
            catch { }
            // 发行商 Key
            const string PublisherKey = "448cb35d-6320-4ec4-9451-979a7c560b51:fx";
            _deepLKey = PublisherKey;
            _deepLKeyLoaded = true;
            return _deepLKey;
        }
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    private static async Task<string> TranslateViaDeepL(string text, string srcLang, string tgtLang, string apiKey)
    {
        string src = MapDeepLLang(srcLang);
        string tgt = MapDeepLLang(tgtLang);
        var parameters = new Dictionary<string, string>
        {
            ["text"] = text,
            ["target_lang"] = tgt
        };
        if (!string.IsNullOrEmpty(src)) parameters["source_lang"] = src;

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api-free.deepl.com/v2/translate");
        req.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + apiKey);
        req.Content = new FormUrlEncodedContent(parameters);
        using var resp = await Http.SendAsync(req);
        if ((int)resp.StatusCode == 456) throw new InvalidOperationException("DeepL quota exceeded");
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream);
        var translations = doc.RootElement.GetProperty("translations");
        if (translations.GetArrayLength() > 0)
        {
            string value = translations[0].GetProperty("text").GetString()?.Trim() ?? "";
            if (value.Length > 0) return value;
        }
        throw new InvalidOperationException("DeepL returned empty");
    }

    private static async Task<string> TranslateViaMyMemory(string text, string srcLang, string tgtLang)
    {
        string query = Uri.EscapeDataString(text);
        string pair = Uri.EscapeDataString(srcLang + "|" + tgtLang);
        string url = $"https://api.mymemory.translated.net/get?q={query}&langpair={pair}";
        using var resp = await Http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream);
        if (doc.RootElement.TryGetProperty("responseData", out var rd) &&
            rd.TryGetProperty("translatedText", out var tt))
        {
            string value = System.Net.WebUtility.HtmlDecode(tt.GetString() ?? "").Trim();
            if (value.Length > 0) return value;
        }
        throw new InvalidOperationException("MyMemory returned empty");
    }

    private static string MapDeepLLang(string code) => code.ToUpperInvariant() switch
    {
        "ZH-CN" => "ZH",
        "ZH-TW" => "ZH-HANT",
        "EN" => "EN",
        "JA" => "JA",
        "KO" => "KO",
        _ => code.ToUpperInvariant()
    };

    private static string DetectSourceLanguage(string text, string targetLang)
    {
        if (text.Any(c => c is >= '\u3040' and <= '\u30ff')) return "ja";
        if (text.Any(c => c is >= '\uac00' and <= '\ud7af')) return "ko";
        if (text.Any(c => c is >= '\u3400' and <= '\u9fff')) return "zh-CN";
        return targetLang.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en";
    }
}
