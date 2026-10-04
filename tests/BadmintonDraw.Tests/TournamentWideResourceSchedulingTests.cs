using System.Text.Json;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Scheduling;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class TournamentWideResourceSchedulingTests
{
    // Catches eager minute-by-court enumeration exhausting the default budget on
    // the user's 276-match bracket despite five full days of available courts.
    [Theory]
    [InlineData(ScheduleAutoSchedulingStrategy.Compact, false)]
    [InlineData(ScheduleAutoSchedulingStrategy.FinalsDayFriendly, false)]
    [InlineData(ScheduleAutoSchedulingStrategy.Compact, true)]
    public async Task FiveWideDaysProduceACompleteHardValidatedSchedule(ScheduleAutoSchedulingStrategy strategy, bool requireFinalDay)
    {
        var fixture = PresidentsCupFixture.Load();
        var courts = new[] { "B", "C", "D" }.SelectMany(zone => Enumerable.Range(1, 8).Select(n => $"粤海东馆 · {zone}{n}")).ToArray();
        var request = fixture.Request with
        {
            Resources = new(Enumerable.Range(0, 5).Select(i => new ScheduleDaySettings(new DateOnly(2026, 10, 4).AddDays(i),
                new(9, 0), new(18, 0), courts)).ToArray(), 24, 20, 10),
            Policy = fixture.Policy with { Strategy = strategy, DayLoadTargets = [], StageWaveTargets = [],
                FinalDayRules = [], SynchronizeStageWaves = false, RequireChampionshipFinalsOnLastDay = requireFinalDay }
        };
        var result = new TournamentScheduler().Generate(request);
        Assert.True(result is TournamentSchedulingResult.Success,
            result is TournamentSchedulingResult.Failure f ? JsonSerializer.Serialize(new { f.Detail.Diagnostics!.FailureKind,
                f.Detail.Diagnostics.Phase, f.Detail.Diagnostics.UsedWorkUnits, Unplaced = f.Detail.UnplacedMatches.Count }) : "Unexpected result");
        var success = (TournamentSchedulingResult.Success)result;
        Assert.Equal(276, success.Schedule.Placements.Count);
        Assert.True(success.Quality.HardValidationComplete);
        Assert.Empty(success.Quality.Violations);
        if (strategy == ScheduleAutoSchedulingStrategy.FinalsDayFriendly || requireFinalDay)
        {
            var finals = fixture.MatchGraphs.SelectMany(g => g.Matches).Where(n => n.IsChampionshipFinal).ToArray();
            Assert.Equal(3, finals.Length);
            Assert.All(finals, n => Assert.Equal("2026-10-08", success.Schedule.Placements[n.Id].DayLabel));
        }
        await PresidentsCupFixture.ValidateExactAsync(request, success.Schedule.Placements);
        PresidentsCupFixture.CheckIndependentSchedule(new(fixture.MatchGraphs, request.Resources, request.Policy,
            success.Schedule.Placements.ToDictionary()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalFourAfternoonsWithZeroRestProduceACompleteValidatedSchedule(bool requireFinalDay)
    {
        var fixture = PresidentsCupFixture.Load();
        var request = fixture.Request with
        {
            Resources = fixture.Resources with { RefereeCount = null, MinimumRestMinutes = 0, MaxPlayerMatchesPerDay = 8 },
            Policy = fixture.Policy with { Strategy = ScheduleAutoSchedulingStrategy.Compact,
                DayLoadTargets = [], StageWaveTargets = [], FinalDayRules = [], SynchronizeStageWaves = false,
                RequireChampionshipFinalsOnLastDay = requireFinalDay }
        };
        var result = new TournamentScheduler().Generate(request);
        Assert.True(result is TournamentSchedulingResult.Success,
            result is TournamentSchedulingResult.Failure f ? f.Detail.Diagnostics?.FailureKind.ToString() : "Unexpected result");
        var success = (TournamentSchedulingResult.Success)result;
        Assert.Equal(276, success.Schedule.Placements.Count);
        Assert.True(success.Quality.HardValidationComplete);
        if (requireFinalDay)
        {
            var finals = fixture.MatchGraphs.SelectMany(g => g.Matches).Where(n => n.IsChampionshipFinal).ToArray();
            Assert.Equal(3, finals.Length);
            Assert.All(finals, n => Assert.Equal("2026-10-06", success.Schedule.Placements[n.Id].DayLabel));
        }
        await PresidentsCupFixture.ValidateExactAsync(request, success.Schedule.Placements);
        PresidentsCupFixture.CheckIndependentSchedule(new(fixture.MatchGraphs, request.Resources, request.Policy,
            success.Schedule.Placements.ToDictionary()));
    }
}
