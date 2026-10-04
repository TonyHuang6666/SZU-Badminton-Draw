using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using Xunit;
using static BadmintonDraw.Tests.TournamentSchedulerTestData;

namespace BadmintonDraw.Tests;

public sealed class TournamentPlayerPairIsolationTests
{
    [Fact]
    public async Task SeparateResultSnapshotsNarrowRelationsWithoutChangingOlderValidators()
    {
        var source = Node(1, Player("A"), Player("B"));
        var next = Node(2, new EntrantSource.WinnerOf(source.Id), Player("C"));
        var other = Node(3, Player("B"), Player("D"), project: 2);
        var key = new WorkspaceMatchKey(source.ProjectId, source.Id);
        var request = Request([new(Id(1), "unchanged", [source, next]), new(Id(2), "unchanged", [other])], 12) with
        {
            BaselinePlacements = new Dictionary<Guid, MatchPlacement> { [source.Id] = Place(source, 9) }
        };
        var unresolved = new TournamentPlacementValidator(request);
        var aWon = new TournamentPlacementValidator(request with
        {
            Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult>
                { [key] = new(key, Player("A"), Player("B"), "21-0", 30, DateTimeOffset.UtcNow) }
        });
        var bWon = new TournamentPlacementValidator(request with
        {
            Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult>
                { [key] = new(key, Player("B"), Player("A"), "21-0", 30, DateTimeOffset.UtcNow) }
        });
        var prior = new Dictionary<Guid, MatchPlacement> { [source.Id] = Place(source, 9), [next.Id] = Place(next, 10) };
        var candidate = Place(other, 10, court: "B");
        // Same graph IDs and revisions, different captured results. Reading any validator
        // concurrently must neither reuse another snapshot nor mutate its older relation.
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            Assert.Contains(unresolved.ValidatePlacement(candidate, prior).Violations, v => v.Code == SchedulingConstraintCode.PlayerOverlap);
            Assert.True(aWon.ValidatePlacement(candidate, prior).IsValid);
            Assert.Contains(bWon.ValidatePlacement(candidate, prior).Violations, v => v.Code == SchedulingConstraintCode.PlayerOverlap);
        })));
    }

    [Fact]
    public void DoublesPartnerIdentityChangesAreIsolatedBetweenOtherwiseIdenticalRequests()
    {
        static EntrantSource.Participant Pair(string name, string firstId, string secondId) =>
            new(name, name, [new(name + "甲", firstId), new(name + "乙", secondId)]);
        var first = Node(1, Pair("AB", "001", "002"), Pair("CD", "003", "004"));
        var other = Node(2, Pair("重命名搭档", "005", "002"), Pair("FG", "006", "007"), project: 2);
        var request = Request([new(Id(1), "v1", [first]), new(Id(2), "v1", [other])], 12);
        var shared = new TournamentPlacementValidator(request);
        var distinct = new TournamentPlacementValidator(request with
        {
            MatchGraphs = [request.MatchGraphs[0], new(Id(2), "v1", [other with { SideA = Pair("重命名搭档", "005", "008") }])]
        });
        var candidate = Place(other, 10, court: "B");
        var prior = new Dictionary<Guid, MatchPlacement> { [first.Id] = Place(first, 10) };
        Assert.Contains(shared.ValidatePlacement(candidate, prior).Violations, v => v.Code == SchedulingConstraintCode.PlayerOverlap);
        Assert.True(distinct.ValidatePlacement(candidate, prior).IsValid);
        Assert.Contains(shared.ValidatePlacement(candidate, prior).Violations, v => v.Code == SchedulingConstraintCode.PlayerOverlap);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WinnerLoserExclusivityAndRepeatedWinnerConflictAreSymmetric(bool reverse)
    {
        var source = Node(1, Player("A"), Player("B"));
        var winner = Node(2, new EntrantSource.WinnerOf(source.Id), Player("C"));
        var loser = Node(3, new EntrantSource.LoserOf(source.Id), Player("D"));
        var repeated = Node(4, new EntrantSource.WinnerOf(source.Id), Player("E"));
        var validator = new TournamentPlacementValidator(Request([new(Id(1), "v1", [source, winner, loser, repeated])], 12));
        var legalCandidate = reverse ? winner : loser;
        var legalOther = reverse ? loser : winner;
        var legalPrior = new Dictionary<Guid, MatchPlacement> { [source.Id] = Place(source, 9), [legalOther.Id] = Place(legalOther, 10) };
        Assert.True(validator.ValidatePlacement(Place(legalCandidate, 10, court: "B"), legalPrior).IsValid);
        var conflictingCandidate = reverse ? winner : repeated;
        var conflictingOther = reverse ? repeated : winner;
        var conflictPrior = new Dictionary<Guid, MatchPlacement> { [source.Id] = Place(source, 9), [conflictingOther.Id] = Place(conflictingOther, 10) };
        Assert.Contains(validator.ValidatePlacement(Place(conflictingCandidate, 10, court: "B"), conflictPrior).Violations,
            v => v.Code == SchedulingConstraintCode.PlayerOverlap);
    }
}
