using BadmintonDraw.Core;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Excel;
using BadmintonDraw.Persistence;

namespace BadmintonDraw.Workflows.Tournaments;

public sealed record WorkspaceError(string Code, string Message, string? CandidatePath = null,
    string? BackupPath = null, bool Committed = false, SchedulingFailure? SchedulingFailure = null);

public sealed class WorkspaceCommandException(WorkspaceError error, Exception? innerException = null)
    : Exception(error.Message, innerException)
{
    public WorkspaceError Error { get; } = error;

    internal static WorkspaceCommandException From(Exception exception)
    {
        if (exception is WorkspaceCommandException command) return command;
        var store = exception as WorkspaceStoreException;
        var cause = exception;
        // Store wraps candidate validation failures; retain their useful domain code and file diagnostics.
        if (store is { Code: "WorkspaceWriteFailed" })
            while (cause.InnerException is not null && cause is not WorkspaceCommandException
                and not WorkspaceValidationException and not DrawValidationException and not ExcelImportException)
                cause = cause.InnerException;
        var (code, message) = cause switch
        {
            WorkspaceCommandException e => (e.Error.Code, e.Error.Message),
            WorkspaceValidationException e => (e.Code, e.Message),
            DrawValidationException e => ("draw.invalid", e.Message),
            ExcelImportException e => ("roster.import", e.Message),
            _ when store is not null => (store.Code, store.Message),
            _ => ("workspace.operation-failed", "操作失败：" + exception.Message)
        };
        return new(new(code, message, store?.CandidatePath, store?.BackupPath, store?.Committed ?? false,
            (cause as WorkspaceCommandException)?.Error.SchedulingFailure), exception);
    }
}
