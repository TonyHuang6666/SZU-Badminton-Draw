using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using Xunit;
using static BadmintonDraw.Tests.TournamentSchedulerTestData;

namespace BadmintonDraw.Tests;

public sealed class TournamentSchedulingFailureTests
{
    [Theory]
    [InlineData(13, false)] [InlineData(15, true)]
    public void FourHourWindowSafelyFailsAndSixHourWindowFits(int endHour, bool success)
    {
        var graph = new MatchGraph(Id(1), "v1", Enumerable.Range(1, 6).Select(i => Node(i, Player("shared"), Player($"B{i}"), 45)).ToArray());
        var request = Request([graph], endHour);
        var result = new TournamentScheduler().Generate(request);
        if (success)
        {
            var schedule = Assert.IsType<TournamentSchedulingResult.Success>(result).Schedule;
            Assert.Equal(6, schedule.Placements.Count);
            Assert.Empty(new TournamentPlacementValidator(request).ValidateSchedule(schedule.Placements).Violations);
        }
        else
        {
            var failure = Assert.IsType<TournamentSchedulingResult.Failure>(result).Detail;
            Assert.NotEmpty(failure.UnplacedMatches);
            Assert.Contains(failure.Violations, v => v.Code == SchedulingConstraintCode.MinimumRest);
            Assert.NotEmpty(failure.Capacity);
            Assert.NotEmpty(failure.Suggestions);
        }
    }

