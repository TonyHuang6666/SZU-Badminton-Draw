using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using Xunit;

namespace BadmintonDraw.Tests;

public class TournamentWorkspaceRulesTests
{
    [Fact]
    public void TeamWorkspaceRejectsIndividualProject() => Assert.Throws<WorkspaceValidationException>(() =>
        TournamentWorkspace.Create("校长杯", TournamentKind.Team, TournamentPurpose.FullTournament,
            [TournamentProject.Create(EventDiscipline.MenSingles, CompetitionMode.SinglesKnockout, 0)]));

    [Fact]
    public void IndividualWorkspaceRejectsDuplicateDisciplines() => Assert.Throws<WorkspaceValidationException>(() =>
        TournamentWorkspace.Create("新生杯", TournamentKind.Individual, TournamentPurpose.PublicDrawOnly,
            [TournamentProject.Create(EventDiscipline.MixedDoubles, CompetitionMode.SinglesKnockout, 0),
             TournamentProject.Create(EventDiscipline.MixedDoubles, CompetitionMode.SinglesKnockout, 1)]));

    public static IEnumerable<object[]> TransitionPairs()
    {
        foreach (var from in Enum.GetValues<TournamentStage>())
        foreach (var to in Enum.GetValues<TournamentStage>())
            yield return [from, to];
    }

