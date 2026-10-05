using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;
using BadmintonDraw.Workflows.Tournaments;
using ClosedXML.Excel;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class TeamScheduleSetupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TeamGenerationIgnoresHiddenPlayerLimitsAndUsesOneDuration(bool roundRobin)
    {
        using var fixture = new TeamFixture(roundRobin);
        fixture.Shell.RegisterPageFactory(WorkspaceRoute.ScheduleSetup, session => new ScheduleSetupPageViewModel(fixture.Shell, session));
        fixture.Shell.Navigate(WorkspaceRoute.ScheduleSetup);
        var page = Assert.IsType<ScheduleSetupPageViewModel>(fixture.Shell.CurrentPage);
        page.Days[0].DateText = "2026-10-03";
        page.Days[0].StartText = "14:00";
        page.Days[0].EndText = "18:00";
        page.Days[0].CourtsText = "A1";
        page.MinimumRestText = "120";
        page.DailyMaximumText = "1";
        var timing = Assert.Single(page.ProjectTimings);
        timing.MinutesText = "10";
        timing.UseTimingSplit = true;
        timing.BoundaryText = "invalid hidden value";
        timing.AfterMinutesText = "invalid hidden value";

        await page.GenerateCommand.ExecuteAsync();

        Assert.Null(fixture.Shell.LastError);
        var saved = fixture.Workflow.CurrentSession!.Workspace.Schedule;
        Assert.NotNull(saved);
        Assert.Equal(roundRobin ? 15 : 7, saved.Placements.Count);
        Assert.Equal(0, saved.Resources.MinimumRestMinutes);
        Assert.Equal(int.MaxValue, saved.Resources.MaxPlayerMatchesPerDay);
        Assert.Equal(new ProjectMatchTiming(10), saved.Policy.ProjectTimings[timing.ProjectId]);
        Assert.All(saved.Placements.Values, p => Assert.Equal(TimeSpan.FromMinutes(10), p.EndTime - p.StartTime));
        // One court: back-to-back slots remain usable, without introducing overlap.
        var ordered = saved.Placements.Values.OrderBy(p => p.StartTime).ToArray();
        Assert.Equal(new TimeOnly(14, 0), ordered[0].StartTime);
        for (var i = 1; i < ordered.Length; i++) Assert.Equal(ordered[i - 1].EndTime, ordered[i].StartTime);
    }

    [Fact]
    public async Task TeamTimeCapacityFailureDoesNotSuggestHiddenIndividualSettings()
    {
        using var fixture = new TeamFixture(true);
        fixture.Shell.RegisterPageFactory(WorkspaceRoute.ScheduleSetup, session => new ScheduleSetupPageViewModel(fixture.Shell, session));
        fixture.Shell.Navigate(WorkspaceRoute.ScheduleSetup);
        var page = Assert.IsType<ScheduleSetupPageViewModel>(fixture.Shell.CurrentPage);
        page.Days[0].DateText = "2026-10-03";
        page.Days[0].StartText = "14:00";
        page.Days[0].EndText = "14:30";
        page.Days[0].CourtsText = "A1,A2,A3,A4,A5,A6,A7,A8";
        page.ProjectTimings[0].MinutesText = "10";

        await page.GenerateCommand.ExecuteAsync();

        Assert.Equal("PlayerTime", page.Failure?.CapacityEvidence?.Kind);
        Assert.Contains("队伍时间容量不足", page.FailureSummary);
        Assert.DoesNotContain("2147483647", page.FailureSummary);
        Assert.DoesNotContain("休息", page.FailureAdvice);
        Assert.DoesNotContain("每日上限", page.FailureAdvice);
        Assert.Null(fixture.Workflow.CurrentSession!.Workspace.Schedule);
    }

    [Fact]
    public void SavedTeamSplitBecomesUniformDraftWithoutRewritingSavedSchedule()
    {
        using var fixture = new TeamFixture(false);
        var project = fixture.Workflow.CurrentSession!.Workspace.Projects[0];
        fixture.Workflow.GenerateSchedule(new([new(new(2026, 10, 3), new(9, 0), new(18, 0), ["A1"])], 1, 20, 4),
            new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], [])
            { ProjectTimings = new Dictionary<Guid, ProjectMatchTiming> { [project.Id] = new(30, 4, 20) } },
            fixture.Workflow.CurrentSession.Workspace.Revision);
        var saved = fixture.Workflow.CurrentSession!;
        var bytes = File.ReadAllBytes(saved.WorkspacePath);
        using var page = new ScheduleSetupPageViewModel(fixture.Shell, saved);
        var timing = Assert.Single(page.ProjectTimings);

        Assert.False(timing.SupportsTimingSplit);
        Assert.False(timing.UseTimingSplit);
        Assert.Equal("20", timing.MinutesText);
        page.MinimumRestText = "invalid hidden value";
        page.DailyMaximumText = "invalid hidden value";
        var draft = page.BuildSetup();
        Assert.Equal(new ProjectMatchTiming(20), draft.Policy.ProjectTimings[project.Id]);
        Assert.Equal(0, draft.Resources.MinimumRestMinutes);
        Assert.Equal(int.MaxValue, draft.Resources.MaxPlayerMatchesPerDay);
        Assert.Equal(bytes, File.ReadAllBytes(saved.WorkspacePath));
        Assert.Same(saved, fixture.Workflow.CurrentSession);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AdvancedSettingsExposePlayerControlsOnlyForIndividualEvents(bool team)
    {
        using var ui = HeadlessUnitTestSession.StartNew(typeof(App));
        await ui.Dispatch(() =>
        {
            using var teams = team ? new TeamFixture(false) : null;
            using var individuals = !team ? new ScheduleUiFixture() : null;
            using var model = new ScheduleSetupPageViewModel(teams?.Shell ?? individuals!.Shell,
                teams?.Workflow.CurrentSession! ?? individuals!.Workflow.CurrentSession!);
            var page = new ScheduleSetupPage { DataContext = model };
            page.FindControl<Expander>("AdvancedScheduleSettings")!.IsExpanded = true;
            var window = new Window { Content = page, Width = 1100, Height = 900 };
            try
            {
                window.Show(); window.UpdateLayout();
                var fields = page.GetVisualDescendants().OfType<TextBox>().ToArray();
                Assert.True(fields.Single(f => AutomationProperties.GetName(f) == "全局裁判人数").IsEffectivelyVisible);
                Assert.Equal(!team, fields.Single(f => AutomationProperties.GetName(f) == "全局最小休息分钟").IsEffectivelyVisible);
                Assert.Equal(!team, fields.Single(f => AutomationProperties.GetName(f) == "全局每日场次上限").IsEffectivelyVisible);
                Assert.Equal(!team, page.GetVisualDescendants().OfType<TextBlock>()
                    .Single(t => t.Text == "分段比赛时长").IsEffectivelyVisible);
                if (team)
                    Assert.DoesNotContain(page.GetVisualDescendants().OfType<TextBlock>(),
                        t => t.IsEffectivelyVisible && (t.Text?.Contains("选手休息") == true || t.Text?.Contains("休息时间") == true));
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    private sealed class TeamFixture : IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("team-schedule-ui-").FullName;
        public TournamentWorkspaceWorkflow Workflow { get; } = new();
        public AppShellViewModel Shell { get; }
        public TeamFixture(bool roundRobin)
        {
            var mode = roundRobin ? CompetitionMode.TeamRoundRobin : CompetitionMode.TeamKnockout;
            Workflow.CreateWorkspace(new("团体排程", TournamentKind.Team, TournamentPurpose.FullTournament,
                [new(EventDiscipline.Team, mode)], Path.Combine(directory, "比赛.szbd")));
            var project = Workflow.CurrentSession!.Workspace.Projects[0];
            var roster = Path.Combine(directory, "单位.xlsx");
            using (var book = new XLWorkbook())
            {
                var sheet = book.AddWorksheet("单位"); sheet.Cell(1, 1).Value = "学院/学部";
                for (var i = 1; i <= (roundRobin ? 6 : 8); i++) sheet.Cell(i + 1, 1).Value = "学院" + i;
                book.SaveAs(roster);
            }
            Workflow.ImportRoster(project.Id, roster, Workflow.CurrentSession.Workspace.Revision);
            Workflow.PreviewDraw(project.Id, new(mode, EventKind.Team, 1, "team-ui"), Workflow.CurrentSession.Workspace.Revision);
            Workflow.ConfirmDraw(project.Id, Workflow.CurrentSession.Workspace.Revision);
            Shell = new(Workflow, () => Task.FromResult<string?>(null), _ => Task.FromResult<string?>(null),
                new RecentWorkspaceStore(Path.Combine(directory, "recent.json")), action => action());
        }
        public void Dispose() { Shell.Dispose(); Directory.Delete(directory, true); }
    }
}
