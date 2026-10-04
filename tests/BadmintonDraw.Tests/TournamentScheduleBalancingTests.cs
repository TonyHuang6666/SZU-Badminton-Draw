using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using System.Text.Json;
using Xunit;
using static BadmintonDraw.Tests.TournamentSchedulerTestData;

namespace BadmintonDraw.Tests;

public sealed class TournamentScheduleBalancingTests
{
    [Fact]
    public void BalancedUsesThreeTwoTwoTwoCapacityWeights()
    {
        // Slack capacity allows Compact to put all 18 on day one; the ratio must
        // come from the Balanced objective rather than physically full days.
        var request = MultiDay(18, [9, 6, 6, 6]);
        var result = Generate(request);
        Assert.Equal(new[] { 6, 4, 4, 4 }, Counts(request, result));
        Assert.True(new TournamentPlacementValidator(request).ValidateSchedule(result.Schedule.Placements).IsValid);
    }

    [Fact]
    public void SymmetricDaysDifferByAtMostOneMatch()
    {
        var request = MultiDay(13, [3, 3, 3, 3]);
        var counts = Counts(request, Generate(request));
        Assert.InRange(counts.Max() - counts.Min(), 0, 1);
    }

    [Fact]
    public void CompactStillFinishesEarlier()
    {
        var request = MultiDay(8, [4, 4]);
        var balanced = Generate(request);
        var compact = Generate(request with { Policy = request.Policy with { Strategy = ScheduleAutoSchedulingStrategy.Compact } });
        Assert.Equal(new[] { 8, 0 }, Counts(request, compact));
        Assert.Equal(new[] { 4, 4 }, Counts(request, balanced));
        Assert.True(compact.Schedule.Placements.Values.Max(p => p.DayLabel)!.CompareTo(
            balanced.Schedule.Placements.Values.Max(p => p.DayLabel)) < 0);
    }

    [Fact]
    public void UnevenBaselineDoesNotFreezeBalancedRegeneration()
    {
        var request = MultiDay(8, [4, 4]);
        var baseline = request.MatchGraphs[0].Matches.ToDictionary(n => n.Id,
            n => Place(n, 9 + (n.Order - 1) / 2, (n.Order - 1) % 2 * 30));
        request = request with { BaselinePlacements = baseline };
        Assert.True(new TournamentPlacementValidator(request).ValidateSchedule(baseline).IsValid);
        Assert.Equal(new[] { 4, 4 }, Counts(request, Generate(request)));
        Assert.All(baseline.Values, p => Assert.Equal(Day, p.DayLabel));
    }

    [Fact]
    public void ZeroCapacityDayIsExcluded()
    {
        var request = MultiDay(6, [2, 2, 2]);
        request = request with { Resources = request.Resources with { Days = request.Resources.Days.Select((d, i) =>
            i == 1 ? d with { RefereeCapacityWindows = [new(d.DayStart, d.DayEnd, 0)] } : d).ToArray() } };
        Assert.Equal(new[] { 3, 0, 3 }, Counts(request, Generate(request)));
    }

    [Fact]
    public void OptimizationExhaustionKeepsValidatedSolution()
    {
        var request = MultiDay(8, [4, 4]);
        var result = Generate(request, new() { OptimizationWorkUnits = 0 });
        Assert.Equal(8, result.Schedule.Placements.Count);
        Assert.True(new TournamentPlacementValidator(request).ValidateSchedule(result.Schedule.Placements).IsValid);
        Assert.Equal(SchedulingRunPhase.Optimization, result.Diagnostics!.ExhaustedPhase);
        Assert.Equal(0, result.Diagnostics.UsedWorkUnits[SchedulingRunPhase.Optimization]);
    }

    [Fact]
    public void LegalSwapImprovesLoadWhenNoSingleMoveImprovesIt()
    {
        var (request, placements) = SwapFixture();
        var improved = Improve(request, placements);
        Assert.Equal(new[] { 60d, 60d }, DayMinutes(request, improved));
        Assert.True(new TournamentPlacementValidator(request).ValidateSchedule(improved).IsValid);
        Assert.Equal(new[] { 80d, 40d }, DayMinutes(request, placements));
    }

    [Fact]
    public void CrossDayMovesImproveAnUnevenCompleteWitness()
    {
        var request = MultiDay(8, [4, 4]);
        var placements = request.MatchGraphs[0].Matches.ToDictionary(n => n.Id,
            n => Place(n, 9 + (n.Order - 1) / 2, (n.Order - 1) % 2 * 30));
        var improved = Improve(request, placements);
        Assert.Equal(new[] { 120d, 120d }, DayMinutes(request, improved));
        Assert.True(new TournamentPlacementValidator(request).ValidateSchedule(improved).IsValid);
    }

