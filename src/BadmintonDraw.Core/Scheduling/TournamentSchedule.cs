using BadmintonDraw.Core.Tournaments;
namespace BadmintonDraw.Core.Scheduling;
public sealed record TournamentSchedule(IReadOnlyDictionary<Guid, MatchPlacement> Placements,
    TournamentResourcePlan Resources, TournamentSchedulingPolicy Policy,
    IReadOnlyDictionary<Guid, string> GraphRevisions, long Revision)
{
    private IReadOnlyDictionary<Guid, MatchPlacement> placements = WorkspaceSnapshot.Dictionary(Placements);
    private IReadOnlyDictionary<Guid, string> graphRevisions = WorkspaceSnapshot.Dictionary(GraphRevisions);
    public IReadOnlyDictionary<Guid, MatchPlacement> Placements { get => placements; init => placements = WorkspaceSnapshot.Dictionary(value); }
    public IReadOnlyDictionary<Guid, string> GraphRevisions { get => graphRevisions; init => graphRevisions = WorkspaceSnapshot.Dictionary(value); }
}
