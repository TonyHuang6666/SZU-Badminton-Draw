using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed partial class AppShellViewModel
{
    private OnsiteRecordViewModel? onsiteRecords;
    public event Action<OnsiteRecordViewModel>? OnsiteRecordWindowRequested;
    public event Action? OnsiteRecordWindowInvalidated;

    internal bool CanOpenOnsiteRecords => !disposed && !IsBusy && CurrentSession is { RequiresReload: false } session &&
        session.Workspace.Purpose == TournamentPurpose.FullTournament && session.Workspace.Schedule is not null;

    internal void OpenOnsiteRecords()
    {
        if (!CanOpenOnsiteRecords) return;
        onsiteRecords ??= new(this, CurrentSession!);
        OnsiteRecordWindowRequested?.Invoke(onsiteRecords);
    }

    private void RefreshOnsiteRecordsSession(WorkspaceSession session, bool sameWorkspace)
    {
        if (!sameWorkspace) InvalidateOnsiteRecordsWindow();
        else onsiteRecords?.RefreshSession(session);
    }

    private void InvalidateOnsiteRecordsWindow()
    {
        onsiteRecords?.Dispose();
        onsiteRecords = null;
        OnsiteRecordWindowInvalidated?.Invoke();
    }

    private bool TryLeaveOnsiteRecords()
    {
        if (onsiteRecords is not { HasDrafts: true } model) return true;
        ReportError(new WorkspaceCommandException(new(PendingEditsErrorCode,
            "现场记录表还有未保存的填写内容，请完成该行或放弃未保存的修改后再切换比赛。")));
        OnsiteRecordWindowRequested?.Invoke(model);
        return false;
    }

    internal async Task<WorkspaceResultImportExecution> SaveOnsiteResultAsync(WorkspaceSession expectedSession,
        WorkspaceOnsiteResultRequest request)
    {
        if (disposed || Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            return new(null, new("desktop.command-busy", "当前操作尚未完成，记录仍待保存。"), workflow.CurrentSession);
        RefreshAvailability();
        try
        {
            var execution = await Task.Run(() =>
            {
                try
                {
                    CheckResultImportSession(expectedSession);
                    var result = workflow.RecordOnsiteResult(request, expectedSession.Workspace.Revision);
                    return new WorkspaceResultImportExecution(result, null, workflow.CurrentSession);
                }
                catch (Exception error)
                {
                    return new WorkspaceResultImportExecution(null, ResultImportError(error), workflow.CurrentSession);
                }
            });
            if (!disposed && workflow.CurrentSession is { } current) ApplySession(current);
            return execution;
        }
        finally
        {
            Interlocked.Exchange(ref busy, 0);
            if (!disposed) RefreshAvailability();
        }
    }

    internal Task<OnsiteDayExecution<WorkspaceDayConfirmationPreview>> PreviewOnsiteDayAsync(
        WorkspaceSession expected, WorkspaceDayConfirmationRequest request) =>
        ExecuteOnsiteDayAsync(expected, () => workflow.PreviewDayConfirmation(request, expected.Workspace.Revision));

    internal Task<OnsiteDayExecution<WorkspaceCommandResult>> ConfirmOnsiteDayAsync(
        WorkspaceSession expected, WorkspaceDayConfirmationPreview preview) =>
        ExecuteOnsiteDayAsync(expected, () => workflow.ConfirmDayResults(preview, expected.Workspace.Revision));

    private async Task<OnsiteDayExecution<T>> ExecuteOnsiteDayAsync<T>(WorkspaceSession expected, Func<T> action) where T : class
    {
        if (disposed || Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            return new(null, new("desktop.command-busy", "当前操作尚未完成，请稍后重试。"), workflow.CurrentSession);
        RefreshAvailability();
        try
        {
            var execution = await Task.Run(() =>
            {
                try { CheckResultImportSession(expected); return new OnsiteDayExecution<T>(action(), null, workflow.CurrentSession); }
                catch (Exception error) { return new OnsiteDayExecution<T>(null, ResultImportError(error), workflow.CurrentSession); }
            });
            if (!disposed && workflow.CurrentSession is { } current) ApplySession(current);
            return execution;
        }
        finally { Interlocked.Exchange(ref busy, 0); if (!disposed) RefreshAvailability(); }
    }
}

internal sealed record OnsiteDayExecution<T>(T? Value, WorkspaceError? Error, WorkspaceSession? CompletionSession) where T : class;
