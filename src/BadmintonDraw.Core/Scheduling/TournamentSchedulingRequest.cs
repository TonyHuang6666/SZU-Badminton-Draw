using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Core.Scheduling;

public sealed record TournamentSchedulingRequest(IReadOnlyList<MatchGraph> MatchGraphs,
    TournamentResourcePlan Resources, TournamentSchedulingPolicy Policy)
{
    private IReadOnlyList<MatchGraph> matchGraphs = WorkspaceSnapshot.List(MatchGraphs);
    private IReadOnlyDictionary<Guid, string> projectNames = WorkspaceSnapshot.Dictionary(new Dictionary<Guid, string>());
    private IReadOnlyDictionary<WorkspaceMatchKey, TournamentMatchResult> results = WorkspaceSnapshot.Dictionary(new Dictionary<WorkspaceMatchKey, TournamentMatchResult>());
    private IReadOnlyDictionary<Guid, MatchPlacement>? baselinePlacements;
    private IReadOnlyList<Guid> lockedMatchIds = WorkspaceSnapshot.List(Array.Empty<Guid>());
    public IReadOnlyList<MatchGraph> MatchGraphs { get => matchGraphs; init => matchGraphs = WorkspaceSnapshot.List(value); }
    public IReadOnlyDictionary<Guid, string> ProjectNames { get => projectNames; init => projectNames = WorkspaceSnapshot.Dictionary(value); }
    public IReadOnlyDictionary<WorkspaceMatchKey, TournamentMatchResult> Results { get => results; init => results = WorkspaceSnapshot.Dictionary(value); }
    /// <summary>Real previous placements. Null means fresh generation. Unlocked entries are scoring references only.</summary>
    public IReadOnlyDictionary<Guid, MatchPlacement>? BaselinePlacements { get => baselinePlacements; init => baselinePlacements = value is null ? null : WorkspaceSnapshot.Dictionary(value); }
    public IReadOnlyList<Guid> LockedMatchIds { get => lockedMatchIds; init => lockedMatchIds = WorkspaceSnapshot.List(value); }
    public long ScheduleRevision { get; init; }
}
