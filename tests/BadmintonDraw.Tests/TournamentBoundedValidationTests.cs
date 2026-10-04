using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using Xunit;
using static BadmintonDraw.Tests.TournamentSchedulerTestData;

namespace BadmintonDraw.Tests;

public sealed class TournamentBoundedValidationTests
{
    [Fact]
    public void BoundedValidatorMatchesExactOracle()
    {
        // The seed generates legal prefixes; proposed placements deliberately cross hard boundaries.
        var first = Node(1, Player("A"), Player("B"));
        var winner = Node(2, new EntrantSource.WinnerOf(first.Id), Player("C"));
        var loser = Node(3, new EntrantSource.LoserOf(first.Id), Player("D"));
        var repeated = Node(4, Player("A"), Player("E"));
        var request = Request([new(Id(1), "v1", [first, winner, loser, repeated])], 13);
        request = request with { Resources = request.Resources with { MaxPlayerMatchesPerDay = 2,
            Days = [request.Resources.Days[0] with {
                RefereeCapacityWindows = [new(new(11, 0), new(12, 0), 1)],
                UnavailableCourtWindows = [new(new(12, 0), new(13, 0), ["B"])] },
                request.Resources.Days[0] with { Date = Date.AddDays(1) }] } };
        var validator = new TournamentPlacementValidator(request);
        var random = new Random(4103);
        for (var iteration = 0; iteration < 120; iteration++)
        {
            var prior = new Dictionary<Guid, MatchPlacement> { [first.Id] = Place(first, 9) };
            if (iteration % 2 == 0) prior[winner.Id] = Place(winner, 10);
            var node = iteration % 3 == 0 ? loser : repeated;
            var candidate = Place(node, 9 + random.Next(4), random.Next(2) * 30,
                court: random.Next(2) == 0 ? "A" : "B", date: iteration % 5 == 0 ? Date.AddDays(1) : Date);
            Assert.True(validator.ValidateSchedule(prior, false).IsValid);
            Compare(validator, candidate, prior);
        }
        // Malformed dictionary identities must not be hidden by the indexes.
        Compare(validator, Place(loser, 10), new Dictionary<Guid, MatchPlacement> { [repeated.Id] = Place(first, 9) });
        Compare(validator, Place(first, 10), new Dictionary<Guid, MatchPlacement> { [first.Id] = Place(first, 9) });
        Compare(validator, Place(loser, 10), new Dictionary<Guid, MatchPlacement> {
            [first.Id] = Place(first, 9, date: Date.AddDays(-1)) });
    }

    [Fact]
    public void BoundedCrossDayRestUsesRealDatesAndSeconds()
    {
        var first = Node(1, Player("A"), Player("B"));
        var second = Node(2, Player("A"), Player("C"));
        var request = Request([new(Id(1), "v1", [first, second])]);
        request = request with { Resources = request.Resources with { MinimumRestMinutes = 48 * 60,
            Days = [request.Resources.Days[0], request.Resources.Days[0] with { Date = Date.AddDays(2) }] } };
        var validator = new TournamentPlacementValidator(request);
        var prior = new Dictionary<Guid, MatchPlacement> { [first.Id] = new(first.Id, Day, new(9, 0, 30), new(9, 30, 30), "A") };
        var candidate = Place(second, 9, 30, date: Date.AddDays(2));
        Compare(validator, candidate, prior);
        Assert.Contains(validator.ValidatePlacement(candidate, prior).Violations, v => v.Code == SchedulingConstraintCode.MinimumRest);
    }

    [Fact]
    public void DeletedUnlockedBaselineResourcesDoNotConstrainBoundedGate()
    {
        var graph = Independent(1);
        var request = Request([graph]) with { BaselinePlacements = new Dictionary<Guid, MatchPlacement> {
            [graph.Matches[0].Id] = Place(graph.Matches[0], 9, court: "deleted", date: Date.AddDays(-7)) } };
        var validator = new TournamentPlacementValidator(request);
        Compare(validator, Place(graph.Matches[0], 10), new Dictionary<Guid, MatchPlacement>());
    }

    [Fact]
    public void MalformedNullCourtIsReportedInsteadOfCrashingIndexConstruction()
    {
        var graph = Independent(2);
        var validator = new TournamentPlacementValidator(Request([graph]));
        var first = Place(graph.Matches[0], 9) with { Court = null! };
        var candidate = Place(graph.Matches[1], 9) with { Court = null! };
        Compare(validator, candidate, new Dictionary<Guid, MatchPlacement> { [first.MatchId] = first });
    }

