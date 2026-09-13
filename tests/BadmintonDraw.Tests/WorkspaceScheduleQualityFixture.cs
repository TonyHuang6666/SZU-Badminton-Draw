using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Tests;

internal static class WorkspaceScheduleQualityFixture
{
    internal static readonly DateTimeOffset GeneratedAt = new(2026, 9, 21, 17, 18, 19, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset Epoch = new(2026, 9, 12, 10, 11, 12, TimeSpan.FromHours(8));
    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:x12}");
    internal static TournamentWorkspace MultiProject(int completed = 0, bool conflict = false, bool inputInvalid = false)
    {
        var projects = new List<TournamentProject>(); var placements = new Dictionary<Guid, MatchPlacement>();
        var timings = new Dictionary<Guid, ProjectMatchTiming>();
        for (var p = 0; p < 2; p++)
        {
            var discipline = p == 0 ? EventDiscipline.MenSingles : EventDiscipline.WomenSingles;
            var roster = new[] { "=1+1", "甲/乙【同名】", "某某胜者", "甲/乙【同名】" }
                .Select((name, i) => new DrawParticipant(name, PrimaryStudentId: $"P{p}-{i}")).ToArray();
            var entrants = roster.Select(r => ProjectEntrantIdentity.Create(discipline, r)).ToArray();
            var projectId = Id(p + 1);
            MatchNode Node(int i, EntrantSource a, EntrantSource b, IReadOnlyList<Guid> dependencies) => new(
                Id(100 + p * 10 + i), projectId, "match-" + i, i, 1, i == 3 ? "决赛" : "首轮", "同名场次/【" + i + "】", a, b, 30, dependencies)
                { KnockoutEntrantCount = i == 3 ? 4 : 8, IsChampionshipFinal = i == 3, IsChampionshipBracket = true };
            var first = Node(1, entrants[0], entrants[1], []); var second = Node(2, entrants[2], entrants[3], []);
            var final = Node(3, new EntrantSource.WinnerOf(first.Id), new EntrantSource.WinnerOf(second.Id), [first.Id, second.Id]);
            var nodes = new[] { first, second, final };
            projects.Add(Project(projectId, discipline, CompetitionMode.SinglesKnockout, p, roster, nodes));
            timings[projectId] = p == 0 ? new(30, 4, 20) : new(25);
            foreach (var node in nodes)
            {
                var start = node.Order == 1 ? new TimeOnly(9, 0, 30).Add(TimeSpan.FromTicks(1234567)) : node.Order == 2 ? new(11, 0) : new(9, 30);
                var duration = p == 0 ? node.Order == 3 ? 30 : 20 : 25;
                placements[node.Id] = new(node.Id, node.Order == 3 ? "2026-09-20" : "2026-09-19", start, start.AddMinutes(duration), p == 0 || conflict ? "A" : "B");
            }
        }
        var resources = new TournamentResourcePlan([
            new(new(2026, 9, 19), new(9, 0), new(18, 0), ["A", "B"], [new(new(12, 0), new(13, 0), 1)],
                [new(new(12, 0), new(13, 0), ["A"]), new(new(12, 30), new(13, 30), ["A"])]),
            new(new(2026, 9, 20), new(9, 0), new(18, 0), ["A", "B"], [new(new(14, 0), new(15, 0), 1)]),
            new(new(2026, 9, 21), new(9, 0), new(18, 0), ["A", "B"], [], [new(new(9, 0), new(18, 0), ["A", "B"])])], null, 15, 1);
        var policy = new TournamentSchedulingPolicy(ScheduleAutoSchedulingStrategy.Compact,
            [new(inputInvalid ? "2099-01-01" : "2026-09-19", .5, .8)], true,
            [new("2026-09-19", .6), new("2026-09-20", 1)],
            [new(projects[0].Id, TournamentFinalDayMatchCategory.Final, TournamentFinalDayPreference.PreferFinalDay)])
            { ProjectTimings = timings };
        var results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult>();
        foreach (var node in projects.SelectMany(p => p.MatchGraph!.Matches).Take(completed))
        {
            EntrantSource.Participant Resolve(EntrantSource source) => source is EntrantSource.Participant player ? player : results[new(node.ProjectId, ((EntrantSource.WinnerOf)source).MatchId)].Winner;
            var key = new WorkspaceMatchKey(node.ProjectId, node.Id);
            results[key] = new(key, Resolve(node.SideA), Resolve(node.SideB), "21-13", 25, Epoch.AddDays(8))
                { ActualPlayedDay = results.Count % 2 == 0 ? new(2026, 9, 20) : null };
        }
        return Workspace(projects, resources, policy, placements, results);
    }

