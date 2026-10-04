using BadmintonDraw.Core.Scheduling;
using Xunit;
using static BadmintonDraw.Tests.TournamentSchedulerTestData;

namespace BadmintonDraw.Tests;

public sealed class TournamentBoundedQualityTests
{
    [Fact]
    public void ValidatedScheduleSurvivesQualityBudgetExhaustion()
    {
        var request = Request([Independent(3)]);
        var result = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request,
            new TournamentSchedulingOptions { QualityWorkUnits = 0 }));
        Assert.Equal(3, result.Schedule.Placements.Count);
        Assert.True(result.Quality.HardValidationComplete);
        Assert.Equal(0, result.Quality.HardConstraintCount);
        Assert.False(result.Quality.PlayerAnalysisComplete);
        Assert.False(result.Quality.SoftAnalysisComplete);
        Assert.Equal(0, result.Diagnostics!.UsedWorkUnits[SchedulingRunPhase.Quality]);
        Assert.Equal(SchedulingRunPhase.Quality, result.Diagnostics.ExhaustedPhase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2_000_000)]
    public void CancellationAfterQualityReturnsCanceledFailureBeforePublication(long qualityWorkUnits)
    {
        using var cancellation = new CancellationTokenSource();
        var request = Request([Independent(3)]);
        var result = new TournamentScheduler().Generate(request, new() { QualityWorkUnits = qualityWorkUnits }, cancellation.Token,
            (effectiveRequest, state, budget) =>
            {
                var quality = new TournamentScheduleQualityAnalyzer().AnalyzeBounded(effectiveRequest, state, budget, true);
                Assert.True(quality.HardValidationComplete);
                Assert.Equal(qualityWorkUnits > 0, quality.PlayerAnalysisComplete);
                cancellation.Cancel();
                return quality;
            });
        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(result).Detail;
        Assert.Equal(SchedulingFailureKind.Canceled, failure.Diagnostics!.FailureKind);
        Assert.Equal(SchedulingRunPhase.Quality, failure.Diagnostics.Phase);
        Assert.Null(failure.Diagnostics.ExhaustedPhase);
        Assert.Empty(failure.Violations);
    }

    [Fact]
    public void IncompleteMaximumIsNullWithBounds()
    {
        var workspace = WorkspaceScheduleQualityFixture.UnknownMaximum();
        var quality = new TournamentScheduleQualityAnalyzer().Analyze(WorkspaceScheduleQualityFixture.Request(workspace), workspace.Schedule!.Placements);
        var load = quality.PlayerLoads.Single(p => p.PlayerKey == "student:P0");
        Assert.Null(load.MaximumCount);
        Assert.Equal(1, load.MaximumLowerBound);
        Assert.Equal(258, load.MaximumUpperBound);
        Assert.True(load.MaximumLowerBound <= load.MaximumUpperBound);
        Assert.Null(load.ExpectedCount); Assert.Null(load.ProbabilityAtOrAboveLimit);
        Assert.False(load.IsExact);
        Assert.False(quality.PlayerAnalysisComplete);
        Assert.False(quality.HardValidationComplete);
    }

    [Fact]
    public void ProbabilityBudgetIsSharedAcrossPlayers()
    {
        var workspace = WorkspaceScheduleQualityFixture.UnknownForecast();
        var request = WorkspaceScheduleQualityFixture.Request(workspace);
        var state = new TournamentSearchState(new GraphSchedulingCandidates(request), workspace.Schedule!.Placements);
        var budget = new SchedulingWorkBudget(new() { QualityWorkUnits = 38_000 }, default);
        var quality = new TournamentScheduleQualityAnalyzer().AnalyzeBounded(request, state, budget, true);
        Assert.Contains(quality.PlayerLoads, p => p.IsExact);
        Assert.Contains(quality.PlayerLoads, p => !p.IsExact);
        Assert.False(quality.PlayerAnalysisComplete);
        Assert.InRange(budget.UsedWorkUnits[SchedulingRunPhase.Quality], 1, 38_000);
        Assert.True(budget.HasFailedSpend(SchedulingRunPhase.Quality));
    }

    [Fact]
    public void NoProbabilityMassIsExposedForPartialEnumeration()
    {
        var first = Enumerable.Range(1, 8).Select(i => Node(i, Player("A"), Player($"B{i}"), 1)).ToArray();
        var next = first.Select((n, i) => Node(i + 10, new BadmintonDraw.Core.Matches.EntrantSource.WinnerOf(n.Id), Player($"C{i}"), 1)).ToArray();
        var nodes = first.Concat(next).ToArray();
        var request = Request([new(Id(1), "v1", nodes)]);
        var placements = nodes.ToDictionary(n => n.Id, n => Place(n, 9, n.Order * 2));
        var state = new TournamentSearchState(new GraphSchedulingCandidates(request), placements);
        var budget = new SchedulingWorkBudget(new() { QualityWorkUnits = 18_000 }, default);
        var quality = new TournamentScheduleQualityAnalyzer().AnalyzeBounded(request, state, budget, true);
        var load = quality.PlayerLoads.Single(p => p.PlayerKey == "student:A");
        Assert.Equal(16, load.MaximumCount);
        Assert.False(load.IsExact);
        Assert.Empty(load.Distribution);
        Assert.Null(load.ExpectedCount);
        Assert.Null(load.ProbabilityAtOrAboveLimit);
        Assert.True(budget.HasFailedSpend(SchedulingRunPhase.Quality));
    }

    [Fact]
    public void IncompleteHardGateDoesNotBecomeAValidatedZeroConflictReport()
    {
        var request = Request([Independent(1)]);
        var placements = request.MatchGraphs[0].Matches.ToDictionary(n => n.Id, n => Place(n, 9));
        var state = new TournamentSearchState(new GraphSchedulingCandidates(request), placements);
        var budget = new SchedulingWorkBudget(new() { ValidationWorkUnits = 0 }, default);
        var quality = new TournamentScheduleQualityAnalyzer().AnalyzeBounded(request, state, budget, false);
        Assert.False(quality.HardValidationComplete);
        Assert.Empty(quality.Violations);
    }

    [Fact]
    public void PublicAnalysisResolvesTheSameBalancedPolicyAsGeneration()
    {
        var request = Request([Independent(2, 10)]) with
        {
            Resources = new([new(Date, new(9, 0), new(10, 0), ["A"]), new(Date.AddDays(1), new(9, 0), new(10, 0), ["A"])], 1, 0, 12)
        };
        var success = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Empty(success.Schedule.Policy.DayLoadTargets);
        var quality = new TournamentScheduleQualityAnalyzer().Analyze(request, success.Schedule.Placements);
        Assert.Equal(50, quality.SoftScore);
        Assert.Equal(quality.SoftScore, success.Quality.SoftScore);
        Assert.True(quality.SoftAnalysisComplete);
    }
}
