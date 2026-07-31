using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeeXCleaner.Models;
using Microsoft.Win32;

namespace BeeXCleaner.Services;

/// <summary>
/// Enumerate and uninstall UWP and Microsoft Store apps using PowerShell.
/// </summary>
public sealed partial class AppxService
{
    /// <summary>
    /// Enumerates the UWP apps that can be uninstalled by the current user.
    /// Error: "Not empty" indicates an enumeration failure (PowerShell startup failure/timeout/parsing failure):
    /// Based on this, the caller distinguishes between “No UWP apps found on this device” and “Scan failed,” and prohibits silent discard.
    /// </summary>
    public async Task<(List<InstalledProgram> Apps, string? Error)> ScanAsync()
    {
        const string script =
            "$sel = Get-AppxPackage | Where-Object { -not $_.IsFramework } | " +
            "Select-Object Name,PackageFullName,PackageFamilyName,Publisher,Version,InstallLocation; " +
            "ConvertTo-Json -Depth 3 -InputObject @($sel)";

        var (ok, stdout, stderr) = await RunPowerShellAsync(script).ConfigureAwait(false);
        var list = new List<InstalledProgram>();
        if (!ok || string.IsNullOrWhiteSpace(stdout))
            return (list, string.IsNullOrWhiteSpace(stderr) ? "PowerShell 未返回任何输出。" : stderr.Trim());

        string? error = null;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return (list, "UWP 枚举输出格式异常。");

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var name = GetStr(el, "Name");
                var full = GetStr(el, "PackageFullName");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(full))
                    continue;

                list.Add(new InstalledProgram
                {
                    DisplayName = name!,
                    Publisher = ExtractCn(GetStr(el, "Publisher")),
                    DisplayVersion = GetStr(el, "Version"),
                    InstallLocation = GetStr(el, "InstallLocation"),
                    Source = ProgramSource.Uwp,
                    PackageFullName = full,
                    RegistryKeyName = GetStr(el, "PackageFamilyName") ?? full!
                });
            }
        }
        catch (Exception ex)
        {
            // If parsing fails, the parsed content is returned, but an error flag is included to indicate that the list may be incomplete.
            error = $"UWP 列表解析失败：{ex.Message}";
        }

        return (list
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList(), error);
    }

    /// <summary>Uninstall a specific UWP app.</summary>
    public async Task<UninstallResult> UninstallAsync(InstalledProgram program)
    {
        if (string.IsNullOrWhiteSpace(program.PackageFullName))
            return UninstallResult.Fail("缺少 PackageFullName。");
        // The package name will be embedded in a PowerShell string enclosed in single quotes: including a single quote allows one to escape the string boundaries and execute arbitrary commands,
        // Aligns with the layered defense provided by ResidualCleaner.HasUnsafeQuote; reject immediately (normal packet names do not contain quotation marks).
        if (program.PackageFullName!.Contains('\''))
            return UninstallResult.Fail("包名含非法字符（单引号），已拒绝执行。");

        var script = $"Remove-AppxPackage -Package '{program.PackageFullName}' -ErrorAction Stop";
        var (ok, _, stderr) = await RunPowerShellAsync(script).ConfigureAwait(false);
        return ok
            ? UninstallResult.Ok()
            : UninstallResult.Fail(string.IsNullOrWhiteSpace(stderr) ? "卸载失败。" : stderr.Trim());
    }

    private static async Task<(bool ok, string stdout, string stderr)> RunPowerShellAsync(string script)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return (false, string.Empty, "无法启动 PowerShell。");

            // Force PowerShell to output in UTF-8 to prevent garbled characters in Chinese publisher and application names
            await proc.StandardInput.WriteLineAsync(
                "$OutputEncoding=[Console]::OutputEncoding=[System.Text.Encoding]::UTF8").ConfigureAwait(false);
            await proc.StandardInput.WriteLineAsync(script).ConfigureAwait(false);
            proc.StandardInput.Close();

            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            // PowerShell May Remain Unresponsive Indefinitely During Service Deployment Errors: A 60-Second Timeout as a Fallback to Prevent the Refresh/Uninstall Process from Becoming Permanently Stuck and to Prevent Process Leaks
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (false, string.Empty, "PowerShell 执行超时(60s)，已强制结束。");
            }

            var stdout = await outTask.ConfigureAwait(false);
            var stderr = await errTask.ConfigureAwait(false);
            return (proc.ExitCode == 0, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }

    private static string? GetStr(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : (el.TryGetProperty(prop, out var v2) && v2.ValueKind != JsonValueKind.Null
                ? v2.ToString()
                : null);

    /// <summary>Extract the CN value from the certificate subject as the issuer's display name.</summary>
    private static string? ExtractCn(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return null;
        var m = CnRegex().Match(publisher);
        return m.Success ? m.Groups[1].Value.Trim() : publisher;
    }

    [GeneratedRegex(@"CN=([^,]+)")]
    private static partial Regex CnRegex();
}
