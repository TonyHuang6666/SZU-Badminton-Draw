namespace BadmintonDraw.Core.Tournaments;

public sealed record WorkspaceRecordLocation(string SheetName, int RowNumber);
public sealed record WorkspaceImportedRow(WorkspaceMatchKey Key, DateOnly RecordDay, WorkspaceRecordLocation Location, bool HadResult);
public sealed record WorkspaceImportWarning(string Code, string Message, WorkspaceRecordLocation? Location = null);

public sealed record WorkspaceImportLog(Guid Id, DateTimeOffset ImportedAt, string SourceFileName, string SourcePath,
    string ContentHash, IReadOnlyList<WorkspaceImportedRow> Rows, int AddedResultCount, int CorrectionCount,
    IReadOnlyList<WorkspaceImportWarning> Warnings)
{
    private IReadOnlyList<WorkspaceImportedRow> rows = WorkspaceSnapshot.List(Rows);
    private IReadOnlyList<WorkspaceImportWarning> warnings = WorkspaceSnapshot.List(Warnings);
    public IReadOnlyList<WorkspaceImportedRow> Rows { get => rows; init => rows = WorkspaceSnapshot.List(value); }
    public IReadOnlyList<WorkspaceImportWarning> Warnings { get => warnings; init => warnings = WorkspaceSnapshot.List(value); }
    public DateTimeOffset? VoidedAt { get; init; }
    public string? VoidReason { get; init; }
    public Guid? VoidedByAuditEventId { get; init; }
}

/// <summary>Imported row coverage, not match/day completion. Only active receipts contribute.</summary>
public sealed record WorkspaceProcessedDay(DateOnly Day, IReadOnlyList<WorkspaceMatchKey> CoveredMatches, DateTimeOffset UpdatedAt)
{
    private IReadOnlyList<WorkspaceMatchKey> coveredMatches = WorkspaceSnapshot.List(CoveredMatches);
    public IReadOnlyList<WorkspaceMatchKey> CoveredMatches { get => coveredMatches; init => coveredMatches = WorkspaceSnapshot.List(value); }
}

public sealed record WorkspaceResultHistory(Guid Id, long Sequence, WorkspaceMatchKey Key,
    TournamentMatchResult Before, TournamentMatchResult After, Guid ImportLogId, WorkspaceRecordLocation Source,
    DateTimeOffset ChangedAt, string Reason);
