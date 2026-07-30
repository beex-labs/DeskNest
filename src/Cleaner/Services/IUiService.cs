using BeeXCleaner.Models;

namespace BeeXCleaner.Services;

/// <summary>
/// 由主窗口实现，供 ViewModel 触发对话框与需要 UI 交互的流程。
/// </summary>
public interface IUiService
{
    /// <summary>普通确认框，返回 true 表示用户同意。</summary>
    bool Confirm(string message, string title = "确认");

    /// <summary>危险操作确认框（红色警告图标）。</summary>
    bool ConfirmDanger(string message, string title = "警告");

    /// <summary>信息提示框。</summary>
    void Alert(string message, string title = "提示");

    /// <summary>错误提示框。</summary>
    void ShowError(string message, string title = "错误");

    /// <summary>
    /// 打开残留清理窗口（模态）。窗口内部自行扫描并让用户勾选清理。
    /// </summary>
    void CleanResiduals(IReadOnlyList<InstalledProgram> programs);

    /// <summary>打开程序详情窗口。</summary>
    void ShowDetails(InstalledProgram program);

    /// <summary>打开遗留扫描窗口（清理已卸载软件的残留）。</summary>
    void ScanOrphans();

    /// <summary>打开空间深度擦除窗口（覆盖可用空间，使已删除文件不可恢复）。</summary>
    void ShowWipe();

    /// <summary>打开快速删除窗口（选择任意文件/文件夹删除，可选安全擦除）。</summary>
    void ShowQuickDelete();

    /// <summary>显示结构化清理结果窗口（成功/失败/重启后删/释放空间/备份/日志）。</summary>
    void ShowResult(ResidualCleanResult result, string title = "清理完成");

    /// <summary>打开备份恢复窗口（工具箱）。</summary>
    void ShowBackupRestore();

    /// <summary>打开清理历史窗口（工具箱）。</summary>
    void ShowCleanupHistory();
}
