using System.Text.Json;
using System.Text.Json.Nodes;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using Xunit;
using static BadmintonDraw.Tests.TournamentSchedulerTestData;

namespace BadmintonDraw.Tests;

public sealed class TournamentChampionshipFinalDayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompactSchedulesEveryProjectsFinalOnLastDayOnlyWhenRequired(bool required)
    {
        var graphs = new[] { 1, 2 }.Select(project =>
            MatchGraphFactory.Create(Id(project), MatchGraphTests.Draw(4, placement: PlacementPlayoff.None))).ToArray();
        var request = TwoDays(graphs, required);

        var success = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));

        Assert.All(graphs.SelectMany(graph => graph.Matches).Where(node => node.IsChampionshipFinal),
            node => Assert.Equal(required ? "2026-09-21" : "2026-09-20", success.Schedule.Placements[node.Id].DayLabel));
        Assert.Empty(new TournamentPlacementValidator(request).ValidateSchedule(success.Schedule.Placements).Violations);
    }

    [Fact]
    public void HardFinalDayKeepsSemifinalsBronzeAndOtherPlacementMatchesEligibleEarlier()
    {
        var graph = MatchGraphFactory.Create(Id(1), MatchGraphTests.Draw(8, placement: PlacementPlayoff.ThirdToEighth));
        var request = TwoDays([graph], true);

        var success = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));

        Assert.Equal("2026-09-21", success.Schedule.Placements[graph.Matches.Single(node => node.IsChampionshipFinal).Id].DayLabel);
        Assert.All(graph.Matches.Where(node => !node.IsChampionshipFinal),
            node => Assert.Equal("2026-09-20", success.Schedule.Placements[node.Id].DayLabel));
    }

    [Fact]
    public void HardFinalDayUsesLatestRealDateAndOverridesSoftAvoidPreference()
    {
        var final = Final();
        var request = TwoDays([new(final.ProjectId, "v1", [final])], true);
        var first = request.Resources.Days[0];
        request = request with
        {
            Resources = request.Resources with { Days = [first with { Date = Date.AddDays(4) }, first, first with { Date = Date.AddDays(1) }] },
            Policy = request.Policy with { FinalDayRules = [new(final.ProjectId,
                TournamentFinalDayMatchCategory.Final, TournamentFinalDayPreference.AvoidFinalDay)] }
        };

        var success = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));

        Assert.Equal("2026-09-24", success.Schedule.Placements[final.Id].DayLabel);
    }

    [Fact]
    public void NoLastDayCapacityFailsWithoutFallingBackToEarlierDay()
    {
        var final = Final();
        var request = TwoDays([new(final.ProjectId, "v1", [final])], true);
        var last = request.Resources.Days[1];
        request = request with { Resources = request.Resources with { Days = [request.Resources.Days[0], last with
            { RefereeCapacityWindows = [new(last.DayStart, last.DayEnd, 0)] }] } };

        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(request));

        Assert.Contains(failure.Detail.UnplacedMatches, match => match.MatchId == final.Id);
        Assert.Contains(failure.Detail.Violations, issue => issue.Code == SchedulingConstraintCode.ChampionshipFinalDay);
    }

    [Fact]
    public void LockedEarlierFinalFailsBeforeSearchWithAnExplicitFinalDayConflict()
    {
        var final = Final();
        var original = Place(final, 9);
        var request = TwoDays([new(final.ProjectId, "v1", [final])], true) with
        {
            LockedMatchIds = [final.Id],
            BaselinePlacements = new Dictionary<Guid, MatchPlacement> { [final.Id] = original }
        };

        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(request));

        Assert.Equal(SchedulingFailureKind.InvalidInput, failure.Detail.Diagnostics!.FailureKind);
        Assert.Equal(0, failure.Detail.Diagnostics.UsedWorkUnits[SchedulingRunPhase.Search]);
        var conflict = Assert.Single(failure.Detail.Violations, issue => issue.Code == SchedulingConstraintCode.ChampionshipFinalDay);
        Assert.Equal(final.Id, conflict.MatchId);
        Assert.Contains("最后比赛日", conflict.Message);
        Assert.Contains("2026-09-21", conflict.Message);
        Assert.Equal(original, request.BaselinePlacements![final.Id]);
    }

    [Theory]
    [InlineData(false, 0, true)]
    [InlineData(true, 0, false)]
    [InlineData(true, 1, true)]
    public void ExactAndBoundedPlacementAndScheduleGatesEnforceTheSameFinalDayRule(bool required, int dayOffset, bool valid)
    {
        var final = Final();
        var request = TwoDays([new(final.ProjectId, "v1", [final])], required);
        var validator = new TournamentPlacementValidator(request);
        var candidate = Place(final, 9, date: Date.AddDays(dayOffset));
        var prior = new Dictionary<Guid, MatchPlacement>();
        var exact = validator.ValidatePlacement(candidate, prior);
        var bounded = validator.ValidatePlacementBounded(candidate, new(validator.Context, prior),
            new(TournamentSchedulingOptions.Default, default), SchedulingRunPhase.Search);
        var placements = new Dictionary<Guid, MatchPlacement> { [final.Id] = candidate };
        var exactSchedule = validator.ValidateSchedule(placements);
        var boundedSchedule = validator.ValidateScheduleBounded(new(validator.Context, placements),
            new(TournamentSchedulingOptions.Default, default), SchedulingRunPhase.Validation);

        Assert.Equal(valid, exact.IsValid);
        Assert.Equal(valid ? BoundedValidationStatus.Valid : BoundedValidationStatus.Invalid, bounded.Status);
        Assert.Equal(valid, exactSchedule.IsValid);
        Assert.Equal(valid ? BoundedValidationStatus.Valid : BoundedValidationStatus.Invalid, boundedSchedule.Status);
        Assert.Equal(exact.Violations.ToHashSet(), bounded.Violations.ToHashSet());
        Assert.Equal(exact.Violations.ToHashSet(), exactSchedule.Violations.ToHashSet());
        Assert.Equal(exact.Violations.ToHashSet(), boundedSchedule.Violations.ToHashSet());
        if (!valid) Assert.Equal(SchedulingConstraintCode.ChampionshipFinalDay, Assert.Single(exact.Violations).Code);
    }

    [Fact]
    public void OneResourceDayNaturallySatisfiesTheHardFinalDayRule()
    {
        var graph = MatchGraphFactory.Create(Id(1), MatchGraphTests.Draw(4, placement: PlacementPlayoff.None));
        var request = TwoDays([graph], true);
        request = request with { Resources = request.Resources with { Days = [request.Resources.Days[0]] } };

        var success = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));

        Assert.All(success.Schedule.Placements.Values, placement => Assert.Equal("2026-09-20", placement.DayLabel));
        Assert.Empty(new TournamentPlacementValidator(request).ValidateSchedule(success.Schedule.Placements).Violations);
    }

    [Fact]
    public void SerializedPolicyRetainsTheHardFinalDayRequirement()
    {
        var final = Final();
        var request = TwoDays([new(final.ProjectId, "v1", [final])], true);
        var json = JsonSerializer.Serialize(request.Policy);
        var saved = JsonSerializer.Deserialize<TournamentSchedulingPolicy>(json)!;

        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("RequireChampionshipFinalsOnLastDay", out var value));
        Assert.True(value.GetBoolean());
        Assert.False(new TournamentPlacementValidator(request with { Policy = saved })
            .ValidatePlacement(Place(final, 9), new Dictionary<Guid, MatchPlacement>()).IsValid);
    }

    [Fact]
    public void PolicyWithoutTheNewSettingPreservesEarlierFinalScheduling()
    {
        var final = Final();
        var request = TwoDays([new(final.ProjectId, "v1", [final])], false);
        var legacy = JsonNode.Parse(JsonSerializer.Serialize(request.Policy))!.AsObject();
        legacy.Remove("RequireChampionshipFinalsOnLastDay");
        request = request with { Policy = legacy.Deserialize<TournamentSchedulingPolicy>()! };

        var success = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(request));

        Assert.Equal("2026-09-20", success.Schedule.Placements[final.Id].DayLabel);
    }

    [Theory]
    [InlineData(false, true, 0, true)]
    [InlineData(true, true, 0, false)]
    [InlineData(true, true, 2, true)]
    [InlineData(true, false, 0, true)]
    public void WorkspaceValidationAndDeserializationPreserveTheFinalDayInvariant(
        bool required, bool championshipFinal, int dayOffset, bool valid)
    {
        var workspace = TournamentWorkspaceRulesTests.Fixture(TournamentStage.ScheduleReady) with
            { Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult>() };
        var project = workspace.Projects[0];
        var node = project.MatchGraph!.Matches[0] with { IsChampionshipFinal = championshipFinal };
        var first = workspace.Resources!.Days[0];
        var resources = workspace.Resources with { Days = [first with { Date = first.Date.AddDays(2) }, first] };
        workspace = workspace with
        {
            Projects = [project with { MatchGraph = project.MatchGraph with { Matches = [node] } }],
            Resources = resources,
            Schedule = workspace.Schedule! with
            {
                Resources = resources,
                Policy = workspace.Schedule.Policy with { RequireChampionshipFinalsOnLastDay = required },
                Placements = new Dictionary<Guid, MatchPlacement> { [node.Id] = Place(node, 9, court: "1", date: first.Date.AddDays(dayOffset)) }
            }
        };
        var json = JsonSerializer.Serialize(workspace);

        if (valid)
        {
            TournamentWorkspaceRules.Validate(workspace);
            Assert.NotNull(JsonSerializer.Deserialize<TournamentWorkspace>(json));
        }
        else
        {
            var conflict = Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.Validate(workspace));
            Assert.Equal("schedule.championship-final-day", conflict.Code);
            Assert.Throws<WorkspaceValidationException>(() => JsonSerializer.Deserialize<TournamentWorkspace>(json));
        }
    }

    private static MatchNode Final() => Node(1, Player("A"), Player("B")) with { IsChampionshipFinal = true };

    private static TournamentSchedulingRequest TwoDays(IReadOnlyList<MatchGraph> graphs, bool required)
    {
        var request = Request(graphs, 18);
        return request with
        {
            Resources = request.Resources with { Days = [request.Resources.Days[0], request.Resources.Days[0] with { Date = Date.AddDays(1) }] },
            Policy = request.Policy with { Strategy = ScheduleAutoSchedulingStrategy.Compact, RequireChampionshipFinalsOnLastDay = required }
        };
    }
}
