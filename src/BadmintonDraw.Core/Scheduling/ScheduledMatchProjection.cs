using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Core.Scheduling;

/// <summary>Read-only, project-qualified projection of graph sources and current outcomes onto placements.</summary>
public static class ScheduledMatchProjection
{
    public static IReadOnlyList<ScheduledMatch> Build(MatchGraph graph, TournamentSchedule schedule,
        IReadOnlyDictionary<WorkspaceMatchKey, TournamentMatchResult> results) => Build([graph], schedule, results);

    /// <summary>Supply every graph in the schedule so foreign and missing placement keys can be detected.</summary>
    public static IReadOnlyList<ScheduledMatch> Build(IReadOnlyList<MatchGraph> graphs, TournamentSchedule schedule,
        IReadOnlyDictionary<WorkspaceMatchKey, TournamentMatchResult> results)
    {
        ArgumentNullException.ThrowIfNull(graphs);
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(results);
        Require(graphs.Select(g => g.ProjectId).Distinct().Count() == graphs.Count, "graph.project");
        Require(schedule.GraphRevisions.Count == graphs.Count && graphs.All(g =>
            schedule.GraphRevisions.TryGetValue(g.ProjectId, out var revision) && revision == g.Revision), "schedule.graph-revision");
        var all = graphs.SelectMany(g => g.Matches).ToArray();
        Require(all.Select(n => n.Id).Distinct().Count() == all.Length, "graph.identity");
        var nodes = all.ToDictionary(n => n.Id);
        foreach (var graph in graphs)
        {
            Require(graph.ProjectId != Guid.Empty && !string.IsNullOrWhiteSpace(graph.Revision), "graph.identity");
            Require(graph.Matches.Select(n => n.Order).Distinct().Count() == graph.Matches.Count, "graph.order");
            foreach (var node in graph.Matches)
            {
                Require(node.Id != Guid.Empty && node.ProjectId == graph.ProjectId && node.Order > 0, "graph.node");
                var references = Sources(node).Select(Reference).Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();
                Require(references.SetEquals(node.Dependencies) && node.Dependencies.Distinct().Count() == node.Dependencies.Count, "graph.dependencies");
                foreach (var reference in references)
                    Require(nodes.TryGetValue(reference, out var parent) && parent.ProjectId == node.ProjectId && parent.Order < node.Order, "graph.source");
            }
        }
        Require(schedule.Placements.Keys.ToHashSet().SetEquals(all.Where(n => n.IsPlayable).Select(n => n.Id)), "schedule.coverage");
        Require(schedule.Placements.All(pair => pair.Key == pair.Value.MatchId), "schedule.placement");
        foreach (var pair in results)
            Require(pair.Key == pair.Value.Key && nodes.TryGetValue(pair.Key.MatchId, out var n) &&
                n.ProjectId == pair.Key.ProjectId && n.IsPlayable, "result.match");

        var sides = new Dictionary<Guid, (Resolved A, Resolved B)>();
        var outcomes = new Dictionary<Guid, TournamentMatchResult>();
        Resolved Resolve(EntrantSource source)
        {
            if (source is EntrantSource.Participant p) return new(p.DisplayName, p.Players, p);
            if (source is EntrantSource.Bye) return new("轮空", [], null);
            var id = Reference(source)!.Value;
            var parent = nodes[id];
            if (outcomes.TryGetValue(id, out var outcome))
            {
                var entrant = source is EntrantSource.WinnerOf ? outcome.Winner : outcome.Loser;
                return new(entrant.DisplayName, entrant.Players, entrant);
            }
            var (a, b) = sides[id];
            if (!parent.IsPlayable)
                return source is EntrantSource.WinnerOf
                    ? (parent.SideA is EntrantSource.Bye ? b : a)
                    : new("轮空", [], null);
            return new(parent.DisplayName + (source is EntrantSource.WinnerOf ? "胜者" : "负者"),
                a.Players.Concat(b.Players).DistinctBy(p => p.IdentityKey).ToArray(), null);
        }

        var projected = new List<ScheduledMatch>();
        foreach (var graph in graphs)
        foreach (var node in graph.Matches.OrderBy(n => n.Order))
        {
            var a = Resolve(node.SideA);
            var b = Resolve(node.SideB);
            sides.Add(node.Id, (a, b));
            if (results.TryGetValue(new(node.ProjectId, node.Id), out var result))
            {
                Require(a.Participant is not null && b.Participant is not null &&
                    ((Same(result.Winner, a.Participant) && Same(result.Loser, b.Participant)) ||
                     (Same(result.Winner, b.Participant) && Same(result.Loser, a.Participant))) &&
                    result.Winner.IdentityKey != result.Loser.IdentityKey &&
                    !string.IsNullOrWhiteSpace(result.Score) && result.DurationMinutes > 0 && result.RecordedAt != default,
                    "result.entrant");
                outcomes.Add(node.Id, result);
            }
            if (!node.IsPlayable) continue;
            var placement = schedule.Placements[node.Id];
            var dependencies = new List<ScheduleMatchDependency>();
            void AddDependency(EntrantSource source, ScheduleMatchSide side)
            {
                if (Reference(source) is not { } id) return;
                dependencies.Add(new(id.ToString("D"), nodes[id].DisplayName,
                    source is EntrantSource.WinnerOf ? ScheduleMatchDependencyOutcome.Winner : ScheduleMatchDependencyOutcome.Loser, side));
            }
            AddDependency(node.SideA, ScheduleMatchSide.SideA);
            AddDependency(node.SideB, ScheduleMatchSide.SideB);
            projected.Add(new(node.Order, placement.DayLabel, placement.StartTime, placement.EndTime, placement.Court,
                node.GroupNumber, node.GroupName, node.Phase, node.DisplayName, a.Label, b.Label,
                node.Note, node.SameUnit, node.Id.ToString("D"), dependencies, a.Players, b.Players));
        }
        return projected.AsReadOnly();
    }

    private static IEnumerable<EntrantSource> Sources(MatchNode node) => [node.SideA, node.SideB];
    private static Guid? Reference(EntrantSource source) => source switch
    {
        EntrantSource.WinnerOf winner => winner.MatchId,
        EntrantSource.LoserOf loser => loser.MatchId,
        _ => null
    };
    private static bool Same(EntrantSource.Participant a, EntrantSource.Participant b) =>
        a.IdentityKey == b.IdentityKey && a.Players.Select(p => p.IdentityKey).ToHashSet(StringComparer.Ordinal)
            .SetEquals(b.Players.Select(p => p.IdentityKey));
    private static void Require(bool valid, string code)
    {
        if (!valid) throw new WorkspaceValidationException(code, "比赛图、编排版本或赛果来源不一致，无法生成赛程视图。");
    }
    private sealed record Resolved(string Label, IReadOnlyList<CrossEventPlayerIdentity> Players, EntrantSource.Participant? Participant);
}
