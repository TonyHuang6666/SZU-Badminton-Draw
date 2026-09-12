using System.Security.Cryptography;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Excel;
using BadmintonDraw.Persistence;
using BadmintonDraw.Workflows.Tournaments;
using ClosedXML.Excel;

namespace BadmintonDraw.Tests;

internal sealed class WorkspaceResultImportFacadeFixture : IDisposable
{
    internal string DirectoryPath { get; } = Directory.CreateTempSubdirectory("workspace-results-").FullName;
    internal ImportObservedStore Store { get; }
    internal TournamentWorkspaceWorkflow Workflow { get; }
    internal WorkspaceSession Session => Workflow.CurrentSession!;
    internal long Revision => Session.Workspace.Revision;
    internal string PathFor(string name) => Path.Combine(DirectoryPath, name);
    internal static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    internal WorkspaceResultImportFacadeFixture(int projectCount = 2, int entrants = 4, ITournamentWorkspaceStore? store = null)
    {
        Store = new(store ?? new TournamentWorkspaceStore());
        Workflow = new(Store);
        Workflow.CreateWorkspace(new("现场导入测试", TournamentKind.Individual, TournamentPurpose.FullTournament,
            new[] { EventDiscipline.MenSingles, EventDiscipline.WomenSingles }.Take(projectCount)
                .Select(d => new WorkspaceProjectRequest(d, CompetitionMode.SinglesKnockout)).ToArray(), PathFor("赛事.szbd")));
        foreach (var project in Session.Workspace.Projects)
        {
            var path = PathFor(project.Id + ".xlsx");
            using var book = new XLWorkbook(); var sheet = book.AddWorksheet("名单");
            sheet.Cell(1, 1).Value = "姓名"; sheet.Cell(1, 2).Value = "学号";
            for (var i = 0; i < entrants; i++)
            {
                sheet.Cell(i + 2, 1).Value = "选手【" + i + "】";
                sheet.Cell(i + 2, 2).Value = project.SortOrder + "-" + i;
            }
            book.SaveAs(path); Workflow.ImportRoster(project.Id, path, Revision);
        }
        foreach (var project in Session.Workspace.Projects)
        {
            Workflow.PreviewDraw(project.Id, new(CompetitionMode.SinglesKnockout, EventKind.Singles, 1, "facade-results"), Revision);
            Workflow.ConfirmDraw(project.Id, Revision);
        }
        Workflow.GenerateSchedule(new([new(new(2026, 9, 13), new(9, 0), new(18, 0), ["B1", "B2"]),
            new(new(2026, 9, 14), new(9, 0), new(18, 0), ["B1", "B2"])], 2, 15, 6),
            new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []), Revision);
        Store.Mutations = 0;
    }

    internal string Export(string name = "记录.xlsx", bool fill = true, IReadOnlyList<WorkspaceMatchKey>? keys = null)
    {
        var context = new WorkspaceScheduleExportContext(Session.Workspace);
        var rows = (keys ?? context.MatchKeys.Reverse().ToArray()).Select(key => new WorkspaceRecordExportRow(key,
            DateOnly.Parse(Session.Workspace.Schedule!.Placements[key.MatchId].DayLabel))).ToArray();
        var path = PathFor(name); new WorkspaceMatchRecordWriter().Write(path, context, rows);
        if (fill) Edit(path, sheet =>
        {
            for (var row = 6; row < 6 + rows.Length; row++)
            {
                sheet.Cell(row, 9).Value = "21-10"; sheet.Cell(row, 10).Value = 20;
                sheet.Cell(row, 12).Value = "A"; sheet.Cell(row, 21).Value = "正常";
                sheet.Cell(row, 22).Value = sheet.Cell(row, 2).GetString();
            }
        });
        return path;
    }

    internal static void Edit(string path, Action<IXLWorksheet> edit)
    { using var book = new XLWorkbook(path); edit(book.Worksheet("对阵记录表")); book.Save(); }
    public void Dispose() => Directory.Delete(DirectoryPath, true);
}

internal sealed class ImportObservedStore(ITournamentWorkspaceStore inner) : ITournamentWorkspaceStore
{
    internal int Mutations { get; set; }
    internal Action? BeforeMutation { get; set; }
    public TournamentWorkspace Create(string path, TournamentWorkspace workspace) => inner.Create(path, workspace);
    public TournamentWorkspace Read(string path) => inner.Read(path);
    public WorkspaceMutationResult Mutate(string path, long expectedRevision, Func<TournamentWorkspace, TournamentWorkspace> mutation)
    {
        Mutations++; var before = BeforeMutation; BeforeMutation = null; before?.Invoke();
        return inner.Mutate(path, expectedRevision, mutation);
    }
    public string CreateBackup(string path) => inner.CreateBackup(path);
    public WorkspaceBackupSnapshot InspectBackup(string path) => inner.InspectBackup(path);
    public WorkspaceRecoveryInspection InspectRecovery(string path, string backup) => inner.InspectRecovery(path, backup);
    public WorkspaceMutationResult RestoreBackup(string path, WorkspaceRestoreRequest request) => inner.RestoreBackup(path, request);
    public WorkspaceMutationResult RecoverFromBackup(string path, WorkspaceRecoveryRequest request) => inner.RecoverFromBackup(path, request);
}
