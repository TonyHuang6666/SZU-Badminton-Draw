using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed partial class AppShellViewModel
{
    // Deliberately narrow: only recovery may inspect an explicit target without a usable current workspace.
    public async Task<WorkspaceQueryResult<WorkspaceRecoveryPreview>> PreviewRecoveryAsync(
        WorkspaceSession? expectedSession, string workspacePath, string backupPath)
    {
        if (disposed || Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            return new(false, null, new("desktop.query-busy", "当前操作尚未完成。"));
        LastError = null; Status = "正在检查恢复来源，尚未保存…"; RefreshAvailability();
        try
        {
            var value = await Task.Run(() =>
            {
                CheckRecoverySession(expectedSession);
                var preview = workflow.PreviewRecovery(workspacePath, backupPath);
                CheckRecoverySession(expectedSession);
                return preview;
            });
            CheckRecoverySession(expectedSession);
            Status = "检查完成，尚未保存任何修改。";
            return new(true, value, null);
        }
        catch (Exception exception)
        {
            var error = exception is WorkspaceCommandException command ? command.Error : new WorkspaceError("desktop.query-failed", "检查失败：" + exception.Message);
            if (!disposed) ReportError(new WorkspaceCommandException(error, exception));
            return new(false, null, error);
        }
        finally { Interlocked.Exchange(ref busy, 0); if (!disposed) RefreshAvailability(); }
    }

    public Task<bool> RecoverFromBackupAsync(WorkspaceSession? expectedSession, WorkspaceRecoveryPreview preview, string reason) =>
        RunCommandAsync(async () => await Task.Run(() =>
        {
            CheckRecoverySession(expectedSession);
            return workflow.RecoverFromBackup(preview, reason);
        }), "损坏工作区已恢复并保存；当前打开的是恢复后的目标文件。", remember: true, ensureWorkspacePage: true);

    private void CheckRecoverySession(WorkspaceSession? expectedSession)
    {
        if (disposed || !ReferenceEquals(workflow.CurrentSession, expectedSession))
            throw new WorkspaceCommandException(new("workspace.session-changed", "工作区已更新或切换，请重新预览并确认恢复。"));
    }
}