    [Fact]
    public void ExhaustedAndCanceledDirectGatesReturnUnknown()
    {
        var graph = Independent(1);
        var validator = new TournamentPlacementValidator(Request([graph]));
        var state = new TournamentSearchState(validator.Context, new Dictionary<Guid, MatchPlacement>());
        var exhausted = new SchedulingWorkBudget(new() { SearchWorkUnits = 0 }, default);
        Assert.Equal(BoundedValidationStatus.Unknown,
            validator.ValidatePlacementBounded(Place(graph.Matches[0], 9), state, exhausted, SchedulingRunPhase.Search).Status);
        var canceled = new SchedulingWorkBudget(TournamentSchedulingOptions.Default, new CancellationToken(true));
        Assert.Equal(BoundedValidationStatus.Unknown,
            validator.ValidateScheduleBounded(state, canceled, SchedulingRunPhase.Validation).Status);
    }

    private static void Compare(TournamentPlacementValidator validator, MatchPlacement candidate,
        Dictionary<Guid, MatchPlacement> prior)
    {
        var exact = validator.ValidatePlacement(candidate, prior);
        var state = new TournamentSearchState(validator.Context, prior);
        var bounded = validator.ValidatePlacementBounded(candidate, state,
            new SchedulingWorkBudget(TournamentSchedulingOptions.Default, default), SchedulingRunPhase.Search);
        Assert.Equal(exact.IsValid, bounded.Status == BoundedValidationStatus.Valid);
        Assert.Equal(exact.Violations.ToHashSet(), bounded.Violations.ToHashSet());
        var all = new Dictionary<Guid, MatchPlacement>(prior) { [candidate.MatchId] = candidate };
        var exactSchedule = validator.ValidateSchedule(all, false);
        var boundedSchedule = validator.ValidateScheduleBounded(new(validator.Context, all),
            new SchedulingWorkBudget(TournamentSchedulingOptions.Default, default), SchedulingRunPhase.Validation, false);
        Assert.Equal(exactSchedule.IsValid, boundedSchedule.Status == BoundedValidationStatus.Valid);
        Assert.Equal(exactSchedule.Violations.ToHashSet(), boundedSchedule.Violations.ToHashSet());
    }

    [Fact]
    public void UnknownNeverMeansValid()
    {
        var request = Request([Independent(1)], 10);
        var result = new TournamentScheduler().Generate(request,
            new TournamentSchedulingOptions { ValidationWorkUnits = 0 });
        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(result);
        Assert.Equal(SchedulingFailureKind.ValidationIncomplete, failure.Detail.Diagnostics!.FailureKind);
        Assert.Equal(SchedulingRunPhase.Validation, failure.Detail.Diagnostics.Phase);
    }

    [Fact]
    public void CandidateValidationCannotRunBeyondSearchAllowance()
    {
        var result = new TournamentScheduler().Generate(Request([Independent(1)], 10),
            new TournamentSchedulingOptions { SearchWorkUnits = 1 });
        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(result);
        Assert.Equal(SchedulingFailureKind.SearchIncomplete, failure.Detail.Diagnostics!.FailureKind);
        Assert.Equal(SchedulingRunPhase.Search, failure.Detail.Diagnostics.Phase);
    }

    [Fact]
    public void LockedSuccessorAllowsLaterPredecessorPlacement()
    {
        var first = Node(1, Player("A"), Player("B"));
        var second = Node(2, new EntrantSource.WinnerOf(first.Id), Player("C"));
        var locked = Place(second, 11);
        var request = Request([new(Id(1), "v1", [first, second])], 12) with
        {
            LockedMatchIds = [second.Id],
            BaselinePlacements = new Dictionary<Guid, MatchPlacement> { [second.Id] = locked }
        };
        var success = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Equal(locked, success.Schedule.Placements[second.Id]);
        Assert.True(success.Schedule.Placements[first.Id].EndTime <= locked.StartTime.AddMinutes(-15));
        Assert.Empty(new TournamentPlacementValidator(request).ValidateSchedule(success.Schedule.Placements).Violations);
    }

    [Fact]
    public void MissingLockedPredecessorDoesNotHideOtherLockedErrors()
    {
        var first = Node(1, Player("A"), Player("B"));
        var second = Node(2, new EntrantSource.WinnerOf(first.Id), Player("C"));
        var request = Request([new(Id(1), "v1", [first, second])], 12) with {
            LockedMatchIds = [second.Id], BaselinePlacements = new Dictionary<Guid, MatchPlacement> {
                [second.Id] = Place(second, 11, court: "deleted") } };
        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(request));
        Assert.Equal(SchedulingFailureKind.InvalidInput, failure.Detail.Diagnostics!.FailureKind);
        Assert.Contains(failure.Detail.Violations, v => v.Code == SchedulingConstraintCode.CourtUnavailable);
        Assert.Equal(0, failure.Detail.Diagnostics.UsedWorkUnits[SchedulingRunPhase.Search]);
    }
}
