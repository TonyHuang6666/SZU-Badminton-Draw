using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class TournamentSchedulerTests
{
    [Theory]
    [InlineData(1)] [InlineData(3)]
    public void FreshGraphsUseOneGlobalScheduleWithAllRevisions(int projectCount)
    {
        var graphs = Enumerable.Range(1, projectCount).Select(i => MatchGraphFactory.Create(
            TournamentSchedulerTestData.Id(i), MatchGraphTests.Draw(4, placement: PlacementPlayoff.None))).ToArray();
        var request = TournamentSchedulerTestData.Request(graphs);
        var result = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Null(request.BaselinePlacements);
        Assert.Equal(projectCount * 3, result.Schedule.Placements.Count);
        Assert.Equal(projectCount, result.GraphRevisions.Count);
        Assert.Equal(ScheduleAutoSchedulingStrategy.BalancedRelaxed, result.Schedule.Policy.Strategy);
        Assert.Empty(new TournamentPlacementValidator(request).ValidateSchedule(result.Schedule.Placements).Violations);
        Assert.Equal(0, result.Quality.HardConstraintCount);
    }

    [Fact]
    public void ProjectTimingUsesStructuralBoundaryAndPlacementAlwaysUsesDefault()
    {
        var graph = MatchGraphFactory.Create(TournamentSchedulerTestData.Id(1), MatchGraphTests.Draw());
        var other = MatchGraphFactory.Create(TournamentSchedulerTestData.Id(2), MatchGraphTests.Draw(2, placement: PlacementPlayoff.None));
        var request = TournamentSchedulerTestData.Request([graph, other]);
        request = request with { Policy = request.Policy with { ProjectTimings = new Dictionary<Guid, ProjectMatchTiming>
            { [graph.ProjectId] = new(40, 4, 20), [other.ProjectId] = new(15) } } };
        var result = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.All(graph.Matches, node => Assert.Equal(node.IsPlacementPlayoff || node.KnockoutEntrantCount <= 4 ? 40 : 20,
            (result.Schedule.Placements[node.Id].EndTime - result.Schedule.Placements[node.Id].StartTime).TotalMinutes));
        var otherPlacement = result.Schedule.Placements[other.Matches[0].Id];
        Assert.Equal(15, (otherPlacement.EndTime - otherPlacement.StartTime).TotalMinutes);
        Assert.All(graph.Matches, n => Assert.Equal(30, n.ExpectedDurationMinutes));
    }

    [Fact]
    public void CourtsAndTimeVaryingRefereesAreGlobalHardConstraints()
    {
        var graph = TournamentSchedulerTestData.Independent(4);
        var request = TournamentSchedulerTestData.Request([graph], endHour: 12) with
        { Resources = new([new(TournamentSchedulerTestData.Date, new(9, 0), new(12, 0), ["A", "B"],
            [new(new(9, 0), new(10, 0), 0), new(new(10, 0), new(11, 0), 1)],
            [new(new(9, 0), new(10, 30), ["A"])])], 2, 0, 6) };
        var result = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.All(result.Schedule.Placements.Values, p => Assert.True(p.StartTime >= new TimeOnly(10, 0)));
        Assert.DoesNotContain(result.Schedule.Placements.Values, p => p.Court == "A" && p.StartTime < new TimeOnly(10, 30));
        Assert.Empty(new TournamentPlacementValidator(request).ValidateSchedule(result.Schedule.Placements).Violations);
    }

    [Fact]
    public void SoftTargetsDoNotRejectUsablePhysicalCapacity()
    {
        var graph = TournamentSchedulerTestData.Independent(4);
        var request = TournamentSchedulerTestData.Request([graph], endHour: 10) with
        { Policy = new(ScheduleAutoSchedulingStrategy.Custom, [new(TournamentSchedulerTestData.Day, .05, .05)], false, [], []) };
        var result = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Equal(4, result.Schedule.Placements.Count);
    }

    [Fact]
    public void ChampionshipStartsAfterAllPlacementPlayoffsInItsOwnProject()
    {
        var graph = MatchGraphFactory.Create(TournamentSchedulerTestData.Id(1), MatchGraphTests.Draw());
        var request = TournamentSchedulerTestData.Request([graph]);
        var result = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        var final = result.Schedule.Placements[graph.Matches.Single(n => n.IsChampionshipFinal).Id];
        Assert.All(graph.Matches.Where(n => n.IsPlacementPlayoff), n =>
            Assert.True(result.Schedule.Placements[n.Id].StartTime <= final.StartTime));
    }

    [Fact]
    public void BalancedUsesBothDaysAndSpreadsWhileCompactFinishesEarly()
    {
        var graph = TournamentSchedulerTestData.Independent(8);
        var request = TournamentSchedulerTestData.Request([graph], 13);
        request = request with { Resources = request.Resources with { Days = [request.Resources.Days[0], request.Resources.Days[0] with { Date = TournamentSchedulerTestData.Date.AddDays(1) }] } };
        var compact = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request with { Policy = request.Policy with { Strategy = ScheduleAutoSchedulingStrategy.Compact } }));
        var balanced = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Single(compact.Schedule.Placements.Values.Select(p => p.DayLabel).Distinct());
        Assert.Equal(2, balanced.Schedule.Placements.Values.Select(p => p.DayLabel).Distinct().Count());
        Assert.True(balanced.Schedule.Placements.Values.Max(p => p.StartTime) > compact.Schedule.Placements.Values.Max(p => p.StartTime));
    }

    [Fact]
    public void FinalDayPreferenceUsesProjectIdAfterDisplayRename()
    {
        var first = TournamentSchedulerTestData.Node(1, TournamentSchedulerTestData.Player("A"), TournamentSchedulerTestData.Player("B")) with { IsChampionshipFinal = true };
        var other = TournamentSchedulerTestData.Node(2, TournamentSchedulerTestData.Player("C"), TournamentSchedulerTestData.Player("D"), project: 2) with { IsChampionshipFinal = true };
        var request = TournamentSchedulerTestData.Request([new(first.ProjectId, "v1", [first]), new(other.ProjectId, "v1", [other])], 11);
        request = request with
        {
            Resources = request.Resources with { Days = [request.Resources.Days[0], request.Resources.Days[0] with { Date = TournamentSchedulerTestData.Date.AddDays(1) }] },
            ProjectNames = new Dictionary<Guid, string> { [first.ProjectId] = "renamed", [other.ProjectId] = "renamed" },
            Policy = request.Policy with { Strategy = ScheduleAutoSchedulingStrategy.Custom, FinalDayRules =
                [new(first.ProjectId, TournamentFinalDayMatchCategory.Final, TournamentFinalDayPreference.StronglyPreferFinalDay),
                 new(other.ProjectId, TournamentFinalDayMatchCategory.Final, TournamentFinalDayPreference.AvoidFinalDay)] }
        };
        var result = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Equal(request.Resources.Days[1].DayLabel, result.Schedule.Placements[first.Id].DayLabel);
        Assert.Equal(request.Resources.Days[0].DayLabel, result.Schedule.Placements[other.Id].DayLabel);
    }

    [Fact]
    public void UnlockedBaselineCanBeRegeneratedAfterDatesCourtsAndDurationChange()
    {
        var graph = TournamentSchedulerTestData.Independent(1);
        var request = TournamentSchedulerTestData.Request([graph], 11);
        var old = new Dictionary<Guid, MatchPlacement> { [graph.Matches[0].Id] = TournamentSchedulerTestData.Place(graph.Matches[0], 9) };
        request = request with
        {
            BaselinePlacements = old,
            Resources = request.Resources with { Days = [new(TournamentSchedulerTestData.Date.AddDays(2), new(10, 0), new(12, 0), ["NEW"])] },
            Policy = request.Policy with { ProjectTimings = new Dictionary<Guid, ProjectMatchTiming> { [graph.ProjectId] = new(45) } }
        };
        var result = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        var placed = Assert.Single(result.Schedule.Placements.Values);
        Assert.Equal("NEW", placed.Court);
        Assert.Equal(request.Resources.Days[0].DayLabel, placed.DayLabel);
        Assert.Equal(45, (placed.EndTime - placed.StartTime).TotalMinutes);
        Assert.Equal(1, result.Quality.MovedMatchCount);
        Assert.Equal(1, result.Quality.CrossDayMoveCount);
        Assert.Equal("A", old.Values.Single().Court);
        Assert.Equal(TournamentSchedulerTestData.Day, old.Values.Single().DayLabel);
    }

    [Fact]
    public void ValidRealBaselineRemainsStableAndCompletedMatchesAreAutomaticallyLocked()
    {
        var graph = TournamentSchedulerTestData.Independent(2);
        var request = TournamentSchedulerTestData.Request([graph], 12);
        var prior = graph.Matches.ToDictionary(n => n.Id, n => TournamentSchedulerTestData.Place(n, 11, court: n.Order == 1 ? "A" : "B"));
        var completed = graph.Matches[0];
        var key = new WorkspaceMatchKey(completed.ProjectId, completed.Id);
        request = request with { BaselinePlacements = prior, Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult>
            { [key] = new(key, (EntrantSource.Participant)completed.SideA, (EntrantSource.Participant)completed.SideB, "21-0", 30, DateTimeOffset.UtcNow) } };
        var result = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Equal(prior[completed.Id], result.Schedule.Placements[completed.Id]);
        Assert.Equal(0, result.Quality.MovedMatchCount);
        Assert.Contains(new TournamentPlacementValidator(request).ValidatePlacement(TournamentSchedulerTestData.Place(completed, 10),
            prior.Where(p => p.Key != completed.Id).ToDictionary()).Violations, v => v.Code == SchedulingConstraintCode.LockedPlacement);
    }

    [Fact]
    public void PolicyAndRequestCaptureCollectionsAndRoundTripV5TimingRules()
    {
        var graph = TournamentSchedulerTestData.Independent(1);
        var timings = new Dictionary<Guid, ProjectMatchTiming> { [graph.ProjectId] = new(40, 4, 20) };
        var request = TournamentSchedulerTestData.Request([graph]);
        request = request with { Policy = request.Policy with { ProjectTimings = timings,
            FinalDayRules = [new(graph.ProjectId, TournamentFinalDayMatchCategory.Final, TournamentFinalDayPreference.PreferFinalDay)] } };
        timings.Clear();
        var policy = System.Text.Json.JsonSerializer.Deserialize<TournamentSchedulingPolicy>(System.Text.Json.JsonSerializer.Serialize(request.Policy))!;
        Assert.Equal(new ProjectMatchTiming(40, 4, 20), policy.ProjectTimings[graph.ProjectId]);
        Assert.Equal(graph.ProjectId, Assert.Single(policy.FinalDayRules).ProjectId);
        var placements = new Dictionary<Guid, MatchPlacement> { [graph.Matches[0].Id] = TournamentSchedulerTestData.Place(graph.Matches[0], 9) };
        request = request with { BaselinePlacements = placements };
        placements.Clear();
        Assert.Single(request.BaselinePlacements!);
    }

    [Fact]
    public void StrongFinalDayPreferenceRemainsSoftWhenOnlyEarlierDayHasCapacity()
    {
        var graph = TournamentSchedulerTestData.Independent(1);
        graph = graph with { Matches = [graph.Matches[0] with { IsChampionshipFinal = true }] };
        var request = TournamentSchedulerTestData.Request([graph], 10);
        request = request with
        {
            Resources = request.Resources with { Days = [request.Resources.Days[0], request.Resources.Days[0] with
                { Date = TournamentSchedulerTestData.Date.AddDays(1), RefereeCapacityWindows = [new(new(9, 0), new(10, 0), 0)] }] },
            Policy = request.Policy with { FinalDayRules = [new(graph.ProjectId, TournamentFinalDayMatchCategory.Final, TournamentFinalDayPreference.StronglyPreferFinalDay)] }
        };
        var result = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Equal(TournamentSchedulerTestData.Day, Assert.Single(result.Schedule.Placements.Values).DayLabel);
    }

    [Fact]
    public void BronzePreferenceUsesStructuralLoserSourcesAfterAllLabelsChange()
    {
        var graph = MatchGraphFactory.Create(TournamentSchedulerTestData.Id(1), MatchGraphTests.Draw(4, placement: PlacementPlayoff.ThirdToEighth));
        var bronzeId = graph.Matches.Single(n => n.IsPlacementPlayoff).Id;
        graph = graph with { Matches = graph.Matches.Select(n => n with { Phase = "改名", DisplayName = "改名" }).ToArray() };
        var request = TournamentSchedulerTestData.Request([graph], 13);
        request = request with
        {
            Resources = request.Resources with { Days = [request.Resources.Days[0], request.Resources.Days[0] with { Date = TournamentSchedulerTestData.Date.AddDays(1) }] },
            Policy = request.Policy with { Strategy = ScheduleAutoSchedulingStrategy.Compact,
                FinalDayRules = [new(graph.ProjectId, TournamentFinalDayMatchCategory.Bronze, TournamentFinalDayPreference.StronglyPreferFinalDay)] }
        };
        var result = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Equal(request.Resources.Days[1].DayLabel, result.Schedule.Placements[bronzeId].DayLabel);
    }

    [Theory]
    [InlineData(CompetitionMode.SinglesRoundRobin, EventKind.Singles)]
    [InlineData(CompetitionMode.TeamRoundRobin, EventKind.Team)]
    public void RealRoundRobinGraphsPreserveSameUnitPriorityAndTeamExemption(CompetitionMode mode, EventKind kind)
    {
        var participants = new[] { new DrawParticipant("甲", TeamName: "单位一"), new DrawParticipant("乙", TeamName: "单位一"),
            new DrawParticipant("丙", TeamName: "单位二"), new DrawParticipant("丁", TeamName: "单位三") };
        var draw = new DrawService().Generate(participants, new(mode, kind, 1, "rr"));
        var graph = MatchGraphFactory.Create(TournamentSchedulerTestData.Id(1), draw);
        var request = TournamentSchedulerTestData.Request([graph], 18);
        request = request with { Resources = request.Resources with { Days = [request.Resources.Days[0] with { Courts = ["A"] }] },
            Policy = request.Policy with { Strategy = ScheduleAutoSchedulingStrategy.Compact } };
        var result = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Equal(6, result.Schedule.Placements.Count);
        var first = result.Schedule.Placements.Values.MinBy(p => p.StartTime)!;
        Assert.Equal(mode == CompetitionMode.SinglesRoundRobin, graph.Matches.Single(n => n.Id == first.MatchId).SameUnit);
    }

    [Fact]
    public void PlayInsAndGroupQualifiersUseBeforeBoundaryDuration()
    {
        var participants = Enumerable.Range(1, 10).Select(i => new DrawParticipant($"P{i}")).ToArray();
        var draw = new DrawService().Generate(participants, new(CompetitionMode.SinglesKnockout, EventKind.Singles, 2, "split", KnockoutGoal: KnockoutGoal.Champion));
        var graph = MatchGraphFactory.Create(TournamentSchedulerTestData.Id(1), draw);
        var request = TournamentSchedulerTestData.Request([graph]);
        request = request with { Policy = request.Policy with { ProjectTimings = new Dictionary<Guid, ProjectMatchTiming> { [graph.ProjectId] = new(40, 4, 20) } } };
        var result = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Contains(graph.Matches, n => n.ForceBeforeTimingBoundary);
        Assert.All(graph.Matches.Where(n => n.ForceBeforeTimingBoundary), n => Assert.Equal(20,
            (result.Schedule.Placements[n.Id].EndTime - result.Schedule.Placements[n.Id].StartTime).TotalMinutes));
        var final = graph.Matches.Single(n => n.GroupNumber == 0);
        Assert.Equal(40, (result.Schedule.Placements[final.Id].EndTime - result.Schedule.Placements[final.Id].StartTime).TotalMinutes);
    }

    [Fact]
    public void ExplicitStageWaveTargetsMoveLaterRoundsToTheirRequestedDay()
    {
        var graph = MatchGraphFactory.Create(TournamentSchedulerTestData.Id(1), MatchGraphTests.Draw(4, placement: PlacementPlayoff.None));
        var request = TournamentSchedulerTestData.Request([graph], 13);
        var laterDay = request.Resources.Days[0] with { Date = TournamentSchedulerTestData.Date.AddDays(1) };
        request = request with { Resources = request.Resources with { Days = [request.Resources.Days[0], laterDay] },
            Policy = request.Policy with { Strategy = ScheduleAutoSchedulingStrategy.Custom, SynchronizeStageWaves = true,
                StageWaveTargets = [new(TournamentSchedulerTestData.Day, .5), new(laterDay.DayLabel, 1)] } };
        var result = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.All(graph.Matches.Where(n => n.Dependencies.Count == 0), n => Assert.Equal(TournamentSchedulerTestData.Day, result.Schedule.Placements[n.Id].DayLabel));
        Assert.Equal(laterDay.DayLabel, result.Schedule.Placements[graph.Matches.Single(n => n.IsChampionshipFinal).Id].DayLabel);
    }

    [Fact]
    public void FreshFinalsStrategySuppliesAndPersistsEffectiveDefaults()
    {
        var graph = TournamentSchedulerTestData.Independent(1);
        graph = graph with { Matches = [graph.Matches[0] with { IsChampionshipFinal = true }] };
        var request = TournamentSchedulerTestData.Request([graph], 11);
        request = request with { Resources = request.Resources with { Days = [request.Resources.Days[0], request.Resources.Days[0] with { Date = TournamentSchedulerTestData.Date.AddDays(1) }] },
            Policy = request.Policy with { Strategy = ScheduleAutoSchedulingStrategy.FinalsDayFriendly } };
        var result = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Equal(request.Resources.Days[1].DayLabel, Assert.Single(result.Schedule.Placements.Values).DayLabel);
        Assert.Equal(2, result.Schedule.Policy.DayLoadTargets.Count);
        Assert.Contains(result.Schedule.Policy.FinalDayRules, r => r.ProjectId == graph.ProjectId && r.Category == TournamentFinalDayMatchCategory.Final && r.Preference != TournamentFinalDayPreference.Flexible);
        Assert.False(result.Schedule.Policy.SynchronizeStageWaves);
        Assert.Empty(request.Policy.DayLoadTargets);
    }

    [Fact]
    public void ExplicitCustomTargetsChangeDistributionFromTheSameRealBaseline()
    {
        var graph = TournamentSchedulerTestData.Independent(6);
        var request = TournamentSchedulerTestData.Request([graph], 12);
        var laterDay = request.Resources.Days[0] with { Date = TournamentSchedulerTestData.Date.AddDays(1) };
        request = request with { Resources = request.Resources with { Days = [request.Resources.Days[0], laterDay] },
            BaselinePlacements = graph.Matches.ToDictionary(n => n.Id, n => TournamentSchedulerTestData.Place(n, 9 + (n.Order - 1) / 2, court: n.Order % 2 == 0 ? "B" : "A")) };
        TournamentSchedulingResult.Success Generate(bool later) => Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request with
        { Policy = request.Policy with { Strategy = ScheduleAutoSchedulingStrategy.Custom,
            DayLoadTargets = [new(TournamentSchedulerTestData.Day, later ? .05 : 1, later ? .05 : 1), new(laterDay.DayLabel, later ? 1 : .05, later ? 1 : .05)] } }));
        var early = Generate(false);
        var late = Generate(true);
        Assert.True(late.Schedule.Placements.Values.Count(p => p.DayLabel == laterDay.DayLabel) > early.Schedule.Placements.Values.Count(p => p.DayLabel == laterDay.DayLabel));
        Assert.Equal(.05, late.Schedule.Policy.DayLoadTargets.Single(t => t.DayLabel == TournamentSchedulerTestData.Day).TargetUtilization);
    }

    [Fact]
    public void PartialExplicitStageTargetsKeepGeneratedDefaultsMonotonic()
    {
        var graph = TournamentSchedulerTestData.Independent(1);
        var request = TournamentSchedulerTestData.Request([graph], 10);
        request = request with { Resources = request.Resources with { Days = Enumerable.Range(0, 3).Select(i => request.Resources.Days[0] with { Date = TournamentSchedulerTestData.Date.AddDays(i) }).ToArray() },
            Policy = request.Policy with { SynchronizeStageWaves = true, StageWaveTargets = [new(TournamentSchedulerTestData.Day, .9)] } };
        var result = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Equal(.9, result.Schedule.Policy.StageWaveTargets[0].CumulativeProgress);
        Assert.True(result.Schedule.Policy.StageWaveTargets[1].CumulativeProgress >= .9);
    }

    [Fact]
    public void CompactRegenerationUsesRealMovementCostsAndRetainsAValidBaseline()
    {
        var graph = TournamentSchedulerTestData.Independent(1);
        var original = TournamentSchedulerTestData.Place(graph.Matches[0], 11);
        var request = TournamentSchedulerTestData.Request([graph], 12);
        request = request with { BaselinePlacements = new Dictionary<Guid, MatchPlacement> { [original.MatchId] = original },
            Policy = request.Policy with { Strategy = ScheduleAutoSchedulingStrategy.Compact } };
        var result = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Equal(original, Assert.Single(result.Schedule.Placements.Values));
        Assert.Equal(0, result.Quality.MovedMatchCount);
    }

    [Fact]
    public void GroupedSixteenEntrantGraphSchedulesEveryMatchWithGlobalValidation()
    {
        var graph = MatchGraphFactory.Create(TournamentSchedulerTestData.Id(1), MatchGraphTests.Draw(16, groups: 4, placement: PlacementPlayoff.None));
        var request = TournamentSchedulerTestData.Request([graph], 18);
        var result = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Equal(15, result.Schedule.Placements.Count);
        Assert.True(new TournamentPlacementValidator(request).ValidateSchedule(result.Schedule.Placements).IsValid);
    }

    [Fact]
    public void ExactResourceBoundaryAndRealSubMinuteBaselineAreSearchCandidates()
    {
        var graph = TournamentSchedulerTestData.Independent(1);
        var request = TournamentSchedulerTestData.Request([graph], 10);
        request = request with { Resources = request.Resources with { Days = [new(TournamentSchedulerTestData.Date, new(9, 0, 30), new(9, 30, 30), ["A"])] } };
        var result = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Equal(new TimeOnly(9, 0, 30), Assert.Single(result.Schedule.Placements.Values).StartTime);
        request = request with { Resources = request.Resources with { Days = [new(TournamentSchedulerTestData.Date, new(9, 0), new(10, 0), ["A"])] },
            BaselinePlacements = result.Schedule.Placements };
        result = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Equal(new TimeOnly(9, 0, 30), Assert.Single(result.Schedule.Placements.Values).StartTime);
    }
}

