namespace BadmintonDraw.Core.Tournaments;
public sealed record WorkspaceAuditEvent(Guid Id, string Action, DateTimeOffset OccurredAt,
    Guid? ProjectId = null, Guid? MatchId = null, string Detail = "");
