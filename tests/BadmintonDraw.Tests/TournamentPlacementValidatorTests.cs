using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using Xunit;
using static BadmintonDraw.Tests.TournamentSchedulerTestData;

namespace BadmintonDraw.Tests;

public sealed class TournamentPlacementValidatorTests
{
    [Fact]
    public void ConditionalWinnerAndLoserBranchesAreMutuallyExclusiveButRepeatedWinnerPathsNeedRest()
    {
        var first = Node(1, Player("A"), Player("B"));
        var winner = Node(2, new EntrantSource.WinnerOf(first.Id), Player("C"));
        var loser = Node(3, new EntrantSource.LoserOf(first.Id), Player("D"));
        var repeated = Node(4, new EntrantSource.WinnerOf(first.Id), Player("E"));
        var request = Request([new(Id(1), "v1", [first, winner, loser, repeated])]);
        var validator = new TournamentPlacementValidator(request);
        var prior = new Dictionary<Guid, MatchPlacement> { [first.Id] = Place(first, 9), [winner.Id] = Place(winner, 10) };
        Assert.True(validator.ValidatePlacement(Place(loser, 10, court: "B"), prior).IsValid);
        Assert.Contains(validator.ValidatePlacement(Place(repeated, 10, court: "B"), prior).Violations, v => v.Code == SchedulingConstraintCode.PlayerOverlap);
        Assert.Contains(validator.ValidatePlacement(Place(repeated, 10, 30), prior).Violations, v => v.Code == SchedulingConstraintCode.MinimumRest);
    }

    [Fact]
    public void DailyLimitCountsDistinctSatisfiableAppearancesNotUnionOrAlternativePaths()
    {
        var first = Node(1, Player("A"), Player("B"));
        var winner = Node(2, new EntrantSource.WinnerOf(first.Id), Player("C"));
        var loser = Node(3, new EntrantSource.LoserOf(first.Id), Player("D"));
        var rejoin = Node(4, new EntrantSource.WinnerOf(winner.Id), new EntrantSource.WinnerOf(loser.Id));
        var graph = new MatchGraph(Id(1), "v1", [first, winner, loser, rejoin]);
        var request = Request([graph]);
        request = request with { Resources = request.Resources with { MaxPlayerMatchesPerDay = 3 } };
        var placements = new Dictionary<Guid, MatchPlacement> { [first.Id] = Place(first, 9), [winner.Id] = Place(winner, 10),
            [loser.Id] = Place(loser, 10, court: "B"), [rejoin.Id] = Place(rejoin, 11) };
        Assert.True(new TournamentPlacementValidator(request).ValidateSchedule(placements).IsValid);
        var load = new PlayerLoadForecastAnalyzer().Analyze(request, placements).Single(p => p.PlayerKey == "student:A");
        Assert.Equal(3, load.MaximumCount);
        Assert.Equal(2.5, load.ExpectedCount);
        Assert.Equal(.5, load.Distribution[2]);
        Assert.Equal(.5, load.Distribution[3]);
        request = request with { Resources = request.Resources with { MaxPlayerMatchesPerDay = 2 } };
        Assert.Contains(new TournamentPlacementValidator(request).ValidateSchedule(placements).Violations, v => v.Code == SchedulingConstraintCode.DailyMatchLimit);
    }

    [Fact]
    public void BothDoublesPartnersAndStudentIdsConstrainAcrossProjects()
    {
        var first = Node(1, new EntrantSource.Participant("pair", "pair", [new("甲", "001"), new("乙", "002")]), Player("X"));
        var other = Node(2, Player("renamed", "002"), Player("Y"), project: 2);
        var request = Request([new(Id(1), "v1", [first]), new(Id(2), "v1", [other])]);
        var validator = new TournamentPlacementValidator(request);
        var prior = new Dictionary<Guid, MatchPlacement> { [first.Id] = Place(first, 9) };
        Assert.Contains(validator.ValidatePlacement(Place(other, 9, court: "B"), prior).Violations, v => v.Code == SchedulingConstraintCode.PlayerOverlap);
        other = other with { SideA = Player("甲", "003") };
        request = request with { MatchGraphs = [request.MatchGraphs[0], new(Id(2), "v2", [other])] };
        Assert.True(new TournamentPlacementValidator(request).ValidatePlacement(Place(other, 9, court: "B"), prior).IsValid);
    }

