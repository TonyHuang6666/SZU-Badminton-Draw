using System.Security.Cryptography;
using System.Text.Json;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Persistence;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class ScheduleSetupPageViewModelTests
{
    private static ScheduleSetupPageViewModel Page(ScheduleUiFixture fixture)
    {
        fixture.Shell.RegisterPageFactory(WorkspaceRoute.ScheduleSetup, s => new ScheduleSetupPageViewModel(fixture.Shell, s));
        fixture.Shell.Navigate(WorkspaceRoute.ScheduleSetup);
        return Assert.IsType<ScheduleSetupPageViewModel>(fixture.Shell.CurrentPage);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task OneGlobalRequestSchedulesEveryConfirmedProjectAndPersistsDurationOverrides(int count)
    {
        using var fixture = new ScheduleUiFixture(count); var page = Page(fixture);
        page.Days[0].DateText = "2026-09-13"; page.Days[0].StartText = "09:00"; page.Days[0].EndText = "15:00"; page.Days[0].CourtsText = "B1, B2";
        page.MinimumRestText = "30"; page.DailyMaximumText = "4"; page.ProjectTimings[0].MinutesText = "45";
        await page.GenerateCommand.ExecuteAsync();
        var workspace = new TournamentWorkspaceStore().Read(fixture.Workflow.CurrentSession!.WorkspacePath);
        Assert.Equal(count, workspace.Schedule!.GraphRevisions.Count);
        Assert.Equal(count, workspace.Schedule.Placements.Count);
        Assert.Equal(45, (workspace.Schedule.Placements[workspace.Projects[0].MatchGraph!.Matches[0].Id].EndTime - workspace.Schedule.Placements[workspace.Projects[0].MatchGraph!.Matches[0].Id].StartTime).TotalMinutes);
        Assert.All(workspace.Schedule.Policy.FinalDayRules, r => Assert.Equal(TournamentFinalDayPreference.Flexible, r.Preference)); // Effective Compact defaults have no hidden final preference.
        Assert.Single(workspace.AuditEvents, a => a.Action == "ScheduleGenerated");
        Assert.Equal(count == 1 ? "项目完成节奏" : "全赛事编排策略", page.PolicyLabel);
    }
    [Fact]
    public void DateEditKeepsTargetsAttachedToEditedDayAndBlankOverridesDifferFromZero()
    {
        using var fixture = new ScheduleUiFixture(); var page = Page(fixture);
        var day = page.Days[0]; day.DateText = "2026-09-13";
        Assert.Empty(page.BuildSetup().Policy.DayLoadTargets);
        day.TargetLoadText = "0"; day.WarningLoadText = "50"; day.StageProgressText = "0";
        day.DateText = "2026-09-15";
        var request = page.BuildSetup();
        Assert.Equal(new TournamentDayLoadTarget("2026-09-15", 0, .5), Assert.Single(request.Policy.DayLoadTargets));
        Assert.Equal(new TournamentStageWaveTarget("2026-09-15", 0), Assert.Single(request.Policy.StageWaveTargets));
        day.RemoveCommand.Execute(null);
        Assert.ThrowsAny<Exception>(() => page.BuildSetup());
    }
    [Fact]
    public void ResourceWindowsAndProjectIdFinalRulesReachOnePolicyWithExactTimePrecision()
    {
        using var fixture = new ScheduleUiFixture(3); var page = Page(fixture);
        page.Days[0].DateText = "2026-09-13"; page.Days[0].StartText = "09:00:30.123"; page.Days[0].EndText = "15:00"; page.Days[0].CourtsText = "B1, B2";
        page.Days[0].AddUnavailableCommand.Execute(null);
        var blocked = page.Days[0].Unavailable[0]; blocked.StartText = "10:00"; blocked.EndText = "11:00"; blocked.CourtsText = "B1";
        page.Days[0].AddRefereeWindowCommand.Execute(null);
        var referees = page.Days[0].RefereeWindows[0]; referees.StartText = "11:00"; referees.EndText = "12:00"; referees.CountText = "0";
        page.ProjectTimings[2].FinalPreferenceIndex = 4;
        var setup = page.BuildSetup();
        Assert.Equal(new TimeOnly(9, 0, 30, 123), setup.Resources.Days[0].DayStart);
        Assert.Equal("B1", Assert.Single(Assert.Single(setup.Resources.Days[0].UnavailableCourtWindows!).Courts));
        Assert.Equal(0, Assert.Single(setup.Resources.Days[0].RefereeCapacityWindows!).RefereeCount);
        Assert.Equal(fixture.Workflow.CurrentSession!.Workspace.Projects[2].Id, Assert.Single(setup.Policy.FinalDayRules).ProjectId);
        Assert.Equal(TournamentFinalDayPreference.StronglyPreferFinalDay, setup.Policy.FinalDayRules[0].Preference);
    }
    [Fact]
    public void CourtEditorTreatsRangesAsLiteralNamesAndOnlySplitsExplicitSeparators()
    {
        using var fixture = new ScheduleUiFixture(); var page = Page(fixture);
        page.Days[0].CourtsText = "B1-C8, B9，B10; B11；B12\nB13";

        var courts = page.BuildSetup().Resources.Days[0].Courts;

        Assert.Equal(["B1-C8", "B9", "B10", "B11", "B12", "B13"], courts);
    }
    [Fact]
    public async Task FailedRegenerationPreservesSavedScheduleAndTypedEditorValues()
    {
        using var fixture = new ScheduleUiFixture(); var page = Page(fixture);
        await page.GenerateCommand.ExecuteAsync();
        var path = fixture.Workflow.CurrentSession!.WorkspacePath; var bytes = SHA256.HashData(File.ReadAllBytes(path));
        var snapshot = JsonSerializer.Serialize(fixture.Workflow.CurrentSession.Workspace.Schedule);
        page.Days[0].StartText = "09:00"; page.Days[0].EndText = "09:05";
        await page.GenerateCommand.ExecuteAsync();
        Assert.NotNull(page.Failure);
        Assert.NotEmpty(page.Failure!.UnplacedMatches);
        Assert.Equal("09:05", page.Days[0].EndText);
        Assert.Equal(snapshot, JsonSerializer.Serialize(fixture.Workflow.CurrentSession.Workspace.Schedule));
        Assert.Equal(bytes, SHA256.HashData(File.ReadAllBytes(path)));
    }
    [Fact]
    public async Task ExternalRegenerationLatchesEditorConflictUntilExplicitReset()
    {
        using var fixture = new ScheduleUiFixture(); var page = Page(fixture);
        await page.GenerateCommand.ExecuteAsync(); page.MinimumRestText = "47";
        var other = new BadmintonDraw.Workflows.Tournaments.TournamentWorkspaceWorkflow(); other.OpenWorkspace(fixture.Workflow.CurrentSession!.WorkspacePath);
        var current = other.CurrentSession!.Workspace.Schedule!;
        other.GenerateSchedule(current.Resources with { MinimumRestMinutes = 15 }, current.Policy, other.CurrentSession.Workspace.Revision);
        await fixture.Shell.ReloadCommand.ExecuteAsync();
        Assert.Equal("47", page.MinimumRestText); Assert.True(page.HasEditorConflict); Assert.False(page.GenerateCommand.CanExecute(null));
        page.ResetCommand.Execute(null);
        Assert.Equal("15", page.MinimumRestText); Assert.False(page.HasEditorConflict); Assert.True(page.GenerateCommand.CanExecute(null));
    }
    [Fact]
    public async Task InvalidTextAndNonFinitePercentNeverChangeArchive()
    {
        using var fixture = new ScheduleUiFixture(); var page = Page(fixture); var session = fixture.Workflow.CurrentSession;
        page.Days[0].TargetLoadText = "NaN";
        await page.GenerateCommand.ExecuteAsync();
        Assert.NotNull(fixture.Shell.LastError); Assert.Same(session, fixture.Workflow.CurrentSession); Assert.Null(session!.Workspace.Schedule);
    }
    [Fact]
    public void AddingWindowWithUnfinishedDayTimeDoesNotThrowOrReplaceInput()
    {
        using var fixture = new ScheduleUiFixture(); var page = Page(fixture); var day = page.Days[0];
        day.StartText = "09:";
        day.AddUnavailableCommand.Execute(null); day.AddRefereeWindowCommand.Execute(null);
        Assert.Equal("09:", day.StartText); Assert.Single(day.Unavailable); Assert.Single(day.RefereeWindows);
    }
    [Fact]
    public async Task ChoosingFreshStrategyDefaultsClearsReturnedOverridesButKeepsDurationsAndResources()
    {
        using var fixture = new ScheduleUiFixture(); var page = Page(fixture);
        page.StrategyIndex = 2; page.ProjectTimings[0].MinutesText = "45";
        await page.GenerateCommand.ExecuteAsync();
        Assert.NotEmpty(page.BuildSetup().Policy.FinalDayRules);
        page.StrategyIndex = 0; page.UseStrategyDefaultsCommand.Execute(null);
        var setup = page.BuildSetup(); Assert.Empty(setup.Policy.FinalDayRules); Assert.Empty(setup.Policy.DayLoadTargets); Assert.Empty(setup.Policy.StageWaveTargets);
        Assert.Equal(45, setup.Policy.ProjectTimings.Values.Single().MatchMinutes); Assert.NotEmpty(setup.Resources.Days);
    }
}
