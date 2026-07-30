using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using BeexWrite.Services;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace BeexWrite.ViewModels;

/// <summary>Copy-as-*, import (Pandoc) and export commands.</summary>
public partial class MainViewModel
{
    private readonly PandocService _pandoc = new();

    /// <summary>MainWindow sets this to its WebView2 PrintToPdfAsync implementation.</summary>
    public Func<string, string, Task>? PdfExportHandler { get; set; }

    /// <summary>URL → saved file name cache used within a single DownloadImages run.</summary>
    private readonly Dictionary<string, string> _downloadedUrls = new();

    /// <summary>MainWindow sets this to its DevTools full-page screenshot implementation.</summary>
    public Func<string, string, Task>? LongImageExportHandler { get; set; }

    [RelayCommand]
    private async Task ExportLongImageAsync()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "PNG (*.png)|*.png",
            Title = Localization.Strings.Instance.DlgExportLongImage,
            FileName = (FilePath is null ? "Untitled" : Path.GetFileNameWithoutExtension(FilePath)) + ".png",
            DefaultExt = ".png"
        };
        if (dlg.ShowDialog() != true) return;
        if (LongImageExportHandler is null)
        {
            MessageBox.Show(Localization.Strings.Instance.MsgLongImageHandlerMissing, "BeexWrite");
            return;
        }
        var content = await _bridge.RequestContentAsync();
        var title = FilePath is null ? "Untitled" : Path.GetFileNameWithoutExtension(FilePath);
        var tmpHtml = Path.Combine(Path.GetTempPath(), $"beex_img_{Guid.NewGuid():N}.html");
        await _export.ExportHtmlAsync(content, tmpHtml, title, _theme.EffectiveTheme, includeStyles: true);
        try
        {
            await LongImageExportHandler(tmpHtml, dlg.FileName);
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show(Localization.Strings.Instance.MsgLongImageHandlerMissing, "BeexWrite");
        }
        finally
        {
            try { File.Delete(tmpHtml); } catch { }
        }
    }

    [RelayCommand]
    private async Task CopyAsHtmlAsync()
    {
        var content = await _bridge.RequestContentAsync();
        var html = _export.RenderHtmlBody(content);
        SetClipboardHtml(html);
    }

    [RelayCommand]
    private async Task CopyAsMarkdownAsync()
    {
        var content = await _bridge.RequestContentAsync();
        Clipboard.SetText(content);
    }

    [RelayCommand]
    private async Task ExportPdfAsync()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "PDF (*.pdf)|*.pdf",
            Title = Localization.Strings.Instance.DlgExportPdf,
            FileName = (FilePath is null ? "Untitled" : Path.GetFileNameWithoutExtension(FilePath)) + ".pdf",
            DefaultExt = ".pdf"
        };
        if (dlg.ShowDialog() != true) return;
        var content = await _bridge.RequestContentAsync();

        // Prefer Pandoc for PDF (generates bookmarks/outline from headings).
        if (_pandoc.IsAvailable)
        {
            var extraArgs = $"--pdf-engine=xelatex -V papersize={_settings.Settings.ExportPaperSize} -V geometry:margin={_settings.Settings.ExportMargin}";
            if (!_settings.Settings.ExportBookmarks) extraArgs += " --toc=false";
            var ok = await _pandoc.ExportAsync(content, dlg.FileName, "pdf", extraArgs);
            if (ok) return;
        }

        // Fallback: WebView2 PrintToPdf (no bookmarks but always available).
        var title = FilePath is null ? "Untitled" : Path.GetFileNameWithoutExtension(FilePath);
        var tmpHtml = Path.Combine(Path.GetTempPath(), $"beex_pdf_{Guid.NewGuid():N}.html");
        await _export.ExportHtmlAsync(content, tmpHtml, title, _theme.EffectiveTheme, includeStyles: true);
        try
        {
            if (PdfExportHandler != null)
                await PdfExportHandler(tmpHtml, dlg.FileName);
            else
                MessageBox.Show(Localization.Strings.Instance.MsgPdfHandlerMissing, "BeexWrite");
        }
        finally
        {
            try { File.Delete(tmpHtml); } catch { }
        }
    }

    [RelayCommand]
    private async Task ExportViaPandocAsync()
    {
        if (!_pandoc.IsAvailable)
        {
            MessageBox.Show(
                Localization.Strings.Instance.MsgPandocRequired,
                "BeexWrite", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new SaveFileDialog
        {
            Filter = "Word (*.docx)|*.docx|RTF (*.rtf)|*.rtf|OpenDocument (*.odt)|*.odt|EPUB (*.epub)|*.epub|LaTeX (*.tex)|*.tex|HTML (*.html)|*.html",
            Title = Localization.Strings.Instance.DlgExportPandoc
        };
        if (dlg.ShowDialog() != true) return;
        var content = await _bridge.RequestContentAsync();
        var ok = await _pandoc.ExportAsync(content, dlg.FileName);
        if (!ok) MessageBox.Show(Localization.Strings.Instance.MsgPandocExportFailed, "BeexWrite");
    }

    [RelayCommand]
    private async Task ImportViaPandocAsync()
    {
        if (!_pandoc.IsAvailable)
        {
            MessageBox.Show(
                Localization.Strings.Instance.MsgPandocRequired,
                "BeexWrite", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new OpenFileDialog
        {
            Filter = "Supported (*.docx;*.rtf;*.odt;*.html;*.htm;*.epub)|*.docx;*.rtf;*.odt;*.html;*.htm;*.epub|All files|*.*",
            Title = Localization.Strings.Instance.DlgImportPandoc
        };
        if (dlg.ShowDialog() != true) return;
        var md = await _pandoc.ImportToMarkdownAsync(dlg.FileName);
        if (md is null)
        {
            MessageBox.Show(Localization.Strings.Instance.MsgPandocImportFailed, "BeexWrite");
            return;
        }
        _bridge.SetContent(md);
        FilePath = null;
        IsDirty = true;
    }

    [RelayCommand]
    private void ImportTextBundle()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "TextPack (*.textpack)|*.textpack|All files|*.*",
            Title = Localization.Strings.Instance.DlgImportTextBundle
        };
        if (dlg.ShowDialog() != true) return;
        var result = Services.TextBundleService.Import(dlg.FileName);
        if (result is null)
        {
            MessageBox.Show(Localization.Strings.Instance.MsgTextBundleFailed, "BeexWrite");
            return;
        }
        var (md, assetsDir) = result.Value;
        if (assetsDir is not null && FilePath is not null)
        {
            var destAssets = Path.Combine(Path.GetDirectoryName(FilePath)!, "assets");
            try
            {
                Directory.CreateDirectory(destAssets);
                foreach (var f in Directory.GetFiles(assetsDir))
                    File.Copy(f, Path.Combine(destAssets, Path.GetFileName(f)), overwrite: true);
            }
            catch { }
        }
        _bridge.SetContent(md);
        FilePath = null;
        IsDirty = true;
    }

    [RelayCommand]
    private async Task DownloadImagesAsync()
    {
        var content = await _bridge.RequestContentAsync();
        var dir = FilePath is not null ? Path.GetDirectoryName(FilePath) : _settings.SettingsDirectory;
        if (string.IsNullOrEmpty(dir)) return;
        var assetsDir = Path.Combine(dir, "assets");
        Directory.CreateDirectory(assetsDir);

        var re = new System.Text.RegularExpressions.Regex(@"!\[[^\]]*\]\((https?://[^)]+)\)");
        var matches = re.Matches(content);
        if (matches.Count == 0) { MessageBox.Show(Localization.Strings.Instance.MsgNoRemoteImages, "BeexWrite"); return; }

        int downloaded = 0;
        using var http = new System.Net.Http.HttpClient();
        var updated = content;
        // Replace positionally from LAST match to first so earlier indices stay valid,
        // and only within the matched image syntax (avoids clobbering the same URL in
        // code blocks / plain links / prefix-substring collisions).
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            var m = matches[i];
            var url = m.Groups[1].Value;
            try
            {
                var fileName = Path.GetFileName(new Uri(url).LocalPath);
                if (string.IsNullOrWhiteSpace(fileName)) fileName = $"image-{i}.png";
                var localPath = Path.Combine(assetsDir, fileName);
                // Different URLs resolving to the same file name must not overwrite each other.
                if (File.Exists(localPath) && !_downloadedUrls.TryGetValue(url, out _))
                {
                    fileName = Path.GetFileNameWithoutExtension(fileName) + $"-{i}" + Path.GetExtension(fileName);
                    localPath = Path.Combine(assetsDir, fileName);
                }
                if (_downloadedUrls.TryGetValue(url, out var cached))
                {
                    fileName = cached; // same URL referenced multiple times — reuse download
                }
                else
                {
                    var data = await http.GetByteArrayAsync(url);
                    File.WriteAllBytes(localPath, data);
                    _downloadedUrls[url] = fileName;
                }
                var g = m.Groups[1];
                updated = updated.Remove(g.Index, g.Length).Insert(g.Index, "assets/" + fileName);
                downloaded++;
            }
            catch { /* skip failed downloads */ }
        }
        _downloadedUrls.Clear();
        if (downloaded > 0)
        {
            _bridge.SetContent(updated, FilePath);
            IsDirty = true;
        }
        MessageBox.Show(string.Format(Localization.Strings.Instance.MsgImagesDownloaded, downloaded, matches.Count), "BeexWrite");
    }

    // ---- clipboard CF_HTML --------------------------------------------------

    private static void SetClipboardHtml(string htmlBody)
    {
        var full = $"<html><body><!--StartFragment-->{htmlBody}<!--EndFragment--></body></html>";
        var encoding = Encoding.UTF8;
        var header = "Version:0.9\r\nStartHTML:<<<<<<<<1\r\nEndHTML:<<<<<<<<2\r\nStartFragment:<<<<<<<<3\r\nEndFragment:<<<<<<<<4\r\n";
        var startHtml = encoding.GetByteCount(header);
        var startFragment = startHtml + encoding.GetByteCount(full[..full.IndexOf("<!--StartFragment-->", StringComparison.Ordinal)]) + "<!--StartFragment-->".Length;
        var endFragment = startHtml + encoding.GetByteCount(full[..full.IndexOf("<!--EndFragment-->", StringComparison.Ordinal)]);
        var endHtml = startHtml + encoding.GetByteCount(full);

        var result = header
            .Replace("<<<<<<<<1", startHtml.ToString("D8"))
            .Replace("<<<<<<<<2", endHtml.ToString("D8"))
            .Replace("<<<<<<<<3", startFragment.ToString("D8"))
            .Replace("<<<<<<<<4", endFragment.ToString("D8"));

        var data = new DataObject();
        data.SetData(DataFormats.Html, result + full);
        data.SetData(DataFormats.UnicodeText, htmlBody);
        Clipboard.SetDataObject(data, true);
    }
}
