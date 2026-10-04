using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using Xunit;
using static BadmintonDraw.Tests.TournamentSchedulerTestData;

namespace BadmintonDraw.Tests;

public sealed class TournamentScheduleBacktrackingTests
{
    [Fact]
    public void RecoversAConstructiveGreedyTrapAcrossDays()
    {
        var request = GreedyTrap();
        var witness = new Dictionary<Guid, MatchPlacement> {
            [Id(101)] = new(Id(101), Date.AddDays(1).ToString("yyyy-MM-dd"), new(14, 0), new(14, 30), "A"),
            [Id(102)] = new(Id(102), Day, new(14, 0), new(14, 30), "A"),
            [Id(103)] = request.BaselinePlacements![Id(103)] };
        Assert.Empty(new TournamentPlacementValidator(request).ValidateSchedule(witness).Violations);

        var success = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Equal(3, success.Schedule.Placements.Count);
        foreach (var pair in witness) Assert.Equal(pair.Value, success.Schedule.Placements[pair.Key]);
        Assert.Empty(new TournamentPlacementValidator(request).ValidateSchedule(success.Schedule.Placements).Violations);
    }

    [Fact]
    public void TinyRecoveryBudgetReportsIncomplete()
    {
        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(GreedyTrap(),
            new TournamentSchedulingOptions { MaxBacktracks = 0 }));
        Assert.Equal(SchedulingFailureKind.SearchIncomplete, failure.Detail.Diagnostics!.FailureKind);
        Assert.NotEqual(SchedulingFailureKind.ProvenInfeasible, failure.Detail.Diagnostics.FailureKind);
    }

    [Fact]
    public void RollbackNeverMovesLocks()
    {
        var request = GreedyTrap();
        var locked = request.BaselinePlacements![Id(103)];
        var success = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Equal(locked, success.Schedule.Placements[Id(103)]);
        Assert.Equal(locked, request.BaselinePlacements[Id(103)]);
        Assert.Empty(new TournamentPlacementValidator(request).ValidateSchedule(success.Schedule.Placements).Violations);
    }

    [Fact]
    public void RepeatedRequestIsDeterministic()
    {
        var request = GreedyTrap();
        var scheduler = new TournamentScheduler();
        var first = Assert.IsType<TournamentSchedulingResult.Success>(scheduler.Generate(request));
        var second = Assert.IsType<TournamentSchedulingResult.Success>(scheduler.Generate(request));
        Assert.Equal(first.Schedule.Placements.OrderBy(p => p.Key), second.Schedule.Placements.OrderBy(p => p.Key));
        Assert.Equal(first.Diagnostics!.UsedWorkUnits, second.Diagnostics!.UsedWorkUnits);
    }

    [Fact]
    public void OversizedProofContinuesToAUsefulLaterDay()
    {
        var request = LargeConditionalDay(lockLast: false);
        var success = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request,
            new TournamentSchedulingOptions { SearchWorkUnits = 200_000_000, ValidationWorkUnits = 200_000_000 }));
        Assert.Equal(Date.AddDays(2).ToString("yyyy-MM-dd"), success.Schedule.Placements[Id(358)].DayLabel);
        Assert.Equal(258, success.Schedule.Placements.Count);
        Assert.Empty(new TournamentPlacementValidator(request).ValidateSchedule(success.Schedule.Placements).Violations);
    }

    [Fact]
    public void OversizedProofIsIncompleteWithoutClaimingBudgetExhaustion()
    {
        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(
            LargeConditionalDay(lockLast: true),
            new TournamentSchedulingOptions { ValidationWorkUnits = 200_000_000 }));
        Assert.Equal(SchedulingFailureKind.ValidationIncomplete, failure.Detail.Diagnostics!.FailureKind);
        Assert.Null(failure.Detail.Diagnostics.ExhaustedPhase);
        Assert.True(failure.Detail.Diagnostics.UsedWorkUnits[SchedulingRunPhase.Validation] < 200_000_000);
    }

    [Fact]
    public void FailedLargeSpendReportsExhaustionEvenWithAllowanceRemaining()
    {
        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(GreedyTrap(),
            new TournamentSchedulingOptions { SearchWorkUnits = 3 }));
        Assert.Equal(SchedulingRunPhase.Search, failure.Detail.Diagnostics!.ExhaustedPhase);
        Assert.True(failure.Detail.Diagnostics.UsedWorkUnits[SchedulingRunPhase.Search] < 3);
    }

    [Fact]
    public void LaterDaySurvivesMoreThanSixtyFourCheaperAlternatives()
    {
        var request = GreedyTrap();
        request = request with {
            MatchGraphs = [request.MatchGraphs[0] with {
                Matches = request.MatchGraphs[0].Matches.Select(n => n with { ExpectedDurationMinutes = 120 }).ToArray() }],
            Resources = request.Resources with { Days = [
                new(Date, new(11, 25), new(16, 30), ["A", "C"], UnavailableCourtWindows: [
                    new(new(14, 30), new(16, 30), ["A"]), new(new(11, 25), new(14, 30), ["C"])]),
                new(Date.AddDays(1), new(14, 0), new(16, 0), ["A"])] },
            BaselinePlacements = new Dictionary<Guid, MatchPlacement> {
                [Id(103)] = new(Id(103), Day, new(14, 30), new(16, 30), "C") }
        };
        var success = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Equal(Date.AddDays(1).ToString("yyyy-MM-dd"), success.Schedule.Placements[Id(101)].DayLabel);
        Assert.Empty(new TournamentPlacementValidator(request).ValidateSchedule(success.Schedule.Placements).Violations);
    }

    [Theory]
    [InlineData(0, 64)]
    [InlineData(64, 0)]
    public void DisabledAlternativesOrRollbackNeverPublishesPartialSchedule(int alternatives, int depth)
    {
        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(GreedyTrap(),
            new TournamentSchedulingOptions { MaxDecisionAlternatives = alternatives, MaxRollbackDepth = depth }));
        Assert.Equal(SchedulingFailureKind.SearchIncomplete, failure.Detail.Diagnostics!.FailureKind);
        Assert.Null(failure.Detail.Diagnostics.ExhaustedPhase);
    }

    [Fact]
    public void OptionalFinalRecheckCannotDiscardAnAlreadyValidatedCompleteSchedule()
    {
        var request = Request([Independent(1)], 10) with {
            Policy = new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []) };
        // Leave the optional recheck one unit short. Derive the resource allowance
        // from a complete run so improved accounting does not silently remove this scenario.
        var full = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        var allowance = full.Diagnostics!.UsedWorkUnits[SchedulingRunPhase.Validation] - 1;
        var success = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request,
            new TournamentSchedulingOptions { ValidationWorkUnits = allowance }));
        Assert.Single(success.Schedule.Placements);
        Assert.Empty(new TournamentPlacementValidator(request).ValidateSchedule(success.Schedule.Placements).Violations);
        Assert.Equal(SchedulingRunPhase.Validation, success.Diagnostics!.ExhaustedPhase);
    }

    private static TournamentSchedulingRequest LargeConditionalDay(bool lockLast)
    {
        var root = Node(1, Player("shared"), Player("other"), 1);
        var leaves = Enumerable.Range(2, 257).Select(i => Node(i, new EntrantSource.WinnerOf(root.Id), Player($"P{i}"), 1)).ToArray();
        var placements = leaves.Take(lockLast ? 257 : 256).ToDictionary(n => n.Id, n => new MatchPlacement(n.Id,
            Date.AddDays(1).ToString("yyyy-MM-dd"), new TimeOnly(0, 0).AddMinutes(n.Order - 2),
            new TimeOnly(0, 0).AddMinutes(n.Order - 1), "A"));
        placements.Add(root.Id, Place(root, 0));
        return Request([new MatchGraph(Id(1), "v1", [root, .. leaves])]) with {
            Resources = new([new(Date, new(0, 0), new(0, 1), ["A"]),
                new(Date.AddDays(1), new(0, 0), new(4, 17), ["A"]),
                new(Date.AddDays(2), new(0, 0), new(0, 1), ["A"])], 1, 0, 256),
            Policy = new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []),
            LockedMatchIds = placements.Keys.ToArray(), BaselinePlacements = placements
        };
    }

    private static TournamentSchedulingRequest GreedyTrap()
    {
        var first = Node(1, Player("X"), Player("Y"));
        var second = Node(2, Player("P"), Player("Q"));
        var locked = Node(3, new EntrantSource.WinnerOf(second.Id), Player("X"));
        return Request([new MatchGraph(Id(1), "v1", [first, second, locked])]) with {
            Resources = new([
                new(Date, new(14, 0), new(15, 0), ["A", "C"], UnavailableCourtWindows: [
                    new(new(14, 30), new(15, 0), ["A"]), new(new(14, 0), new(14, 30), ["C"])]),
                new(Date.AddDays(1), new(14, 0), new(14, 30), ["A"])], 2, 0, 3),
            Policy = new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []),
            LockedMatchIds = [locked.Id],
            BaselinePlacements = new Dictionary<Guid, MatchPlacement> { [locked.Id] = Place(locked, 14, 30, "C") }
        };
    }
}
