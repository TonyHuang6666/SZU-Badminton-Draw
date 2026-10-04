using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using Xunit;
using static BadmintonDraw.Tests.TournamentSchedulerTestData;

namespace BadmintonDraw.Tests;

public sealed class TournamentBoundarySearchTests
{
    [Fact]
    public void DenseFallbackKeepsInteriorMinutesAnchoredToTheExactDayStart()
    {
        var graph = Independent(1);
        var request = Request([graph]) with { Resources = new([
            new(Date, new(9, 0, 30), new(10, 0, 30), ["A"])], 1, 0, 10) };
        var context = new GraphSchedulingCandidates(request);
        var day = request.Resources.Days[0];
        var empty = new Dictionary<Guid, MatchPlacement>();
        var sparse = TournamentCandidateEnumeration.Starts(day, graph.Matches[0].Id, context, empty, false).ToArray();
        Assert.DoesNotContain(new TimeOnly(9, 1, 30), sparse);
        var dense = TournamentCandidateEnumeration.Starts(day, graph.Matches[0].Id, context, empty).ToArray();
        Assert.Contains(new TimeOnly(9, 1, 30), dense);
        Assert.DoesNotContain(new TimeOnly(9, 1, 0), dense);
        Assert.All(sparse, boundary => Assert.Contains(boundary, dense));
    }

    [Fact]
    public void RecoveryCanChooseAnotherCourtAtTheSameTime()
    {
        var shortMatch = Node(1, Player("A"), Player("B"), 30);
        var longMatch = Node(2, Player("C"), Player("D"), 60);
        var request = Request([new MatchGraph(Id(1), "v1", [shortMatch, longMatch])], 10) with
        {
            Resources = new([new(Date, new(9, 0), new(10, 0), ["A", "B"],
                UnavailableCourtWindows: [new(new(9, 30), new(10, 0), ["B"])])], 2, 0, 10),
            Policy = new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], [])
        };
        var success = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request,
            new TournamentSchedulingOptions { MaxDecisionAlternatives = 4 }));
        Assert.Equal("B", success.Schedule.Placements[shortMatch.Id].Court);
        Assert.Equal(new TimeOnly(9, 0), success.Schedule.Placements[shortMatch.Id].StartTime);
        Assert.Equal("A", success.Schedule.Placements[longMatch.Id].Court);
        Assert.Empty(new TournamentPlacementValidator(request).ValidateSchedule(success.Schedule.Placements).Violations);
    }

    [Fact]
    public void RestBoundaryFromAnEarlierDateIsConsideredBeforeLaterFreeTime()
    {
        var first = Node(1, Player("shared"), Player("B"), 15);
        var second = Node(2, Player("shared"), Player("C"), 30);
        var locked = new MatchPlacement(first.Id, Day, new(9, 0), new(9, 15), "A");
        var request = Request([new MatchGraph(Id(1), "v1", [first, second])], 10) with
        {
            Resources = new([new(Date, new(9, 0), new(9, 15), ["A"]),
                new(Date.AddDays(1), new(9, 0), new(10, 0), ["A"])], 1, 24 * 60, 10),
            Policy = new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []),
            BaselinePlacements = new Dictionary<Guid, MatchPlacement> { [first.Id] = locked },
            LockedMatchIds = [first.Id]
        };
        var success = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Equal(new TimeOnly(9, 15), success.Schedule.Placements[second.Id].StartTime);
        Assert.Equal(locked, success.Schedule.Placements[first.Id]);
        Assert.Empty(new TournamentPlacementValidator(request).ValidateSchedule(success.Schedule.Placements).Violations);
    }
}