internal static class TournamentSchedulerTestData
{
    internal static readonly DateOnly Date = new(2026, 9, 20);
    internal static string Day => Date.ToString("yyyy-MM-dd");
    internal static Guid Id(int value) => new(value, 0, 0, new byte[8]);
    internal static EntrantSource.Participant Player(string name, string? studentId = null) =>
        new(name, name, [new(name, studentId ?? name)]);
    internal static MatchNode Node(int id, EntrantSource a, EntrantSource b, int minutes = 30, int project = 1)
    {
        Guid? SourceId(EntrantSource s) => s switch { EntrantSource.WinnerOf w => w.MatchId, EntrantSource.LoserOf l => l.MatchId, _ => null };
        return new(Id(100 + id), Id(project), id.ToString(), id, 1, "小组赛", $"M{id}", a, b, minutes,
            new[] { SourceId(a), SourceId(b) }.OfType<Guid>().Distinct().ToArray());
    }
    internal static MatchGraph Independent(int count, int minutes = 30) => new(Id(1), "graph-v1",
        Enumerable.Range(1, count).Select(i => Node(i, Player($"A{i}"), Player($"B{i}"), minutes)).ToArray());
    internal static TournamentSchedulingRequest Request(IReadOnlyList<MatchGraph> graphs, int endHour = 23) =>
        new(graphs, new([new(Date, new(9, 0), new(endHour, 0), ["A", "B"])], 2, 15, 12),
            new(ScheduleAutoSchedulingStrategy.BalancedRelaxed, [], false, [], []));
    internal static MatchPlacement Place(MatchNode node, int hour, int minute = 0, string court = "A", int? minutes = null, DateOnly? date = null) =>
        new(node.Id, (date ?? Date).ToString("yyyy-MM-dd"), new(hour, minute), new TimeOnly(hour, minute).AddMinutes(minutes ?? node.ExpectedDurationMinutes), court);
}
