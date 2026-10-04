using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using Xunit;
using static BadmintonDraw.Tests.TournamentSchedulerTestData;

namespace BadmintonDraw.Tests;

public sealed class TournamentValidationEfficiencyTests
{
    [Fact]
    public void BoundedDailyCapReusesGroupedPathsWhenAPlayerCanExceedTheLimit()
    {
        var identityPrefix = new string('s', 128);
        var round = Enumerable.Range(1, 32).Select(i => Node(i,
            Player($"A{i}", $"{identityPrefix}-A{i}"), Player($"B{i}", $"{identityPrefix}-B{i}"))).ToArray();
        var nodes = round.ToList();
        var prior = new Dictionary<Guid, MatchPlacement>();
        var nextId = 33;
        var hour = 9;
        while (round.Length > 1)
        {
            for (var i = 0; i < round.Length; i++)
                prior.Add(round[i].Id, Place(round[i], hour, court: $"C{i}"));
            round = round.Chunk(2).Select(pair => Node(nextId++,
                new EntrantSource.WinnerOf(pair[0].Id), new EntrantSource.WinnerOf(pair[1].Id))).ToArray();
            nodes.AddRange(round);
            hour++;
        }
        var request = Request([new(Id(1), "v1", nodes)]);
        request = request with { Resources = request.Resources with {
            Days = [request.Resources.Days[0] with { Courts = Enumerable.Range(0, 32).Select(i => $"C{i}").ToArray() }],
            RefereeCount = 32, MaxPlayerMatchesPerDay = 5 } };
        var validator = new TournamentPlacementValidator(request);
        var candidate = Place(round[0], hour, court: "C0");
        Assert.True(validator.ValidateSchedule(prior, false).IsValid);
        var exact = validator.ValidatePlacement(candidate, prior);
        Assert.Equal(SchedulingConstraintCode.DailyMatchLimit, Assert.Single(exact.Violations).Code);

        var bounded = validator.ValidatePlacementBounded(candidate, new(validator.Context, prior),
            new SchedulingWorkBudget(new() { SearchWorkUnits = 20_000 }, default), SchedulingRunPhase.Search);

        Assert.Equal(BoundedValidationStatus.Invalid, bounded.Status);
        Assert.Equal(exact.Violations, bounded.Violations);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    public void BoundedDailyCapUsesAppearanceUpperBoundBeforeExpandingPlayerPaths(int unrelatedSameDayMatches)
    {
        var round = Enumerable.Range(1, 16)
            .Select(i => Node(i, Player($"A{i}"), Player($"B{i}"))).ToArray();
        var nodes = round.ToList();
        var prior = new Dictionary<Guid, MatchPlacement>();
        var nextId = 17;
        var hour = 9;
        while (round.Length > 1)
        {
            for (var i = 0; i < round.Length; i++)
                prior.Add(round[i].Id, Place(round[i], hour, court: $"C{i}"));
            round = round.Chunk(2).Select(pair => Node(nextId++,
                new EntrantSource.WinnerOf(pair[0].Id), new EntrantSource.WinnerOf(pair[1].Id))).ToArray();
            nodes.AddRange(round);
            hour++;
        }
        for (var i = 0; i < unrelatedSameDayMatches; i++)
        {
            var other = Node(1000 + i, Player($"X{i}"), Player($"Y{i}"));
            nodes.Add(other);
            prior.Add(other.Id, Place(other, 9, court: $"C{i}", date: Date.AddDays(1)));
        }
        var request = Request([new(Id(1), "v1", nodes)]);
        var day = request.Resources.Days[0] with { Courts = Enumerable.Range(0, 16).Select(i => $"C{i}").ToArray() };
        request = request with { Resources = request.Resources with {
            Days = [day, day with { Date = Date.AddDays(1) }], RefereeCount = 16 } };
        var validator = new TournamentPlacementValidator(request);
        var candidate = Place(round[0], 9, court: "C15", date: Date.AddDays(1));
        Assert.True(validator.ValidateSchedule(prior, false).IsValid);
        Assert.True(validator.ValidatePlacement(candidate, prior).IsValid);

        var bounded = validator.ValidatePlacementBounded(candidate, new(validator.Context, prior),
            new SchedulingWorkBudget(new() { SearchWorkUnits = 10_000 }, default), SchedulingRunPhase.Search);

        Assert.Equal(BoundedValidationStatus.Valid, bounded.Status);
        Assert.Empty(bounded.Violations);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, true)]
    public void BoundedGateResolvesShortMatchAmongManyDisjointResourceWindows(int activeReferees, bool valid)
    {
        var graph = Independent(1);
        var request = Request([graph]);
        var morning = Enumerable.Range(0, 128)
            .Select(i => new TimeOnly(9, 0).Add(TimeSpan.FromSeconds(i * 10))).ToArray();
        var day = request.Resources.Days[0] with
        {
            RefereeCapacityWindows = morning.Select(start => new ScheduleRefereeCapacityWindow(
                start, start.Add(TimeSpan.FromSeconds(5)), 0))
                .Append(new(new(10, 0, 20), new(10, 0, 40), activeReferees)).ToArray(),
            UnavailableCourtWindows = morning.Select(start => new ScheduleCourtAvailabilityBlock(
                start, start.Add(TimeSpan.FromSeconds(5)), ["A"])).ToArray()
        };
        request = request with { Resources = request.Resources with { Days = [day] } };
        var validator = new TournamentPlacementValidator(request);
        var prior = new Dictionary<Guid, MatchPlacement>();
        var candidate = Place(graph.Matches[0], 10, court: "B");
        var exact = validator.ValidatePlacement(candidate, prior);
        Assert.Equal(valid, exact.IsValid);
        Assert.Equal(valid ? Array.Empty<SchedulingConstraintCode>() : [SchedulingConstraintCode.RefereeCapacity],
            exact.Violations.Select(v => v.Code));

        var budget = new SchedulingWorkBudget(new() { SearchWorkUnits = 10_000 }, default);
        var bounded = validator.ValidatePlacementBounded(candidate, new(validator.Context, prior), budget,
            SchedulingRunPhase.Search);

        Assert.Equal(valid ? BoundedValidationStatus.Valid : BoundedValidationStatus.Invalid, bounded.Status);
        Assert.Equal(exact.Violations, bounded.Violations);
    }
}
