namespace BadmintonDraw.Workflows.Tournaments;

public sealed record OperationalExportRequest(string OutputDirectory, Guid? ProjectId = null,
    IReadOnlyList<DateOnly>? Days = null, DateOnly? PendingCarryoverDay = null,
    bool OverwriteExisting = false, DrawExportLayout? DrawLayout = null)
{
    private IReadOnlyList<DateOnly>? days = Days is null ? null : Array.AsReadOnly(Days.ToArray());
    public IReadOnlyList<DateOnly>? Days { get => days; init => days = value is null ? null : Array.AsReadOnly(value.ToArray()); }
}

public enum OperationalMaterialKind
{
    TimedDrawExcel, TimedDrawA4Pdf, ProjectRecordExcel, DailyScheduleExcel, MergedRecordExcel,
    IndividualScorePdf, TeamScoreExcel, QualityExcel, Description, Manifest
}
public sealed record OperationalPackageOutput(OperationalMaterialKind Kind, Guid? ProjectId, DateOnly? RecordDay,
    string Path, long ByteLength, string Sha256);
public sealed record OperationalPackageSkip(string Code, string Message, Guid? ProjectId = null, DateOnly? RecordDay = null);
public sealed record OperationalPackageScope(IReadOnlyList<Guid> ProjectIds, IReadOnlyList<DateOnly> Days, DateOnly? PendingCarryoverDay)
{
    private IReadOnlyList<Guid> projectIds = Array.AsReadOnly(ProjectIds.ToArray());
    private IReadOnlyList<DateOnly> days = Array.AsReadOnly(Days.ToArray());
    public IReadOnlyList<Guid> ProjectIds { get => projectIds; init => projectIds = Array.AsReadOnly(value.ToArray()); }
    public IReadOnlyList<DateOnly> Days { get => days; init => days = Array.AsReadOnly(value.ToArray()); }
}
public sealed record OperationalPackageCounts(int DistinctMatchCount, int RecordRowCount,
    int PendingCarryoverCount, int TimedDrawMatchCount, int RequiredOutputCount);
public sealed record OperationalPackageOutcome(WorkspaceCommandResult Command, long SourceRevision, Guid AuditId,
    DateTimeOffset ExportedAt, OperationalPackageScope Scope, OperationalPackageCounts Counts,
    IReadOnlyList<OperationalPackageOutput> Outputs, IReadOnlyList<OperationalPackageSkip> Skips)
{
    private IReadOnlyList<OperationalPackageOutput> outputs = Array.AsReadOnly(Outputs.ToArray());
    private IReadOnlyList<OperationalPackageSkip> skips = Array.AsReadOnly(Skips.ToArray());
    public IReadOnlyList<OperationalPackageOutput> Outputs { get => outputs; init => outputs = Array.AsReadOnly(value.ToArray()); }
    public IReadOnlyList<OperationalPackageSkip> Skips { get => skips; init => skips = Array.AsReadOnly(value.ToArray()); }
    public bool AuditRecorded => true;
}
