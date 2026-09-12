using BadmintonDraw.Core.Tournaments;
namespace BadmintonDraw.Core.Matches;
public sealed record MatchGraph(Guid ProjectId, string Revision, IReadOnlyList<MatchNode> Matches)
{
    private IReadOnlyList<MatchNode> matches = WorkspaceSnapshot.List(Matches);
    public IReadOnlyList<MatchNode> Matches { get => matches; init => matches = WorkspaceSnapshot.List(value); }

    // Current formats advance byes directly; explicit bye nodes require normalization before entering a workspace.
    internal void RequirePlayableNodes()
    {
        if (Matches.Any(node => !node.IsPlayable))
            throw new WorkspaceValidationException("graph.bye", "比赛关系图不能包含轮空场次；轮空参赛方必须直接进入后续比赛。");
    }
}