    [Fact]
    public void AlreadyBalancedScheduleSkipsMovesThatCannotImproveLoad()
    {
        var request = MultiDay(8, [4, 4]);
        var placements = request.MatchGraphs[0].Matches.ToDictionary(n => n.Id, n =>
            Place(n, 9 + ((n.Order - 1) % 4) / 2, ((n.Order - 1) % 2) * 30, date: Date.AddDays((n.Order - 1) / 4)));
        var options = new TournamentSchedulingOptions { OptimizationWorkUnits = 8_000 };
        var validator = new TournamentPlacementValidator(request);
        var budget = new SchedulingWorkBudget(options, default);
        var improved = TournamentScheduleBalancer.Improve(validator, new(validator.Context, placements), budget, options);
        Assert.Equal(placements.OrderBy(p => p.Key), improved.OrderBy(p => p.Key));
        Assert.False(budget.HasFailedSpend(SchedulingRunPhase.Optimization));
    }

    [Fact]
    public void LocksPreventOtherwiseImprovingSwap()
    {
        var (request, placements) = SwapFixture();
        request = request with { BaselinePlacements = placements, LockedMatchIds = [Id(101), Id(102)] };
        var improved = Improve(request, placements);
        Assert.Equal(placements.OrderBy(p => p.Key), improved.OrderBy(p => p.Key));
    }

    [Fact]
    public void DescendantDependencyPreventsOtherwiseImprovingSwap()
    {
        var (request, placements) = SwapFixture();
        var nodes = request.MatchGraphs[0].Matches.ToArray();
        nodes[1] = nodes[1] with { SideA = new EntrantSource.WinnerOf(nodes[0].Id), Dependencies = [nodes[0].Id] };
        request = request with { MatchGraphs = [request.MatchGraphs[0] with { Matches = nodes }],
            BaselinePlacements = placements, LockedMatchIds = [nodes[1].Id] };
        var improved = Improve(request, placements);
        Assert.Equal(placements.OrderBy(p => p.Key), improved.OrderBy(p => p.Key));
        Assert.True(new TournamentPlacementValidator(request).ValidateSchedule(improved).IsValid);
    }

    [Fact]
    public void PlayerRestPreventsOtherwiseImprovingSwap()
    {
        var (request, placements) = SwapFixture();
        var nodes = request.MatchGraphs[0].Matches.ToArray();
        nodes[2] = nodes[2] with { SideA = nodes[0].SideA };
        request = request with { MatchGraphs = [request.MatchGraphs[0] with { Matches = nodes }],
            Resources = request.Resources with { MinimumRestMinutes = 30 },
            BaselinePlacements = placements, LockedMatchIds = [nodes[1].Id] };
        var improved = Improve(request, placements);
        Assert.Equal(placements.OrderBy(p => p.Key), improved.OrderBy(p => p.Key));
        Assert.True(new TournamentPlacementValidator(request).ValidateSchedule(improved).IsValid);
    }

    [Fact]
    public void ExhaustionDuringOptimizationRetainsOriginalWitness()
    {
        var (request, placements) = SwapFixture();
        var options = new TournamentSchedulingOptions { OptimizationWorkUnits = 1_000 };
        var validator = new TournamentPlacementValidator(request);
        var budget = new SchedulingWorkBudget(options, default);
        var improved = TournamentScheduleBalancer.Improve(validator, new(validator.Context, placements), budget, options);
        Assert.Equal(placements.OrderBy(p => p.Key), improved.OrderBy(p => p.Key));
        Assert.True(budget.HasFailedSpend(SchedulingRunPhase.Optimization));
        Assert.True(validator.ValidateSchedule(improved).IsValid);
    }

    [Fact]
    public void BalancedKeepsExplicitFinalPreferenceAheadOfLoad()
    {
        var request = MultiDay(4, [2, 2]);
        request = request with { MatchGraphs = [request.MatchGraphs[0] with {
            Matches = request.MatchGraphs[0].Matches.Select(n => n with { IsChampionshipFinal = true }).ToArray() }],
            Policy = request.Policy with { FinalDayRules = [new(Id(1), TournamentFinalDayMatchCategory.Final,
                TournamentFinalDayPreference.StronglyPreferFinalDay)] } };
        Assert.Equal(new[] { 0, 4 }, Counts(request, Generate(request)));
    }

    [Fact]
    public void DefaultStageWavesDoNotOverruleCapacityBalance()
    {
        var request = MultiDay(8, [4, 4]);
        request = request with { Policy = request.Policy with { SynchronizeStageWaves = true } };
        Assert.Equal(new[] { 4, 4 }, Counts(request, Generate(request)));
    }

    [Fact]
    public void RegenerationKeepsDefaultWavesBehindBalancedLoad()
    {
        var request = MultiDay(8, [4, 4]);
        request = request with { Policy = request.Policy with { SynchronizeStageWaves = true } };
        var first = Generate(request);
        var savedPolicy = JsonSerializer.Deserialize<TournamentSchedulingPolicy>(JsonSerializer.Serialize(first.Schedule.Policy))!;
        var regenerated = Generate(request with { Policy = savedPolicy, BaselinePlacements = first.Schedule.Placements });
        Assert.Equal(new[] { 4, 4 }, Counts(request, regenerated));
    }

