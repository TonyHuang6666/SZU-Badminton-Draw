using System.Text.Json;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class MatchGraphTests
{
    internal static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static DrawResult Draw(int count = 8, int groups = 1, KnockoutGoal goal = KnockoutGoal.Champion,
        PlacementPlayoff placement = PlacementPlayoff.ThirdToEighth, CompetitionMode mode = CompetitionMode.SinglesKnockout,
        EventKind kind = EventKind.Singles)
    {
        var all = Enumerable.Range(1, count).Select(i => new DrawParticipant($"P{i}", PrimaryName: $"P{i}",
            PrimaryStudentId: $"ID{i}")).ToArray();
        var settings = new DrawSettings(mode, kind, groups, "graph-test", KnockoutGoal: goal, PlacementPlayoff: placement);
        var drawGroups = Enumerable.Range(1, groups).Select(n => new DrawGroup(n, all.Skip((n - 1) * count / groups).Take(count / groups).ToArray())).ToArray();
        return new(drawGroups, [], drawGroups, settings,
            new(DrawAlgorithmVersion.PerGroupPowerOfTwo, "graph-test", DateTimeOffset.UtcNow, "input", count, 0, groups));
    }

    [Fact]
    public void KnockoutKeepsExactQuarterfinalSemifinalAndPlacementBranches()
    {
        var graph = MatchGraphFactory.Create(ProjectId, Draw());
        Assert.Equal(12, graph.Matches.Count);
        var nodes = graph.Matches.ToDictionary(n => n.OriginalMatchId);
        Assert.Equal("P1", Assert.IsType<EntrantSource.Participant>(nodes["1"].SideA).DisplayName);
        Assert.Equal("P2", Assert.IsType<EntrantSource.Participant>(nodes["1"].SideB).DisplayName);
        void Edge(string target, string a, string b, bool loser)
        {
            Guid Id(EntrantSource s) => loser ? Assert.IsType<EntrantSource.LoserOf>(s).MatchId : Assert.IsType<EntrantSource.WinnerOf>(s).MatchId;
            Assert.Equal(nodes[a].Id, Id(nodes[target].SideA));
            Assert.Equal(nodes[b].Id, Id(nodes[target].SideB));
            Assert.Equal(new[] { nodes[a].Id, nodes[b].Id }, nodes[target].Dependencies);
        }
        Edge("5", "1", "2", false); Edge("6", "3", "4", false); Edge("7", "5", "6", false);
        Edge("8", "5", "6", true); Edge("9", "1", "2", true); Edge("10", "3", "4", true);
        Edge("11", "9", "10", false); Edge("12", "9", "10", true);
        Assert.Equal("3/4名赛", nodes["8"].DisplayName);
        Assert.Equal("5/6名赛", nodes["11"].DisplayName);
        Assert.Equal("7/8名赛", nodes["12"].DisplayName);
        Assert.True(nodes["7"].IsChampionshipFinal);
        Assert.True(nodes["9"].IsPlacementPlayoff);
        Assert.Equal(8, nodes["1"].KnockoutEntrantCount);
        Assert.Equal(12, graph.Matches.Select(n => n.Id).Distinct().Count());
        Assert.All(graph.Matches, n => Assert.All(n.Dependencies, d =>
            Assert.True(graph.Matches.Single(x => x.Id == d).Order < n.Order)));
    }

    [Fact]
    public void GraphSemanticHashIgnoresGenerationTimeButTracksIdentityAndDrawChanges()
    {
        var draw = Draw();
        var graph = MatchGraphFactory.Create(ProjectId, draw);
        var again = MatchGraphFactory.Create(ProjectId, draw with { Audit = draw.Audit with { GeneratedAt = draw.Audit.GeneratedAt.AddDays(1) } });
        Assert.Equal(graph.Revision, again.Revision);
        Assert.Equal(graph.Matches.Select(n => n.Id), again.Matches.Select(n => n.Id));
        var renamed = draw.Groups[0].Participants.Select((p, i) => i == 0 ? p with { PrimaryStudentId = "OTHER" } : p).ToArray();
        var group = new DrawGroup(1, renamed);
        Assert.NotEqual(graph.Revision, MatchGraphFactory.Create(ProjectId, draw with { Groups = [group], ByeGroups = [group] }).Revision);
        var other = MatchGraphFactory.Create(Guid.NewGuid(), draw);
        Assert.Empty(graph.Matches.Select(n => n.Id).Intersect(other.Matches.Select(n => n.Id)));
        var json = JsonSerializer.Serialize(graph);
        foreach (var forbidden in new[] { "DayLabel", "StartTime", "EndTime", "Court", "ScheduleSettings", "Days" })
            Assert.DoesNotContain($"\"{forbidden}\"", json);
        Assert.DoesNotContain(typeof(MatchNode).GetProperties(), p => p.PropertyType == typeof(TimeOnly) || p.PropertyType == typeof(DateOnly));
    }

    [Theory]
    [InlineData(3, 1)] [InlineData(5, 1)] [InlineData(6, 2)] [InlineData(7, 3)]
    public void NonPowerOfTwoPreservesPlayInsAndDirectEntriesWithoutByeMatches(int count, int playIns)
    {
        var draw = new DrawService().Generate(Enumerable.Range(1, count).Select(i => new DrawParticipant($"P{i}", IsSeed: i == 1, SeedRank: i == 1 ? 1 : null)).ToArray(),
            new(CompetitionMode.SinglesKnockout, EventKind.Singles, 1, "graph", KnockoutGoal: KnockoutGoal.Champion));
        var graph = MatchGraphFactory.Create(ProjectId, draw);
        Assert.Equal(count - 1, graph.Matches.Count);
        Assert.Equal(playIns, graph.Matches.Count(n => n.Phase == "首轮赛"));
        Assert.All(graph.Matches, n => Assert.True(n.IsPlayable));
        Assert.DoesNotContain(graph.Matches.Where(n => n.Phase == "首轮赛"), n =>
            (n.SideA as EntrantSource.Participant)?.DisplayName == "P1" || (n.SideB as EntrantSource.Participant)?.DisplayName == "P1");
        AssertProjectedTopology(graph);
    }

    [Theory]
    [InlineData(KnockoutGoal.Champion, 7)] [InlineData(KnockoutGoal.OneQualifierPerGroup, 6)]
    public void GroupFinalsFeedOnlyChampionTree(KnockoutGoal goal, int count)
    {
        var draw = Draw(groups: 2, goal: goal, placement: PlacementPlayoff.None);
        var graph = MatchGraphFactory.Create(ProjectId, draw);
        Assert.Equal(count, graph.Matches.Count);
        if (goal == KnockoutGoal.Champion)
        {
            var final = graph.Matches.Single(n => n.GroupNumber == 0);
            Assert.Equal(graph.Matches.Single(n => n.OriginalMatchId == "3").Id, Assert.IsType<EntrantSource.WinnerOf>(final.SideA).MatchId);
            Assert.Equal(graph.Matches.Single(n => n.OriginalMatchId == "6").Id, Assert.IsType<EntrantSource.WinnerOf>(final.SideB).MatchId);
            Assert.All(graph.Matches.Where(n => n.GroupNumber > 0), n => Assert.True(n.ForceBeforeTimingBoundary));
        }
        else Assert.DoesNotContain(graph.Matches, n => n.GroupNumber == 0);
        AssertProjectedTopology(graph);
    }

    [Theory]
    [InlineData(CompetitionMode.SinglesRoundRobin)] [InlineData(CompetitionMode.TeamRoundRobin)]
    public void OddRoundRobinContainsEachPairOnceAndNoIdleMatches(CompetitionMode mode)
    {
        var draw = Draw(5, mode: mode, kind: mode == CompetitionMode.TeamRoundRobin ? EventKind.Team : EventKind.Singles, placement: PlacementPlayoff.None);
        var graph = MatchGraphFactory.Create(ProjectId, draw);
        Assert.Equal(10, graph.Matches.Count);
        Assert.All(graph.Matches, n => { Assert.Empty(n.Dependencies); Assert.True(n.IsPlayable); });
        var pairs = graph.Matches.Select(n => string.Join("/", new[] { ((EntrantSource.Participant)n.SideA).DisplayName, ((EntrantSource.Participant)n.SideB).DisplayName }.Order())).Order().ToArray();
        Assert.Equal(new[] { "P1/P2", "P1/P3", "P1/P4", "P1/P5", "P2/P3", "P2/P4", "P2/P5", "P3/P4", "P3/P5", "P4/P5" }, pairs);
        if (mode == CompetitionMode.TeamRoundRobin)
            Assert.All(graph.Matches, n => { Assert.False(n.SameUnit); Assert.True(Assert.Single(((EntrantSource.Participant)n.SideA).Players).IsTeam); });
    }

    [Fact]
    public void DoublesUseRosterIdentityAndRetainBothStudentIds()
    {
        var draw = Draw(2, kind: EventKind.Doubles, placement: PlacementPlayoff.None);
        var group = new DrawGroup(1, draw.Groups[0].Participants.Select((p, i) => p with { PartnerName = $"Q{i}", PartnerStudentId = $"QID{i}" }).ToArray());
        draw = draw with { Groups = [group], ByeGroups = [group] };
        var side = Assert.IsType<EntrantSource.Participant>(Assert.Single(MatchGraphFactory.Create(ProjectId, draw).Matches).SideA);
        Assert.Equal(ProjectEntrantIdentity.Create(EventDiscipline.MenDoubles, group.Participants[0]).IdentityKey, side.IdentityKey);
        Assert.Equal(new[] { "ID1", "QID0" }, side.Players.Select(p => p.StudentId));
    }

    [Fact]
    public void TeamKnockoutRetainsTeamIdentityAndThreeGroupsStopAtQualifiers()
    {
        var teams = Enumerable.Range(1, 6).Select(i => new DrawParticipant($"Team{i}", TeamName: $"Team{i}")).ToArray();
        var draw = new DrawService().Generate(teams,
            new(CompetitionMode.TeamKnockout, EventKind.Team, 3, "teams", KnockoutGoal: KnockoutGoal.Champion));
        Assert.Equal(KnockoutGoal.OneQualifierPerGroup, draw.Settings.KnockoutGoal);
        var graph = MatchGraphFactory.Create(ProjectId, draw);
        Assert.Equal(3, graph.Matches.Count);
        Assert.DoesNotContain(graph.Matches, n => n.GroupNumber == 0);
        Assert.All(graph.Matches.SelectMany(n => new[] { n.SideA, n.SideB }), source =>
        {
            var participant = Assert.IsType<EntrantSource.Participant>(source);
            var player = Assert.Single(participant.Players);
            Assert.True(player.IsTeam);
            Assert.Equal(participant.DisplayName, player.Name);
        });
        AssertProjectedTopology(graph);
    }

    private static void AssertProjectedTopology(MatchGraph graph)
    {
        var result = Assert.IsType<TournamentSchedulingResult.Success>(
            new TournamentScheduler().Generate(TournamentSchedulerTestData.Request([graph])));
        var schedule = ScheduledMatchProjection.Build(graph, result.Schedule,
            new Dictionary<WorkspaceMatchKey, TournamentMatchResult>());
        foreach (var node in graph.Matches)
        {
            var projected = schedule.Single(match => match.MatchId == node.Id.ToString("D"));
            Assert.Equal(node.Order, projected.Order);
            Assert.Equal(node.DisplayName, projected.MatchName);
            Assert.Equal(node.Phase, projected.Phase);
            Assert.Equal(node.Note, projected.Note);
            Assert.Equal(node.Dependencies.Order(), projected.Dependencies
                .Select(dependency => Guid.Parse(dependency.SourceMatchId)).Order());
        }
    }
}
