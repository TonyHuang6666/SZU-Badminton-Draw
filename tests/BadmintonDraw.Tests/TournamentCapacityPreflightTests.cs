using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using Xunit;
using static BadmintonDraw.Tests.TournamentSchedulerTestData;

namespace BadmintonDraw.Tests;

public sealed class TournamentCapacityPreflightTests
{
    [Theory]
    [InlineData(15, SchedulingPreflightStatus.ProvenInfeasible)]
    [InlineData(14, SchedulingPreflightStatus.Unknown)]
    public void CheapResourceProofHasPriorityOverPlayerScans(int allowance, SchedulingPreflightStatus expected)
    {
        // Three independent half-hour matches require 90 court-minutes; only 60 exist.
        var request = Request([Independent(3)], 10);
        request = request with { Resources = request.Resources with { Days = [request.Resources.Days[0] with { Courts = ["A"] }] } };
        var budget = new SchedulingWorkBudget(new() { PreflightWorkUnits = allowance }, default);
        Assert.True(GraphSchedulingCandidates.TryCreate(request, budget, out var context));
        var result = TournamentPlayerCapacity.Check(context!, budget);
        Assert.Equal(expected, result.Status);
        if (expected == SchedulingPreflightStatus.ProvenInfeasible)
        {
            Assert.Equal("ResourceTime", result.Evidence!.Kind);
            Assert.Equal(90 * TimeSpan.TicksPerMinute, result.Evidence.RequiredLowerBound);
            Assert.Equal(60 * TimeSpan.TicksPerMinute, result.Evidence.CapacityUpperBound);
            Assert.Equal(3, result.Evidence.WitnessMatchIds.Count);
            Assert.Equal(15, budget.UsedWorkUnits[SchedulingRunPhase.Preflight]);
            Assert.False(budget.HasFailedSpend(SchedulingRunPhase.Preflight));
        }
        else Assert.Null(result.Evidence);
    }

