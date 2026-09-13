using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed partial class AppShellViewModel
{
    // The owning editor decides whether feedback is still current. Ordinary runners remain unchanged.
    internal async Task<WorkspaceQueryResult<WorkspaceResultImportPreview>> PreviewResultImportAsync(
        WorkspaceSession expectedSession, IReadOnlyList<string> paths)
    {
        if (disposed || Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            return new(false, null, new("desktop.query-busy", "当前操作尚未完成。"));
        RefreshAvailability();
        try
        {
            var selected = paths.ToArray();
            var value = await Task.Run(() =>
            {
                CheckResultImportSession(expectedSession);
                var result = workflow.PreviewResultImport(selected);
                CheckResultImportSession(expectedSession); return result;
            });
            CheckResultImportSession(expectedSession); return new(true, value, null);
        }
        catch (Exception error) { return new(false, null, ResultImportError(error)); }
        finally { Interlocked.Exchange(ref busy, 0); if (!disposed) RefreshAvailability(); }
    }
    internal async Task<WorkspaceResultImportExecution> ImportResultsAsync(WorkspaceSession expectedSession,
        WorkspaceResultImportPreview preview, ResultCorrectionConfirmation confirmation)
    {
        if (disposed || Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            return new(null, new("desktop.command-busy", "当前操作尚未完成。"), workflow.CurrentSession);
        RefreshAvailability();
        try
        {
            var execution = await Task.Run(() =>
            {
                try
                {
                    CheckResultImportSession(expectedSession);
                    var result = workflow.ImportResults(preview, confirmation, preview.SourceRevision);
                    return new WorkspaceResultImportExecution(result, null, workflow.CurrentSession);
                }
                catch (Exception error) { return new WorkspaceResultImportExecution(null, ResultImportError(error), workflow.CurrentSession); }
            });
            // A stale/disposed editor cannot reverse publication. Also handles queued events and RequiresReload.
            if (!disposed && workflow.CurrentSession is { } current) ApplySession(current);
            return execution;
        }
        finally { Interlocked.Exchange(ref busy, 0); if (!disposed) RefreshAvailability(); }
    }
    private void CheckResultImportSession(WorkspaceSession? expected)
    {
        if (disposed || expected is null || !ReferenceEquals(workflow.CurrentSession, expected) || !ReferenceEquals(CurrentSession, expected))
            throw new WorkspaceCommandException(new("workspace.session-changed", "工作区已更新或切换，请重新预览导入。"));
        if (expected.RequiresReload)
            throw new WorkspaceCommandException(new("workspace.reload-required", "工作区已保存但无法重新读取，请重新载入后再导入。"));
    }
    private static WorkspaceError ResultImportError(Exception error) => error is WorkspaceCommandException command
        ? command.Error : new("desktop.operation-failed", "操作失败：" + error.Message);
}

internal sealed record WorkspaceResultImportExecution(WorkspaceResultImportOutcome? Outcome,
    WorkspaceError? Error, WorkspaceSession? CompletionSession);
