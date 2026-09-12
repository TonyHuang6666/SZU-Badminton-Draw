using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using Xunit;
using static BadmintonDraw.Tests.TournamentSchedulerTestData;

namespace BadmintonDraw.Tests;

public sealed class TournamentSchedulerSearchEquivalenceTests
{
    [Fact]
    public void ShuffledNonconsecutiveDaysUseChronologicalScoresAndActualRest()
    {
        var first = Node(1, Player("shared"), Player("B"));
        var second = Node(2, Player("shared"), Player("C"));
        var request = Request([new MatchGraph(Id(1), "v1", [first, second])], 10) with
        {
            Resources = new([new(Date.AddDays(3), new(9, 0), new(10, 0), ["A"]),
                new(Date, new(9, 0), new(10, 0), ["A"])], 1, 48 * 60, 1),
            Policy = new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], [])
        };
        var success = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Equal(Place(first, 9), success.Schedule.Placements[first.Id]);
        Assert.Equal(Place(second, 9, date: Date.AddDays(3)), success.Schedule.Placements[second.Id]);
        Assert.Empty(new TournamentPlacementValidator(request).ValidateSchedule(success.Schedule.Placements).Violations);
    }

    [Fact]
    public void DuplicateResourceDatesRemainTypedFailuresBeforeSearch()
    {
        var request = Request([Independent(1)], 10);
        request = request with { Resources = request.Resources with { Days = [request.Resources.Days[0], request.Resources.Days[0]] } };
        Assert.Contains(new TournamentPlacementValidator(request).ValidateInput().Violations,
            v => v.Code == SchedulingConstraintCode.InvalidResources);
        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(request));
        Assert.Contains(failure.Detail.Violations, v => v.Code == SchedulingConstraintCode.InvalidResources);
    }

    [Theory]
    [InlineData(false, "unknown-day")]
    [InlineData(true, "unknown-day")]
    [InlineData(false, null)]
    [InlineData(true, null)]
    public void UnknownManualPlacementDayRemainsTypedForCandidateOrExistingPosition(bool unknownExisting, string? unknownDay)
    {
        var graph = Independent(2);
        var validator = new TournamentPlacementValidator(Request([graph], 10));
        var candidate = Place(graph.Matches[0], 9);
        var existing = Place(graph.Matches[1], 9, court: "B");
        if (unknownExisting) existing = existing with { DayLabel = unknownDay! };
        else candidate = candidate with { DayLabel = unknownDay! };
        var result = validator.ValidatePlacement(candidate, new Dictionary<Guid, MatchPlacement> { [existing.MatchId] = existing });
        Assert.Contains(result.Violations, v => v.Code == SchedulingConstraintCode.DayBounds);
    }

    [Fact]
    public void EqualScoresKeepFirstLegalCourtInResourceOrder()
    {
        var graph = Independent(2);
        var request = Request([graph], 10) with
        {
            Resources = new([new(Date, new(9, 0), new(10, 0), ["B", "A"])], 2, 15, 12),
            Policy = new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], [])
        };
        var success = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Equal(Place(graph.Matches[0], 9, court: "B"), success.Schedule.Placements[graph.Matches[0].Id]);
        Assert.Equal(Place(graph.Matches[1], 9, court: "A"), success.Schedule.Placements[graph.Matches[1].Id]);
        Assert.Empty(new TournamentPlacementValidator(request).ValidateSchedule(success.Schedule.Placements).Violations);
    }

    [Fact]
    public void AnInvalidCheapestCandidateDoesNotBecomeTheBestScore()
    {
        var graph = Independent(1);
        var request = Request([graph], 10) with
        {
            Resources = new([new(Date, new(9, 0), new(10, 0), ["A", "B"],
                UnavailableCourtWindows: [new(new(9, 0), new(10, 0), ["A"])])], 2, 15, 12),
            Policy = new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], [])
        };
        var success = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Equal(Place(graph.Matches[0], 9, court: "B"), Assert.Single(success.Schedule.Placements).Value);
        Assert.Equal(0, success.Quality.HardConstraintCount);
    }

    [Fact]
    public void LaterExplicitPreferredDayCanImproveAnEarlierLegalCompactPlacement()
    {
        var graph = Independent(1);
        graph = graph with { Matches = [graph.Matches[0] with { IsChampionshipFinal = true }] };
        var request = Request([graph], 10) with
        {
            Resources = new([new(Date, new(9, 0), new(10, 0), ["A"]),
                new(Date.AddDays(1), new(9, 0), new(10, 0), ["A"])], 1, 15, 12),
            Policy = new(ScheduleAutoSchedulingStrategy.Compact, [], false, [],
                [new(graph.ProjectId, TournamentFinalDayMatchCategory.Final, TournamentFinalDayPreference.StronglyPreferFinalDay)])
        };
        var success = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Equal(Place(graph.Matches[0], 9, date: Date.AddDays(1)), Assert.Single(success.Schedule.Placements).Value);
        Assert.Equal(0, success.Quality.HardConstraintCount);
    }

    [Fact]
    public void ExactSubminuteBaselineOnSecondCourtBeatsEarlierLegalCandidates()
    {
        var graph = Independent(1);
        var baseline = new MatchPlacement(graph.Matches[0].Id, Day, new(9, 0, 30), new(9, 30, 30), "B");
        var request = Request([graph], 10) with
        {
            BaselinePlacements = new Dictionary<Guid, MatchPlacement> { [baseline.MatchId] = baseline },
            Policy = new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], [])
        };
        var success = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Equal(baseline, Assert.Single(success.Schedule.Placements).Value);
        Assert.Equal(0, success.Quality.MovedMatchCount);
        Assert.Empty(new TournamentPlacementValidator(request).ValidateSchedule(success.Schedule.Placements).Violations);
    }

    [Fact]
    public void NoLegalBestRetainsCompleteFailureDiagnosticsAfterPartialSearch()
    {
        var graph = Independent(2);
        var request = Request([graph], 10) with
        {
            Resources = new([new(Date, new(9, 0), new(9, 30), ["A"])], 1, 15, 12),
            Policy = new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], [])
        };
        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(request)).Detail;
        Assert.Equal(graph.Matches[1].Id, Assert.Single(failure.UnplacedMatches).MatchId);
        Assert.Contains(failure.Violations, v => v.Code == SchedulingConstraintCode.CourtOverlap);
        Assert.Contains(failure.Violations, v => v.Code == SchedulingConstraintCode.RefereeCapacity);
        Assert.Contains(failure.Violations, v => v.Code == SchedulingConstraintCode.SearchExhausted);
        Assert.Equal(30, Assert.Single(failure.Capacity).RequiredPlacedMinutes);
        Assert.Null(request.BaselinePlacements);
    }
}
