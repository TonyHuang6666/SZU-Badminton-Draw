using BadmintonDraw.Core;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Persistence;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class ScheduleSimplePolicyTests
{
    [Theory]
    [InlineData(ScheduleAutoSchedulingStrategy.Custom)]
    [InlineData(ScheduleAutoSchedulingStrategy.FinalsDayFriendly)]
    [InlineData(ScheduleAutoSchedulingStrategy.BalancedRelaxed)]
    public void ReopeningOldSettingsBuildsCompactWithoutHiddenSoftTargets(ScheduleAutoSchedulingStrategy strategy)
    {
        using var fixture = new ScheduleUiFixture();
        var workspace = fixture.Workflow.CurrentSession!.Workspace;
        fixture.Workflow.GenerateSchedule(new([new(new(2026, 10, 3), new(9, 0), new(18, 0), ["B1", "B2"])], 2, 20, 8),
            new(strategy, [new("2026-10-03", .3, .5)], true, [new("2026-10-03", 1)],
                [new(workspace.Projects[0].Id, TournamentFinalDayMatchCategory.Final, TournamentFinalDayPreference.StronglyPreferFinalDay)])
            { ProjectTimings = new Dictionary<Guid, ProjectMatchTiming> { [workspace.Projects[0].Id] = new(45) } }, workspace.Revision);
        var saved = fixture.Workflow.CurrentSession!;
        var bytes = File.ReadAllBytes(saved.WorkspacePath);
        using var page = new ScheduleSetupPageViewModel(fixture.Shell, saved);

        var setup = page.BuildSetup();

        Assert.Equal(ScheduleAutoSchedulingStrategy.Compact, setup.Policy.Strategy);
        Assert.Empty(setup.Policy.DayLoadTargets);
        Assert.Empty(setup.Policy.StageWaveTargets);
        Assert.Empty(setup.Policy.FinalDayRules);
        Assert.False(setup.Policy.SynchronizeStageWaves);
        Assert.False(setup.Policy.RequireChampionshipFinalsOnLastDay);
        Assert.Equal(45, setup.Policy.ProjectTimings[workspace.Projects[0].Id].MatchMinutes);
        Assert.Equal(20, setup.Resources.MinimumRestMinutes);
        Assert.Equal(8, setup.Resources.MaxPlayerMatchesPerDay);
        Assert.Equal(bytes, File.ReadAllBytes(saved.WorkspacePath));
        Assert.Same(saved, fixture.Workflow.CurrentSession);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalDayChoiceReachesSavedScheduleAndReopenedEditor(bool requireLastDay)
    {
        using var fixture = new ScheduleUiFixture(3);
        using var page = Page(fixture);
        AddTwoDays(page);
        page.RequireChampionshipFinalsOnLastDay = requireLastDay;
        await page.GenerateCommand.ExecuteAsync();
        Assert.Null(fixture.Shell.LastError);
        var saved = new TournamentWorkspaceStore().Read(fixture.Workflow.CurrentSession!.WorkspacePath);
        Assert.Equal(requireLastDay, saved.Schedule!.Policy.RequireChampionshipFinalsOnLastDay);
        Assert.All(saved.Schedule.Placements.Values, p => Assert.Equal(requireLastDay ? "2026-10-04" : "2026-10-03", p.DayLabel));
        using var reopened = new ScheduleSetupPageViewModel(fixture.Shell, fixture.Workflow.CurrentSession!);
        Assert.Equal(requireLastDay, reopened.RequireChampionshipFinalsOnLastDay);
        Assert.Equal(ScheduleAutoSchedulingStrategy.Compact, reopened.BuildSetup().Policy.Strategy);
        Assert.Empty(reopened.BuildSetup().Policy.DayLoadTargets);
        if (requireLastDay) Assert.Contains("冠亚军决赛固定在最后比赛日：2026-10-04", reopened.SuccessSummary);
    }

    [Fact]
    public async Task FailureExplainsCapturedFinalDayRequirementAndKeepsTheSavedSchedule()
    {
        using var fixture = new ScheduleUiFixture();
        using var page = Page(fixture);
        AddTwoDays(page);
        await page.GenerateCommand.ExecuteAsync();
        var saved = fixture.Workflow.CurrentSession!;
        var bytes = File.ReadAllBytes(saved.WorkspacePath);
        page.RequireChampionshipFinalsOnLastDay = true;
        page.Days[0].AddRefereeWindowCommand.Execute(null);
        // The last day has zero referees, though day one has ample physical capacity.
        await page.GenerateCommand.ExecuteAsync();
        Assert.NotNull(page.Failure);
        Assert.Contains("2026-10-04", page.FailureAdvice);
        Assert.Contains("决赛", page.FailureAdvice);
        Assert.Contains("不会", page.FailureAdvice);
        Assert.True(page.RequireChampionshipFinalsOnLastDay);
        Assert.Same(saved, fixture.Workflow.CurrentSession);
        Assert.Equal(bytes, File.ReadAllBytes(saved.WorkspacePath));
        // Changing the draft must not rewrite the explanation of the failed attempt.
        page.RequireChampionshipFinalsOnLastDay = false;
        page.Days[0].DateText = "2026-10-10";
        Assert.Contains("2026-10-04", page.FailureAdvice);
        Assert.DoesNotContain("2026-10-10", page.FailureAdvice);
    }

    [Fact]
    public void FinalDayHintTracksLatestDateRatherThanCardOrder()
    {
        using var fixture = new ScheduleUiFixture();
        using var page = Page(fixture);
        AddTwoDays(page);
        page.Days[1].DateText = "2026-10-06";
        Assert.Contains("2026-10-06", page.FinalDayHint);
        page.Days[1].RemoveCommand.Execute(null);
        Assert.Contains("2026-10-04", page.FinalDayHint);
        page.Days[0].RemoveCommand.Execute(null);
        Assert.Contains("请先选择比赛日期", page.FinalDayHint);
    }

    private static ScheduleSetupPageViewModel Page(ScheduleUiFixture fixture)
    {
        fixture.Shell.RegisterPageFactory(WorkspaceRoute.ScheduleSetup, session => new ScheduleSetupPageViewModel(fixture.Shell, session));
        fixture.Shell.Navigate(WorkspaceRoute.ScheduleSetup);
        return Assert.IsType<ScheduleSetupPageViewModel>(fixture.Shell.CurrentPage);
    }

    private static void AddTwoDays(ScheduleSetupPageViewModel page)
    {
        page.Days[0].DateText = "2026-10-03";
        page.AddDayCommand.Execute(null);
        foreach (var day in page.Days) { day.CourtsText = "B1, B2, B3"; day.StartText = "14:00"; day.EndText = "18:00"; }
    }
}
