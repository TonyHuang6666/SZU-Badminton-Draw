using BadmintonDraw.Core.Scheduling;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class ConditionalAppearanceProofTests
{
    [Fact]
    public void IndependentProjectsCombineEightSevenSixWitness()
    {
        var groups = IndependentChains(8, 7, 6);
        var proof = Prove(groups, 21);
        Assert.Equal(21, proof.LowerBound);
        Assert.Equal(21, proof.Witness.MatchIds.Distinct().Count());
        Assert.True(WitnessSatisfiesGroups(groups, proof.Witness));
    }

    [Fact]
    public void AlternativePathsForOneMatchStayInOneComponent()
    {
        var x = Guid.NewGuid();
        var y = Guid.NewGuid();
        AppearanceGroup[] groups = [Group(Path((x, true)), Path((y, true))),
            Group(Path((x, false))), Group(Path((y, false)))];
        var proof = Prove(groups);
        Assert.Equal(2, proof.LowerBound);
        Assert.Equal(2, proof.UpperBound);
        Assert.True(proof.IsExact);
        Assert.True(WitnessSatisfiesGroups(groups, proof.Witness));
    }

    [Fact]
    public void WinnerAndLoserAreNotDoubleCounted()
    {
        var outcome = Guid.NewGuid();
        AppearanceGroup[] groups = [Group(Path()), Group(Path((outcome, true))), Group(Path((outcome, false)))];
        var proof = Prove(groups);
        Assert.Equal(2, proof.LowerBound);
        Assert.Equal(2, proof.UpperBound);
        Assert.True(proof.IsExact);
        Assert.True(WitnessSatisfiesGroups(groups, proof.Witness));
    }

    [Fact]
    public void UnconditionalMatchCountsOnce()
    {
        AppearanceGroup[] groups = [Group(Path(), Path(), Path())];
        var proof = Prove(groups);
        Assert.Equal(1, proof.LowerBound);
        Assert.Single(proof.Witness.MatchIds);
        Assert.Empty(proof.Witness.Conditions);
        Assert.True(proof.IsExact);
    }

    [Fact]
    public void ZeroBudgetIsUnknown()
    {
        var groups = IndependentChains(3);
        var proof = Prove(groups, work: 0);
        Assert.Equal(0, proof.LowerBound);
        Assert.Equal(3, proof.UpperBound);
        Assert.False(proof.IsExact);
        Assert.True(proof.BudgetExhausted);
        Assert.True(WitnessSatisfiesGroups(groups, proof.Witness));
    }

    [Fact]
    public void ThresholdWitnessDoesNotClaimTheMaximum()
    {
        var groups = IndependentChains(5);
        var proof = Prove(groups, stopAt: 2);
        Assert.InRange(proof.LowerBound, 2, 4);
        Assert.Equal(5, proof.UpperBound);
        Assert.False(proof.IsExact);
        Assert.False(proof.BudgetExhausted);
        Assert.True(WitnessSatisfiesGroups(groups, proof.Witness));
    }

    [Fact]
    public void OversizedComponentReturnsReliableUnknownBounds()
    {
        var groups = IndependentChains(257);
        var proof = Prove(groups);
        Assert.False(proof.IsExact);
        Assert.InRange(proof.LowerBound, 0, 257);
        Assert.Equal(257, proof.UpperBound);
        Assert.True(WitnessSatisfiesGroups(groups, proof.Witness));
    }

    [Fact]
    public void IndependentComponentsCanExceedTheRecursionGroupLimit()
    {
        var groups = Enumerable.Range(0, 300).Select(_ => Group(Path())).ToArray();
        var proof = Prove(groups);
        Assert.Equal(300, proof.LowerBound);
        Assert.True(proof.IsExact);
        Assert.True(WitnessSatisfiesGroups(groups, proof.Witness));
    }

    [Fact]
    public void DuplicateMatchIdsAreOneAppearanceWithAlternativePaths()
    {
        var match = Guid.NewGuid();
        var x = Guid.NewGuid();
        AppearanceGroup[] groups = [new(match, [Path((x, true))]), new(match, [Path((x, false))]), Group(Path((x, true)))];
        var proof = Prove(groups);
        Assert.Equal(2, proof.LowerBound);
        Assert.Equal(2, proof.UpperBound);
        Assert.True(WitnessSatisfiesGroups(groups, proof.Witness));
    }

    [Fact]
    public void EmptyAlternativesCannotWitnessAMatch()
    {
        AppearanceGroup[] groups = [Group(), Group(Path())];
        var proof = Prove(groups);
        Assert.Equal(1, proof.LowerBound);
        Assert.Equal(1, proof.UpperBound);
        Assert.True(proof.IsExact);
        Assert.True(WitnessSatisfiesGroups(groups, proof.Witness));
    }

    [Fact]
    public void ExhaustionAndCancellationPreserveBoundsAcrossSharedCalls()
    {
        var groups = IndependentChains(8, 7, 6);
        var budget = new SchedulingWorkBudget(new() { PreflightWorkUnits = 100 }, default);
        for (var i = 0; i < 3; i++)
        {
            var proof = ConditionalAppearanceProof.Prove(groups, int.MaxValue, budget, SchedulingRunPhase.Preflight);
            Assert.InRange(proof.LowerBound, 0, 21);
            Assert.Equal(21, proof.UpperBound);
            Assert.False(proof.IsExact);
            Assert.True(proof.BudgetExhausted);
            Assert.True(WitnessSatisfiesGroups(groups, proof.Witness));
        }
        Assert.InRange(budget.UsedWorkUnits[SchedulingRunPhase.Preflight], 1, 100);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceled = new SchedulingWorkBudget(TournamentSchedulingOptions.Default, cancellation.Token);
        var result = ConditionalAppearanceProof.Prove(groups, 21, canceled, SchedulingRunPhase.Preflight);
        Assert.Equal(0, result.LowerBound);
        Assert.False(result.IsExact);
        Assert.True(result.BudgetExhausted);
        Assert.Equal(0, canceled.UsedWorkUnits[SchedulingRunPhase.Preflight]);
    }

    [Fact]
    public void SeededSmallProblemsHaveSoundBoundsAndIndependentlyVerifiableWitnesses()
    {
        var random = new Random(731_009);
        for (var sample = 0; sample < 100; sample++)
        {
            var variables = Enumerable.Range(0, random.Next(1, 9)).Select(_ => Guid.NewGuid()).ToArray();
            var groups = Enumerable.Range(0, random.Next(1, 10)).Select(_ => Group(
                Enumerable.Range(0, random.Next(0, 4)).Select(_ => Path(variables
                    .Where(_ => random.Next(3) == 0).Select(v => (v, random.Next(2) == 0)).ToArray())).ToArray())).ToArray();
            var exact = EnumerateMaximum(groups);
            foreach (var work in new long[] { 0, 10, 100, 1_000, 10_000_000 })
            {
                var proof = Prove(groups, sample % 2 == 0 ? int.MaxValue : random.Next(1, groups.Length + 1), work);
                Assert.True(proof.LowerBound <= exact && exact <= proof.UpperBound,
                    $"Sample {sample}, work {work}: [{proof.LowerBound}, {proof.UpperBound}], exact {exact}");
                Assert.Equal(proof.LowerBound, proof.Witness.MatchIds.Count);
                Assert.True(WitnessSatisfiesGroups(groups, proof.Witness));
                if (proof.IsExact) Assert.Equal(exact, proof.LowerBound);
                if (work == 10_000_000 && sample % 2 == 0) Assert.True(proof.IsExact);
            }
        }
    }

    private static AppearanceBounds Prove(IReadOnlyList<AppearanceGroup> groups, int stopAt = int.MaxValue, long work = 10_000_000) =>
        ConditionalAppearanceProof.Prove(groups, stopAt,
            new SchedulingWorkBudget(new() { PreflightWorkUnits = work }, default), SchedulingRunPhase.Preflight);

    private static AppearanceGroup Group(params ConditionalPlayerPath[] paths) => new(Guid.NewGuid(), paths);
    private static ConditionalPlayerPath Path(params (Guid Id, bool Value)[] conditions) =>
        new("player", "Player", conditions.ToDictionary(x => x.Id, x => x.Value));

    private static IReadOnlyList<AppearanceGroup> IndependentChains(params int[] lengths)
    {
        var groups = new List<AppearanceGroup>();
        foreach (var length in lengths)
        {
            var variables = Enumerable.Range(0, length).Select(_ => Guid.NewGuid()).ToArray();
            for (var i = 0; i < length; i++)
                groups.Add(Group(Path(variables.Take(i + 1).Select(id => (id, true)).ToArray())));
        }
        return groups;
    }

    // Independent oracle: enumerate complete outcome assignments, then count satisfied matches.
    private static int EnumerateMaximum(IReadOnlyList<AppearanceGroup> groups)
    {
        var variables = groups.SelectMany(g => g.Alternatives).SelectMany(p => p.Conditions.Keys).Distinct().ToArray();
        Assert.InRange(variables.Length, 0, 8);
        var maximum = 0;
        for (var mask = 0; mask < (1 << variables.Length); mask++)
        {
            var assignment = variables.Select((id, bit) => (id, value: (mask & (1 << bit)) != 0)).ToDictionary(x => x.id, x => x.value);
            maximum = Math.Max(maximum, groups.GroupBy(g => g.MatchId).Count(match => match.SelectMany(g => g.Alternatives)
                .Any(p => p.Conditions.All(pair => assignment[pair.Key] == pair.Value))));
        }
        return maximum;
    }

    private static bool WitnessSatisfiesGroups(IReadOnlyList<AppearanceGroup> groups, AppearanceWitness witness) =>
        witness.MatchIds.Count == witness.MatchIds.Distinct().Count() &&
        witness.MatchIds.All(id => groups.Where(g => g.MatchId == id).SelectMany(g => g.Alternatives)
            .Any(p => p.Conditions.All(pair => witness.Conditions.TryGetValue(pair.Key, out var value) && value == pair.Value)));
}