    [Fact]
    public void ExplicitStageTargetsEqualToDefaultsRemainExplicitAcrossRegeneration()
    {
        var request = MultiDay(8, [4, 4]);
        request = request with { Policy = request.Policy with { SynchronizeStageWaves = true,
            StageWaveTargets = [new(Day, .5), new(Date.AddDays(1).ToString("yyyy-MM-dd"), 1)],
            DayLoadTargets = [new(Day, .5, .65), new(Date.AddDays(1).ToString("yyyy-MM-dd"), .5, .65)] } };
        var first = Generate(request);
        var savedPolicy = JsonSerializer.Deserialize<TournamentSchedulingPolicy>(JsonSerializer.Serialize(first.Schedule.Policy))!;
        var regenerated = Generate(request with { Policy = savedPolicy, BaselinePlacements = first.Schedule.Placements });
        Assert.Equal(request.Policy.StageWaveTargets, savedPolicy.StageWaveTargets);
        Assert.Equal(request.Policy.DayLoadTargets, savedPolicy.DayLoadTargets);
        Assert.Equal(new[] { 0, 8 }, Counts(request, first));
        Assert.Equal(new[] { 0, 8 }, Counts(request, regenerated));
    }

    [Fact]
    public void DecimalLoadDeltaRetainsSubMinuteCapacityAndSignedImprovement()
    {
        var request = MultiDay(2, [1, 1]);
        request = request with { Resources = request.Resources with { Days = [request.Resources.Days[0],
            request.Resources.Days[1] with { DayEnd = new(10, 0, 30) }] } };
        var validator = new TournamentPlacementValidator(request);
        var state = new TournamentSearchState(validator.Context, new Dictionary<Guid, MatchPlacement>());
        var scorer = new TournamentPlacementScorer(validator.Context);
        var node = request.MatchGraphs[0].Matches[0];
        var first = scorer.ScoreBalancedDelta(node, Place(node, 9), state);
        var second = scorer.ScoreBalancedDelta(node, Place(node, 9, date: Date.AddDays(1)), state);
        Assert.True(second < first);
        Assert.True(first < 0 && second < 0);
        Assert.NotEqual(decimal.Truncate(second), second);
    }

    private static (TournamentSchedulingRequest, Dictionary<Guid, MatchPlacement>) SwapFixture()
    {
        var request = MultiDay(4, [2, 2]);
        var nodes = request.MatchGraphs[0].Matches.Select(n => n with { ExpectedDurationMinutes = n.Order <= 2 ? 40 : 20 }).ToArray();
        request = request with { MatchGraphs = [request.MatchGraphs[0] with { Matches = nodes }],
            Resources = request.Resources with { Days = request.Resources.Days.Select(d => d with { DayEnd = new(10, 30) }).ToArray() } };
        var placements = new Dictionary<Guid, MatchPlacement> {
            [nodes[0].Id] = Place(nodes[0], 9), [nodes[1].Id] = Place(nodes[1], 9, 40),
            [nodes[2].Id] = Place(nodes[2], 9, date: Date.AddDays(1)),
            [nodes[3].Id] = Place(nodes[3], 9, 20, date: Date.AddDays(1)) };
        return (request, placements);
    }

    private static IReadOnlyDictionary<Guid, MatchPlacement> Improve(TournamentSchedulingRequest request,
        IReadOnlyDictionary<Guid, MatchPlacement> placements)
    {
        var validator = new TournamentPlacementValidator(request);
        Assert.True(validator.ValidateSchedule(placements).IsValid);
        return TournamentScheduleBalancer.Improve(validator, new(validator.Context, placements),
            new(TournamentSchedulingOptions.Default, default), TournamentSchedulingOptions.Default);
    }

    private static double[] DayMinutes(TournamentSchedulingRequest request, IReadOnlyDictionary<Guid, MatchPlacement> placements) =>
        request.Resources.Days.Select(d => placements.Values.Where(p => p.DayLabel == d.DayLabel)
            .Sum(p => (p.EndTime - p.StartTime).TotalMinutes)).ToArray();

    private static TournamentSchedulingRequest MultiDay(int matches, int[] hours) => Request([Independent(matches)]) with {
        Resources = new(hours.Select((h, i) => new ScheduleDaySettings(Date.AddDays(i), new(9, 0), new(9 + h, 0), ["A"])).ToArray(), 1, 0, 12),
        Policy = new(ScheduleAutoSchedulingStrategy.BalancedRelaxed, [], false, [], []) };

    private static TournamentSchedulingResult.Success Generate(TournamentSchedulingRequest request, TournamentSchedulingOptions? options = null) =>
        Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request, options ?? TournamentSchedulingOptions.Default));

    private static int[] Counts(TournamentSchedulingRequest request, TournamentSchedulingResult.Success result) =>
        request.Resources.Days.Select(d => result.Schedule.Placements.Values.Count(p => p.DayLabel == d.DayLabel)).ToArray();
}