    [Fact]
    public void FourDaysRejectCompatibleTwentyOneBeforeSearch()
    {
        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(Shared(21, 4, 30, 30, 6))).Detail;
        Assert.NotNull(failure.Diagnostics);
        Assert.Equal(SchedulingFailureKind.ProvenInfeasible, failure.Diagnostics.FailureKind);
        Assert.Equal(0, failure.Diagnostics.UsedWorkUnits[SchedulingRunPhase.Search]);
        Assert.True(failure.CapacityEvidence!.RequiredLowerBound > failure.CapacityEvidence.CapacityUpperBound);
        Assert.Equal("PlayerTime", failure.CapacityEvidence.Kind);
        Assert.Equal(17, failure.CapacityEvidence.WitnessMatchIds.Count);
        Assert.All(failure.Capacity, day => Assert.Equal(0, day.RequiredPlacedMinutes));
    }

    [Fact]
    public void MoreCourtsDoesNotFixPlayerTimeCapacity()
    {
        var request = Shared(5, 1, 30, 30, 10);
        request = request with { Resources = request.Resources with { RefereeCount = 20,
            Days = [request.Resources.Days[0] with { Courts = Enumerable.Range(1, 20).Select(i => $"C{i}").ToArray() }] } };
        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(request)).Detail;
        Assert.NotNull(failure.Diagnostics);
        Assert.Equal(SchedulingFailureKind.ProvenInfeasible, failure.Diagnostics.FailureKind);
        Assert.Equal(0, failure.Diagnostics.UsedWorkUnits[SchedulingRunPhase.Search]);
        Assert.Equal("PlayerTime", failure.CapacityEvidence!.Kind);
        Assert.True(failure.CapacityEvidence.RequiredLowerBound > failure.CapacityEvidence.CapacityUpperBound);
    }

    [Fact]
    public void MixedDurationUsesMinimumNotDefaultThirty()
    {
        var request = Shared(5, 1, 20, 30, 6);
        var graph = request.MatchGraphs[0];
        request = request with { MatchGraphs = [graph with { Matches = graph.Matches.Select((n, i) => n with { ExpectedDurationMinutes = i == 0 ? 30 : 20 }).ToArray() }] };
        var success = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.NotNull(success.Diagnostics);
        Assert.Equal(SchedulingPreflightStatus.NoContradictionFound, success.Diagnostics.PreflightStatus);
        Assert.Empty(new TournamentPlacementValidator(request).ValidateSchedule(success.Schedule.Placements).Violations);
    }

    [Fact]
    public void ExactTicksDoNotCauseFalseCapacityRejection()
    {
        var request = Request([Independent(1)], 10);
        var day = request.Resources.Days[0] with { DayEnd = new TimeOnly(9, 30, 0), Courts = ["A"],
            RefereeCapacityWindows = [new(new TimeOnly(9, 0, 1), new TimeOnly(9, 29, 59), 1)] };
        request = request with { Resources = request.Resources with { Days = [day], RefereeCount = 1 } };
        Assert.Equal(30 * TimeSpan.TicksPerMinute, ScheduleResourceCalculator.CalculateDayCapacityTicks(request.Resources, day));
        var success = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.NotNull(success.Diagnostics);
        Assert.Equal(SchedulingPreflightStatus.NoContradictionFound, success.Diagnostics.PreflightStatus);
    }

    [Fact]
    public void InvalidInputsAndLocksStillTakePrecedence()
    {
        var request = Shared(5, 1, 30, 30, 10);
        var invalid = request with { Resources = request.Resources with { Days = [request.Resources.Days[0], request.Resources.Days[0]] } };
        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(invalid)).Detail;
        Assert.Contains(failure.Violations, v => v.Code == SchedulingConstraintCode.InvalidResources);
        Assert.NotNull(failure.Diagnostics);
        Assert.Equal(SchedulingFailureKind.InvalidInput, failure.Diagnostics.FailureKind);
        var node = request.MatchGraphs[0].Matches[0];
        var locked = request with { LockedMatchIds = [node.Id], BaselinePlacements = new Dictionary<Guid, MatchPlacement> { [node.Id] = Place(node, 8) } };
        failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(locked)).Detail;
        Assert.Contains(failure.Violations, v => v.Code == SchedulingConstraintCode.DayBounds);
        Assert.Equal(SchedulingFailureKind.InvalidInput, failure.Diagnostics!.FailureKind);
    }

    internal static TournamentSchedulingRequest Shared(int count, int days, int duration, int rest, int cap)
    {
        var graph = new MatchGraph(Id(1), "v1", Enumerable.Range(1, count)
            .Select(i => Node(i, Player("shared"), Player($"other-{i}"), duration)).ToArray());
        var request = Request([graph], 13);
        return request with { Resources = request.Resources with { MinimumRestMinutes = rest, MaxPlayerMatchesPerDay = cap,
            Days = Enumerable.Range(0, days).Select(i => request.Resources.Days[0] with { Date = Date.AddDays(i) }).ToArray() } };
    }

    [Theory]
    [InlineData(30, 30, 12, 4)]
    [InlineData(20, 30, 12, 5)]
    [InlineData(20, 30, 4, 4)]
    [InlineData(30, 0, 12, 8)]
    [InlineData(30, 30, int.MaxValue, 4)]
    public void PlayerCapacityUsesDurationRestAndCap(int duration, int rest, int cap, long expected)
    {
        var request = Shared(1, 1, duration, rest, cap);
        Assert.Equal(expected, TournamentPlayerCapacity.CalculatePlayerMatchCapacity(request.Resources.Days, duration, rest, cap));
    }

    [Fact]
    public void PerDayRemaindersCannotBeCombinedIntoAnotherAppearance()
    {
        var request = Shared(4, 3, 60, 0, 10);
        request = request with { Resources = request.Resources with { Days = request.Resources.Days.Select(d => d with { DayEnd = new TimeOnly(10, 40) }).ToArray() } };
        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(request)).Detail;
        Assert.Equal("PlayerTime", failure.CapacityEvidence!.Kind);
        Assert.Equal(240 * TimeSpan.TicksPerMinute, failure.CapacityEvidence.RequiredLowerBound);
        Assert.Equal(180 * TimeSpan.TicksPerMinute, failure.CapacityEvidence.CapacityUpperBound);
        Assert.Equal(0, failure.Diagnostics!.UsedWorkUnits[SchedulingRunPhase.Search]);
    }

    [Fact]
    public void ResourceEvidenceUsesExactBoundaryIntegration()
    {
        var request = Request([Independent(3)], 10);
        var day = request.Resources.Days[0] with { RefereeCapacityWindows = [new(new TimeOnly(9, 0), new TimeOnly(9, 30), 1)],
            UnavailableCourtWindows = [new(new TimeOnly(9, 30), new TimeOnly(10, 0), ["A", "B"])] };
        request = request with { Resources = request.Resources with { Days = [day] } };
        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(request)).Detail;
        Assert.Equal("ResourceTime", failure.CapacityEvidence!.Kind);
        Assert.Equal(90 * TimeSpan.TicksPerMinute, failure.CapacityEvidence.RequiredLowerBound);
        Assert.Equal(30 * TimeSpan.TicksPerMinute, failure.CapacityEvidence.CapacityUpperBound);
        Assert.Equal(3, failure.CapacityEvidence.WitnessMatchIds.Count);
        Assert.Equal(0, failure.Diagnostics!.UsedWorkUnits[SchedulingRunPhase.Search]);
    }

    [Fact]
    public void ContextBudgetAndCancellationNeverPublishPartialContext()
    {
        var request = Shared(4, 1, 30, 30, 10);
        var budget = new SchedulingWorkBudget(new() { ContextWorkUnits = 20 }, default);
        Assert.False(GraphSchedulingCandidates.TryCreate(request, budget, out var context));
        Assert.Null(context);
        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(request, new TournamentSchedulingOptions { ContextWorkUnits = 20 })).Detail;
        Assert.Equal(SchedulingFailureKind.SearchIncomplete, failure.Diagnostics!.FailureKind);
        Assert.Equal(SchedulingRunPhase.Context, failure.Diagnostics.ExhaustedPhase);
        Assert.Null(failure.CapacityEvidence);
        var canceled = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(request, new(), new CancellationToken(true))).Detail;
        Assert.Equal(SchedulingFailureKind.Canceled, canceled.Diagnostics!.FailureKind);
        Assert.Equal(0, canceled.Diagnostics.UsedWorkUnits[SchedulingRunPhase.Search]);
    }

    [Fact]
    public void InvalidContextReturnsInputViolationsRatherThanBudgetFailure()
    {
        var request = Shared(1, 1, 30, 30, 10);
        request = request with { Resources = request.Resources with { MaxPlayerMatchesPerDay = 0 } };
        Assert.True(GraphSchedulingCandidates.TryCreate(request, new(new(), default), out var context));
        Assert.NotNull(context);
        Assert.Contains(context.InputViolations, issue => issue.Code == SchedulingConstraintCode.InvalidResources);
    }

    [Fact]
    public void VeryLargeRestDoesNotOverflowCapacityEvidence()
    {
        var request = Shared(9, 8, 30, int.MaxValue, 10);
        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(request)).Detail;
        Assert.Equal(SchedulingFailureKind.ProvenInfeasible, failure.Diagnostics!.FailureKind);
        Assert.True(failure.CapacityEvidence!.RequiredLowerBound > failure.CapacityEvidence.CapacityUpperBound);
    }

    [Fact]
    public void CapacityEvidenceWithCopiesCannotBeMutatedThroughCallerCollections()
    {
        var ids = new List<Guid> { Id(1) };
        var conditions = new Dictionary<Guid, bool> { [Id(2)] = true };
        var evidence = new SchedulingCapacityEvidence("PlayerTime", "p", "P", 2, 1, [], new Dictionary<Guid, bool>())
            with { WitnessMatchIds = ids, OutcomeConditions = conditions };
        ids.Clear(); conditions.Clear();
        Assert.Equal(Id(1), Assert.Single(evidence.WitnessMatchIds));
        Assert.True(evidence.OutcomeConditions[Id(2)]);
    }

    [Fact]
    public void InvalidPolicyDoesNotIntegrateResourceCapacityOutsideContextAllowance()
    {
        var request = Request([Independent(1)], 10);
        var day = request.Resources.Days[0] with
        {
            RefereeCapacityWindows = Enumerable.Range(0, 64).Select(i => new ScheduleRefereeCapacityWindow(
                new TimeOnly(9, 0).Add(TimeSpan.FromSeconds(i * 2)),
                new TimeOnly(9, 0).Add(TimeSpan.FromSeconds(i * 2 + 1)), 1)).ToArray()
        };
        request = request with { Resources = request.Resources with { Days = [day] },
            Policy = request.Policy with { ProjectTimings = new Dictionary<Guid, ProjectMatchTiming> { [Id(999)] = new(30) } } };
        var options = new TournamentSchedulingOptions { ContextWorkUnits = 1_000 };
        var inspectionBudget = new SchedulingWorkBudget(options, default);
        Assert.True(GraphSchedulingCandidates.TryCreate(request, inspectionBudget, out var context));
        Assert.Contains(context!.InputViolations, violation => violation.Code == SchedulingConstraintCode.InvalidPolicy);
        Assert.True(inspectionBudget.Remaining(SchedulingRunPhase.Context) < SchedulingCapacityArithmetic.DayIntegrationWork(day));

        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(request, options)).Detail;
        Assert.Equal(SchedulingFailureKind.InvalidInput, failure.Diagnostics!.FailureKind);
        Assert.Contains(failure.Violations, violation => violation.Code == SchedulingConstraintCode.InvalidPolicy);
        Assert.Empty(failure.Capacity);
        Assert.Equal(0, failure.Diagnostics.UsedWorkUnits[SchedulingRunPhase.Search]);
        Assert.Null(failure.CapacityEvidence);
    }
}
