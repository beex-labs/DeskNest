using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using Clipboard = System.Windows.Clipboard;
using MouseButtonState = System.Windows.Input.MouseButtonState;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfLabel = System.Windows.Controls.Label;
using WpfButton = System.Windows.Controls.Button;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfHAlign = System.Windows.HorizontalAlignment;

namespace BeeX.DeskNest;

/// <summary>
/// 悬浮翻译结果窗口：显示 OCR 原文及翻译译文，支持语言切换、复制、拖动。
/// </summary>
internal sealed class TranslateResultWindow : Window
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    private readonly string _ocrText;
    private readonly WpfTextBox _sourceBox;
    private readonly WpfTextBox _targetBox;
    private readonly WpfComboBox _langCombo;
    private readonly WpfLabel _statusLabel;
    private string _targetLangCode;

    sealed record LangOption(string DisplayName, string Code);

    private TranslateResultWindow(string ocrText, string targetLanguage)
    {
        _ocrText = ocrText;
        _targetLangCode = targetLanguage;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;

        /* ── 标题栏 ── */
        var titleText = new TextBlock
        {
            Text = "翻译结果",
            Foreground = Brushes.White,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };

        var closeBtn = new WpfButton
        {
            Content = "×",
            FontSize = 16,
            Width = 28,
            Height = 28,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 138, 0)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            HorizontalAlignment = WpfHAlign.Right
        };
        closeBtn.Click += (_, _) => Close();

        var titleBar = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(titleText, 0);
        titleBar.Children.Add(titleText);
        Grid.SetColumn(closeBtn, 1);
        titleBar.Children.Add(closeBtn);

        /* ── 原文区域 ── */
        var sourceLabel = new WpfLabel
        {
            Content = Localization.T("原文", Localization.CurrentLanguage),
            Foreground = new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xaa)),
            FontSize = 12,
            Padding = new Thickness(0, 0, 0, 4)
        };

        _sourceBox = new WpfTextBox
        {
            Text = ocrText,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            FontSize = 14,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(30, 25, 40, 60)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 6, 8, 6),
            MaxHeight = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var sourcePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        sourcePanel.Children.Add(sourceLabel);
        sourcePanel.Children.Add(_sourceBox);

        /* ── 译文区域 ── */
        var targetLabel = new WpfLabel
        {
            Content = Localization.T("譯文", Localization.CurrentLanguage),
            Foreground = new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xaa)),
            FontSize = 12,
            Padding = new Thickness(0, 0, 0, 4)
        };

        _targetBox = new WpfTextBox
        {
            Text = "翻译中...",
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            FontSize = 14,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(30, 25, 40, 60)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 6, 8, 6),
            MaxHeight = 200,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var targetPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        targetPanel.Children.Add(targetLabel);
        targetPanel.Children.Add(_targetBox);

        /* ── 工具栏 ── */
        var languages = GetTargetLanguages();
        _langCombo = new WpfComboBox { HorizontalAlignment = WpfHAlign.Left, MinWidth = 100, DisplayMemberPath = nameof(LangOption.DisplayName) };
        foreach (var lang in languages)
        {
            _langCombo.Items.Add(lang);
        }
        // 设置默认选中
        for (int i = 0; i < languages.Count; i++)
        {
            if (string.Equals(languages[i].Code, _targetLangCode, StringComparison.OrdinalIgnoreCase))
            {
                _langCombo.SelectedIndex = i;
                break;
            }
        }
        if (_langCombo.SelectedIndex < 0 && languages.Count > 0)
        {
            _langCombo.SelectedIndex = 0;
            _targetLangCode = languages[0].Code;
        }
        _langCombo.SelectionChanged += OnLanguageChanged;

        _statusLabel = new WpfLabel
        {
            Content = "",
            Foreground = new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xaa)),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };

        var copyBtn = new WpfButton
        {
            Content = Localization.T("複製譯文", Localization.CurrentLanguage),
            FontSize = 12,
            Padding = new Thickness(10, 4, 10, 4),
            Background = new SolidColorBrush(Color.FromArgb(60, 255, 138, 0)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 138, 0)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            HorizontalAlignment = WpfHAlign.Left,
            Margin = new Thickness(10, 0, 0, 0)
        };
        copyBtn.Click += (_, _) =>
        {
            try { Clipboard.SetText(_targetBox.Text); } catch { }
        };

        var toolbar = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        toolbar.Children.Add(_langCombo);
        toolbar.Children.Add(copyBtn);
        toolbar.Children.Add(_statusLabel);

        /* ── 主布局 ── */
        var root = new StackPanel { Width = 480 };
        root.Children.Add(titleBar);
        root.Children.Add(sourcePanel);
        root.Children.Add(targetPanel);
        root.Children.Add(toolbar);

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(240, 13, 19, 33)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(220, 255, 138, 0)),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12),
            Child = root,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(255, 138, 0),
                BlurRadius = 18,
                ShadowDepth = 0,
                Opacity = 0.5
            }
        };
        Content = card;

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
        var copyMenu = new WpfMenuItem { Header = Localization.T("複製譯文", Localization.CurrentLanguage), Foreground = Brushes.White };
        copyMenu.Click += (_, _) => { try { Clipboard.SetText(_targetBox.Text); } catch { } };
        var closeMenu = new WpfMenuItem { Header = Localization.T("關閉", Localization.CurrentLanguage), Foreground = Brushes.White };
        closeMenu.Click += (_, _) => Close();
        menu.Items.Add(copyMenu);
        menu.Items.Add(closeMenu);
        ContextMenu = menu;
    }

    /// <summary>创建窗口并开始翻译。</summary>
    public static async Task ShowAsync(string ocrText, string targetLanguage)
    {
        var window = new TranslateResultWindow(ocrText, targetLanguage);
        window.Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            window.Left = area.Left + Math.Max(0, (area.Width - window.ActualWidth) / 2);
            window.Top = area.Top + Math.Max(0, (area.Height - window.ActualHeight) / 3);
        };
        window.Show();
        window.Activate();
        await window.DoTranslateAsync();
    }

    private async Task DoTranslateAsync()
    {
        try
        {
            _targetBox.Text = "翻译中...";
            _statusLabel.Content = "";
            string result = await TranslateAsync(_ocrText, _targetLangCode);
            _targetBox.Text = string.IsNullOrWhiteSpace(result) ? "（无翻译结果）" : result;
        }
        catch (Exception ex)
        {
            _targetBox.Text = "翻译失败";
            _statusLabel.Content = ex.Message;
        }
    }

    private async void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_langCombo.SelectedItem is LangOption selected)
        {
            _targetLangCode = selected.Code;
            await DoTranslateAsync();
        }
    }

    /// <summary>根据文本内容推断默认目标语言：中文→英文，否则→中文。</summary>
    internal static string InferTargetLanguage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "en";

        bool hasCjk = text.Any(c => c is >= '\u3400' and <= '\u9fff');
        return hasCjk ? "en" : "zh-CN";
    }

    /* ── 内联翻译逻辑（与 BeeX.OCR.TranslationService 同算法，避免跨程序集依赖） ── */

    private static string? _cachedDeepLKey;
    private static bool _deepLKeyLoaded;
    private static readonly object DeepLLock = new();

    /// <summary>
    /// 获取 DeepL API Key，优先级：用户 Key > 发行商 Key > null。
    /// </summary>
    private static string? GetDeepLApiKey()
    {
        if (_deepLKeyLoaded) return _cachedDeepLKey;
        lock (DeepLLock)
        {
            if (_deepLKeyLoaded) return _cachedDeepLKey;

            // 1. 优先：用户设置的 Key（从 config.json 读取）
            string? userKey = UserConfigHelper.ReadDeepLApiKey();
            if (!string.IsNullOrWhiteSpace(userKey))
            {
                _cachedDeepLKey = userKey;
                _deepLKeyLoaded = true;
                return _cachedDeepLKey;
            }

            // 2. 发行商内置 Key（硬编码）
            const string PublisherKey = "448cb35d-6320-4ec4-9451-979a7c560b51:fx";
            if (!string.IsNullOrWhiteSpace(PublisherKey))
            {
                _cachedDeepLKey = PublisherKey;
                _deepLKeyLoaded = true;
                return _cachedDeepLKey;
            }

            // 3. 无 Key
            _cachedDeepLKey = null;
            _deepLKeyLoaded = true;
            return null;
        }
    }

    /// <summary>
    /// 清除 DeepL Key 缓存，使下次调用时重新读取。
    /// </summary>
    public static void ClearDeepLKeyCache()
    {
        lock (DeepLLock)
        {
            _cachedDeepLKey = null;
            _deepLKeyLoaded = false;
        }
    }

    private static List<LangOption> GetTargetLanguages() =>
    [
        new("中文", "zh-CN"),
        new("英文", "en"),
        new("日文", "ja"),
        new("韩文", "ko"),
        new("繁体中文", "zh-TW")
    ];

    private static async Task<string> TranslateAsync(string text, string targetLangCode)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string sourceLangCode = DetectSourceLanguage(text, targetLangCode);
        if (string.Equals(sourceLangCode, targetLangCode, StringComparison.OrdinalIgnoreCase))
            return text;

        // 1. 尝试 DeepL
        string? deepLKey = GetDeepLApiKey();
        if (!string.IsNullOrEmpty(deepLKey))
        {
            try
            {
                return await TranslateViaDeepL(text, sourceLangCode, targetLangCode, deepLKey);
            }
            catch
            {
                // DeepL 失败，回退 MyMemory
            }
        }

        // 2. 兜底 MyMemory
        return await TranslateViaMyMemory(text, sourceLangCode, targetLangCode);
    }

    private static async Task<string> TranslateViaDeepL(string text, string sourceLangCode, string targetLangCode, string apiKey)
    {
        string sourceLang = MapToDeepLLangCode(sourceLangCode);
        string targetLang = MapToDeepLLangCode(targetLangCode);

        var parameters = new Dictionary<string, string>
        {
            ["text"] = text,
            ["target_lang"] = targetLang
        };
        if (!string.IsNullOrEmpty(sourceLang))
            parameters["source_lang"] = sourceLang;

        // DeepL 2025-11 起弃用 form body 里的 auth_key，必须用 Authorization 头
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api-free.deepl.com/v2/translate");
        request.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + apiKey);
        request.Content = new FormUrlEncodedContent(parameters);
        using HttpResponseMessage response = await Http.SendAsync(request);

        if ((int)response.StatusCode == 456)
            throw new InvalidOperationException("DeepL 翻译额度已耗尽。");
        response.EnsureSuccessStatusCode();

        using Stream stream = await response.Content.ReadAsStreamAsync();
        using JsonDocument document = await JsonDocument.ParseAsync(stream);

        JsonElement translations = document.RootElement.GetProperty("translations");
        if (translations.GetArrayLength() > 0)
        {
            string value = translations[0].GetProperty("text").GetString()?.Trim() ?? string.Empty;
            if (value.Length > 0) return value;
        }
        throw new InvalidOperationException("DeepL 没有返回有效译文。");
    }

    private static async Task<string> TranslateViaMyMemory(string text, string sourceLangCode, string targetLangCode)
    {
        string query = Uri.EscapeDataString(text);
        string langPair = Uri.EscapeDataString(sourceLangCode + "|" + targetLangCode);
        string url = "https://api.mymemory.translated.net/get?q=" + query + "&langpair=" + langPair;

        using HttpResponseMessage response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();

        using Stream stream = await response.Content.ReadAsStreamAsync();
        using JsonDocument doc = await JsonDocument.ParseAsync(stream);

        if (doc.RootElement.TryGetProperty("responseData", out JsonElement rd) &&
            rd.TryGetProperty("translatedText", out JsonElement tt))
        {
            string value = WebUtility.HtmlDecode(tt.GetString() ?? string.Empty).Trim();
            if (value.Contains("QUERY LENGTH LIMIT EXCEEDED", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("翻译内容超过在线接口限制。");
            return value;
        }
        throw new InvalidOperationException("翻译服务没有返回有效译文。");
    }

    private static string MapToDeepLLangCode(string langCode)
    {
        return langCode.ToUpperInvariant() switch
        {
            "ZH-CN" => "ZH",
            "ZH-TW" => "ZH-HANT",
            "EN" => "EN",
            "JA" => "JA",
            "KO" => "KO",
            _ => langCode.ToUpperInvariant()
        };
    }

    private static string DetectSourceLanguage(string text, string targetLangCode)
    {
        if (text.Any(c => c is >= '\u3040' and <= '\u30ff')) return "ja";
        if (text.Any(c => c is >= '\uac00' and <= '\ud7af')) return "ko";
        if (text.Any(c => c is >= '\u3400' and <= '\u9fff')) return "zh-CN";
        return targetLangCode.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en";
    }
}
