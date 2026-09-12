using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Persistence;
using BadmintonDraw.Workflows;
using BadmintonDraw.Workflows.Tournaments;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using SkiaSharp;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class DrawPackageWorkflowTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("draw-package-").FullName;
    private string PathFor(string name) => Path.Combine(directory, name);
    public void Dispose() => Directory.Delete(directory, true);

    [Fact]
    public void ExportRequiresAnExistingDrawAndMatchingConfirmationState()
    {
        var workflow = Ready();
        var id = workflow.CurrentSession!.Workspace.Projects[0].Id;
        AssertExportUnchanged(workflow, () => workflow.ExportDrawPackage(id,
            Request(DrawExportState.Preview), Revision(workflow)), "draw.preview-required");
        Assert.Null(workflow.CurrentSession.Workspace.Projects[0].Draw);
        Preview(workflow);
        AssertExportUnchanged(workflow, () => workflow.ExportDrawPackage(id,
            Request(DrawExportState.Confirmed), Revision(workflow)), "draw.export-state");
        workflow.ConfirmDraw(id, Revision(workflow));
        AssertExportUnchanged(workflow, () => workflow.ExportDrawPackage(id,
            Request(DrawExportState.Preview), Revision(workflow)), "draw.export-state");
        Assert.False(Directory.Exists(PathFor("exports")));
    }

    [Fact]
    public void PreviewAllFormatsContainVisibleStatusAndIdentityAndOneSavedAudit()
    {
        var workflow = Ready();
        Preview(workflow);
        var before = workflow.CurrentSession!.Workspace;
        var result = workflow.ExportDrawPackage(null, Request(DrawExportState.Preview, WorkflowExportFormat.All), before.Revision);
        Assert.Equal(4, result.Outputs.Count);
        Assert.Equal(before.Revision, result.SourceRevision);
        Assert.Equal(before.Revision + 1, result.Command.Workspace.Revision);
        Assert.Equal(TournamentStage.RostersReady, result.Command.Workspace.Stage);
        Assert.Equal(JsonSerializer.Serialize(before.Projects), JsonSerializer.Serialize(result.Command.Workspace.Projects));
        Assert.Null(result.Command.Workspace.Schedule);
        var audit = Assert.Single(result.Command.Workspace.AuditEvents, a => a.Action == "DrawPackageExported");
        Assert.Equal(result.AuditId, audit.Id);
        using var auditJson = JsonDocument.Parse(audit.Detail);
        Assert.Equal(result.Outputs.Select(o => o.Path), auditJson.RootElement.GetProperty("Outputs")
            .EnumerateArray().Select(o => o.GetProperty("Path").GetString()));
        Assert.Equal(before.Revision + 1, new TournamentWorkspaceStore().Read(result.Command.WorkspacePath).Revision);
        var excel = result.Outputs.Single(o => o.Format == WorkflowExportFormat.Excel).Path;
        using var book = new XLWorkbook(excel);
        var bracket = book.Worksheet("对阵表");
        Assert.Contains("未确认抽签预览", bracket.Cell(1, 1).GetString());
        Assert.Contains("合成测试杯", bracket.Cell(1, 1).GetString());
        Assert.Contains("男子单打", bracket.Cell(1, 1).GetString());
        Assert.Equal("首轮赛", bracket.Cell(4, 1).GetString());
        Assert.Contains(bracket.MergedRanges, range => range.RangeAddress.FirstAddress.RowNumber == 3 &&
            range.RangeAddress.FirstAddress.ColumnNumber == 1 &&
            range.RangeAddress.LastAddress.ColumnNumber == bracket.LastColumnUsed(XLCellsUsedOptions.All)!.ColumnNumber());
        var cells = string.Join("\n", bracket.CellsUsed().Select(c => c.GetString()));
        Assert.Contains(before.Id.ToString(), cells);
        Assert.Contains(before.Projects[0].Id.ToString(), cells);
        Assert.Contains("public-seed", cells);
        Assert.Contains(before.Projects[0].Draw!.Result.Audit.InputHash, cells);
        var auditCells = string.Join("\n", book.Worksheet("抽签设置与审计信息").CellsUsed().Select(c => c.GetString()));
        Assert.Contains("来源文件 SHA-256", auditCells);
        Assert.Contains(result.AuditId.ToString(), auditCells);
        Assert.Contains(before.Projects[0].Roster!.ContentHash, auditCells);
        Assert.True(book.Worksheets.Contains("当前名单"));
        foreach (var output in result.Outputs.Where(o => o.Format is WorkflowExportFormat.Png or WorkflowExportFormat.Jpeg))
        {
            using var bitmap = SKBitmap.Decode(output.Path);
            Assert.NotNull(bitmap);
            Assert.True(bitmap.Width > 100 && bitmap.Height > 100);
        }
        var pdf = File.ReadAllBytes(result.Outputs.Single(o => o.Format == WorkflowExportFormat.A4Pdf).Path);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(pdf));
        Assert.Contains("/ToUnicode", Encoding.ASCII.GetString(pdf));
        RetainFixture("preview", result);
    }

    [Fact]
    public void ConfirmedMultiProjectPackageReopensWithoutScheduleAndUpgradePreservesStoredDrawAndGraphBytes()
    {
        var workflow = Ready(twoProjects: true);
        Preview(workflow);
        var first = workflow.CurrentSession!.Workspace.Projects[0].Id;
        workflow.ConfirmDraw(first, Revision(workflow));
        Assert.Equal(TournamentStage.RostersReady, workflow.CurrentSession.Workspace.Stage);
        AssertExportUnchanged(workflow, () => workflow.ExportDrawPackage(null,
            Request(DrawExportState.Confirmed), Revision(workflow)), "draw.export-state");
        var selected = workflow.ExportDrawPackage(first, Request(DrawExportState.Confirmed), Revision(workflow));
        Assert.Single(selected.Outputs);
        workflow.ConfirmDraw(workflow.CurrentSession.Workspace.Projects[1].Id, Revision(workflow));
        var result = workflow.ExportDrawPackage(null, Request(DrawExportState.Confirmed, WorkflowExportFormat.All) with
            { OutputDirectory = PathFor("confirmed") }, Revision(workflow));
        Assert.Equal(8, result.Outputs.Count);
        foreach (var output in result.Outputs.Where(o => o.Format == WorkflowExportFormat.Excel))
        {
            using var book = new XLWorkbook(output.Path);
            Assert.Contains("已确认抽签结果", book.Worksheet("对阵表").Cell(1, 1).GetString());
            Assert.DoesNotContain("未确认抽签预览", book.Worksheet("对阵表").Cell(1, 1).GetString());
        }
        var stored = ReadDrawGraphJson(result.Command.WorkspacePath);
        var reopenedWorkflow = new TournamentWorkspaceWorkflow();
        var reopened = reopenedWorkflow.OpenWorkspace(result.Command.WorkspacePath);
        Assert.Equal(TournamentStage.DrawsConfirmed, reopened.Workspace.Stage);
        Assert.Null(reopened.Workspace.Schedule);
        Assert.Equal(0L, Scalar(reopened.WorkspacePath, "select count(*) from schedule"));
        var upgraded = reopenedWorkflow.UpgradeToFullTournament(reopened.Workspace.Revision);
        Assert.Equal(TournamentPurpose.FullTournament, upgraded.Workspace.Purpose);
        Assert.Equal(stored, ReadDrawGraphJson(upgraded.WorkspacePath));
        Assert.Equal(0L, Scalar(upgraded.WorkspacePath, "select count(*) from schedule"));
        RetainFixture("confirmed", result);
    }

    [Fact]
    public void ExistingOutputsRequireExplicitConsentAndStaleRevisionProducesNoFiles()
    {
        var workflow = Ready(); Preview(workflow);
        var request = Request(DrawExportState.Preview);
        var result = workflow.ExportDrawPackage(null, request, Revision(workflow));
        var path = Assert.Single(result.Outputs).Path;
        var original = SHA256.HashData(File.ReadAllBytes(path));
        AssertExportUnchanged(workflow, () => workflow.ExportDrawPackage(null, request, Revision(workflow)), "export.exists");
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(path)));
        var replaced = workflow.ExportDrawPackage(null, request with { OverwriteExisting = true }, Revision(workflow));
        Assert.Equal(path, Assert.Single(replaced.Outputs).Path);
        AssertExportUnchanged(workflow, () => workflow.ExportDrawPackage(null,
            request with { OutputDirectory = PathFor("stale") }, Revision(workflow) - 1), "RevisionConflict");
        Assert.False(Directory.Exists(PathFor("stale")));
    }

    [Fact]
    public void PartialPublicationReportsExistingFilesWithoutSuccessAuditOrBusinessMutation()
    {
        var files = new FailingExportFiles { FailAtPublication = 2 };
        var workflow = Ready(packages: new DrawPackageWorkflow(files)); Preview(workflow);
        var session = workflow.CurrentSession!;
        var hash = SHA256.HashData(File.ReadAllBytes(session.WorkspacePath));
        var error = Assert.Throws<DrawPackageExportException>(() => workflow.ExportDrawPackage(null,
            Request(DrawExportState.Preview, WorkflowExportFormat.All), Revision(workflow)));
        Assert.False(error.AuditRecorded);
        Assert.Single(error.Outputs);
        Assert.True(File.Exists(error.Outputs[0].Path));
        Assert.Same(session, workflow.CurrentSession);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(session.WorkspacePath)));
        Assert.Single(Directory.GetFiles(PathFor("exports")));
        Assert.Empty(Directory.GetDirectories(PathFor("exports")));
    }

    [Fact]
    public void AuditSaveFailureReportsAllPublishedFilesAndRecoveryDetails()
    {
        var files = new FailingWorkspaceFiles();
        var workflow = Ready(store: new TournamentWorkspaceStore(files)); Preview(workflow);
        var session = workflow.CurrentSession!;
        files.FailPublication = true;
        var error = Assert.Throws<DrawPackageExportException>(() => workflow.ExportDrawPackage(null,
            Request(DrawExportState.Preview), Revision(workflow)));
        Assert.Single(error.Outputs);
        Assert.True(File.Exists(error.Outputs[0].Path));
        Assert.False(error.AuditRecorded);
        Assert.False(error.Error.Committed);
        Assert.True(File.Exists(error.Error.BackupPath));
        Assert.True(File.Exists(error.Error.CandidatePath));
        Assert.Same(session, workflow.CurrentSession);
        Assert.DoesNotContain(new TournamentWorkspaceStore().Read(session.WorkspacePath).AuditEvents,
            a => a.Action == "DrawPackageExported");
    }

    [Fact]
    public void TemplateExportProtectsWorkspaceBackupRosterAndExistingTargets()
    {
        var workflow = Ready();
        var projectId = workflow.CurrentSession!.Workspace.Projects[0].Id;
        foreach (var target in new[] { workflow.CurrentSession.WorkspacePath, PathFor("backup.szbd"), PathFor("roster.xlsx") })
            AssertExportUnchanged(workflow, () => workflow.ExportRosterTemplate(projectId, target, Revision(workflow), true),
                "export.protected-path");
        var result = workflow.ExportRosterTemplate(projectId, PathFor("template.xlsx"), Revision(workflow));
        using var workbook = new XLWorkbook(PathFor("template.xlsx"));
        Assert.Equal("学号", workbook.Worksheet(1).Cell(1, 2).GetString());
        Assert.Single(result.Workspace.AuditEvents, a => a.Action == "RosterTemplateExported");
        Assert.Null(result.Workspace.Projects[0].Draw);
        AssertExportUnchanged(workflow, () => workflow.ExportRosterTemplate(projectId, PathFor("template.xlsx"), Revision(workflow)),
            "export.exists");
    }

    [Fact]
    public void ExportRejectsSymbolicLinkTargetsEvenWithOverwriteConsent()
    {
        var workflow = Ready(); Preview(workflow);
        var result = workflow.ExportDrawPackage(null, Request(DrawExportState.Preview), Revision(workflow));
        var path = Assert.Single(result.Outputs).Path;
        File.Delete(path);
        File.CreateSymbolicLink(path, workflow.CurrentSession!.WorkspacePath);
        AssertExportUnchanged(workflow, () => workflow.ExportDrawPackage(null,
            Request(DrawExportState.Preview) with { OverwriteExisting = true }, Revision(workflow)), "export.protected-path");
    }

    [Fact]
    public void ChangedArchiveIdentityWithSameRevisionCannotExportAnotherWorkspace()
    {
        var workflow = Ready(); Preview(workflow);
        var session = workflow.CurrentSession!;
        var otherPath = PathFor("other.szbd");
        new TournamentWorkspaceStore().Create(otherPath, session.Workspace with { Id = Guid.NewGuid(), Name = "另一赛事" });
        File.Copy(otherPath, session.WorkspacePath, true);
        AssertExportUnchanged(workflow, () => workflow.ExportDrawPackage(null,
            Request(DrawExportState.Preview), Revision(workflow)), "workspace.session-changed");
        Assert.False(Directory.Exists(PathFor("exports")));
    }

    [Fact]
    public void CollisionInDerivedPdfPreventsPublishingOtherRequestedFormats()
    {
        var workflow = Ready(); Preview(workflow);
        workflow.ExportDrawPackage(null, Request(DrawExportState.Preview, WorkflowExportFormat.A4Pdf), Revision(workflow));
        AssertExportUnchanged(workflow, () => workflow.ExportDrawPackage(null,
            Request(DrawExportState.Preview, WorkflowExportFormat.All), Revision(workflow)), "export.exists");
        Assert.Equal(".pdf", Path.GetExtension(Assert.Single(Directory.GetFiles(PathFor("exports")))));
    }

    [Fact]
    public void CommittedAuditReadFailureReturnsFilesAndMarksAuditRecordedAndReloadRequired()
    {
        var files = new FailingWorkspaceFiles();
        var store = new FailingReadStore(files);
        var workflow = Ready(store: store); Preview(workflow);
        var session = workflow.CurrentSession!;
        store.FailedPath = session.WorkspacePath;
        files.AfterPublication = () => store.FailuresRemaining = 2;
        var error = Assert.Throws<DrawPackageExportException>(() => workflow.ExportDrawPackage(null,
            Request(DrawExportState.Preview), Revision(workflow)));
        Assert.Single(error.Outputs);
        Assert.True(File.Exists(error.Outputs[0].Path));
        Assert.True(error.AuditRecorded);
        Assert.True(error.Error.Committed);
        Assert.True(workflow.CurrentSession!.RequiresReload);
        var saved = new TournamentWorkspaceStore().Read(session.WorkspacePath);
        Assert.Equal(session.Workspace.Revision + 1, saved.Revision);
        Assert.Single(saved.AuditEvents, a => a.Action == "DrawPackageExported");
    }

    private TournamentWorkspaceWorkflow Ready(bool twoProjects = false, ITournamentWorkspaceStore? store = null,
        DrawPackageWorkflow? packages = null)
    {
        var workflow = new TournamentWorkspaceWorkflow(store, packages);
        var created = workflow.CreateWorkspace(new("合成测试杯", TournamentKind.Individual, TournamentPurpose.PublicDrawOnly,
            twoProjects ? [new(EventDiscipline.MenSingles, CompetitionMode.SinglesKnockout),
                new(EventDiscipline.WomenSingles, CompetitionMode.SinglesRoundRobin)] :
                [new(EventDiscipline.MenSingles, CompetitionMode.SinglesKnockout)], PathFor("workspace.szbd")));
        using (var book = new XLWorkbook())
        {
            var sheet = book.AddWorksheet("名单");
            sheet.Cell(1, 1).Value = "姓名"; sheet.Cell(1, 2).Value = "学号";
            for (var i = 0; i < 4; i++) { sheet.Cell(i + 2, 1).Value = "测试选手" + (i + 1); sheet.Cell(i + 2, 2).Value = "ID00" + i; }
            book.SaveAs(PathFor("roster.xlsx"));
        }
        foreach (var project in created.Workspace.Projects)
            workflow.ImportRoster(project.Id, PathFor("roster.xlsx"), Revision(workflow));
        return workflow;
    }
    private static void Preview(TournamentWorkspaceWorkflow workflow)
    {
        foreach (var project in workflow.CurrentSession!.Workspace.Projects)
            workflow.PreviewDraw(project.Id, new(project.CompetitionMode, EventKind.Singles, 1, "public-seed"), Revision(workflow));
    }
    private DrawExportRequest Request(DrawExportState state, WorkflowExportFormat format = WorkflowExportFormat.Excel) =>
        new(PathFor("exports"), format, state);
    private static long Revision(TournamentWorkspaceWorkflow workflow) => workflow.CurrentSession!.Workspace.Revision;
    private static void AssertExportUnchanged(TournamentWorkspaceWorkflow workflow, Action action, string code)
    {
        var before = workflow.CurrentSession!;
        var hash = SHA256.HashData(File.ReadAllBytes(before.WorkspacePath));
        var error = Assert.Throws<DrawPackageExportException>(action);
        Assert.Equal(code, error.Error.Code);
        Assert.Empty(error.Outputs);
        Assert.False(error.AuditRecorded);
        Assert.Same(before, workflow.CurrentSession);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(before.WorkspacePath)));
    }
    private static object? Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar();
    }
    private static string ReadDrawGraphJson(string path) =>
        (string)Scalar(path, "select group_concat(json, '|') from (select json from project_draws order by project_id)")! +
        (string)Scalar(path, "select group_concat(json, '|') from (select json from match_graphs order by project_id)")!;
    private static void RetainFixture(string name, DrawPackageExportResult result)
    {
        var root = Environment.GetEnvironmentVariable("BADMINTON_DRAW_FIXTURES");
        if (string.IsNullOrEmpty(root)) return;
        var folder = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        foreach (var output in result.Outputs) File.Copy(output.Path, Path.Combine(folder, Path.GetFileName(output.Path)), true);
    }
    private sealed class FailingExportFiles : DrawPackageFileOperations
    {
        private int count;
        public int FailAtPublication { get; init; }
        public override void Publish(string stagedPath, string destination, bool overwrite)
        {
            if (++count == FailAtPublication) throw new IOException("模拟导出发布失败");
            base.Publish(stagedPath, destination, overwrite);
        }
    }
    private sealed class FailingWorkspaceFiles : WorkspaceFileOperations
    {
        public bool FailPublication { get; set; }
        public Action? AfterPublication { get; set; }
        public override void Publish(string candidate, string destination, bool overwrite)
        {
            if (FailPublication) throw new IOException("模拟审计保存失败");
            base.Publish(candidate, destination, overwrite);
            AfterPublication?.Invoke();
        }
    }
    private sealed class FailingReadStore(WorkspaceFileOperations files) : TournamentWorkspaceStore(files)
    {
        public string? FailedPath { get; set; }
        public int FailuresRemaining { get; set; }
        public override TournamentWorkspace Read(string path)
        {
            if (path == FailedPath && FailuresRemaining-- > 0)
                throw new WorkspaceStoreException("InvalidWorkspace", "模拟已提交审计重新读取失败");
            return base.Read(path);
        }
    }
}
