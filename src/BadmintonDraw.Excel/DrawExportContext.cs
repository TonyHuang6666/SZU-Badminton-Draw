namespace BadmintonDraw.Excel;

public sealed record DrawExportContext(Guid WorkspaceId, string WorkspaceName, long SourceRevision,
    Guid ProjectId, string ProjectName, DateTimeOffset? ConfirmedAt, string SourceFileName, string SourceFileHash,
    Guid ExportAuditId, DateTimeOffset ExportedAt, string RandomSeed, string ParticipantHash)
{
    public string Status => ConfirmedAt is null ? "抽签结果（待确认）" : "抽签结果（已确认）";
    public string Heading => $"{Status} · {WorkspaceName} · {ProjectName}";
}
