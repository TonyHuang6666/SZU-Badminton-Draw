using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using Xunit;
using static BadmintonDraw.Tests.TournamentSchedulerTestData;

namespace BadmintonDraw.Tests;

public sealed class TournamentPlayerSelfOverlapTests
{
    [Fact]
    public void DistinctDoublesPairsSharingAStudentFailAutomaticAndManualValidation()
    {
        var draw = MatchGraphTests.Draw(2, kind: EventKind.Doubles, placement: PlacementPlayoff.None);
        var group = new DrawGroup(1,
        [
            new("A/B", PrimaryName: "A", PartnerName: "B", PrimaryStudentId: "001", PartnerStudentId: "002"),
            new("A renamed/C", PrimaryName: "A renamed", PartnerName: "C", PrimaryStudentId: "001", PartnerStudentId: "003")
        ]);
        var graph = MatchGraphFactory.Create(Id(1), draw with { Groups = [group], ByeGroups = [group] });
        var match = Assert.Single(graph.Matches);
        var request = Request([graph]);
        AssertRejected(request, match, new Dictionary<Guid, MatchPlacement> { [match.Id] = Place(match, 9) });
    }

    [Fact]
    public void SameUnresolvedWinnerOnBothSidesFailsAutomaticAndManualValidation()
    {
        var source = Node(1, Player("A"), Player("B"));
        var match = Node(2, new EntrantSource.WinnerOf(source.Id), new EntrantSource.WinnerOf(source.Id));
        var request = Request([new(Id(1), "v1", [source, match])]);
        AssertRejected(request, match, new Dictionary<Guid, MatchPlacement>
            { [source.Id] = Place(source, 9), [match.Id] = Place(match, 10) });
    }

    [Fact]
    public void MutuallyExclusiveWinnerAndLoserSourcesRemainPlayable()
    {
        var source = Node(1, Player("A"), Player("B"));
        var match = Node(2, new EntrantSource.WinnerOf(source.Id), new EntrantSource.LoserOf(source.Id));
        var request = Request([new(Id(1), "v1", [source, match])]);
        var validator = new TournamentPlacementValidator(request);
        Assert.True(validator.ValidateInput().IsValid);
        Assert.True(validator.ValidateSchedule(new Dictionary<Guid, MatchPlacement>
            { [source.Id] = Place(source, 9), [match.Id] = Place(match, 10) }).IsValid);
        var result = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));
        Assert.Equal(2, result.Schedule.Placements.Count);
        Assert.Equal(0, result.Quality.HardConstraintCount);
    }

    private static void AssertRejected(TournamentSchedulingRequest request, MatchNode match,
        IReadOnlyDictionary<Guid, MatchPlacement> placements)
    {
        var validator = new TournamentPlacementValidator(request);
        Assert.Contains(validator.ValidateInput().Violations, v =>
            v.Code == SchedulingConstraintCode.PlayerOnBothSides && v.MatchId == match.Id && v.ProjectId == match.ProjectId);
        Assert.Contains(validator.ValidateSchedule(placements).Violations, v =>
            v.Code == SchedulingConstraintCode.PlayerOnBothSides && v.MatchId == match.Id);
        Assert.Contains(validator.ValidatePlacement(placements[match.Id], placements.Where(p => p.Key != match.Id).ToDictionary()).Violations,
            v => v.Code == SchedulingConstraintCode.PlayerOnBothSides && v.MatchId == match.Id);
        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(request));
        Assert.Contains(failure.Detail.Violations, v => v.Code == SchedulingConstraintCode.PlayerOnBothSides && v.MatchId == match.Id);
    }
}