    internal static TournamentWorkspace UnknownForecast()
    {
        var roster = new[] { "A" }.Concat(Enumerable.Range(1, 19).Select(i => "B" + i)).Concat(Enumerable.Range(1, 19).Select(i => "C" + i))
            .Select(name => new DrawParticipant(name, PrimaryStudentId: name)).ToArray();
        var players = roster.Select(r => ProjectEntrantIdentity.Create(EventDiscipline.MenSingles, r)).ToArray();
        var projectId = Id(1); var nodes = new List<MatchNode>();
        for (var i = 0; i < 19; i++) nodes.Add(new(Id(100 + i), projectId, "first-" + i, i + 1, 1, "初赛", "初赛 " + i, players[0], players[i + 1], 1, []));
        for (var i = 0; i < 19; i++) nodes.Add(new(Id(200 + i), projectId, "next-" + i, i + 20, 1, "后续", "后续 " + i,
            new EntrantSource.WinnerOf(nodes[i].Id), players[i + 20], 1, [nodes[i].Id]));
        var project = Project(projectId, EventDiscipline.MenSingles, CompetitionMode.SinglesRoundRobin, 0, roster, nodes);
        var resources = new TournamentResourcePlan([new(new(2026, 9, 19), new(9, 0), new(18, 0), ["A"])], 1, 0, 40);
        var placements = nodes.ToDictionary(n => n.Id, n => new MatchPlacement(n.Id, "2026-09-19", new TimeOnly(9, 0).AddMinutes(n.Order * 2), new TimeOnly(9, 1).AddMinutes(n.Order * 2), "A"));
        return Workspace([project], resources, new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []), placements,
            new Dictionary<WorkspaceMatchKey, TournamentMatchResult>());
    }

    private static TournamentProject Project(Guid id, EventDiscipline discipline, CompetitionMode mode, int order,
        DrawParticipant[] roster, IReadOnlyList<MatchNode> nodes)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(roster))));
        var draw = new DrawResult([new(1, roster)], [], [], new(mode, EventKind.Singles, 1, "quality-seed"),
            new(DrawAlgorithmVersion.PerGroupPowerOfTwo, "quality-seed", Epoch, hash, roster.Length, 0, 1));
        return new(id, discipline, "=项目/【同名】", mode, new(roster, $"quality-roster-{order}.xlsx", hash, []),
            new(draw, Epoch.AddMinutes(order + 1)), new(id, "graph-" + id, nodes), order);
    }
    private static TournamentWorkspace Workspace(IReadOnlyList<TournamentProject> projects, TournamentResourcePlan resources,
        TournamentSchedulingPolicy policy, IReadOnlyDictionary<Guid, MatchPlacement> placements,
        IReadOnlyDictionary<WorkspaceMatchKey, TournamentMatchResult> results)
    {
        var workspace = new TournamentWorkspace(Id(999), "=校长杯/质量【验证】", TournamentKind.Individual, TournamentPurpose.FullTournament,
            results.Count == placements.Count ? TournamentStage.Completed : results.Count > 0 ? TournamentStage.InProgress : TournamentStage.ScheduleReady,
            projects, resources, new(placements, resources, policy, projects.ToDictionary(p => p.Id, p => p.MatchGraph!.Revision), 9), results, [], Epoch, Epoch.AddDays(9), 17);
        TournamentWorkspaceRules.Validate(workspace); return workspace;
    }
    internal static TournamentSchedulingRequest Request(TournamentWorkspace workspace) => new(
        workspace.Projects.OrderBy(p => p.SortOrder).ThenBy(p => p.Id).Select(p => p.MatchGraph!).ToArray(), workspace.Schedule!.Resources, workspace.Schedule.Policy)
    { ProjectNames = workspace.Projects.ToDictionary(p => p.Id, p => p.DisplayName), Results = workspace.Results,
        BaselinePlacements = workspace.Schedule.Placements, ScheduleRevision = workspace.Schedule.Revision };
    internal static string Snapshot(TournamentWorkspace workspace) => JsonSerializer.Serialize(new
    { workspace.Id, workspace.Name, workspace.Stage, workspace.Projects, workspace.Schedule, workspace.Resources,
        Results = workspace.Results.OrderBy(p => p.Key.ProjectId).ThenBy(p => p.Key.MatchId).Select(p => p.Value), workspace.AuditEvents, workspace.Revision, workspace.UpdatedAt });
}