    [Fact]
    public void BrokenPredecessorFailsWithAllBlockedDescendantsAndStableProjectIdentity()
    {
        var first = Node(1, Player("A"), Player("B"), 180);
        var next = Node(2, new EntrantSource.WinnerOf(first.Id), Player("C"));
        var request = Request([new(Id(1), "v1", [first, next])], 10) with { ProjectNames = new Dictionary<Guid, string> { [Id(1)] = "男单" } };
        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(request)).Detail;
        Assert.Equal(new[] { first.Id, next.Id }, failure.UnplacedMatches.Select(m => m.MatchId));
        Assert.All(failure.UnplacedMatches, m => { Assert.Equal(Id(1), m.ProjectId); Assert.Equal("男单", m.ProjectName); });
        Assert.Contains(SchedulingConstraintCode.MissingPlacement, failure.UnplacedMatches[1].ConstraintCodes);
    }

    [Theory]
    [InlineData("missing", SchedulingConstraintCode.MissingDependency)]
    [InlineData("cycle", SchedulingConstraintCode.CyclicDependency)]
    [InlineData("cross", SchedulingConstraintCode.CrossProjectDependency)]
    public void InvalidGraphEdgesReturnTypedFailuresBeforeSearch(string kind, SchedulingConstraintCode expected)
    {
        var first = Node(1, kind == "cycle" ? new EntrantSource.WinnerOf(Id(102)) : Player("A"), Player("B"));
        var second = Node(2, new EntrantSource.WinnerOf(kind == "missing" ? Id(999) : first.Id), Player("C"), project: kind == "cross" ? 2 : 1);
        MatchGraph[] graphs = kind == "cross" ? [new(Id(1), "v1", [first]), new(Id(2), "v1", [second])] : [new(Id(1), "v1", [first, second])];
        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(Request(graphs))).Detail;
        Assert.Contains(failure.Violations, v => v.Code == expected);
    }

    [Fact]
    public void InvalidLockedPlacementsFailWithoutMutatingTheBaseline()
    {
        var graph = Independent(2);
        var original = graph.Matches.ToDictionary(n => n.Id, n => Place(n, 9));
        var request = Request([graph]) with { BaselinePlacements = original, LockedMatchIds = original.Keys.ToArray() };
        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(request)).Detail;
        Assert.Contains(failure.Violations, v => v.Code == SchedulingConstraintCode.CourtOverlap);
        Assert.All(original.Values, p => Assert.Equal(new TimeOnly(9, 0), p.StartTime));
    }

    [Fact]
    public void LockWithoutBaselineAndForeignTimingRuleFailClearly()
    {
        var graph = Independent(1);
        var request = Request([graph]) with { LockedMatchIds = [graph.Matches[0].Id, Id(999)] };
        Assert.Contains(new TournamentPlacementValidator(request).ValidateInput().Violations, v => v.Code == SchedulingConstraintCode.LockedPlacement);
        request = Request([graph]);
        request = request with { Policy = request.Policy with { ProjectTimings = new Dictionary<Guid, ProjectMatchTiming> { [Id(999)] = new(30) } } };
        Assert.Contains(new TournamentPlacementValidator(request).ValidateInput().Violations, v => v.Code == SchedulingConstraintCode.InvalidPolicy);
    }

    [Fact]
    public void ForeignWinnerCannotNarrowConditionalPaths()
    {
        var first = Node(1, Player("A"), Player("B"));
        var next = Node(2, new EntrantSource.WinnerOf(first.Id), Player("C"));
        var key = new WorkspaceMatchKey(first.ProjectId, first.Id);
        var request = Request([new(Id(1), "v1", [first, next])]) with
        {
            BaselinePlacements = new Dictionary<Guid, MatchPlacement> { [first.Id] = Place(first, 9) },
            Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult> { [key] = new(key, Player("FOREIGN"), Player("B"), "21-0", 30, DateTimeOffset.UtcNow) }
        };
        Assert.Contains(new TournamentPlacementValidator(request).ValidateInput().Violations, v => v.Code == SchedulingConstraintCode.InvalidResult);
    }

    [Fact]
    public void ResourceWindowsOutsideTheDeclaredDayAreInvalid()
    {
        var request = Request([Independent(1)], 11);
        request = request with { Resources = request.Resources with { Days = [request.Resources.Days[0] with
            { RefereeCapacityWindows = [new(new(8, 0), new(10, 0), 1)] }] } };
        Assert.Contains(new TournamentPlacementValidator(request).ValidateInput().Violations, v => v.Code == SchedulingConstraintCode.InvalidResources);
    }

    [Fact]
    public void IdentityFreeEntrantsCannotBypassPlayerConstraints()
    {
        var graph = Independent(1);
        graph = graph with { Matches = [graph.Matches[0] with { SideA = new EntrantSource.Participant("", "", []) }] };
        Assert.Contains(new TournamentPlacementValidator(Request([graph])).ValidateInput().Violations, v => v.Code == SchedulingConstraintCode.InvalidGraph);
    }

    [Fact]
    public void FullyClosedDayReportsZeroUsableCapacity()
    {
        var request = Request([Independent(1)], 10);
        request = request with { Resources = request.Resources with { Days = [request.Resources.Days[0] with
            { RefereeCapacityWindows = [new(new(9, 0), new(10, 0), 0)] }] } };
        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(request)).Detail;
        Assert.Equal(0, Assert.Single(failure.Capacity).AvailableMatchMinutes);
    }

    [Fact]
    public async Task CapacityHandlesAResourceDayEndingWithinTheLastMinute()
    {
        var day = new ScheduleDaySettings(Date, new(23, 59, 0), new(23, 59, 30), ["A"]);
        var resources = new TournamentResourcePlan([day], 1, 0, 6);
        var capacity = await Task.Run(() => ScheduleResourceCalculator.CalculateDayCapacityMinutes(resources, day)).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, capacity);
    }

    [Fact]
    public void KnockoutTimingBoundaryMustHaveAtLeastTwoEntrants()
    {
        var request = Request([Independent(1)]);
        request = request with { Policy = request.Policy with { ProjectTimings = new Dictionary<Guid, ProjectMatchTiming> { [Id(1)] = new(30, 1, 15) } } };
        Assert.Contains(new TournamentPlacementValidator(request).ValidateInput().Violations, v => v.Code == SchedulingConstraintCode.InvalidPolicy);
    }
}