    [Fact]
    public void ActualNonconsecutiveDatesGovernRestInsteadOfDayIndices()
    {
        var first = Node(1, Player("A"), Player("B"));
        var second = Node(2, Player("A"), Player("C"));
        var request = Request([new(Id(1), "v1", [first, second])]);
        request = request with { Resources = new([new(Date, new(9, 0), new(10, 0), ["A"]),
            new(Date.AddDays(3), new(9, 0), new(10, 0), ["A"])], 1, 48 * 60, 1) };
        Assert.True(new TournamentPlacementValidator(request).ValidateSchedule(new Dictionary<Guid, MatchPlacement>
            { [first.Id] = Place(first, 9), [second.Id] = Place(second, 9, date: Date.AddDays(3)) }).IsValid);
    }

    [Fact]
    public void CandidateChecksPredecessorsSuccessorsAndWholeScheduleCompleteness()
    {
        var first = Node(1, Player("A"), Player("B"));
        var second = Node(2, new EntrantSource.WinnerOf(first.Id), Player("C"));
        var validator = new TournamentPlacementValidator(Request([new(Id(1), "v1", [first, second])]));
        Assert.Contains(validator.ValidatePlacement(Place(second, 10), new Dictionary<Guid, MatchPlacement>()).Violations, v => v.Code == SchedulingConstraintCode.MissingPlacement);
        Assert.Contains(validator.ValidatePlacement(Place(first, 11), new Dictionary<Guid, MatchPlacement> { [second.Id] = Place(second, 10) }).Violations, v => v.Code == SchedulingConstraintCode.DependencyOrder);
        Assert.Contains(validator.ValidateSchedule(new Dictionary<Guid, MatchPlacement> { [first.Id] = Place(first, 9) }).Violations, v => v.Code == SchedulingConstraintCode.MissingPlacement);
    }

    [Fact]
    public void RefereeCapacityCountsConcurrentIntervalsNotAllTouchingMatches()
    {
        var graph = Independent(3);
        var request = Request([graph]);
        request = request with { Policy = request.Policy with { ProjectTimings = new Dictionary<Guid, ProjectMatchTiming>() } };
        var longMatch = graph.Matches[2] with { ExpectedDurationMinutes = 60 };
        request = request with { MatchGraphs = [graph with { Matches = [graph.Matches[0], graph.Matches[1], longMatch] }] };
        var others = new Dictionary<Guid, MatchPlacement> { [graph.Matches[0].Id] = Place(graph.Matches[0], 9), [graph.Matches[1].Id] = Place(graph.Matches[1], 9, 30) };
        Assert.True(new TournamentPlacementValidator(request).ValidatePlacement(Place(longMatch, 9, court: "B"), others).IsValid);
    }

    [Fact]
    public void SubMinuteGapDoesNotRoundUpToTheHardRestMinimum()
    {
        var first = Node(1, Player("A"), Player("B"));
        var second = Node(2, Player("A"), Player("C"));
        var validator = new TournamentPlacementValidator(Request([new(Id(1), "v1", [first, second])]));
        var earlier = new MatchPlacement(first.Id, Day, new(9, 0, 30), new(9, 30, 30), "A");
        Assert.Contains(validator.ValidatePlacement(Place(second, 9, 45), new Dictionary<Guid, MatchPlacement> { [first.Id] = earlier }).Violations,
            v => v.Code == SchedulingConstraintCode.MinimumRest);
    }

