using System.Collections;
using System.Reflection;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using Xunit;
using static BadmintonDraw.Tests.TournamentSchedulerTestData;

namespace BadmintonDraw.Tests;

public sealed class TournamentPlayerCapacityTests
{
    [Fact]
    public void ThreeProjectCompatibleOverloadIsProvenBeforeAnyPlacementSearch()
    {
        var request = ThreeProjects(2);
        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(request)).Detail;
        var issue = Assert.Single(failure.Violations);
        Assert.Equal(SchedulingConstraintCode.DailyMatchLimit, issue.Code);
        Assert.Contains("student:shared", issue.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("至少 3", issue.Message);
        Assert.Contains("1 个比赛日", issue.Message);
        Assert.Contains("每日 2", issue.Message);
        Assert.DoesNotContain(SchedulingConstraintCode.SearchExhausted, failure.Violations.Select(v => v.Code));
        Assert.All(failure.Capacity, d => Assert.Equal(0, d.RequiredPlacedMinutes));
        Assert.Equal(3, failure.UnplacedMatches.Count);
    }

    [Fact]
    public void ExactTotalCapacityBoundaryProducesACompleteValidSchedule()
    {
        var request = ThreeProjects(3);
        AssertComplete(request, new TournamentScheduler().Generate(request));
    }

    [Fact]
    public void MutuallyExclusiveWinnerAndLoserAreNotSummedAsRequiredAppearances()
    {
        var request = ExclusiveBranches();
        AssertComplete(request, new TournamentScheduler().Generate(request));
        Assert.Equal("ProvenBelow", Proof(request, "student:A", 3, 100_000));
    }

    [Fact]
    public void DistinctStudentIdsDoNotBecomeOnePlayerBecauseTheirNamesMatch()
    {
        var request = ThreeProjects(1);
        request = request with { MatchGraphs = request.MatchGraphs.Select((g, i) => g with
            { Matches = [g.Matches[0] with { SideA = Player("同名", $"different-{i}") }] }).ToArray() };
        AssertComplete(request, new TournamentScheduler().Generate(request));
    }

