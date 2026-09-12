using BadmintonDraw.Core.Scheduling;
namespace BadmintonDraw.Core.Tournaments;
/// <summary>Immutable candidate snapshot. Validate after deserialization and before accepting/persisting a candidate.</summary>
public sealed record TournamentWorkspace(Guid Id, string Name, TournamentKind Kind, TournamentPurpose Purpose,
    TournamentStage Stage, IReadOnlyList<TournamentProject> Projects, TournamentResourcePlan? Resources,
    TournamentSchedule? Schedule, IReadOnlyDictionary<WorkspaceMatchKey, TournamentMatchResult> Results,
    IReadOnlyList<WorkspaceAuditEvent> AuditEvents, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long Revision) : System.Text.Json.Serialization.IJsonOnDeserialized
{
    void System.Text.Json.Serialization.IJsonOnDeserialized.OnDeserialized() => TournamentWorkspaceRules.Validate(this);
    private IReadOnlyList<TournamentProject> projects = WorkspaceSnapshot.List(Projects);
    private IReadOnlyDictionary<WorkspaceMatchKey, TournamentMatchResult> results = WorkspaceSnapshot.Dictionary(Results);
    private IReadOnlyList<WorkspaceAuditEvent> auditEvents = WorkspaceSnapshot.List(AuditEvents);
    public IReadOnlyList<TournamentProject> Projects { get => projects; init => projects = WorkspaceSnapshot.List(value); }
    public IReadOnlyDictionary<WorkspaceMatchKey, TournamentMatchResult> Results { get => results; init => results = WorkspaceSnapshot.Dictionary(value); }
    public IReadOnlyList<WorkspaceAuditEvent> AuditEvents { get => auditEvents; init => auditEvents = WorkspaceSnapshot.List(value); }
    public static TournamentWorkspace Create(string name, TournamentKind kind, TournamentPurpose purpose,
        IReadOnlyList<TournamentProject> projects)
    {
        var now = DateTimeOffset.UtcNow;
        var workspace = new TournamentWorkspace(Guid.NewGuid(), name, kind, purpose, TournamentStage.Draft,
            projects, null, null, new Dictionary<WorkspaceMatchKey, TournamentMatchResult>(), [], now, now, 0);
        TournamentWorkspaceRules.Validate(workspace);
        return workspace;
    }
}