    [Theory, MemberData(nameof(TransitionPairs))]
    public void TransitionMatrixAllowsOnlyAdjacentForwardStages(TournamentStage from, TournamentStage to)
    {
        var workspace = Fixture(from);
        if ((int)to == (int)from + 1)
        {
            var next = TournamentWorkspaceRules.Transition(workspace, to);
            Assert.Equal(to, next.Stage);
            Assert.Equal(workspace.Id, next.Id);
        }
        else Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.Transition(workspace, to));
    }

    [Fact]
    public void StageGuardsRequireRealRosterGraphScheduleAndResults()
    {
        var draft = TournamentWorkspace.Create("杯赛", TournamentKind.Individual, TournamentPurpose.FullTournament,
            [TournamentProject.Create(EventDiscipline.MenSingles, CompetitionMode.SinglesKnockout, 0)]);
        Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.Transition(draft, TournamentStage.RostersReady));
        var ready = Fixture(TournamentStage.RostersReady);
        Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.Transition(ready with
            { Projects = [ready.Projects[0] with { MatchGraph = null, Draw = null }] }, TournamentStage.DrawsConfirmed));
        var confirmed = Fixture(TournamentStage.DrawsConfirmed) with { Schedule = null, Resources = null };
        Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.Transition(confirmed, TournamentStage.ScheduleReady));
        var scheduled = Fixture(TournamentStage.ScheduleReady) with { Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult>() };
        Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.Transition(scheduled, TournamentStage.InProgress));
        var active = Fixture(TournamentStage.InProgress);
        var graph = active.Projects[0].MatchGraph!;
        var second = graph.Matches[0] with { Id = Guid.NewGuid(), OriginalMatchId = "second", Order = 2 };
        var project = active.Projects[0] with { MatchGraph = graph with { Matches = [graph.Matches[0], second] } };
        var placements = active.Schedule!.Placements.ToDictionary();
        placements.Add(second.Id, new(second.Id, "2026-09-13", new(10, 0), new(10, 30), "1"));
        active = active with { Projects = [project], Schedule = active.Schedule with { Placements = placements } };
        Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.Transition(active, TournamentStage.Completed));
    }

    [Fact]
    public void DrawOnlyStopsAndExplicitUpgradePreservesConfirmedDraws()
    {
        var workspace = Fixture(TournamentStage.DrawsConfirmed) with
            { Purpose = TournamentPurpose.PublicDrawOnly, Schedule = null, Resources = null, Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult>() };
        Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.Transition(workspace, TournamentStage.ScheduleReady));
        var upgraded = TournamentWorkspaceRules.UpgradeToFullTournament(workspace);
        Assert.Equal(TournamentPurpose.FullTournament, upgraded.Purpose);
        Assert.Equal(workspace.Stage, upgraded.Stage);
        Assert.Equal(workspace.Projects[0].Draw, upgraded.Projects[0].Draw);
        Assert.Single(upgraded.AuditEvents);
    }

    [Fact]
    public void ExplicitReopenInvalidatesDownstreamAndRequiresNoResults()
    {
        var workspace = Fixture(TournamentStage.ScheduleReady) with { Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult>() };
        var reopened = TournamentWorkspaceRules.ReopenDraw(workspace, workspace.Projects[0].Id, "名单更正");
        Assert.Equal(TournamentStage.RostersReady, reopened.Stage);
        Assert.Null(reopened.Schedule);
        Assert.Null(reopened.Projects[0].MatchGraph);
        Assert.Null(reopened.Projects[0].Draw);
        Assert.Null(reopened.Resources);
        Assert.Single(reopened.AuditEvents);
        Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.ReopenDraw(Fixture(TournamentStage.InProgress), workspace.Projects[0].Id, "更正"));
    }

    [Fact]
    public void SnapshotsDoNotAliasNestedCallerCollections()
    {
        var participants = new List<DrawParticipant> { new("甲"), new("乙") };
        var warnings = new List<ProjectRosterWarning> { new("warning", "提示") };
        var groups = new List<DrawGroup> { new(1, participants) };
        var baseline = Fixture(TournamentStage.RostersReady);
        var project = baseline.Projects[0] with { Roster = new(participants, "名单.xlsx", "hash", warnings),
            Draw = new(baseline.Projects[0].Draw!.Result with { Groups = groups }, null), MatchGraph = null };
        var projects = new List<TournamentProject> { project };
        var workspace = baseline with { Projects = projects };
        participants.Clear(); warnings.Clear(); groups.Clear(); projects.Clear();
        Assert.Equal(2, workspace.Projects[0].Roster!.Participants.Count);
        Assert.Single(workspace.Projects[0].Roster!.Warnings);
        Assert.Equal(2, workspace.Projects[0].Draw!.Result.Groups[0].Participants.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<DrawParticipant>)workspace.Projects[0].Roster!.Participants).Clear());
    }

    [Fact]
    public void InvalidIdentityEnumsGraphAndStalePlacementsAreRejected()
    {
        var workspace = Fixture(TournamentStage.Completed);
        foreach (var invalid in new[] {
            workspace with { Id = Guid.Empty }, workspace with { Name = " " }, workspace with { Kind = (TournamentKind)99 },
            workspace with { Stage = (TournamentStage)99 }, workspace with { Purpose = (TournamentPurpose)99 },
            workspace with { Projects = [] }, workspace with { Revision = -1 },
            workspace with { Schedule = workspace.Schedule! with { GraphRevisions = new Dictionary<Guid,string>() } },
            workspace with { Schedule = workspace.Schedule! with { Placements = new Dictionary<Guid,MatchPlacement>() } },
            workspace with { Projects = [workspace.Projects[0] with { CompetitionMode = CompetitionMode.TeamKnockout }] },
            workspace with { Projects = [workspace.Projects[0] with { MatchGraph = workspace.Projects[0].MatchGraph! with { Matches = [workspace.Projects[0].MatchGraph!.Matches[0] with { SideA = new EntrantSource.WinnerOf(Guid.NewGuid()) }] } }] }
        }) Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.Validate(invalid));
    }

    internal static TournamentWorkspace Fixture(TournamentStage stage)
    {
        var project = TournamentProject.Create(EventDiscipline.MenSingles, CompetitionMode.SinglesKnockout, 0);
        var a = new EntrantSource.Participant("a", "甲", [new("甲", "001")]);
        var b = new EntrantSource.Participant("b", "乙", [new("乙", "002")]);
        var match = new MatchNode(Guid.NewGuid(), project.Id, "final", 1, 1, "决赛", "决赛", a, b, 30, []);
        var settings = new DrawSettings(CompetitionMode.SinglesKnockout, EventKind.Singles, 1, "seed");
        var draw = new DrawResult([new(1, [new("甲"), new("乙")])], [], [], settings,
            new(DrawAlgorithmVersion.PerGroupPowerOfTwo, "seed", DateTimeOffset.UtcNow, "hash", 2, 0, 1));
        project = project with { Roster = new([new("甲", PrimaryStudentId: "001"), new("乙", PrimaryStudentId: "002")], "名单.xlsx", "hash", []),
            Draw = new(draw, DateTimeOffset.UtcNow), MatchGraph = new(project.Id, "graph-v1", [match]) };
        var resources = new TournamentResourcePlan([new(new(2026, 9, 13), new(9, 0), new(18, 0), ["1"])], 1, 15, 6);
        var key = new WorkspaceMatchKey(project.Id, match.Id);
        var workspace = TournamentWorkspace.Create("杯赛", TournamentKind.Individual, TournamentPurpose.FullTournament, [project with { Draw = null, MatchGraph = null }]) with
        { Stage = stage, Resources = resources, Schedule = new(new Dictionary<Guid, MatchPlacement>
            { [match.Id] = new(match.Id, "2026-09-13", new(9, 0), new(9, 30), "1") }, resources,
            new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []), new Dictionary<Guid,string> { [project.Id] = "graph-v1" }, 0),
          Projects = [project],
          Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult> { [key] = new(key, a, b, "21-10", 30, DateTimeOffset.UtcNow) } };
        if (stage < TournamentStage.ScheduleReady) workspace = workspace with { Results = new Dictionary<WorkspaceMatchKey,TournamentMatchResult>() };
        if (stage < TournamentStage.DrawsConfirmed) workspace = workspace with { Resources = null, Schedule = null };
        if (stage == TournamentStage.Draft) workspace = workspace with { Projects = [project with { Draw = null, MatchGraph = null }] };
        return workspace;
    }

    [Fact]
    public void SerializedInvalidStageCannotBypassValidation()
    {
        var workspace = TournamentWorkspace.Create("杯赛", TournamentKind.Individual, TournamentPurpose.FullTournament,
            [TournamentProject.Create(EventDiscipline.MenSingles, CompetitionMode.SinglesKnockout, 0)]);
        var json = System.Text.Json.JsonSerializer.Serialize(workspace with { Stage = TournamentStage.Completed });
        Assert.Throws<WorkspaceValidationException>(() => System.Text.Json.JsonSerializer.Deserialize<TournamentWorkspace>(json));
    }

    [Fact]
    public void DraftCannotCarryScheduleOrResults()
    {
        Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.Validate(Fixture(TournamentStage.Completed) with { Stage = TournamentStage.Draft }));
    }

    [Fact]
    public void ResultCannotSubstitutePlayerIdentityOrUnknownMatch()
    {
        var workspace = Fixture(TournamentStage.Completed);
        var entry = workspace.Results.Single();
        var falseWinner = entry.Value.Winner with { Players = [new("冒名", "999")] };
        Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.Validate(workspace with
            { Results = new Dictionary<WorkspaceMatchKey,TournamentMatchResult> { [entry.Key] = entry.Value with { Winner = falseWinner } } }));
        Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.Validate(workspace with
            { Results = new Dictionary<WorkspaceMatchKey,TournamentMatchResult> { [new(Guid.NewGuid(), entry.Key.MatchId)] = entry.Value } }));
    }

    [Fact]
    public void ResourceGraphAndResultSnapshotsRejectCallerMutation()
    {
        var workspace = Fixture(TournamentStage.Completed);
        var courts = new List<string> { "1" };
        var blocked = new List<string> { "1" };
        var days = new List<ScheduleDaySettings> { new(new(2026,9,13), new(9,0), new(18,0), courts,
            [], [new(new(12,0),new(13,0),blocked)]) };
        var resources = new TournamentResourcePlan(days, 1, 15, 6);
        var placements = workspace.Schedule!.Placements.ToDictionary();
        var revisions = workspace.Schedule.GraphRevisions.ToDictionary();
        var results = workspace.Results.ToDictionary();
        var snapshot = workspace with { Resources = resources, Results = results,
            Schedule = workspace.Schedule with { Resources = resources, Placements = placements, GraphRevisions = revisions } };
        courts.Clear(); blocked.Clear(); days.Clear(); placements.Clear(); revisions.Clear(); results.Clear();
        TournamentWorkspaceRules.Validate(snapshot);
        Assert.Single(snapshot.Resources!.Days[0].Courts);
        Assert.Single(snapshot.Resources.Days[0].UnavailableCourtWindows![0].Courts);
        Assert.Single(snapshot.Results);
    }

    [Fact]
    public void GraphRoundTripRetainsBothDoublesPartnersAndSourceKinds()
    {
        var players = new List<CrossEventPlayerIdentity> { new("甲", "001"), new("乙", "002") };
        var participant = new EntrantSource.Participant("pair", "甲/乙", players);
        var graph = Fixture(TournamentStage.Completed).Projects[0].MatchGraph!;
        graph = graph with { Matches = [graph.Matches[0] with { SideA = participant }] };
        players.Clear();
        var json = System.Text.Json.JsonSerializer.Serialize(graph);
        var restored = System.Text.Json.JsonSerializer.Deserialize<MatchGraph>(json)!;
        var side = Assert.IsType<EntrantSource.Participant>(restored.Matches[0].SideA);
        Assert.Equal(new[] { "001", "002" }, side.Players.Select(p => p.StudentId));
    }

    [Fact]
    public void ReopenPreservesHistoricalMatchAuditAfterGraphIsRemoved()
    {
        var workspace = Fixture(TournamentStage.DrawsConfirmed);
        workspace = workspace with { AuditEvents = [new(Guid.NewGuid(), "MatchMoved", DateTimeOffset.UtcNow,
            workspace.Projects[0].Id, workspace.Projects[0].MatchGraph!.Matches[0].Id)] };
        var reopened = TournamentWorkspaceRules.ReopenDraw(workspace, workspace.Projects[0].Id, "重新抽签");
        Assert.Equal(2, reopened.AuditEvents.Count);
    }

    [Fact]
    public void ReopenClearsOnlySelectedProjectAndAllGlobalSchedulingInputs()
    {
        var workspace = Fixture(TournamentStage.DrawsConfirmed) with { Schedule = null };
        var other = workspace.Projects[0] with { Id = Guid.NewGuid(), Discipline = EventDiscipline.WomenSingles,
            Draw = null, MatchGraph = null };
        workspace = workspace with { Stage = TournamentStage.RostersReady, Projects = [workspace.Projects[0], other] };
        var reopened = TournamentWorkspaceRules.ReopenDraw(workspace, workspace.Projects[0].Id, "更正");
        Assert.Null(reopened.Projects[0].Draw);
        Assert.Null(reopened.Projects[0].MatchGraph);
        Assert.Null(reopened.Schedule);
        Assert.Null(reopened.Resources);
        Assert.Equal(other, reopened.Projects[1]);
    }

    [Fact]
    public void PolicyUsesAutoSchedulingStrategyWithoutNumericCrossEventCast()
    {
        var policy = System.Text.Json.JsonSerializer.Deserialize<TournamentSchedulingPolicy>(
            "{\"Strategy\":1,\"DayLoadTargets\":[],\"SynchronizeStageWaves\":false,\"StageWaveTargets\":[],\"FinalDayRules\":[]}")!;
        Assert.Equal("BalancedRelaxed", policy.Strategy.ToString());
        Assert.IsType<ScheduleAutoSchedulingStrategy>(policy.Strategy);
    }

    [Fact]
    public void RosterAllowsSameNamesWithDifferentIdsButRejectsDuplicateIdentities()
    {
        var project = TournamentProject.Create(EventDiscipline.MenSingles, CompetitionMode.SinglesKnockout, 0) with
            { Roster = new([new("同名", PrimaryStudentId: "001"), new("同名", PrimaryStudentId: "002")], "名单.xlsx", "hash", []) };
        TournamentWorkspaceRules.ValidateProject(project);
        Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.ValidateProject(project with
            { Roster = project.Roster with { Participants = [new("名字甲", PrimaryStudentId: "001"), new("名字乙", PrimaryStudentId: "001")] } }));
    }

    [Fact]
    public void DoublesGraphRequiresBothRosterPartnersAndTheirIds()
    {
        var project = Fixture(TournamentStage.RostersReady).Projects[0];
        var pair = new DrawParticipant("甲/乙", PrimaryName: "甲", PartnerName: "乙", PrimaryStudentId: "001", PartnerStudentId: "002");
        var opponents = new DrawParticipant("丙/丁", PrimaryName: "丙", PartnerName: "丁", PrimaryStudentId: "003", PartnerStudentId: "004");
        var a = new EntrantSource.Participant("pair-a", "甲/乙", [new("甲", "001"), new("乙", "002")]);
        var b = new EntrantSource.Participant("pair-b", "丙/丁", [new("丙", "003"), new("丁", "004")]);
        project = project with { Discipline = EventDiscipline.MenDoubles, Roster = new([pair, opponents], "名单.xlsx", "hash", []),
            Draw = project.Draw! with { Result = project.Draw.Result with { Settings = project.Draw.Result.Settings with { EventKind = EventKind.Doubles } } },
            MatchGraph = project.MatchGraph! with { Matches = [project.MatchGraph.Matches[0] with { SideA = a, SideB = b }] } };
        TournamentWorkspaceRules.ValidateProject(project);
        foreach (var invalid in new[] { a with { Players = [new("甲", "001")] },
            a with { Players = [new("甲", "001"), new("乙", "999")] }, a with { Players = [new("甲", "001"), new("乙")] } })
            Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.ValidateProject(project with
                { MatchGraph = project.MatchGraph with { Matches = [project.MatchGraph.Matches[0] with { SideA = invalid }] } }));
        Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.ValidateProject(project with
            { Roster = project.Roster with { Participants = [pair, pair with { DisplayName = "乙/甲", PrimaryName = "乙", PartnerName = "甲", PrimaryStudentId = "002", PartnerStudentId = "001" }] } }));
    }

    [Fact]
    public void SinglesGraphRejectsForeignPlayerEvenWhenDisplayNameMatches()
    {
        var project = Fixture(TournamentStage.RostersReady).Projects[0];
        var node = project.MatchGraph!.Matches[0];
        Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.ValidateProject(project with
            { MatchGraph = project.MatchGraph with { Matches = [node with { SideA = new EntrantSource.Participant("a", "甲", [new("甲", "999")]) }] } }));
    }
}
