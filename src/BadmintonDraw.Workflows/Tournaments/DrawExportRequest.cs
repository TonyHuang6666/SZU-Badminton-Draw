namespace BadmintonDraw.Workflows.Tournaments;

public enum DrawExportState { Preview, Confirmed }
public sealed record DrawExportLayout(int PdfRows = 1, int PdfColumns = 1);
public sealed record DrawExportRequest(string OutputDirectory, WorkflowExportFormat Format,
    DrawExportState State = DrawExportState.Confirmed, DrawExportLayout? Layout = null, bool OverwriteExisting = false);
public sealed record DrawPackageOutput(Guid ProjectId, WorkflowExportFormat Format, string Path);
public sealed record DrawPackageExportResult(WorkspaceCommandResult Command, IReadOnlyList<DrawPackageOutput> Outputs,
    long SourceRevision, Guid AuditId);

/// <summary>Outputs already published remain usable even when publication or the subsequent audit save fails.</summary>
public sealed class DrawPackageExportException(WorkspaceError error, IReadOnlyList<DrawPackageOutput> outputs,
    string? retainedStagingDirectory, Exception innerException) : Exception(error.Message, innerException)
{
    public WorkspaceError Error { get; } = error;
    public IReadOnlyList<DrawPackageOutput> Outputs { get; } = Array.AsReadOnly(outputs.ToArray());
    public bool AuditRecorded => Error.Committed;
    public string? RetainedStagingDirectory { get; } = retainedStagingDirectory;
}
