using BeeXCleaner.Models;

namespace BeeXCleaner.Services;

/// <summary>
/// Implemented in the main window, it allows the ViewModel to trigger dialog boxes and handle processes that require UI interaction.
/// </summary>
public interface IUiService
{
    /// <summary>Standard confirmation dialog; returning `true` indicates the user's consent.</summary>
    bool Confirm(string message, string title = "确认");

    /// <summary>Hazardous Operation Confirmation Dialog Box (red warning icon).</summary>
    bool ConfirmDanger(string message, string title = "警告");

    /// <summary>Information dialog box. </summary>
    void Alert(string message, string title = "提示");

    /// <summary>Error message box.</summary>
    void ShowError(string message, string title = "错误");

    /// <summary>
    /// Open the Residual Cleanup window (modal). The window automatically scans its contents and allows the user to select items to clean up.
    /// </summary>
    void CleanResiduals(IReadOnlyList<InstalledProgram> programs);

    /// <summary> Opens the program details window. </summary>
    void ShowDetails(InstalledProgram program);

    /// <summary>Open the Residual Scan window (to clean up remnants of uninstalled software).</summary>
    void ScanOrphans();

    /// <summary>Opens the Deep Erase Free Space window (overwrites free space to make deleted files unrecoverable).</summary>
    void ShowWipe();

    /// <summary>Open the Quick Delete window (select any files or folders to delete; secure erasure is optional).</summary>
    void ShowQuickDelete();

    /// <summary>Displays the window showing the results of the structured cleanup (Success/Failure/Delete after restart/Free up space/Backup/Log).</summary>
    void ShowResult(ResidualCleanResult result, string title = "清理完成");

    /// <summary>Open the Backup and Restore window (Toolbox). </summary>
    void ShowBackupRestore();

    /// <summary>Open the Clear History window (Toolbox). </summary>
    void ShowCleanupHistory();
}