    [Fact]
    public void NewResultSnapshotCanRemoveAPreviouslyProvenOverload()
    {
        var source = Node(1, Player("A"), Player("B"));
        var next = Node(2, new EntrantSource.WinnerOf(source.Id), Player("C"));
        var other = Node(3, Player("B"), Player("D"), project: 2);
        var request = Request([new(Id(1), "v1", [source, next]), new(Id(2), "v1", [other])], 12);
        request = request with { Resources = request.Resources with { MaxPlayerMatchesPerDay = 2 },
            BaselinePlacements = new Dictionary<Guid, MatchPlacement> { [source.Id] = Place(source, 9) } };
        Assert.Equal("ProvenAtLeast", Proof(request, "student:B", 3, 100_000));
        var initial = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(request));
        Assert.All(initial.Detail.Capacity, d => Assert.Equal(0, d.RequiredPlacedMinutes));
        var key = new WorkspaceMatchKey(source.ProjectId, source.Id);
        var narrowed = request with { Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult>
            { [key] = new(key, Player("A"), Player("B"), "21-0", 30, DateTimeOffset.UtcNow) } };
        AssertComplete(narrowed, new TournamentScheduler().Generate(narrowed));
        Assert.Equal("ProvenAtLeast", Proof(request, "student:B", 3, 100_000));
    }

    [Fact]
    public void MalformedInputAndInvalidLockedPositionsKeepTheirOriginalPrecedence()
    {
        var request = ThreeProjects(2);
        var invalid = request with { Resources = request.Resources with { Days = [request.Resources.Days[0], request.Resources.Days[0]] } };
        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(invalid)).Detail;
        Assert.Contains(failure.Violations, v => v.Code == SchedulingConstraintCode.InvalidResources);
        Assert.DoesNotContain(failure.Violations, v => v.Code == SchedulingConstraintCode.DailyMatchLimit);
        var node = request.MatchGraphs[0].Matches[0];
        var locked = request with { LockedMatchIds = [node.Id], BaselinePlacements = new Dictionary<Guid, MatchPlacement>
            { [node.Id] = Place(node, 8) } };
        failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(locked)).Detail;
        Assert.Contains(failure.Violations, v => v.Code == SchedulingConstraintCode.DayBounds);
        Assert.DoesNotContain(failure.Violations, v => v.Code == SchedulingConstraintCode.DailyMatchLimit);
    }

    [Fact]
    public void LargeDailyLimitMultipliesWithoutOverflowOrFalseRejection()
    {
        var request = ThreeProjects(int.MaxValue);
        request = request with { Resources = request.Resources with { Days = [request.Resources.Days[0], request.Resources.Days[0] with { Date = Date.AddDays(1) }] } };
        AssertComplete(request, new TournamentScheduler().Generate(request));
    }

    [Fact]
    public void ZeroProofBudgetFallsBackToTheOriginalSearchAndCompleteValidator()
    {
        var request = ExclusiveBranches();
        AssertComplete(request, GenerateWithBudget(request, 0));
        // Skipping the optional preflight never skips the real daily-cap hard gate.
        var overload = Assert.IsType<TournamentSchedulingResult.Failure>(GenerateWithBudget(ThreeProjects(2), 0)).Detail;
        Assert.Contains(overload.Violations, v => v.Code == SchedulingConstraintCode.DailyMatchLimit);
        Assert.Contains(overload.Violations, v => v.Code == SchedulingConstraintCode.SearchExhausted);
        Assert.True(overload.Capacity.Sum(d => d.RequiredPlacedMinutes) > 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    public void IncompleteProofIsUnknownRatherThanAFabricatedMaximum(int budget)
    {
        Assert.Equal("Unknown", Proof(ThreeProjects(2), "student:shared", 3, budget));
    }

    [Fact]
    public void MatchGroupGuardReturnsUnknownInsteadOfRecursingWithoutBound()
    {
        var graph = new MatchGraph(Id(1), "v1", Enumerable.Range(1, 257)
            .Select(i => Node(i, Player("shared"), Player($"other-{i}"))).ToArray());
        Assert.Equal("Unknown", Proof(Request([graph]), "student:shared", 2, 100_000));
    }

    [Fact]
    public void DoublesPartnerSharesTheSinglesPlayersTotalCapacity()
    {
        var request = ThreeProjects(2);
        var third = request.MatchGraphs[2];
        request = request with { MatchGraphs = [request.MatchGraphs[0], request.MatchGraphs[1], third with
        {
            Matches = [third.Matches[0] with { SideA = new EntrantSource.Participant("pair", "双打搭档",
                [new("不同显示名", "SHARED"), new("另一搭档", "partner")]) }]
        }] };
        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(request)).Detail;
        Assert.Equal(SchedulingConstraintCode.DailyMatchLimit, Assert.Single(failure.Violations).Code);
        Assert.All(failure.Capacity, d => Assert.Equal(0, d.RequiredPlacedMinutes));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(24)]
    public void PartialRequestBudgetCannotTurnUnprovenCapacityIntoRejection(int budget)
    {
        // An actual feasible schedule, with raw union count three but compatible count two.
        // Budgets can end during grouping, sort preparation or exact proof; all must fall back.
        var request = ExclusiveBranches();
        AssertComplete(request, GenerateWithBudget(request, budget));
    }

    [Fact]
    public void LaterPlayersDoNotReceiveAFreshProofBudgetAfterEarlierPlayersUseIt()
    {
        var source = Node(1, Player("A"), Player("B"));
        var graph = new MatchGraph(Id(1), "v1", [source,
            Node(2, new EntrantSource.WinnerOf(source.Id), Player("C")),
            Node(3, new EntrantSource.LoserOf(source.Id), Player("D")),
            Node(4, Player("E"), Player("F")), Node(5, Player("E"), Player("G")), Node(6, Player("E"), Player("H"))]);
        var request = Request([graph], 12);
        request = request with { Resources = request.Resources with { MaxPlayerMatchesPerDay = 2 } };
        // A and B each have three alternatives but at most two compatible matches;
        // E is truly overloaded. The small shared budget runs out before proving E.
        var limited = Assert.IsType<TournamentSchedulingResult.Failure>(GenerateWithBudget(request, 75)).Detail;
        Assert.Contains(limited.Violations, v => v.Code == SchedulingConstraintCode.SearchExhausted);
        Assert.True(limited.Capacity.Sum(d => d.RequiredPlacedMinutes) > 0);
        var proved = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(request)).Detail;
        Assert.Equal(SchedulingConstraintCode.DailyMatchLimit, Assert.Single(proved.Violations).Code);
        Assert.All(proved.Capacity, d => Assert.Equal(0, d.RequiredPlacedMinutes));
    }

    private static TournamentSchedulingRequest ThreeProjects(int cap)
    {
        var graphs = Enumerable.Range(1, 3).Select(i => new MatchGraph(Id(i), "v1",
            [Node(i, Player("共享选手", i == 2 ? "SHARED" : "shared"), Player($"opponent-{i}"), project: i)])).ToArray();
        var request = Request(graphs, 12);
        return request with { Resources = request.Resources with { MaxPlayerMatchesPerDay = cap } };
    }

    private static TournamentSchedulingRequest ExclusiveBranches()
    {
        var first = Node(1, Player("A"), Player("B"));
        var winner = Node(2, new EntrantSource.WinnerOf(first.Id), Player("C"));
        var loser = Node(3, new EntrantSource.LoserOf(first.Id), Player("D"));
        var request = Request([new(Id(1), "v1", [first, winner, loser])], 12);
        return request with { Resources = request.Resources with { MaxPlayerMatchesPerDay = 2 } };
    }

    private static void AssertComplete(TournamentSchedulingRequest request, TournamentSchedulingResult result)
    {
        var success = Assert.IsType<TournamentSchedulingResult.Success>(result);
        Assert.Equal(request.MatchGraphs.Sum(g => g.Matches.Count), success.Schedule.Placements.Count);
        Assert.Empty(new TournamentPlacementValidator(request).ValidateSchedule(success.Schedule.Placements).Violations);
    }

    private static TournamentSchedulingResult GenerateWithBudget(TournamentSchedulingRequest request, int budget)
    {
        var method = typeof(TournamentScheduler).GetMethod("Generate", BindingFlags.NonPublic | BindingFlags.Instance,
            [typeof(TournamentSchedulingRequest), typeof(int)]);
        Assert.NotNull(method);
        return (TournamentSchedulingResult)method.Invoke(new TournamentScheduler(), [request, budget])!;
    }

    private static string Proof(TournamentSchedulingRequest request, string key, int target, int budget)
    {
        var validator = new TournamentPlacementValidator(request);
        Assert.True(validator.ValidateInput().IsValid);
        var context = typeof(TournamentPlacementValidator).GetProperty("Context", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(validator)!;
        var paths = (IDictionary)context.GetType().GetProperty("Paths", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(context)!;
        var assembly = typeof(TournamentScheduler).Assembly;
        var pathType = assembly.GetType("BadmintonDraw.Core.Scheduling.ConditionalPlayerPath")!;
        var groups = new List<Array>();
        foreach (DictionaryEntry match in paths)
        {
            var alternatives = ((IEnumerable)match.Value!).Cast<object>().Where(p => string.Equals(
                (string)pathType.GetProperty("PlayerKey")!.GetValue(p)!, key, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (alternatives.Length == 0) continue;
            var typed = Array.CreateInstance(pathType, alternatives.Length);
            for (var i = 0; i < alternatives.Length; i++) typed.SetValue(alternatives[i], i);
            groups.Add(typed);
        }
        var input = Array.CreateInstance(typeof(IReadOnlyList<>).MakeGenericType(pathType), groups.Count);
        for (var i = 0; i < groups.Count; i++) input.SetValue(groups[i], i);
        var method = assembly.GetType("BadmintonDraw.Core.Scheduling.ConditionalPlayerPaths")!
            .GetMethod("ProveAtLeast", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var budgetType = assembly.GetType("BadmintonDraw.Core.Scheduling.AppearanceProofBudget");
        Assert.NotNull(budgetType);
        var instance = Activator.CreateInstance(budgetType, BindingFlags.NonPublic | BindingFlags.Instance, null, [budget], null);
        var proof = method.Invoke(null, [input, target, instance])!.ToString()!;
        if (proof != "Unknown")
        {
            var exact = assembly.GetType("BadmintonDraw.Core.Scheduling.ConditionalPlayerPaths")!
                .GetMethod("MaximumAppearances", BindingFlags.NonPublic | BindingFlags.Static)!;
            var maximum = (int)exact.Invoke(null, [input, int.MaxValue])!;
            Assert.Equal(maximum >= target, proof == "ProvenAtLeast");
        }
        return proof;
    }
}
