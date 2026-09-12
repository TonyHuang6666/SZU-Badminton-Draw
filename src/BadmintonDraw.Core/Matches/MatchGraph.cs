using BadmintonDraw.Core.Tournaments;
namespace BadmintonDraw.Core.Matches;
public sealed record MatchGraph(Guid ProjectId, string Revision, IReadOnlyList<MatchNode> Matches)
{
    private IReadOnlyList<MatchNode> matches = WorkspaceSnapshot.List(Matches);
    public IReadOnlyList<MatchNode> Matches { get => matches; init => matches = WorkspaceSnapshot.List(value); }
}
