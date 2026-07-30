using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeeXCleaner.Models;
using Microsoft.Win32;

namespace BeeXCleaner.Services;

/// <summary>
/// 通过 PowerShell 枚举与卸载 UWP / Microsoft Store 应用。
/// </summary>
public sealed partial class AppxService
{
    /// <summary>
    /// 枚举当前用户的可卸载 UWP 应用。
    /// Error 非空表示枚举失败（PowerShell 启动失败/超时/解析失败）：
    /// 调用方据此区分“本机确无 UWP 应用”与“扫描失败”，禁止静默丢弃。
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
            // 解析失败返回已收集内容，但携带错误信号告知列表可能不完整
            error = $"UWP 列表解析失败：{ex.Message}";
        }

        return (list
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList(), error);
    }

    /// <summary>卸载指定 UWP 应用。</summary>
    public async Task<UninstallResult> UninstallAsync(InstalledProgram program)
    {
        if (string.IsNullOrWhiteSpace(program.PackageFullName))
            return UninstallResult.Fail("缺少 PackageFullName。");
        // 包名将拼入 PowerShell 单引号字符串：含单引号即可逃逸出字符串边界执行任意命令，
        // 与 ResidualCleaner.HasUnsafeQuote 的纵深防御对齐，直接拒绝（正常包名字符集不含引号）。
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

            // 强制 PowerShell 以 UTF-8 输出，避免中文发行商名/应用名出现乱码
            await proc.StandardInput.WriteLineAsync(
                "$OutputEncoding=[Console]::OutputEncoding=[System.Text.Encoding]::UTF8").ConfigureAwait(false);
            await proc.StandardInput.WriteLineAsync(script).ConfigureAwait(false);
            proc.StandardInput.Close();

            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            // 部署服务异常时 PowerShell 可能永久无输出：60s 超时兜底，避免刷新/卸载流程永久卡死与进程泄漏
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

    /// <summary>从证书主题中提取 CN 值作为发行商显示名。</summary>
    private static string? ExtractCn(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return null;
        var m = CnRegex().Match(publisher);
        return m.Success ? m.Groups[1].Value.Trim() : publisher;
    }

    [GeneratedRegex(@"CN=([^,]+)")]
    private static partial Regex CnRegex();
}
