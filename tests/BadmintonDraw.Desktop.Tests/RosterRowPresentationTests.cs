using Avalonia;
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
public sealed class RosterRowPresentationTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ImportedTeamNameAppearsOnceOnTheSameLineAsItsSerialNumber(bool hasPrimaryName) => ui.Dispatch(() =>
    {
        using var fixture = new RosterFixture(EventDiscipline.Team, hasPrimaryName);
        var window = new AppShellWindow(fixture.Workflow, new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "recent.json")))
            { Width = 1100, Height = 900 };
        try
        {
            window.Show();
            var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            Assert.True(shell.Navigate(WorkspaceRoute.Rosters));
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var page = Assert.IsType<RostersPageViewModel>(shell.CurrentPage);
            var row = page.SelectedProject!.Rows[0];
            Assert.Equal("计算机学院", row.Name);
            Assert.Equal("", row.Identity);

            var view = Assert.Single(window.GetVisualDescendants().OfType<RostersPage>());
            var labels = view.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => ReferenceEquals(text.DataContext, row) && text.IsEffectivelyVisible).ToArray();
            var name = Assert.Single(labels, text => text.Text == "计算机学院");
            var order = Assert.Single(labels, text => text.Text == "1");
            var primaryCell = name.GetVisualAncestors().OfType<StackPanel>().First();
            Assert.DoesNotContain(primaryCell.GetVisualDescendants().OfType<SelectableTextBlock>(),
                text => text.IsEffectivelyVisible && string.IsNullOrWhiteSpace(text.Text) && text.Bounds.Height > 0);
            var nameCenter = name.TranslatePoint(new Point(0, name.Bounds.Height / 2), view)!.Value.Y;
            var orderCenter = order.TranslatePoint(new Point(0, order.Bounds.Height / 2), view)!.Value.Y;
            Assert.InRange(Math.Abs(nameCenter - orderCenter), 0, 1);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Theory]
    [InlineData(EventDiscipline.MenSingles)]
    [InlineData(EventDiscipline.MenDoubles)]
    public void IndividualRowsKeepPlayerIdentityAndDoublesPartnerDetails(EventDiscipline discipline)
    {
        using var fixture = new RosterFixture(discipline);
        using var shell = new AppShellViewModel(fixture.Workflow, () => Task.FromResult<string?>(null),
            _ => Task.FromResult<string?>(null), new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "recent.json")), action => action());
        var page = new RostersPageViewModel(shell, fixture.Workflow.CurrentSession!, () => Task.FromResult<string?>(null),
            _ => Task.FromResult<string?>(null));

        var row = page.SelectedProject!.Rows[0];
        Assert.Equal("张三", row.Name);
        Assert.Equal("2026001 · 计算机学院", row.Identity);
        Assert.Equal(discipline == EventDiscipline.MenDoubles ? "李四 · 2026002 · 数学学院" : "", row.Partner);
        Assert.Equal("备注内容", row.Note);
    }

    public void Dispose() => ui.Dispose();

    private sealed class RosterFixture : IDisposable
    {
        public string DirectoryPath { get; } = Directory.CreateTempSubdirectory("roster-presentation-").FullName;
        public TournamentWorkspaceWorkflow Workflow { get; } = new();

        public RosterFixture(EventDiscipline discipline, bool hasPrimaryName = true)
        {
            var team = discipline == EventDiscipline.Team;
            Workflow.CreateWorkspace(new("名单显示测试", team ? TournamentKind.Team : TournamentKind.Individual,
                TournamentPurpose.PublicDrawOnly,
                [new(discipline, team ? CompetitionMode.TeamKnockout : CompetitionMode.SinglesKnockout)],
                Path.Combine(DirectoryPath, "比赛.szbd")));
            var path = Path.Combine(DirectoryPath, "名单.xlsx");
            using (var book = new XLWorkbook())
            {
                var sheet = book.AddWorksheet("名单");
                var headers = new[] { "学院/学部", "姓名", "学号", "搭档姓名", "搭档学号", "搭档学院/学部", "备注" };
                for (var index = 0; index < headers.Length; index++) sheet.Cell(1, index + 1).Value = headers[index];
                sheet.Cell(2, 1).Value = "计算机学院";
                if (hasPrimaryName) sheet.Cell(2, 2).Value = "张三";
                if (!team) sheet.Cell(2, 3).Value = "2026001";
                if (discipline == EventDiscipline.MenDoubles)
                {
                    sheet.Cell(2, 4).Value = "李四";
                    sheet.Cell(2, 5).Value = "2026002";
                    sheet.Cell(2, 6).Value = "数学学院";
                }
                sheet.Cell(2, 7).Value = "备注内容";
                sheet.Cell(3, 1).Value = "物理学院";
                if (hasPrimaryName) sheet.Cell(3, 2).Value = "王五";
                if (!team) sheet.Cell(3, 3).Value = "2026003";
                if (discipline == EventDiscipline.MenDoubles)
                {
                    sheet.Cell(3, 4).Value = "赵六";
                    sheet.Cell(3, 5).Value = "2026004";
                    sheet.Cell(3, 6).Value = "化学学院";
                }
                book.SaveAs(path);
            }
            var project = Workflow.CurrentSession!.Workspace.Projects[0];
            Workflow.ImportRoster(project.Id, path, Workflow.CurrentSession.Workspace.Revision);
        }

        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }
}
