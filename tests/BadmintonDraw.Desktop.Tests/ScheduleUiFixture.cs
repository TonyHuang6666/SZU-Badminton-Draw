using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Workflows.Tournaments;
using ClosedXML.Excel;

namespace BadmintonDraw.Desktop.Tests;

internal sealed class ScheduleUiFixture : IDisposable
{
    public string DirectoryPath { get; } = Directory.CreateTempSubdirectory("schedule-ui-").FullName;
    public TournamentWorkspaceWorkflow Workflow { get; }
    public AppShellViewModel Shell { get; }
    public ScheduleUiFixture(int count = 1, TournamentWorkspaceWorkflow? workflow = null)
    {
        Workflow = workflow ?? new();
        var disciplines = new[] { EventDiscipline.MenSingles, EventDiscipline.WomenSingles, EventDiscipline.MenDoubles };
        Workflow.CreateWorkspace(new("统一赛程测试", TournamentKind.Individual, TournamentPurpose.FullTournament,
            disciplines.Take(count).Select(d => new WorkspaceProjectRequest(d, CompetitionMode.SinglesKnockout)).ToArray(),
            Path.Combine(DirectoryPath, "比赛.szbd")));
        foreach (var project in Workflow.CurrentSession!.Workspace.Projects)
        {
            var path = Path.Combine(DirectoryPath, project.Id + ".xlsx");
            using (var book = new XLWorkbook())
            {
                var sheet = book.AddWorksheet("名单"); sheet.Cell(1, 1).Value = "姓名"; sheet.Cell(1, 2).Value = "学号";
                if (project.Discipline == EventDiscipline.MenDoubles) { sheet.Cell(1, 3).Value = "搭档姓名"; sheet.Cell(1, 4).Value = "搭档学号"; }
                for (var i = 1; i <= 2; i++)
                {
                    sheet.Cell(i + 1, 1).Value = project.DisplayName + i; sheet.Cell(i + 1, 2).Value = project.Id + "-" + i;
                    if (project.Discipline == EventDiscipline.MenDoubles) { sheet.Cell(i + 1, 3).Value = "搭档" + i; sheet.Cell(i + 1, 4).Value = project.Id + "-p-" + i; }
                }
                book.SaveAs(path);
            }
            Workflow.ImportRoster(project.Id, path, Workflow.CurrentSession!.Workspace.Revision);
        }
        foreach (var project in Workflow.CurrentSession!.Workspace.Projects)
        {
            Workflow.PreviewDraw(project.Id, new(project.CompetitionMode, project.Discipline == EventDiscipline.MenDoubles ? EventKind.Doubles : EventKind.Singles, 1, "ui-test"), Workflow.CurrentSession.Workspace.Revision);
            Workflow.ConfirmDraw(project.Id, Workflow.CurrentSession.Workspace.Revision);
        }
        Shell = new(Workflow, () => Task.FromResult<string?>(null), _ => Task.FromResult<string?>(null), new RecentWorkspaceStore(Path.Combine(DirectoryPath, "recent.json")), action => action());
    }
    public void Dispose() { Shell.Dispose(); Directory.Delete(DirectoryPath, true); }
}
