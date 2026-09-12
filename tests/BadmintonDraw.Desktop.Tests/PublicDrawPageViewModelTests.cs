using System.Text.Json;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Persistence;
using BadmintonDraw.Workflows;
using BadmintonDraw.Workflows.Tournaments;
using ClosedXML.Excel;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class PublicDrawPageViewModelTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("desktop-draw-").FullName;
    private readonly List<IDisposable> owned = [];
    public void Dispose() { foreach (var item in owned) item.Dispose(); Directory.Delete(directory, true); }
    private string PathFor(string name) => Path.Combine(directory, name);

    [Fact]
    public async Task RosterImportAndNavigationNeverDrawAndLastRosterEnablesExplicitPreview()
    {
        var (workflow, shell) = Create(2);
        var input = WriteRoster("input.xlsx");
        Register(shell, () => Task.FromResult<string?>(input));
        Assert.True(shell.Navigate(WorkspaceRoute.Rosters));
        var rosters = Assert.IsType<RostersPageViewModel>(shell.CurrentPage);
        await rosters.Projects[0].ImportCommand.ExecuteAsync();
        Assert.False(shell.CanNavigate(WorkspaceRoute.PublicDraw));
        Assert.Equal("input.xlsx", rosters.Projects[0].SourceFileName);
        Assert.Equal(8, rosters.Projects[0].Rows.Count);
        await rosters.Projects[1].ImportCommand.ExecuteAsync();
        Assert.True(shell.Navigate(WorkspaceRoute.PublicDraw));
        var page = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage);
        Assert.True(page.SelectedProject!.PreviewDrawCommand.CanExecute(null));
        Assert.False(page.SelectedProject.ConfirmDrawCommand.CanExecute(null));
        Assert.All(workflow.CurrentSession!.Workspace.Projects, project => { Assert.Null(project.Draw); Assert.Null(project.MatchGraph); });
        Assert.Null(workflow.CurrentSession.Workspace.Schedule);
        Assert.DoesNotContain(workflow.CurrentSession.Workspace.AuditEvents, e => e.Action == "DrawPreviewed");
    }

    [Fact]
    public async Task PartialAndFinalConfirmationRemainDrawOnlyUntilExplicitUpgrade()
    {
        var (workflow, shell) = Ready(2);
        Register(shell);
        shell.Navigate(WorkspaceRoute.PublicDraw);
        var page = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage);
        var first = page.Projects[0];
        first.RandomSeed = "public-meeting-1";
        await first.PreviewDrawCommand.ExecuteAsync();
        Assert.Contains("public-meeting-1", first.DrawAudit);
        Assert.NotEmpty(first.Groups);
        await first.ConfirmDrawCommand.ExecuteAsync();
        Assert.Equal(TournamentStage.RostersReady, workflow.CurrentSession!.Workspace.Stage);
        Assert.False(first.PreviewDrawCommand.CanExecute(null));
        Assert.True(first.ExportConfirmedCommand.CanExecute(null));
        Assert.False(page.ExportAllConfirmedCommand.CanExecute(null));
        page.SelectedProject = page.Projects[1];
        await page.SelectedProject.PreviewDrawCommand.ExecuteAsync();
        await page.SelectedProject.ConfirmDrawCommand.ExecuteAsync();
        Assert.Same(page, shell.CurrentPage);
        Assert.Equal(page.Projects[1], page.SelectedProject);
        Assert.Equal(TournamentStage.DrawsConfirmed, workflow.CurrentSession.Workspace.Stage);
        var identity = JsonSerializer.Serialize(workflow.CurrentSession.Workspace.Projects.Select(p => new { p.Draw, p.MatchGraph }));
        Assert.True(page.ExportAllConfirmedCommand.CanExecute(null));
        await shell.ReloadCommand.ExecuteAsync();
        Assert.Null(workflow.CurrentSession!.Workspace.Schedule);
        Assert.Equal(identity, JsonSerializer.Serialize(workflow.CurrentSession.Workspace.Projects.Select(p => new { p.Draw, p.MatchGraph })));
        await page.UpgradeCommand.ExecuteAsync();
        Assert.Equal(TournamentPurpose.FullTournament, workflow.CurrentSession.Workspace.Purpose);
        Assert.Null(workflow.CurrentSession.Workspace.Schedule);
        Assert.Equal(identity, JsonSerializer.Serialize(workflow.CurrentSession.Workspace.Projects.Select(p => new { p.Draw, p.MatchGraph })));
    }

    [Fact]
    public async Task EditedSettingsCannotConfirmAnOlderPreviewAndSupportedSettingsReachWorkflow()
    {
        var (workflow, shell) = Ready(); Register(shell); shell.Navigate(WorkspaceRoute.PublicDraw);
        var project = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage).SelectedProject!;
        project.GroupCountText = "4"; project.KnockoutGoalIndex = 1; project.PlacementPlayoffIndex = 1; project.RandomSeed = "meeting";
        await project.PreviewDrawCommand.ExecuteAsync();
        var result = workflow.CurrentSession!.Workspace.Projects[0].Draw!.Result;
        Assert.Equal(4, result.Settings.GroupCount);
        Assert.Equal(KnockoutGoal.Champion, result.Settings.KnockoutGoal);
        Assert.Equal(PlacementPlayoff.ThirdPlace, result.Settings.PlacementPlayoff);
        Assert.True(project.ConfirmDrawCommand.CanExecute(null));
        var revision = workflow.CurrentSession.Workspace.Revision;
        project.RandomSeed = "not-yet-drawn";
        Assert.False(project.ConfirmDrawCommand.CanExecute(null));
        await project.ConfirmDrawCommand.ExecuteAsync();
        Assert.Equal(revision, workflow.CurrentSession.Workspace.Revision);
        Assert.Null(workflow.CurrentSession.Workspace.Projects[0].Draw!.ConfirmedAt);
        project.ResetSettingsCommand.Execute(null);
        Assert.Equal("meeting", project.RandomSeed);
        Assert.True(project.ConfirmDrawCommand.CanExecute(null));
    }

    [Fact]
    public async Task SeedEditorRetainsOriginalIndicesAndBlocksReloadedRosterUntilExplicitReset()
    {
        var (workflow, shell) = Ready(); Register(shell); shell.Navigate(WorkspaceRoute.Rosters);
        var project = Assert.IsType<RostersPageViewModel>(shell.CurrentPage).Projects[0];
        project.BeginSeedEditCommand.Execute(null);
        project.Rows.Move(3, 0);
        project.Rows[0].IsSeed = true; project.Rows[0].SeedRankText = "1";
        await project.SaveSeedsCommand.ExecuteAsync();
        Assert.True(workflow.CurrentSession!.Workspace.Projects[0].Roster!.Participants[3].IsSeed);
        Assert.False(workflow.CurrentSession.Workspace.Projects[0].Roster!.Participants[0].IsSeed);
        project.BeginSeedEditCommand.Execute(null);
        project.Rows[0].IsSeed = true; project.Rows[0].SeedRankText = "2";
        var other = new TournamentWorkspaceWorkflow(); other.OpenWorkspace(workflow.CurrentSession.WorkspacePath);
        other.ImportRoster(project.ProjectId, WriteRoster("replacement.xlsx", "新名单"), other.CurrentSession!.Workspace.Revision);
        await shell.ReloadCommand.ExecuteAsync();
        Assert.True(project.HasEditorConflict);
        Assert.False(project.SaveSeedsCommand.CanExecute(null));
        Assert.Equal("选手1", project.Rows[0].Name);
        var revision = workflow.CurrentSession.Workspace.Revision;
        await project.SaveSeedsCommand.ExecuteAsync();
        Assert.Equal(revision, workflow.CurrentSession.Workspace.Revision);
        Assert.All(workflow.CurrentSession.Workspace.Projects[0].Roster!.Participants, p => Assert.False(p.IsSeed));
        project.ResetSeedsCommand.Execute(null);
        Assert.Equal("新名单1", project.Rows[0].Name);
        Assert.False(project.HasEditorConflict);
    }

    [Fact]
    public async Task DrawEditorPreservesUnrelatedChangesButBlocksExternalRosterOrPreviewChanges()
    {
        var (workflow, shell) = Ready(); Register(shell); shell.Navigate(WorkspaceRoute.PublicDraw);
        var page = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage);
        var project = page.SelectedProject!;
        project.RandomSeed = "unfinished-input";
        await page.UpgradeCommand.ExecuteAsync();
        Assert.Equal("unfinished-input", project.RandomSeed);
        Assert.True(project.PreviewDrawCommand.CanExecute(null));
        var other = new TournamentWorkspaceWorkflow(); other.OpenWorkspace(workflow.CurrentSession!.WorkspacePath);
        other.PreviewDraw(project.ProjectId, new(CompetitionMode.SinglesKnockout, EventKind.Singles, 1, "external"), other.CurrentSession!.Workspace.Revision);
        await shell.ReloadCommand.ExecuteAsync();
        Assert.True(project.HasEditorConflict);
        Assert.Equal("unfinished-input", project.RandomSeed);
        Assert.False(project.PreviewDrawCommand.CanExecute(null));
        Assert.False(project.ConfirmDrawCommand.CanExecute(null));
        project.ResetSettingsCommand.Execute(null);
        Assert.Equal("external", project.RandomSeed);
        Assert.True(project.ConfirmDrawCommand.CanExecute(null));
    }

    [Fact]
    public async Task ReopenRequiresReasonAndAcknowledgementAndNeverRegenerates()
    {
        var (workflow, shell) = Ready(); Register(shell); shell.Navigate(WorkspaceRoute.PublicDraw);
        var project = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage).SelectedProject!;
        await project.PreviewDrawCommand.ExecuteAsync(); await project.ConfirmDrawCommand.ExecuteAsync();
        Assert.False(project.ReopenCommand.CanExecute(null));
        project.ReopenReason = "种子更正";
        Assert.False(project.ReopenCommand.CanExecute(null));
        project.AcknowledgeInvalidation = true;
        Assert.True(project.ReopenCommand.CanExecute(null));
        await project.ReopenCommand.ExecuteAsync();
        var saved = workflow.CurrentSession!.Workspace;
        Assert.Null(saved.Projects[0].Draw); Assert.Null(saved.Projects[0].MatchGraph); Assert.Null(saved.Schedule);
        Assert.Equal(TournamentStage.RostersReady, saved.Stage);
        Assert.Contains(saved.AuditEvents, e => e.Action == "DrawReopened" && e.Detail.Contains("种子更正"));
        Assert.False(project.AcknowledgeInvalidation);
    }

    [Fact]
    public async Task PreviewExportDoesNotConfirmAndOverwriteNeedsFreshExplicitConsent()
    {
        var (workflow, shell) = Ready(); Register(shell, output: () => Task.FromResult<string?>(directory)); shell.Navigate(WorkspaceRoute.PublicDraw);
        var page = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage);
        page.ExportFormatIndex = 0;
        var project = page.SelectedProject!;
        await project.PreviewDrawCommand.ExecuteAsync();
        await project.ExportPreviewCommand.ExecuteAsync();
        Assert.Contains(".xlsx", page.ExportDetails);
        Assert.Single(Directory.GetFiles(directory, "*未确认抽签预览.xlsx"));
        Assert.Null(workflow.CurrentSession!.Workspace.Projects[0].Draw!.ConfirmedAt);
        var revision = workflow.CurrentSession.Workspace.Revision;
        await project.ExportPreviewCommand.ExecuteAsync();
        Assert.Equal(revision, workflow.CurrentSession.Workspace.Revision);
        Assert.NotNull(shell.LastError);
        page.OverwriteExisting = true;
        await project.ExportPreviewCommand.ExecuteAsync();
        Assert.Null(shell.LastError);
        Assert.Equal(revision + 1, workflow.CurrentSession.Workspace.Revision);
        Assert.False(page.OverwriteExisting);
    }

    [Fact]
    public async Task PickerIntentCannotMoveToAnotherSession()
    {
        var (workflow, shell) = Ready();
        var picker = new TaskCompletionSource<string?>();
        Register(shell, () => picker.Task); shell.Navigate(WorkspaceRoute.Rosters);
        var project = Assert.IsType<RostersPageViewModel>(shell.CurrentPage).Projects[0];
        var pending = project.ImportCommand.ExecuteAsync();
        await shell.ReloadCommand.ExecuteAsync();
        var revision = workflow.CurrentSession!.Workspace.Revision;
        picker.SetResult(WriteRoster("new.xlsx", "不可导入"));
        await pending;
        Assert.Equal("workspace.session-changed", shell.LastError!.Code);
        Assert.Equal(revision, workflow.CurrentSession.Workspace.Revision);
        Assert.Equal("选手1", workflow.CurrentSession.Workspace.Projects[0].Roster!.Participants[0].DisplayName);
    }

    [Fact]
    public async Task UnrelatedRefreshRetainsDisplayedRandomSeedBeforeTheFirstDraw()
    {
        var (_, shell) = Ready(); Register(shell); shell.Navigate(WorkspaceRoute.PublicDraw);
        var page = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage);
        var seed = page.SelectedProject!.RandomSeed;
        await page.UpgradeCommand.ExecuteAsync();
        Assert.Equal(seed, page.SelectedProject.RandomSeed);
        Assert.False(page.SelectedProject.HasPreview);
    }

    [Fact]
    public async Task ReloadOfAChangedConfirmedDrawRevokesPriorReopenAcknowledgement()
    {
        var (workflow, shell) = Ready(); Register(shell); shell.Navigate(WorkspaceRoute.PublicDraw);
        var project = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage).SelectedProject!;
        await project.PreviewDrawCommand.ExecuteAsync(); await project.ConfirmDrawCommand.ExecuteAsync();
        project.ReopenReason = "待核实"; project.AcknowledgeInvalidation = true;
        var other = new TournamentWorkspaceWorkflow(); other.OpenWorkspace(workflow.CurrentSession!.WorkspacePath);
        other.ReopenDraw(project.ProjectId, "另一个窗口更正", other.CurrentSession!.Workspace.Revision);
        other.PreviewDraw(project.ProjectId, new(CompetitionMode.SinglesKnockout, EventKind.Singles, 1, "changed"), other.CurrentSession.Workspace.Revision);
        other.ConfirmDraw(project.ProjectId, other.CurrentSession.Workspace.Revision);
        await shell.ReloadCommand.ExecuteAsync();
        Assert.Equal("待核实", project.ReopenReason);
        Assert.False(project.AcknowledgeInvalidation);
        Assert.False(project.ReopenCommand.CanExecute(null));
    }

    [Fact]
    public async Task TemplateExportFromDraftUsesSharedBusyGateAndDoesNotImportOrDraw()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var files = new HookedOutputs(() => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); });
        var (workflow, shell) = Create(suppliedWorkflow: new(drawPackages: new(files)));
        Register(shell); shell.Navigate(WorkspaceRoute.Rosters);
        var project = Assert.IsType<RostersPageViewModel>(shell.CurrentPage).Projects[0];
        var task = project.ExportTemplateCommand.ExecuteAsync();
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(10))));
            Assert.True(shell.IsBusy);
            Assert.False(project.ImportCommand.CanExecute(null));
            Assert.False(shell.ReloadCommand.CanExecute(null));
        }
        finally { release.Set(); }
        await task;
        Assert.False(shell.IsBusy);
        Assert.Null(shell.LastError);
        Assert.Contains(PathFor("template.xlsx"), project.ExportDetails);
        Assert.True(File.Exists(PathFor("template.xlsx")));
        var saved = workflow.CurrentSession!.Workspace;
        Assert.Equal(TournamentStage.Draft, saved.Stage);
        Assert.Null(saved.Projects[0].Roster); Assert.Null(saved.Projects[0].Draw); Assert.Null(saved.Schedule);
        Assert.Contains(saved.AuditEvents, e => e.Action == "RosterTemplateExported");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialOutputAndUncommittedAuditFailuresKeepUsablePathsAndHonestStatus(bool auditFailure)
    {
        var saves = new FailingSave(); var publishCount = 0;
        var outputs = new HookedOutputs(() => { if (!auditFailure && ++publishCount == 2) throw new IOException("second output failed"); });
        var (workflow, shell) = Ready(suppliedWorkflow: new(new TournamentWorkspaceStore(saves), new(outputs)));
        Register(shell, output: () => Task.FromResult<string?>(directory)); shell.Navigate(WorkspaceRoute.PublicDraw);
        var page = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage);
        await page.SelectedProject!.PreviewDrawCommand.ExecuteAsync();
        page.ExportFormatIndex = auditFailure ? 0 : 4;
        var revision = workflow.CurrentSession!.Workspace.Revision;
        saves.Fail = auditFailure;
        await page.SelectedProject.ExportPreviewCommand.ExecuteAsync();
        Assert.NotNull(shell.LastError);
        Assert.False(shell.LastError.Committed);
        Assert.Contains("未完整完成", page.ExportDetails);
        Assert.Contains("审计未写入", page.ExportDetails);
        var published = Assert.Single(Directory.GetFiles(directory, "*未确认抽签预览.xlsx"));
        Assert.Contains(published, page.ExportDetails);
        Assert.Equal(revision, workflow.CurrentSession.Workspace.Revision);
        Assert.DoesNotContain(workflow.CurrentSession.Workspace.AuditEvents, e => e.Action == "DrawPackageExported");
        if (auditFailure) { Assert.NotNull(shell.LastError.BackupPath); Assert.Contains(shell.LastError.BackupPath, shell.Status); }
    }

    [Fact]
    public async Task CommittedAuditRereadFailureShowsSavedAuditAndBlocksFurtherDrawActionsUntilReload()
    {
        var store = new FailingCommittedReadStore();
        var (workflow, shell) = Ready(suppliedWorkflow: new(store));
        Register(shell, output: () => Task.FromResult<string?>(directory)); shell.Navigate(WorkspaceRoute.PublicDraw);
        var page = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage);
        await page.SelectedProject!.PreviewDrawCommand.ExecuteAsync();
        store.FailRead = true;
        await page.SelectedProject.ExportPreviewCommand.ExecuteAsync();
        Assert.True(shell.LastError!.Committed);
        Assert.Contains("审计已写入", page.ExportDetails);
        Assert.Contains(".xlsx", page.ExportDetails);
        Assert.True(shell.CurrentSession!.RequiresReload);
        Assert.False(page.SelectedProject.PreviewDrawCommand.CanExecute(null));
        Assert.False(page.SelectedProject.ExportPreviewCommand.CanExecute(null));
        store.FailRead = false;
        await shell.ReloadCommand.ExecuteAsync();
        Assert.False(shell.CurrentSession.RequiresReload);
        Assert.True(page.SelectedProject.ConfirmDrawCommand.CanExecute(null));
        Assert.Single(workflow.CurrentSession!.Workspace.AuditEvents, e => e.Action == "DrawPackageExported");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    public async Task InvalidSeedTextKeepsStoredRosterAndUnconfirmedPreview(string rank)
    {
        var (workflow, shell) = Ready(); Register(shell); shell.Navigate(WorkspaceRoute.PublicDraw);
        await Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage).SelectedProject!.PreviewDrawCommand.ExecuteAsync();
        shell.Navigate(WorkspaceRoute.Rosters);
        var project = Assert.IsType<RostersPageViewModel>(shell.CurrentPage).Projects[0];
        project.BeginSeedEditCommand.Execute(null); project.Rows[0].IsSeed = true; project.Rows[0].SeedRankText = rank;
        var session = workflow.CurrentSession;
        await project.SaveSeedsCommand.ExecuteAsync();
        Assert.Equal("roster.seed-rank", shell.LastError!.Code);
        Assert.Same(session, workflow.CurrentSession);
        Assert.NotNull(workflow.CurrentSession!.Workspace.Projects[0].Draw);
    }

    [Fact]
    public async Task ReopenedRoundRobinPreviewCanBeConfirmedWithoutRegeneratingUnusedKnockoutOptions()
    {
        var (workflow, shell) = Ready();
        var id = workflow.CurrentSession!.Workspace.Projects[0].Id;
        workflow.UpdateConfiguration(new("循环赛", [new(EventDiscipline.MenSingles, CompetitionMode.SinglesRoundRobin, ProjectId: id)]), workflow.CurrentSession.Workspace.Revision);
        workflow.PreviewDraw(id, new(CompetitionMode.SinglesRoundRobin, EventKind.Singles, 2, "round-robin"), workflow.CurrentSession.Workspace.Revision);
        var audit = workflow.CurrentSession.Workspace.Projects[0].Draw!.Result.Audit;
        Register(shell); shell.Navigate(WorkspaceRoute.PublicDraw);
        var project = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage).SelectedProject!;
        Assert.False(project.ShowKnockoutGoal); Assert.False(project.ShowPlacementPlayoff);
        Assert.True(project.ConfirmDrawCommand.CanExecute(null));
        await project.ConfirmDrawCommand.ExecuteAsync();
        Assert.Equal(audit, workflow.CurrentSession.Workspace.Projects[0].Draw!.Result.Audit);
        Assert.NotNull(workflow.CurrentSession.Workspace.Projects[0].Draw!.ConfirmedAt);
    }

    private (TournamentWorkspaceWorkflow Workflow, AppShellViewModel Shell) Create(int count = 1, TournamentWorkspaceWorkflow? suppliedWorkflow = null)
    {
        var workflow = suppliedWorkflow ?? new TournamentWorkspaceWorkflow();
        workflow.CreateWorkspace(new("公开抽签测试", TournamentKind.Individual, TournamentPurpose.PublicDrawOnly,
            new[] { EventDiscipline.MenSingles, EventDiscipline.WomenSingles }.Take(count).Select(d => new WorkspaceProjectRequest(d, CompetitionMode.SinglesKnockout)).ToArray(), PathFor(Guid.NewGuid() + ".szbd")));
        var shell = new AppShellViewModel(workflow, () => Task.FromResult<string?>(null), _ => Task.FromResult<string?>(null),
            new RecentWorkspaceStore(PathFor("recent.json")), action => action());
        owned.Add(shell); return (workflow, shell);
    }
    private (TournamentWorkspaceWorkflow Workflow, AppShellViewModel Shell) Ready(int count = 1, TournamentWorkspaceWorkflow? suppliedWorkflow = null)
    {
        var pair = Create(count, suppliedWorkflow); var input = WriteRoster("input.xlsx");
        foreach (var project in pair.Workflow.CurrentSession!.Workspace.Projects)
            pair.Workflow.ImportRoster(project.Id, input, pair.Workflow.CurrentSession!.Workspace.Revision);
        return pair;
    }
    private void Register(AppShellViewModel shell, Func<Task<string?>>? import = null, Func<Task<string?>>? output = null)
    {
        shell.RegisterPageFactory(WorkspaceRoute.Rosters, session => new RostersPageViewModel(shell, session,
            import ?? (() => Task.FromResult<string?>(null)), _ => Task.FromResult<string?>(PathFor("template.xlsx"))));
        shell.RegisterPageFactory(WorkspaceRoute.PublicDraw, session => new PublicDrawPageViewModel(shell, session,
            output ?? (() => Task.FromResult<string?>(null))));
    }
    private string WriteRoster(string filename, string prefix = "选手")
    {
        var path = PathFor(filename); using var book = new XLWorkbook(); var sheet = book.AddWorksheet("名单");
        sheet.Cell(1, 1).Value = "姓名"; sheet.Cell(1, 2).Value = "学号";
        for (var i = 1; i <= 8; i++) { sheet.Cell(i + 1, 1).Value = prefix + i; sheet.Cell(i + 1, 2).Value = prefix + "-id-" + i; }
        book.SaveAs(path); return path;
    }

    private sealed class HookedOutputs(Action beforePublish) : DrawPackageFileOperations
    {
        public override void Publish(string stagedPath, string destination, bool overwrite)
        { beforePublish(); base.Publish(stagedPath, destination, overwrite); }
    }
    private sealed class FailingSave : WorkspaceFileOperations
    {
        public bool Fail { get; set; }
        public override void Publish(string candidate, string destination, bool overwrite)
        { if (Fail) throw new IOException("audit publication failed"); base.Publish(candidate, destination, overwrite); }
    }
    private sealed class FailingCommittedReadStore : ITournamentWorkspaceStore
    {
        private readonly TournamentWorkspaceStore actual = new();
        public bool FailRead { get; set; }
        public TournamentWorkspace Create(string path, TournamentWorkspace workspace) => actual.Create(path, workspace);
        public TournamentWorkspace Read(string path) => FailRead ? throw new IOException("reread failed") : actual.Read(path);
        public WorkspaceMutationResult Mutate(string path, long revision, Func<TournamentWorkspace, TournamentWorkspace> mutation)
        {
            var result = actual.Mutate(path, revision, mutation);
            if (FailRead) throw new WorkspaceStoreException("CommittedReadFailed", "文件已保存，但重新读取失败。", backupPath: result.BackupPath, committed: true);
            return result;
        }
        public string CreateBackup(string path) => actual.CreateBackup(path);
        public TournamentWorkspace RestoreBackup(string path, string backupPath) => actual.RestoreBackup(path, backupPath);
        public TournamentWorkspace RecoverFromBackup(string path, string backupPath) => actual.RecoverFromBackup(path, backupPath);
    }
}
