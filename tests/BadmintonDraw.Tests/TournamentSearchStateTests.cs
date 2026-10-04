using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core;
using Xunit;
using static BadmintonDraw.Tests.TournamentSchedulerTestData;

namespace BadmintonDraw.Tests;

public sealed class TournamentSearchStateTests
{
    [Theory]
    [InlineData(ScheduleAutoSchedulingStrategy.Compact)]
    [InlineData(ScheduleAutoSchedulingStrategy.BalancedRelaxed)]
    [InlineData(ScheduleAutoSchedulingStrategy.FinalsDayFriendly)]
    public void CourtScoreReusePreservesExactRankAndHundredPointBaselinePenalty(ScheduleAutoSchedulingStrategy strategy)
    {
        var graph = Independent(2);
        graph = graph with { Matches = graph.Matches.Select((n, i) => n with { IsChampionshipFinal = i == 0 }).ToArray() };
        foreach (var baseline in new[] { false, true })
        {
            var request = Request([graph]);
            request = request with { Policy = request.Policy with { Strategy = strategy, SynchronizeStageWaves = true,
                StageWaveTargets = [new(Day, 1)], FinalDayRules = [new(graph.ProjectId, TournamentFinalDayMatchCategory.Final, TournamentFinalDayPreference.PreferFinalDay)] },
                BaselinePlacements = baseline ? new Dictionary<Guid, MatchPlacement> { [graph.Matches[0].Id] = Place(graph.Matches[0], 9, court: "a") } : null };
            var context = new GraphSchedulingCandidates(request);
            context.ApplyEffectivePolicy();
            var scorer = new TournamentPlacementScorer(context);
            var state = new TournamentSearchState(context, new Dictionary<Guid, MatchPlacement>());
            var first = Place(graph.Matches[0], 10, court: "A");
            var rank = scorer.Rank(graph.Matches[0], first, state);
            foreach (var court in new[] { "a", "B", "C" })
            {
                var reused = scorer.RankForCourt(rank, graph.Matches[0].Id, first.Court, court);
                Assert.Equal(scorer.Rank(graph.Matches[0], first with { Court = court }, state), reused);
                var delta = baseline && court != "a" ? 100 : 0;
                Assert.Equal(delta, strategy == ScheduleAutoSchedulingStrategy.BalancedRelaxed
                    ? reused.Secondary - rank.Secondary : reused.ExplicitPreference - rank.ExplicitPreference);
                Assert.Equal(rank.LoadDelta, reused.LoadDelta);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void CachePeaksMeasureEntriesAfterEvictionAndSurviveInvalidation(int limit)
    {
        var graph = Independent(2);
        var budget = new SchedulingWorkBudget(new() { MaxCacheEntries = limit }, default);
        Assert.True(GraphSchedulingCandidates.TryCreate(Request([graph]), budget, out var context));
        var state = new TournamentSearchState(context!, new Dictionary<Guid, MatchPlacement>());
        for (var i = 0; i < 5; i++)
        {
            state.CacheTimeCheck(new(graph.Matches[0].Id, Day, new(9, i), new(10, i), null), []);
            context!.CacheAppearanceCheck(new("player", [Id(100 + i)]), false);
        }
        state.Add(Place(graph.Matches[0], 9));
        Assert.False(state.TryGetTimeCheck(new(graph.Matches[0].Id, Day, new(9, 4), new(10, 4), null), out _));
        var peaks = budget.CachePeaks;
        Assert.Equal(limit, peaks.GetValueOrDefault("AppearanceChecks"));
        Assert.Equal(limit, peaks.GetValueOrDefault("StateTimeChecks"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ProofCacheEvictionRetainsExactGroupIdentityAndSharedBudget(int cacheCapacity)
    {
        var matches = Enumerable.Range(1, 3).Select(i => Node(i, Player("A"), Player($"B{i}"))).ToArray();
        var request = Request([new(Id(1), "v1", matches)]);
        request = request with { Resources = request.Resources with { MaxPlayerMatchesPerDay = 1 } };
        var budget = new SchedulingWorkBudget(new() { MaxCacheEntries = cacheCapacity }, default);
        Assert.True(GraphSchedulingCandidates.TryCreate(request, budget, out var context));
        var validator = TournamentPlacementValidator.FromContext(context!);
        var state = new TournamentSearchState(context!, new Dictionary<Guid, MatchPlacement>());
        state.Add(Place(matches[0], 9));
        long Validate(int hour)
        {
            var before = budget.UsedWorkUnits[SchedulingRunPhase.Search];
            var result = validator.ValidatePlacementBounded(Place(matches[1], hour), state, budget, SchedulingRunPhase.Search);
            Assert.Equal(BoundedValidationStatus.Invalid, result.Status);
            Assert.Contains(result.Violations, v => v.Code == SchedulingConstraintCode.DailyMatchLimit);
            return budget.UsedWorkUnits[SchedulingRunPhase.Search] - before;
        }
        var first = Validate(10);
        var repeated = Validate(11);
        if (cacheCapacity == 0) Assert.Equal(first, repeated);
        else Assert.True(repeated < first);
        state.Remove(matches[0].Id);
        state.Add(Place(matches[2], 9));
        var differentGroup = Validate(12);
        state.Remove(matches[2].Id);
        state.Add(Place(matches[0], 9));
        var evicted = Validate(13);
        Assert.Equal(first, differentGroup);
        Assert.Equal(first, evicted);
        Assert.Equal(first + repeated + differentGroup + evicted, budget.UsedWorkUnits[SchedulingRunPhase.Search]);
    }

    [Fact]
    public void CourtChoiceReusesTimeRulesButRechecksCourtOverlap()
    {
        var graph = Independent(2);
        var validator = new TournamentPlacementValidator(Request([graph]));
        var state = new TournamentSearchState(validator.Context,
            new Dictionary<Guid, MatchPlacement> { [graph.Matches[0].Id] = Place(graph.Matches[0], 9) });
        var budget = new SchedulingWorkBudget(TournamentSchedulingOptions.Default, default);
        var blocked = validator.ValidatePlacementBounded(Place(graph.Matches[1], 9), state, budget, SchedulingRunPhase.Search);
        var firstCost = budget.UsedWorkUnits[SchedulingRunPhase.Search];
        Assert.Contains(blocked.Violations, v => v.Code == SchedulingConstraintCode.CourtOverlap);
        var valid = validator.ValidatePlacementBounded(Place(graph.Matches[1], 9, court: "B"), state, budget, SchedulingRunPhase.Search);
        Assert.Equal(BoundedValidationStatus.Valid, valid.Status);
        Assert.True(budget.UsedWorkUnits[SchedulingRunPhase.Search] - firstCost < firstCost);
    }

    [Fact]
    public void AddRemoveRestoresAllIndexes()
    {
        var graph = Independent(3);
        var validator = new TournamentPlacementValidator(Request([graph]));
        var initial = new Dictionary<Guid, MatchPlacement> { [graph.Matches[0].Id] = Place(graph.Matches[0], 9) };
        var state = new TournamentSearchState(validator.Context, initial);
        var before = state.Snapshot();
        initial.Clear();
        var candidate = Place(graph.Matches[1], 10, court: "b");
        state.Add(candidate);
        Assert.Equal(TimeSpan.FromMinutes(60).Ticks, state.UsedTicksByDay[Day]);
        Assert.Contains(candidate, state.OnCourt("B"));
        Assert.Contains(candidate, state.OnDay(Day));
        Assert.Contains(candidate, state.ForPlayer("STUDENT:A2"));
        Assert.Equal(candidate, state.Remove(candidate.MatchId));
        Assert.Equal(before.OrderBy(x => x.Key), state.Snapshot().OrderBy(x => x.Key));
        Assert.Equal(TimeSpan.FromMinutes(30).Ticks, state.UsedTicksByDay[Day]);
        Assert.Empty(state.OnCourt("B"));
        Assert.Single(state.OnDay(Day));
        Assert.Empty(state.ForPlayer("student:A2"));
        state.Remove(graph.Matches[0].Id);
        Assert.Empty(state.UsedTicksByDay);
        Assert.Single(before);
    }

    [Fact]
    public void LockedPlacementCannotBeRemoved()
    {
        var graph = Independent(1);
        var placement = Place(graph.Matches[0], 9);
        var initial = new Dictionary<Guid, MatchPlacement> { [placement.MatchId] = placement };
        var validator = new TournamentPlacementValidator(Request([graph]) with
            { BaselinePlacements = initial, LockedMatchIds = [placement.MatchId] });
        var state = new TournamentSearchState(validator.Context, initial);
        Assert.Throws<InvalidOperationException>(() => state.Remove(placement.MatchId));
        Assert.Equal(placement, state.Placements[placement.MatchId]);
        Assert.Equal(TimeSpan.FromMinutes(30).Ticks, state.UsedTicksByDay[Day]);
    }

    [Fact]
    public void DailyCapCacheInvalidatesAfterRollback()
    {
        var first = Node(1, Player("A"), Player("B"));
        var second = Node(2, Player("A"), Player("C"));
        var request = Request([new(Id(1), "v1", [first, second])]);
        request = request with { Resources = request.Resources with { MaxPlayerMatchesPerDay = 1 } };
        var validator = new TournamentPlacementValidator(request);
        var state = new TournamentSearchState(validator.Context, new Dictionary<Guid, MatchPlacement>());
        var budget = new SchedulingWorkBudget(TournamentSchedulingOptions.Default, default);
        state.Add(Place(first, 9));
        Assert.Equal(BoundedValidationStatus.Invalid,
            validator.ValidatePlacementBounded(Place(second, 10), state, budget, SchedulingRunPhase.Search).Status);
        state.Remove(first.Id);
        Assert.Equal(BoundedValidationStatus.Valid,
            validator.ValidatePlacementBounded(Place(second, 10), state, budget, SchedulingRunPhase.Search).Status);
        state.Add(Place(first, 9));
        Assert.Equal(BoundedValidationStatus.Invalid,
            validator.ValidatePlacementBounded(Place(second, 10), state, budget, SchedulingRunPhase.Search).Status);
    }
}
