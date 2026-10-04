namespace BadmintonDraw.Workflows.Tournaments;

/// <summary>Returned publications and the possibly changed attempted target are deliberately separate.</summary>
public sealed class OperationalPackageExportException : Exception
{
    public WorkspaceError Error { get; }
    public long? SourceRevision { get; }
    public Guid AuditId { get; }
    public DateTimeOffset? ExportedAt { get; }
    public OperationalPackageScope? Scope { get; }
    public OperationalPackageCounts? Counts { get; }
    public IReadOnlyList<OperationalPackageOutput> Outputs { get; }
    public IReadOnlyList<OperationalPackageSkip> Skips { get; }
    public string? RetainedStagingDirectory { get; }
    public string? AttemptedOutputPath { get; }
    public bool AuditRecorded => Error.Committed;

    internal OperationalPackageExportException(WorkspaceError error, long? sourceRevision, Guid auditId,
        DateTimeOffset? exportedAt, OperationalPackageScope? scope, OperationalPackageCounts? counts,
        IReadOnlyList<OperationalPackageOutput> outputs, IReadOnlyList<OperationalPackageSkip> skips,
        string? retainedStagingDirectory, string? attemptedOutputPath, Exception innerException) : base(error.Message, innerException)
    {
        Error = error; SourceRevision = sourceRevision; AuditId = auditId; ExportedAt = exportedAt;
        Scope = scope; Counts = counts; Outputs = Array.AsReadOnly(outputs.ToArray()); Skips = Array.AsReadOnly(skips.ToArray());
        RetainedStagingDirectory = retainedStagingDirectory; AttemptedOutputPath = attemptedOutputPath;
    }
}
