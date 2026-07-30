using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using BeeXCleaner.Infrastructure;
using BeeXCleaner.Models;
using BeeXCleaner.Services;
using Microsoft.Win32;

namespace BeeXCleaner.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly ProgramScanner _scanner = new();
    private readonly UninstallService _uninstaller = new();
    private readonly AppxService _appx = new();
    private readonly ResidualScanner _residual = new();
    private readonly IUiService _ui;

    private List<InstalledProgram> _all = new();

    public MainViewModel(IUiService ui)
    {
        _ui = ui;

        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        UninstallCommand = new AsyncRelayCommand(_ => UninstallSelectedAsync(), _ => SelectedProgram is not null);
        ForceRemoveCommand = new AsyncRelayCommand(_ => ForceRemoveSelectedAsync(), _ => HasActionTargets);
        BatchUninstallCommand = new AsyncRelayCommand(_ => BatchUninstallAsync(), _ => CheckedCount > 0);
        CleanResidualsCommand = new RelayCommand(_ => CleanResidualsForSelected(), _ => HasActionTargets);
        DetailsCommand = new RelayCommand(_ => { if (SelectedProgram is not null) _ui.ShowDetails(SelectedProgram); },
            _ => SelectedProgram is not null);
        OpenFolderCommand = new RelayCommand(_ => OpenInstallFolder(), _ => SelectedProgram is not null);
        OpenWebsiteCommand = new RelayCommand(_ => OpenWebsite(),
            _ => !string.IsNullOrWhiteSpace(SelectedProgram?.UrlInfoAbout));

        SelectAllCommand = new RelayCommand(_ => SetAllChecked(true));
        SelectNoneCommand = new RelayCommand(_ => SetAllChecked(false));
        InvertSelectionCommand = new RelayCommand(_ => InvertChecked());
        ExitCommand = new RelayCommand(_ => (_ui as System.Windows.Window)?.Close());
        ScanOrphansCommand = new RelayCommand(_ => _ui.ScanOrphans());
        WipeCommand = new RelayCommand(_ => _ui.ShowWipe());
        QuickDeleteCommand = new RelayCommand(_ => _ui.ShowQuickDelete());
        BackupRestoreCommand = new RelayCommand(_ => _ui.ShowBackupRestore());
        CleanupHistoryCommand = new RelayCommand(_ => _ui.ShowCleanupHistory());
    }

    // ------------- 集合与状态 -------------
    public ObservableCollection<InstalledProgram> Programs { get; } = new();

    private InstalledProgram? _selectedProgram;
    public InstalledProgram? SelectedProgram
    {
        get => _selectedProgram;
        set => SetProperty(ref _selectedProgram, value);
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) ApplyFilter(); }
    }

    private bool _showUwp = true;
    public bool ShowUwp
    {
        get => _showUwp;
        set { if (SetProperty(ref _showUwp, value)) _ = RefreshAsync(); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    private string _busyText = "正在加载…";
    public string BusyText
    {
        get => _busyText;
        set => SetProperty(ref _busyText, value);
    }

    private string _statusText = "就绪";
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public int CheckedCount => _all.Count(p => p.IsSelected);

    /// <summary>是否存在可操作对象（勾选项优先，否则看当前高亮行）。</summary>
    public bool HasActionTargets => CheckedCount > 0 || SelectedProgram is not null;

    /// <summary>
    /// 右键/操作的目标集：只要有勾选项就作用于“全部勾选项”（批量），
    /// 否则回退到当前高亮的单个程序。
    /// </summary>
    private List<InstalledProgram> GetActionTargets()
    {
        var checkedItems = _all.Where(p => p.IsSelected).ToList();
        if (checkedItems.Count > 0) return checkedItems;
        return SelectedProgram is not null
            ? new List<InstalledProgram> { SelectedProgram }
            : new List<InstalledProgram>();
    }

    public string CountText
    {
        get
        {
            var totalSize = Programs.Sum(p => p.SizeBytes);
            return $"共 {Programs.Count} 个程序 · {InstalledProgram.FormatSize(totalSize)}";
        }
    }

    public string SelectedCountText => CheckedCount > 0 ? $"已勾选 {CheckedCount} 个" : string.Empty;

    // ------------- 命令 -------------
    public ICommand RefreshCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand ForceRemoveCommand { get; }
    public ICommand BatchUninstallCommand { get; }
    public ICommand CleanResidualsCommand { get; }
    public ICommand DetailsCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenWebsiteCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand SelectNoneCommand { get; }
    public ICommand InvertSelectionCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand ScanOrphansCommand { get; }
    public ICommand WipeCommand { get; }
    public ICommand QuickDeleteCommand { get; }
    public ICommand BackupRestoreCommand { get; }
    public ICommand CleanupHistoryCommand { get; }

    // ------------- 加载 -------------
    private bool _refreshing;
    private bool _refreshPending;

    public async Task RefreshAsync()
    {
        // 防止 ShowUwp 开关等途径与命令触发的刷新交错：交错会覆盖 _all 并泄漏事件订阅。
        // 被拦下的请求记“待重扫”而非静默丢弃，否则开关状态与列表内容会漂移。
        if (_refreshing) { _refreshPending = true; return; }
        _refreshing = true;
        IsBusy = true;
        BusyText = "正在扫描已安装的程序…";
        var eventsDetached = false;
        try
        {
            DetachSelectionEvents();
            eventsDetached = true;

            var win32 = await Task.Run(() => _scanner.Scan());
            var combined = new List<InstalledProgram>(win32);

            string? uwpError = null;
            if (ShowUwp)
            {
                BusyText = "正在扫描 UWP 应用…";
                var (uwp, err) = await _appx.ScanAsync();
                combined.AddRange(uwp);
                uwpError = err;
            }

            _all = combined
                .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            AttachSelectionEvents();
            eventsDetached = false;
            ApplyFilter();
            // UWP 枚举失败不能静默呈现为“扫描完成”：否则用户会误判 UWP 应用已卸载
            StatusText = uwpError is null
                ? $"扫描完成，找到 {_all.Count} 个程序"
                : $"扫描完成，找到 {_all.Count} 个程序（⚠ UWP 应用枚举失败，列表可能不含 UWP 项：{uwpError}）";

            // 后台渐进实测缺失大小（注册表未提供 EstimatedSize 的程序）
            _ = ComputeMissingSizesAsync(_all);
        }
        catch (Exception ex)
        {
            // 扫描失败时 _all 仍是旧列表且继续显示：必须恢复事件订阅，
            // 否则勾选计数与批量命令可用性从此静默失效
            if (eventsDetached) AttachSelectionEvents();
            _ui.ShowError($"扫描失败: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _refreshing = false;
        }

        // 刷新期间又有新请求（如切换 ShowUwp）：补一次重扫，使列表与最新口径一致
        if (_refreshPending)
        {
            _refreshPending = false;
            await RefreshAsync();
        }
    }

    private void ApplyFilter()
    {
        var q = _searchText?.Trim() ?? string.Empty;
        IEnumerable<InstalledProgram> view = _all;

        if (q.Length > 0)
            view = _all.Where(p =>
                p.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (p.Publisher?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));

        Programs.Clear();
        foreach (var p in view)
            Programs.Add(p);

        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(AllCheckedState));
    }

    // ------------- 大小/日期实测（需求5，含 UWP）-------------
    private async Task ComputeMissingSizesAsync(List<InstalledProgram> snapshot)
    {
        var targets = new List<(InstalledProgram prog, string folder, bool needSize, bool needDate)>();
        foreach (var p in snapshot)
        {
            var needSize = p.SizeBytes <= 0;
            var needDate = p.InstallDate is null;
            if (!needSize && !needDate) continue;
            var folder = GuessInstallFolder(p);
            if (folder is not null) targets.Add((p, folder, needSize, needDate));
        }
        if (targets.Count == 0) return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        await Task.Run(() =>
        {
            foreach (var (prog, folder, needSize, needDate) in targets)
            {
                var size = needSize ? FileSystemUtil.DirectorySize(folder) : 0;
                var date = needDate ? SafeCreationTime(folder) : null;
                if (size <= 0 && date is null) continue;

                void Apply()
                {
                    // 先置实测标记再赋值大小：SizeBytes setter 会刷新 SizeDisplay，顺序颠倒会漏掉“（实测）”后缀
                    if (needSize && size > 0) { prog.SizeMeasured = true; prog.SizeBytes = size; }
                    if (needDate && date is not null) prog.InstallDate = date;
                }

                if (dispatcher is not null) dispatcher.BeginInvoke(Apply);
                else Apply();
            }
        });

        OnPropertyChanged(nameof(CountText));
    }

    private static DateTime? SafeCreationTime(string folder)
    {
        try
        {
            var t = Directory.GetCreationTime(folder);
            return t.Year > 1980 ? t : null;
        }
        catch { return null; }
    }

    /// <summary>推断可测量的安装目录：优先 InstallLocation，其次 DisplayIcon 所在目录。</summary>
    private static string? GuessInstallFolder(InstalledProgram p)
    {
        if (!string.IsNullOrWhiteSpace(p.InstallLocation) && Directory.Exists(p.InstallLocation))
            return p.InstallLocation;

        if (!string.IsNullOrWhiteSpace(p.DisplayIcon))
        {
            var iconPath = p.DisplayIcon!.Split(',')[0].Trim().Trim('"');
            try
            {
                var dir = Path.GetDirectoryName(iconPath);
                if (!string.IsNullOrWhiteSpace(dir)
                    && Directory.Exists(dir)
                    && !IsUnderWindows(dir))
                    return dir;
            }
            catch { /* 忽略无效路径 */ }
        }
        return null;
    }

    private static bool IsUnderWindows(string dir)
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(win)) return false;
        try
        {
            return Path.GetFullPath(dir)
                .StartsWith(win.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ------------- 单个卸载 -------------
    private async Task UninstallSelectedAsync()
    {
        var p = SelectedProgram;
        if (p is null) return;

        if (!_ui.Confirm($"确定要卸载 “{p.DisplayName}” 吗？\n\n卸载完成后将自动扫描并清理残留。", "卸载程序"))
            return;

        await UninstallOneAsync(p, silent: false, autoResidual: true);
        await RefreshAsync();
    }

    private async Task<bool> UninstallOneAsync(InstalledProgram p, bool silent, bool autoResidual)
    {
        IsBusy = true;
        BusyText = $"正在卸载 {p.DisplayName}…";
        try
        {
            var result = p.Source == ProgramSource.Uwp
                ? await _appx.UninstallAsync(p)
                : await _uninstaller.UninstallAsync(p, silent);

            if (!result.Success)
            {
                _ui.ShowError($"卸载 “{p.DisplayName}” 未成功:\n\n{result.Message}");
                return false;
            }

            StatusText = $"已卸载: {p.DisplayName}";

            if (autoResidual)
            {
                IsBusy = false;
                _ui.CleanResiduals(new[] { p });
            }
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ------------- 强制删除（扫描全部残留 → 用户确认后才执行一切删除/卸载，支持多选）-------------
    private async Task ForceRemoveSelectedAsync()
    {
        var targets = GetActionTargets();
        if (targets.Count == 0) return;

        var uwpTargets = targets.Where(p => p.Source == ProgramSource.Uwp).ToList();
        var win32 = targets.Where(p => p.Source != ProgramSource.Uwp).ToList();

        IsBusy = true;
        try
        {
            // 1) 先扫描全部残留（无任何副作用；UWP 卸载延后到用户确认之后，
            //    避免确认框弹出前就发生不可撤销的卸载）
            BusyText = "正在扫描全部文件与注册表残留…";
            var items = await Task.Run(() =>
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var all = new List<ResidualItem>();
                foreach (var p in targets)
                    foreach (var it in _residual.Scan(p))
                        if (seen.Add($"{it.Type}:{it.Path}:{it.RegistryValueName}"))
                            all.Add(it);
                return all;
            });
            IsBusy = false;

            // 只删除默认勾选（高置信）项：低置信/需确认项遵守扫描器 CanAutoSelect 安全契约。
            var selectedItems = items.Where(i => i.IsSelected).ToList();
            var skippedCount = items.Count - selectedItems.Count;

            // 未扫到任何可自动删除的残留（含“全部为低置信项”的情形）：
            // 不能弹“将删除全部残留”的失真文案，而是明确告知只移除卸载登记项。
            if (selectedItems.Count == 0)
            {
                var ask = (items.Count == 0
                              ? $"未发现“{DescribeTargets(targets)}”的可删除文件/注册表残留。\n\n"
                                + "（可能该程序未安装在本机本地磁盘，或已被清理）\n\n"
                              : $"扫描到“{DescribeTargets(targets)}”的 {items.Count} 项残留，但全部为低置信/需确认项，"
                                + "不会自动删除任何残留（可在“清理残留”中逐项确认）。\n\n")
                          + (uwpTargets.Count > 0 ? $"将卸载 {uwpTargets.Count} 个 UWP 应用（不可撤销）。\n" : "")
                          + (win32.Count > 0
                              ? "是否仅移除它们的卸载登记项（从列表清除）？删除注册表登记项前会自动导出 .reg 备份。"
                              : "确定继续吗？");
                if (_ui.ConfirmDanger(ask, "强制删除"))
                {
                    // 与主删除路径一致：建立清理会话、删注册表登记项前自动备份、最后展示结果窗口。
                    IsBusy = true;
                    BusyText = "正在移除卸载登记项…";
                    var regSession = new CleanupSession(CleanupOperation.ForceRemove, targets.Select(p => p.DisplayName));
                    var regOnlyResult = new ResidualCleanResult();
                    await UninstallUwpTargetsAsync(uwpTargets, regSession, regOnlyResult);
                    await Task.Run(() =>
                    {
                        foreach (var p in win32)
                            RemoveUninstallEntry(p, regSession, regOnlyResult);
                        regOnlyResult.BackupPath = regSession.HasBackups ? regSession.BackupFolder : null;
                    });
                    IsBusy = false;

                    StatusText = $"强制删除完成: 移除登记项 {regOnlyResult.Deleted} 项，失败 {regOnlyResult.Failed} 项";
                    regOnlyResult.LogPath = regSession.Flush($"仅移除卸载登记项：成功 {regOnlyResult.Deleted}，失败 {regOnlyResult.Failed}");
                    _ui.ShowResult(regOnlyResult, "强制删除结果");
                }
                await RefreshAsync();
                return;
            }

            // 3) 列出清单，最终确认一次（唯一安全闸门；不可恢复）。
            var folders = selectedItems.Count(x => x.Type == ResidualType.Folder);
            var regs = selectedItems.Count(x => x.Type == ResidualType.RegistryKey);
            var files = selectedItems.Count(x => x.Type is ResidualType.File or ResidualType.Shortcut);
            var size = selectedItems.Where(x => x.Type == ResidualType.Folder).Sum(x => x.SizeBytes);
            var sample = string.Join("\n", selectedItems.Take(12).Select(x => $"· [{x.TypeDisplay}] {x.Path}"));
            if (selectedItems.Count > 12) sample += $"\n… 等 {selectedItems.Count} 项";

            var cloudRoots = selectedItems
                .Where(x => x.Type == ResidualType.Folder && UninstallService.IsCloudSyncRoot(x.Path))
                .Select(x => x.Path).ToList();

            var msg = $"将直接删除 {targets.Count} 个程序的全部残留（不可恢复）：\n"
                      + $"文件夹 {folders} · 注册表 {regs} · 文件/快捷方式 {files}，约 {InstalledProgram.FormatSize(size)}\n\n"
                      + sample
                      + (uwpTargets.Count > 0 ? $"\n\n将同时卸载 {uwpTargets.Count} 个 UWP 应用（不可撤销）。" : "")
                      + (skippedCount > 0
                          ? $"\n\n另有 {skippedCount} 项低置信/需确认项已自动跳过（可能与其它软件/版本共享，可在“清理残留”中逐项确认）。"
                          : "")
                      + "\n\n清理注册表前会自动导出 .reg 备份。删除时若文件被占用，将关闭其句柄或安排重启后删除（不会默认结束进程）。"
                      + (cloudRoots.Count > 0
                          ? "\n\n⚠ 检测到疑似云同步目录，删除会同步到云端：\n" + string.Join("\n", cloudRoots.Take(5))
                          : "")
                      + "\n\n（网络盘/NAS、系统关键目录、厂商共享目录已自动排除）确定全部删除？";

            if (!_ui.ConfirmDanger(msg, "强制删除"))
            {
                await RefreshAsync();
                return;
            }

            // 4) 删除阶段（确认之后才产生副作用）：先卸载 UWP，再备份并移除卸载登记项，
            //    最后删除勾选的残留（统一会话，删注册表前自动备份）
            IsBusy = true;
            BusyText = "正在删除全部残留…";
            var session = new CleanupSession(CleanupOperation.ForceRemove, targets.Select(p => p.DisplayName));
            var uwpWarnings = await UninstallUwpTargetsAsync(uwpTargets, session, null);
            BusyText = "正在删除全部残留…";
            // 登记项移除与“仅移除登记项”分支同口径记入结果：成功/失败都计数并进清单，
            // 否则两条路径的结果窗口口径不一致（极端情况显示“成功 0”但登记项确已被删）。
            var regResult = new ResidualCleanResult();
            var result = await Task.Run(() =>
            {
                foreach (var p in win32)
                    RemoveUninstallEntry(p, session, regResult);
                return _residual.Clean(items, secureErase: false, session);
            });
            result.Deleted += regResult.Deleted;
            result.DeletedItems.AddRange(regResult.DeletedItems);
            result.Failed += regResult.Failed;
            result.FailedItems.AddRange(regResult.FailedItems);
            result.Warnings.AddRange(uwpWarnings);
            result.Warnings.AddRange(regResult.Warnings);
            IsBusy = false;

            StatusText = $"强制删除完成: 删除 {result.Deleted} 项，失败 {result.Failed} 项";
            result.LogPath = session.Flush($"成功 {result.Deleted}，失败 {result.Failed}，重启后删除 {result.PendingReboot}，释放 {InstalledProgram.FormatSize(result.FreedBytes)}");
            _ui.ShowResult(result, "强制删除结果");
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    /// <summary>
    /// 逐个卸载 UWP 目标（仅在用户确认后调用），结果记入会话日志；
    /// 失败项返回警告清单（result 非空时同步计入其 Warnings）。
    /// </summary>
    private async Task<List<string>> UninstallUwpTargetsAsync(
        List<InstalledProgram> uwpTargets, CleanupSession session, ResidualCleanResult? result)
    {
        var warnings = new List<string>();
        var i = 0;
        foreach (var p in uwpTargets)
        {
            BusyText = $"正在卸载 UWP ({++i}/{uwpTargets.Count}) {p.DisplayName}…";
            var r = await _appx.UninstallAsync(p);
            if (r.Success)
            {
                session.Log($"✔ 已卸载 UWP: {p.DisplayName}");
                if (result is not null) { result.Deleted++; result.DeletedItems.Add($"(UWP) {p.DisplayName}"); }
            }
            else
            {
                session.Log($"✗ 卸载 UWP 失败: {p.DisplayName} — {r.Message}");
                warnings.Add($"UWP 卸载失败: {p.DisplayName} — {r.Message}");
                if (result is not null) { result.Failed++; result.FailedItems.Add($"(UWP) {p.DisplayName}"); }
            }
        }
        result?.Warnings.AddRange(warnings);
        return warnings;
    }

    /// <summary>
    /// 备份并移除单个程序的卸载登记项，成败记入结果与会话日志。
    /// 契约与 ResidualCleaner.CleanRegistry 对齐：备份失败且键仍存在时中止删除，
    /// 不让“删除注册表登记项前会自动导出 .reg 备份”的承诺落空。
    /// </summary>
    private void RemoveUninstallEntry(InstalledProgram p, CleanupSession session, ResidualCleanResult result)
    {
        var fullKey = p.FullRegistryPath;
        var backup = RegistryBackup.Export(fullKey, session.EnsureBackupFolder());
        if (backup is null && ResidualCleaner.RegistryKeyExists(fullKey))
        {
            result.Failed++; result.FailedItems.Add(fullKey);
            result.Warnings.Add($"注册表备份失败，已中止移除登记项（无备份即无法恢复）: {p.DisplayName}");
            session.Log($"⛔ 注册表备份失败，已中止移除登记项: {p.DisplayName}（{fullKey}）");
            return;
        }

        var rm = _uninstaller.ForceRemove(p, deleteInstallFolder: false);
        if (rm.Success)
        {
            result.Deleted++; result.DeletedItems.Add(fullKey);
            session.Log($"✔ 已移除卸载登记项: {p.DisplayName}（{fullKey}）");
        }
        else
        {
            result.Failed++; result.FailedItems.Add(fullKey);
            result.Warnings.Add($"移除卸载登记项失败: {p.DisplayName} — {rm.Message}");
            session.Log($"✗ 移除卸载登记项失败: {p.DisplayName} — {rm.Message}");
        }
    }

    private static string DescribeTargets(IReadOnlyList<InstalledProgram> targets)
        => targets.Count == 1 ? targets[0].DisplayName : $"{targets.Count} 个程序";

    // ------------- 批量卸载 -------------
    private async Task BatchUninstallAsync()
    {
        var targets = _all.Where(p => p.IsSelected).ToList();
        if (targets.Count == 0) return;

        var names = string.Join("\n", targets.Take(15).Select(p => "· " + p.DisplayName));
        if (targets.Count > 15) names += $"\n… 等 {targets.Count} 个";

        if (!_ui.ConfirmDanger(
                $"即将批量卸载以下 {targets.Count} 个程序（将尽量静默执行），完成后统一清理残留:\n\n{names}",
                "批量卸载"))
            return;

        int ok = 0, fail = 0;
        // 仅对“确实卸载成功”的程序进入残留清理：失败/不确定项仍在本机可用，
        // 若也清残留会误删仍在使用的软件文件与注册表项。
        var succeeded = new List<InstalledProgram>();
        foreach (var p in targets)
        {
            IsBusy = true;
            BusyText = $"正在卸载 ({ok + fail + 1}/{targets.Count}) {p.DisplayName}…";
            try
            {
                var r = p.Source == ProgramSource.Uwp
                    ? await _appx.UninstallAsync(p)
                    : await _uninstaller.UninstallAsync(p, silent: true);
                if (r.Success) { ok++; succeeded.Add(p); } else fail++;
            }
            catch (Exception ex)
            {
                // 失败原因必须落日志：静默吞掉后用户无从得知该程序为何卸载失败
                fail++;
                AppLogger.Warn($"批量卸载失败: {p.DisplayName}", ex);
            }
        }
        IsBusy = false;

        StatusText = $"批量卸载完成: 成功 {ok}，失败 {fail}";
        if (succeeded.Count > 0)
            _ui.CleanResiduals(succeeded);
        await RefreshAsync();
    }

    // ------------- 仅清理残留（支持多选批量）-------------
    private void CleanResidualsForSelected()
    {
        var targets = GetActionTargets();
        if (targets.Count == 0) return;
        _ui.CleanResiduals(targets);
    }

    // ------------- 选择辅助 -------------
    /// <summary>
    /// 勾选：作用于当前可见（搜索过滤后）列表；
    /// 取消勾选：作用于全量列表——避免被过滤隐藏的勾选项残留，
    /// 随后被批量卸载/强制删除（它们作用于全量勾选项）静默波及。
    /// </summary>
    public void SetAllChecked(bool value)
    {
        if (value)
            foreach (var p in Programs) p.IsSelected = true;
        else
            foreach (var p in _all) p.IsSelected = false;
    }

    private void InvertChecked()
    {
        // 反选仅作用于可见项；被搜索过滤隐藏的勾选项同时清零——
        // 与 SetAllChecked(false) 同一防护契约，避免隐藏勾选项被批量操作静默波及
        var visible = new HashSet<InstalledProgram>(Programs);
        foreach (var p in _all)
            p.IsSelected = visible.Contains(p) && !p.IsSelected;
    }

    /// <summary>表头全选框回显状态：可见项全选=true，全不选=false，部分=null。</summary>
    public bool? AllCheckedState
    {
        get
        {
            if (Programs.Count == 0) return false;
            var selected = Programs.Count(p => p.IsSelected);
            if (selected == 0) return false;
            return selected == Programs.Count ? true : null;
        }
    }

    private void AttachSelectionEvents()
    {
        foreach (var p in _all) p.PropertyChanged += OnProgramPropertyChanged;
    }

    private void DetachSelectionEvents()
    {
        foreach (var p in _all) p.PropertyChanged -= OnProgramPropertyChanged;
    }

    private void OnProgramPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstalledProgram.IsSelected))
        {
            OnPropertyChanged(nameof(CheckedCount));
            OnPropertyChanged(nameof(SelectedCountText));
            OnPropertyChanged(nameof(AllCheckedState));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // ------------- 打开操作 -------------
    private void OpenInstallFolder()
    {
        var loc = SelectedProgram?.InstallLocation;
        if (string.IsNullOrWhiteSpace(loc) || !Directory.Exists(loc))
        {
            _ui.Alert("未找到安装目录信息。");
            return;
        }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{loc}\"") { UseShellExecute = true }); }
        catch (Exception ex) { _ui.ShowError(ex.Message); }
    }

    private void OpenWebsite()
    {
        var url = SelectedProgram?.UrlInfoAbout;
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { _ui.ShowError(ex.Message); }
    }
}

