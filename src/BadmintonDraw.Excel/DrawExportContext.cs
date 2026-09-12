namespace BadmintonDraw.Excel;

public sealed record DrawExportContext(Guid WorkspaceId, string WorkspaceName, long SourceRevision,
    Guid ProjectId, string ProjectName, DateTimeOffset? ConfirmedAt, string SourceFileName, string SourceFileHash,
    Guid ExportAuditId, DateTimeOffset ExportedAt, string RandomSeed, string ParticipantHash)
{
    public string Status => ConfirmedAt is null ? "未确认抽签预览" : "已确认抽签结果";
    public string Heading => $"{Status} · {WorkspaceName} · {ProjectName}";
}