    [Fact]
    public void ConstraintAndQualityReportsUseTheFullGateForManualProposals()
    {
        var graph = Independent(2);
        var request = Request([graph]);
        var conflict = graph.Matches.ToDictionary(n => n.Id, n => Place(n, 9));
        var report = new ScheduleConstraintAnalyzer().Analyze(request, conflict);
        var quality = new TournamentScheduleQualityAnalyzer().Analyze(request, conflict);
        Assert.Contains(report.Violations, v => v.Code == SchedulingConstraintCode.CourtOverlap);
        Assert.Equal(report.Violations.Count, quality.HardConstraintCount);
        Assert.True(quality.HardConstraintCount > 0);
    }

    [Fact]
    public void GlobalDailyCapAppliesAcrossProjectsWithoutHiddenProjectLimits()
    {
        var matches = Enumerable.Range(1, 4).Select(i => Node(i, Player("shared"), Player($"B{i}"), project: i < 3 ? 1 : 2)).ToArray();
        var request = Request([new(Id(1), "a", matches[..2]), new(Id(2), "b", matches[2..])]);
        var placements = matches.ToDictionary(n => n.Id, n => Place(n, 8 + n.Order));
        request = request with { Resources = request.Resources with { MaxPlayerMatchesPerDay = 4 } };
        Assert.True(new TournamentPlacementValidator(request).ValidateSchedule(placements).IsValid);
        request = request with { Resources = request.Resources with { MaxPlayerMatchesPerDay = 3 } };
        Assert.Contains(new TournamentPlacementValidator(request).ValidateSchedule(placements).Violations, v => v.Code == SchedulingConstraintCode.DailyMatchLimit);
    }

    [Fact]
    public void CompletedOutcomeNarrowsOnlyTheActualWinnerPaths()
    {
        var first = Node(1, Player("A"), Player("B"));
        var winner = Node(2, new EntrantSource.WinnerOf(first.Id), Player("C"));
        var other = Node(3, Player("B"), Player("D"), project: 2);
        var key = new WorkspaceMatchKey(first.ProjectId, first.Id);
        var request = Request([new(Id(1), "v1", [first, winner]), new(Id(2), "v1", [other])]) with
        {
            BaselinePlacements = new Dictionary<Guid, MatchPlacement> { [first.Id] = Place(first, 9) },
            Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult> { [key] = new(key, Player("A"), Player("B"), "21-0", 30, DateTimeOffset.UtcNow) }
        };
        var placements = new Dictionary<Guid, MatchPlacement> { [first.Id] = Place(first, 9), [winner.Id] = Place(winner, 10), [other.Id] = Place(other, 10, court: "B") };
        Assert.True(new TournamentPlacementValidator(request).ValidateSchedule(placements).IsValid);
        request = request with { Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult>() };
        Assert.Contains(new TournamentPlacementValidator(request).ValidateSchedule(placements).Violations, v => v.Code == SchedulingConstraintCode.PlayerOverlap);
    }

    [Fact]
    public void LargeOutcomeForecastKeepsExactMaximumAndMarksProbabilityUnavailable()
    {
        var first = Enumerable.Range(1, 19).Select(i => Node(i, Player("A"), Player($"B{i}"), 1)).ToArray();
        var next = first.Select((n, i) => Node(i + 20, new EntrantSource.WinnerOf(n.Id), Player($"C{i}"), 1)).ToArray();
        var graph = new MatchGraph(Id(1), "v1", first.Concat(next).ToArray());
        var request = Request([graph]);
        var placements = graph.Matches.ToDictionary(n => n.Id, n => Place(n, 9 + n.Order / 60, n.Order % 60));
        var forecast = new PlayerLoadForecastAnalyzer().Analyze(request, placements).Single(p => p.PlayerKey == "student:A");
        Assert.False(forecast.IsExact);
        Assert.Equal(38, forecast.MaximumCount);
        Assert.Equal(19, forecast.ConfirmedCount);
        Assert.Null(forecast.ExpectedCount);
        Assert.Null(forecast.ProbabilityAtOrAboveLimit);
        Assert.Empty(forecast.Distribution);
    }
}
