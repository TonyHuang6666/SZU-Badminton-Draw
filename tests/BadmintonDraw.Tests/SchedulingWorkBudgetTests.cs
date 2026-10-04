using BadmintonDraw.Core;
using BadmintonDraw.Core.Scheduling;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class SchedulingWorkBudgetTests
{
    [Fact]
    public void ZeroBudgetNeverSpends()
    {
        var budget = new SchedulingWorkBudget(new() { SearchWorkUnits = 0 }, default);

        Assert.False(budget.TrySpend(SchedulingRunPhase.Search, 1));
        Assert.False(budget.TrySpend(SchedulingRunPhase.Search, long.MaxValue));
        Assert.Equal(0, budget.Remaining(SchedulingRunPhase.Search));
        Assert.Equal(0, budget.UsedWorkUnits[SchedulingRunPhase.Search]);
    }

    [Fact]
    public void NegativeSpendIsRejected()
    {
        var budget = new SchedulingWorkBudget(new() { SearchWorkUnits = 3 }, default);

        Assert.Throws<ArgumentOutOfRangeException>(() => budget.TrySpend(SchedulingRunPhase.Search, -1));
        Assert.Equal(3, budget.Remaining(SchedulingRunPhase.Search));
        Assert.Equal(0, budget.UsedWorkUnits[SchedulingRunPhase.Search]);
    }

    [Fact]
    public void LongLimitDoesNotOverflow()
    {
        var budget = new SchedulingWorkBudget(new() { SearchWorkUnits = long.MaxValue }, default);

        Assert.True(budget.TrySpend(SchedulingRunPhase.Search, long.MaxValue - 1));
        Assert.False(budget.TrySpend(SchedulingRunPhase.Search, long.MaxValue));
        Assert.Equal(1, budget.Remaining(SchedulingRunPhase.Search));
        Assert.Equal(long.MaxValue - 1, budget.UsedWorkUnits[SchedulingRunPhase.Search]);
        Assert.True(budget.TrySpend(SchedulingRunPhase.Search));
        Assert.False(budget.TrySpend(SchedulingRunPhase.Search));
        Assert.Equal(0, budget.Remaining(SchedulingRunPhase.Search));
        Assert.Equal(long.MaxValue, budget.UsedWorkUnits[SchedulingRunPhase.Search]);
    }

    [Fact]
    public void CancellationStopsAllPhases()
    {
        using var cancellation = new CancellationTokenSource();
        var budget = new SchedulingWorkBudget(TournamentSchedulingOptions.Default, cancellation.Token);
        Assert.False(budget.IsCanceled);
        Assert.True(budget.TrySpend(SchedulingRunPhase.Context));
        var beforeCancellation = budget.UsedWorkUnits;

        cancellation.Cancel();

        Assert.True(budget.IsCanceled);
        foreach (var phase in Enum.GetValues<SchedulingRunPhase>())
        {
            Assert.False(budget.TrySpend(phase));
            Assert.False(budget.TrySpend(phase, 0));
            Assert.Equal(beforeCancellation[phase], budget.UsedWorkUnits[phase]);
        }
    }

    [Fact]
    public void OnePlayerCannotResetRequestBudget()
    {
        var budget = new SchedulingWorkBudget(new() { PreflightWorkUnits = 3, SearchWorkUnits = 2 }, default);

        // Player A uses two units, then player B uses the last shared unit.
        Assert.True(budget.TrySpend(SchedulingRunPhase.Preflight, 2));
        Assert.True(budget.TrySpend(SchedulingRunPhase.Preflight));
        Assert.False(budget.TrySpend(SchedulingRunPhase.Preflight));
        Assert.True(budget.TrySpend(SchedulingRunPhase.Search, 2));
        Assert.False(budget.TrySpend(SchedulingRunPhase.Preflight));
        Assert.Equal(3, budget.UsedWorkUnits[SchedulingRunPhase.Preflight]);
        Assert.Equal(0, budget.Remaining(SchedulingRunPhase.Preflight));
    }

    [Fact]
    public void DiagnosticSnapshotDoesNotAliasMutableCollections()
    {
        var counts = new Dictionary<SchedulingRunPhase, long> { [SchedulingRunPhase.Search] = 2 };
        var courts = new List<string> { "A" };
        var blockedCourts = new List<string> { "B" };
        var days = new List<ScheduleDaySettings>
        {
            new(new(2026, 10, 3), new(9, 0), new(18, 0), courts,
                UnavailableCourtWindows: [new(new(10, 0), new(11, 0), blockedCourts)])
        };
        var targets = new List<TournamentDayLoadTarget> { new("2026-10-03", .5, .8) };
        var timings = new Dictionary<Guid, ProjectMatchTiming> { [Guid.Empty] = new(30) };
        var resources = new TournamentResourcePlan(days, 1, 30, 6);
        var policy = new TournamentSchedulingPolicy(ScheduleAutoSchedulingStrategy.Compact, targets, false, [], [])
        { ProjectTimings = timings };
        var diagnostic = new SchedulingRunDiagnostics(SchedulingRunPhase.Search,
            SchedulingPreflightStatus.NoContradictionFound, SchedulingFailureKind.SearchIncomplete,
            counts, resources, policy) { ExhaustedPhase = SchedulingRunPhase.Search };

        counts[SchedulingRunPhase.Search] = 99;
        courts.Clear();
        blockedCourts.Clear();
        days.Clear();
        targets.Clear();
        timings.Clear();

        Assert.Equal(2, diagnostic.UsedWorkUnits[SchedulingRunPhase.Search]);
        var day = Assert.Single(diagnostic.Resources.Days);
        Assert.Equal("A", Assert.Single(day.Courts));
        Assert.Equal("B", Assert.Single(Assert.Single(day.UnavailableCourtWindows!).Courts));
        Assert.Single(diagnostic.Policy.DayLoadTargets);
        Assert.Equal(new ProjectMatchTiming(30), diagnostic.Policy.ProjectTimings[Guid.Empty]);
        var mutableView = Assert.IsAssignableFrom<IDictionary<SchedulingRunPhase, long>>(diagnostic.UsedWorkUnits);
        Assert.Throws<NotSupportedException>(() => mutableView[SchedulingRunPhase.Search] = 123);
    }

    [Fact]
    public void DiagnosticWithReplacementCopiesWorkCounts()
    {
        var request = TournamentSchedulerTestData.Request([]);
        var original = new SchedulingRunDiagnostics(SchedulingRunPhase.Context,
            SchedulingPreflightStatus.NotRun, null,
            new Dictionary<SchedulingRunPhase, long> { [SchedulingRunPhase.Search] = 1 },
            request.Resources, request.Policy);
        var replacement = new Dictionary<SchedulingRunPhase, long> { [SchedulingRunPhase.Search] = 3 };

        var copy = original with { UsedWorkUnits = replacement, Phase = SchedulingRunPhase.Search };
        replacement.Clear();

        Assert.Equal(3, copy.UsedWorkUnits[SchedulingRunPhase.Search]);
        Assert.Equal(1, original.UsedWorkUnits[SchedulingRunPhase.Search]);
        Assert.Equal(SchedulingRunPhase.Search, copy.Phase);
        Assert.Null(copy.ExhaustedPhase);
    }

    [Fact]
    public void WorkCountSnapshotDoesNotChangeAfterFurtherSpending()
    {
        var budget = new SchedulingWorkBudget(new() { SearchWorkUnits = 2 }, default);
        var before = budget.UsedWorkUnits;
        Assert.True(budget.TrySpend(SchedulingRunPhase.Search));
        var after = budget.UsedWorkUnits;

        Assert.Equal(0, before[SchedulingRunPhase.Search]);
        Assert.Equal(1, after[SchedulingRunPhase.Search]);
        Assert.Equal(6, after.Count);
    }

    [Fact]
    public void ZeroSpendPreservesBudget()
    {
        var budget = new SchedulingWorkBudget(new() { SearchWorkUnits = 0 }, default);

        Assert.True(budget.TrySpend(SchedulingRunPhase.Search, 0));
        Assert.Equal(0, budget.UsedWorkUnits[SchedulingRunPhase.Search]);
        Assert.Equal(0, budget.Remaining(SchedulingRunPhase.Search));
    }

    [Theory]
    [InlineData(SchedulingRunPhase.Context, 8_000_000L)]
    [InlineData(SchedulingRunPhase.Preflight, 1_000_000L)]
    [InlineData(SchedulingRunPhase.Search, 5_000_000_000L)]
    [InlineData(SchedulingRunPhase.Validation, 30_000_000L)]
    [InlineData(SchedulingRunPhase.Optimization, 5_000_000L)]
    [InlineData(SchedulingRunPhase.Quality, 2_000_000L)]
    public void DefaultBudgetStopsAtEachSpecifiedLimit(SchedulingRunPhase phase, long limit)
    {
        var budget = new SchedulingWorkBudget(TournamentSchedulingOptions.Default, default);

        Assert.False(budget.TrySpend(phase, limit + 1));
        Assert.True(budget.TrySpend(phase, limit));
        Assert.False(budget.TrySpend(phase));
        Assert.Equal(limit, budget.UsedWorkUnits[phase]);
        Assert.Equal(0, budget.Remaining(phase));
    }

    [Theory]
    [InlineData("ContextWorkUnits")]
    [InlineData("PreflightWorkUnits")]
    [InlineData("SearchWorkUnits")]
    [InlineData("ValidationWorkUnits")]
    [InlineData("OptimizationWorkUnits")]
    [InlineData("QualityWorkUnits")]
    [InlineData("MaxDecisionAlternatives")]
    [InlineData("MaxRollbackDepth")]
    [InlineData("MaxBacktracks")]
    [InlineData("MaxCacheEntries")]
    public void NegativeOptionsAreRejectedAndZeroIsAllowed(string property)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OptionsWithValue(property, -1));
        var zero = OptionsWithValue(property, 0);
        Assert.NotNull(new SchedulingWorkBudget(zero, default));
    }

    [Fact]
    public void InvalidPhaseIsRejectedWithoutSpending()
    {
        var budget = new SchedulingWorkBudget(TournamentSchedulingOptions.Default, default);

        Assert.Throws<ArgumentOutOfRangeException>(() => budget.TrySpend((SchedulingRunPhase)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.Remaining((SchedulingRunPhase)99));
        Assert.All(budget.UsedWorkUnits.Values, count => Assert.Equal(0, count));
    }

    [Fact]
    public void LegacyResultConstructorsSupportOptionalDiagnostics()
    {
        var request = TournamentSchedulerTestData.Request([]);
        var schedule = new TournamentSchedule(new Dictionary<Guid, MatchPlacement>(), request.Resources,
            request.Policy, new Dictionary<Guid, string>(), 0);
        var quality = new TournamentScheduleQuality(0, 0, [], [], [], 0, 0);
        var success = new TournamentSchedulingResult.Success(schedule, new Dictionary<Guid, string>(), quality);
        var failure = new SchedulingFailure("Incomplete", [], [], [], []);
        Assert.Null(success.Diagnostics);
        Assert.Null(failure.Diagnostics);
        var diagnostic = new SchedulingRunDiagnostics(SchedulingRunPhase.Validation,
            SchedulingPreflightStatus.Unknown, SchedulingFailureKind.ValidationIncomplete,
            new Dictionary<SchedulingRunPhase, long>(), request.Resources, request.Policy)
        { ExhaustedPhase = SchedulingRunPhase.Validation };

        Assert.Same(diagnostic, (success with { Diagnostics = diagnostic }).Diagnostics);
        Assert.Same(diagnostic, (failure with { Diagnostics = diagnostic }).Diagnostics);
    }

    private static TournamentSchedulingOptions OptionsWithValue(string property, int value) => property switch
    {
        "ContextWorkUnits" => new() { ContextWorkUnits = value },
        "PreflightWorkUnits" => new() { PreflightWorkUnits = value },
        "SearchWorkUnits" => new() { SearchWorkUnits = value },
        "ValidationWorkUnits" => new() { ValidationWorkUnits = value },
        "OptimizationWorkUnits" => new() { OptimizationWorkUnits = value },
        "QualityWorkUnits" => new() { QualityWorkUnits = value },
        "MaxDecisionAlternatives" => new() { MaxDecisionAlternatives = value },
        "MaxRollbackDepth" => new() { MaxRollbackDepth = value },
        "MaxBacktracks" => new() { MaxBacktracks = value },
        "MaxCacheEntries" => new() { MaxCacheEntries = value },
        _ => throw new ArgumentOutOfRangeException(nameof(property))
    };
}
