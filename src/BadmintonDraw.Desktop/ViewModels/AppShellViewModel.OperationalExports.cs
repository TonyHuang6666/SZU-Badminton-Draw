using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed partial class AppShellViewModel
{
    // Like result import, feedback belongs to the current editor; session publication always belongs to Shell.
    internal async Task<WorkspaceOperationalExportExecution> ExportOperationalPackageAsync(
        WorkspaceSession expectedSession, OperationalExportRequest request)
    {
        if (disposed || Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            return new(null, null, new("desktop.command-busy", "当前操作尚未完成。"), workflow.CurrentSession);
        RefreshAvailability();
        try
        {
            var selected = request with { Days = request.Days?.ToArray() };
            var execution = await Task.Run(() =>
            {
                try
                {
                    if (disposed || !ReferenceEquals(workflow.CurrentSession, expectedSession) || !ReferenceEquals(CurrentSession, expectedSession))
                        throw new WorkspaceCommandException(new("workspace.session-changed", "工作区已更新或切换，请重新核对导出范围。"));
                    if (expectedSession.RequiresReload)
                        throw new WorkspaceCommandException(new("workspace.reload-required", "工作区已保存但无法重新读取，请重新载入后再导出。"));
                    var outcome = workflow.ExportOperationalPackage(selected, expectedSession.Workspace.Revision);
                    return new WorkspaceOperationalExportExecution(outcome, null, null, workflow.CurrentSession);
                }
                catch (OperationalPackageExportException failure)
                { return new WorkspaceOperationalExportExecution(null, failure, failure.Error, workflow.CurrentSession); }
                catch (Exception error)
                { return new WorkspaceOperationalExportExecution(null, null, error is WorkspaceCommandException command ? command.Error : new("desktop.operation-failed", error.Message), workflow.CurrentSession); }
            });
            if (!disposed && workflow.CurrentSession is { } current) ApplySession(current);
            return execution;
        }
        finally { Interlocked.Exchange(ref busy, 0); if (!disposed) RefreshAvailability(); }
    }
}

internal sealed record WorkspaceOperationalExportExecution(OperationalPackageOutcome? Outcome,
    OperationalPackageExportException? ExportFailure, WorkspaceError? Error, WorkspaceSession? CompletionSession);
