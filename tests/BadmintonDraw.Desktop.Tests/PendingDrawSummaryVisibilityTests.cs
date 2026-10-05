using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;
using BadmintonDraw.Workflows.Tournaments;
using ClosedXML.Excel;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class PendingDrawSummaryVisibilityTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Theory]
    [InlineData(TournamentKind.Team, 1, false)]
    [InlineData(TournamentKind.Individual, 1, false)]
    [InlineData(TournamentKind.Individual, 2, true)]
    public Task PendingSummaryOnlyShowsForMultipleIndividualProjectsWhileConfirmationStillGatesScheduling(
        TournamentKind kind, int projectCount, bool showSummary) => ui.Dispatch(() =>
    {
        using var data = new PendingDrawFixture(kind, projectCount);
        var window = new AppShellWindow(data.Workflow,
            new RecentWorkspaceStore(Path.Combine(data.DirectoryPath, "recent.json")));
        try
        {
            window.Show();
            var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            Assert.True(shell.Navigate(WorkspaceRoute.PublicDraw));
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
            Layout();
            var page = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage);
            var view = Assert.Single(window.GetVisualDescendants().OfType<PublicDrawPage>());
            var summary = view.FindControl<Button>("PendingDrawSummaryButton")!;
            var schedule = Assert.Single(view.GetVisualDescendants().OfType<Button>(), button =>
                ReferenceEquals(button.Command, page.ContinueToScheduleCommand));

            Assert.NotNull(summary);
            Assert.True(page.HasPendingDraws);
            Assert.Equal(showSummary, summary.IsEffectivelyVisible);
            Assert.False(schedule.IsEffectivelyEnabled);
            foreach (var project in data.Workflow.CurrentSession!.Workspace.Projects)
            {
                data.Workflow.PreviewDraw(project.Id,
                    new(project.CompetitionMode, kind == TournamentKind.Team ? EventKind.Team : EventKind.Singles, 1, "pending-summary"),
                    data.Workflow.CurrentSession.Workspace.Revision);
                Layout();
                Assert.Equal(showSummary, summary.IsEffectivelyVisible);
                Assert.False(schedule.IsEffectivelyEnabled);

                data.Workflow.ConfirmDraw(project.Id, data.Workflow.CurrentSession.Workspace.Revision);
                Layout();
                if (page.PendingProjects.Count > 0)
                {
                    Assert.Equal(showSummary, summary.IsEffectivelyVisible);
                    Assert.False(schedule.IsEffectivelyEnabled);
                }
            }
            Assert.False(summary.IsEffectivelyVisible);
            Assert.False(page.HasPendingDraws);
            Assert.True(schedule.IsEffectivelyEnabled);
            Assert.Null(data.Workflow.CurrentSession.Workspace.Schedule);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    private sealed class PendingDrawFixture : IDisposable
    {
        internal string DirectoryPath { get; } = Directory.CreateTempSubdirectory("pending-summary-").FullName;
        internal TournamentWorkspaceWorkflow Workflow { get; } = new();

        internal PendingDrawFixture(TournamentKind kind, int projectCount)
        {
            var disciplines = kind == TournamentKind.Team
                ? new[] { EventDiscipline.Team }
                : new[] { EventDiscipline.MenSingles, EventDiscipline.WomenSingles }.Take(projectCount).ToArray();
            Workflow.CreateWorkspace(new("待确认提示测试", kind, TournamentPurpose.FullTournament,
                disciplines.Select(discipline => new WorkspaceProjectRequest(discipline,
                    kind == TournamentKind.Team ? CompetitionMode.TeamKnockout : CompetitionMode.SinglesKnockout)).ToArray(),
                Path.Combine(DirectoryPath, "比赛.szbd")));
            var rosterPath = Path.Combine(DirectoryPath, "名单.xlsx");
            using (var workbook = new XLWorkbook())
            {
                var sheet = workbook.AddWorksheet("名单");
                sheet.Cell(1, 1).Value = kind == TournamentKind.Team ? "队伍" : "姓名";
                sheet.Cell(1, 2).Value = "学号";
                for (var i = 1; i <= 2; i++)
                {
                    sheet.Cell(i + 1, 1).Value = "参赛方" + i;
                    if (kind == TournamentKind.Individual) sheet.Cell(i + 1, 2).Value = "student-" + i;
                }
                workbook.SaveAs(rosterPath);
            }
            foreach (var project in Workflow.CurrentSession!.Workspace.Projects)
                Workflow.ImportRoster(project.Id, rosterPath, Workflow.CurrentSession.Workspace.Revision);
        }

        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }

    public void Dispose() => ui.Dispose();
}
