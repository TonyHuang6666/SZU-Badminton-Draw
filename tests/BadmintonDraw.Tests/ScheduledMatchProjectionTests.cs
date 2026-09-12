using System.Text.Json;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class ScheduledMatchProjectionTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void DomainAndProjectionRejectExplicitByeNodesWithTheSameContract(bool includeByePlacement, bool byeOnSideA)
    {
        var workspace = TournamentWorkspaceRulesTests.Fixture(TournamentStage.ScheduleReady) with
            { Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult>() };
        var project = workspace.Projects[0];
        var final = project.MatchGraph!.Matches[0];
        var bye = final with { Id = Guid.NewGuid(), OriginalMatchId = "bye", DisplayName = "轮空节点",
            SideA = byeOnSideA ? new EntrantSource.Bye() : final.SideA,
            SideB = byeOnSideA ? final.SideA : new EntrantSource.Bye() };
        final = final with { Order = 2, SideA = new EntrantSource.WinnerOf(bye.Id), Dependencies = [bye.Id] };
        var graph = project.MatchGraph with { Matches = [bye, final] };
        project = project with { MatchGraph = graph };
        var placements = workspace.Schedule!.Placements.ToDictionary();
        if (includeByePlacement)
            placements.Add(bye.Id, new(bye.Id, "2026-09-13", new(10, 0), new(10, 30), "1"));
        workspace = workspace with { Projects = [project], Schedule = workspace.Schedule with { Placements = placements } };

        var domain = Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.ValidateProject(project));
        var savedWorkspace = Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.Validate(workspace));
        var projection = Assert.Throws<WorkspaceValidationException>(() => ScheduledMatchProjection.Build(graph, workspace.Schedule, workspace.Results));
        Assert.Equal("graph.bye", domain.Code);
        Assert.Equal(domain.Code, savedWorkspace.Code);
        Assert.Equal(domain.Code, projection.Code);
        Assert.Equal(domain.Message, projection.Message);
    }

    [Theory]
    [InlineData(3)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void FactoryDirectAdvancementGraphsAreValidWorkspacesAndProjectEveryDependency(int count)
    {
        var participants = Enumerable.Range(1, count).Select(i => new DrawParticipant($"P{i}", PrimaryStudentId: $"ID{i}")).ToArray();
        var draw = new DrawService().Generate(participants,
            new(CompetitionMode.SinglesKnockout, EventKind.Singles, 1, "direct-byes", KnockoutGoal: KnockoutGoal.Champion));
        var workspace = TournamentWorkspaceRulesTests.Fixture(TournamentStage.ScheduleReady) with
            { Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult>() };
        var project = workspace.Projects[0];
        var graph = MatchGraphFactory.Create(project.Id, draw);
        project = project with { Roster = new(participants, "roster.xlsx", "roster", []),
            Draw = new(draw, DateTimeOffset.UtcNow), MatchGraph = graph };
        workspace = workspace with { Projects = [project], Schedule = workspace.Schedule! with
        {
            Placements = graph.Matches.ToDictionary(n => n.Id, n => new MatchPlacement(n.Id, "2026-09-13",
                new TimeOnly(9, 0).AddMinutes((n.Order - 1) * 30), new TimeOnly(9, 0).AddMinutes(n.Order * 30), "1")),
            GraphRevisions = new Dictionary<Guid, string> { [project.Id] = graph.Revision }
        } };

        TournamentWorkspaceRules.Validate(workspace);
        var rows = ScheduledMatchProjection.Build(graph, workspace.Schedule, workspace.Results);
        Assert.Equal(count - 1, rows.Count);
        Assert.All(graph.Matches, node => Assert.True(node.IsPlayable));
        var rowIds = rows.Select(row => row.MatchId).ToHashSet();
        Assert.All(rows.SelectMany(row => row.Dependencies), dependency => Assert.Contains(dependency.SourceMatchId, rowIds));
    }

    private static TournamentSchedule Schedule(params MatchGraph[] graphs) => new(
        graphs.SelectMany(g => g.Matches).ToDictionary(n => n.Id, n => new MatchPlacement(n.Id, "比赛日", new(9, 0), new(9, 30), "A")),
        new([], null, 0, 20), new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []),
        graphs.ToDictionary(g => g.ProjectId, g => g.Revision), 1);

    private static TournamentMatchResult Result(MatchGraph graph, string localId,
        EntrantSource.Participant winner, EntrantSource.Participant loser) => new(
            new(graph.ProjectId, graph.Matches.Single(n => n.OriginalMatchId == localId).Id),
            winner, loser, "21:10", 30, DateTimeOffset.UtcNow);

    [Fact]
    public void ResultsResolveWinnerAndLoserRecursivelyWithoutChangingStoredGraph()
    {
        var graph = MatchGraphFactory.Create(MatchGraphTests.ProjectId, MatchGraphTests.Draw());
        var json = JsonSerializer.Serialize(graph);
        var results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult>();
        var participants = graph.Matches.Take(4).SelectMany(n => new[] { (EntrantSource.Participant)n.SideA, (EntrantSource.Participant)n.SideB }).ToArray();
        foreach (var i in Enumerable.Range(0, 4))
        {
            var r = Result(graph, (i + 1).ToString(), participants[2 * i], participants[2 * i + 1]);
            results.Add(r.Key, r);
        }
        var semi = Result(graph, "5", participants[0], participants[2]); results.Add(semi.Key, semi);
        var rows = ScheduledMatchProjection.Build(graph, Schedule(graph), results).ToDictionary(m => m.MatchId);
        var final = rows[graph.Matches.Single(n => n.OriginalMatchId == "7").Id.ToString("D")];
        Assert.Equal("P1", final.SideA);
        Assert.Equal("A组半决赛第2场胜者", final.SideB);
        Assert.Equal(new[] { "ID1" }, final.SideAPlayerIdentities.Select(p => p.StudentId));
        Assert.Equal(new[] { "ID5", "ID7" }, final.SideBPlayerIdentities.Select(p => p.StudentId));
        var bronze = rows[graph.Matches.Single(n => n.OriginalMatchId == "8").Id.ToString("D")];
        Assert.Equal("P3", bronze.SideA);
        Assert.Equal("A组半决赛第2场负者", bronze.SideB);
        Assert.Equal(ScheduleMatchDependencyOutcome.Loser, bronze.Dependencies[0].Outcome);
        Assert.Equal(graph.Matches.Single(n => n.OriginalMatchId == "5").Id.ToString("D"), bronze.Dependencies[0].SourceMatchId);
        Assert.Equal(json, JsonSerializer.Serialize(graph));
    }

    [Fact]
    public void ProjectQualifiedResultsCannotLeakToAnIdenticalOtherProject()
    {
        var first = MatchGraphFactory.Create(MatchGraphTests.ProjectId, MatchGraphTests.Draw(4, placement: PlacementPlayoff.None));
        var second = MatchGraphFactory.Create(Guid.NewGuid(), MatchGraphTests.Draw(4, placement: PlacementPlayoff.None));
        var opening = first.Matches[0];
        var r = Result(first, "1", (EntrantSource.Participant)opening.SideA, (EntrantSource.Participant)opening.SideB);
        var rows = ScheduledMatchProjection.Build(new[] { first, second }, Schedule(first, second), new Dictionary<WorkspaceMatchKey, TournamentMatchResult> { [r.Key] = r });
        Assert.Equal("P1", rows.Single(m => m.MatchId == first.Matches[2].Id.ToString("D")).SideA);
        Assert.Equal("A组半决赛第1场胜者", rows.Single(m => m.MatchId == second.Matches[2].Id.ToString("D")).SideA);
        Assert.Equal(6, rows.Select(m => m.MatchId).Distinct().Count());
    }

    [Theory]
    [InlineData("stale")] [InlineData("missing")] [InlineData("foreign")] [InlineData("key")]
    public void RejectsStaleOrMismatchedPlacements(string invalid)
    {
        var graph = MatchGraphFactory.Create(MatchGraphTests.ProjectId, MatchGraphTests.Draw(2, placement: PlacementPlayoff.None));
        var schedule = Schedule(graph);
        if (invalid == "stale") schedule = schedule with { GraphRevisions = new Dictionary<Guid, string> { [graph.ProjectId] = "old" } };
        if (invalid == "missing") schedule = schedule with { Placements = new Dictionary<Guid, MatchPlacement>() };
        if (invalid == "foreign") schedule = schedule with { Placements = schedule.Placements.Append(new(Guid.NewGuid(), new(Guid.NewGuid(), "比赛日", new(9, 0), new(9, 30), "A"))).ToDictionary() };
        if (invalid == "key") schedule = schedule with { Placements = new Dictionary<Guid, MatchPlacement> { [graph.Matches[0].Id] = schedule.Placements.Values.Single() with { MatchId = Guid.NewGuid() } } };
        Assert.Throws<WorkspaceValidationException>(() => ScheduledMatchProjection.Build(graph, schedule, new Dictionary<WorkspaceMatchKey, TournamentMatchResult>()));
    }

    [Fact]
    public void RejectsImpossibleResultsAndCyclesBeforeResolving()
    {
        var graph = MatchGraphFactory.Create(MatchGraphTests.ProjectId, MatchGraphTests.Draw(4, placement: PlacementPlayoff.None));
        var a = (EntrantSource.Participant)graph.Matches[0].SideA;
        var b = (EntrantSource.Participant)graph.Matches[1].SideB;
        var r = Result(graph, "1", a, b);
        Assert.Throws<WorkspaceValidationException>(() => ScheduledMatchProjection.Build(graph, Schedule(graph), new Dictionary<WorkspaceMatchKey, TournamentMatchResult> { [r.Key] = r }));
        var cyclic = graph with { Matches = graph.Matches.Select((n, i) => i == 0 ? n with { SideA = new EntrantSource.WinnerOf(n.Id), Dependencies = [n.Id] } : n).ToArray() };
        Assert.Throws<WorkspaceValidationException>(() => ScheduledMatchProjection.Build(cyclic, Schedule(cyclic), new Dictionary<WorkspaceMatchKey, TournamentMatchResult>()));
    }
}
