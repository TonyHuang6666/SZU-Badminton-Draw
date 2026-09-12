using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Persistence;
using BadmintonDraw.Workflows.Tournaments;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class AppShellViewModelTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("desktop-shell-").FullName;
    private string PathFor(string name) => Path.Combine(directory, name);
    public void Dispose() => Directory.Delete(directory, true);
    private AppShellViewModel Shell(TournamentWorkspaceWorkflow workflow, string? recentPath = null, Action<Action>? post = null) =>
        new(workflow, () => Task.FromResult<string?>(null), _ => Task.FromResult<string?>(null),
            new RecentWorkspaceStore(recentPath ?? PathFor("recent.json")), post ?? (action => action()));
    private CreateWorkspaceRequest Request(string name = "赛事", TournamentPurpose purpose = TournamentPurpose.PublicDrawOnly) =>
        new(name, TournamentKind.Individual, purpose, [new(EventDiscipline.MenSingles, CompetitionMode.SinglesKnockout)], PathFor(name + ".szbd"));

    [Fact]
    public async Task WizardCreatesOnceAndPublishesDraftOverviewWithoutImportDrawOrSchedule()
    {
        var workflow = new TournamentWorkspaceWorkflow();
        using var shell = Shell(workflow);
        shell.StartNewWorkspace();
        var wizard = Assert.IsType<NewWorkspaceWizardViewModel>(shell.CurrentPage);
        wizard.Name = "赛事";
        wizard.SelectKind(TournamentKind.Individual);
        wizard.SelectPurpose(TournamentPurpose.PublicDrawOnly);
        wizard.SetDisciplines([EventDiscipline.MenSingles]);
        wizard.WorkspacePath = PathFor("赛事.szbd");
        wizard.NextCommand.Execute(null);
        wizard.NextCommand.Execute(null);
        var first = wizard.CreateCommand.ExecuteAsync();
        await wizard.CreateCommand.ExecuteAsync();
        await first;
        Assert.IsType<WorkspaceOverviewPageViewModel>(shell.CurrentPage);
        Assert.Equal(WorkspaceRoute.Overview, shell.Navigator.CurrentRoute);
        var workspace = new TournamentWorkspaceWorkflow().OpenWorkspace(wizard.WorkspacePath).Workspace;
        Assert.Equal(0, workspace.Revision);
        Assert.Single(workspace.AuditEvents);
        Assert.Null(workspace.Projects[0].Roster);
        Assert.Null(workspace.Projects[0].Draw);
        Assert.Null(workspace.Projects[0].MatchGraph);
        Assert.Null(workspace.Schedule);
        Assert.Single(new RecentWorkspaceStore(PathFor("recent.json")).Read());
    }

    [Fact]
    public async Task FailedVersionOpenKeepsCurrentWorkspaceAndGivesReleaseAddress()
    {
        var workflow = new TournamentWorkspaceWorkflow();
        using var shell = Shell(workflow);
        await shell.CreateWorkspaceAsync(Request());
        var session = shell.CurrentSession;
        var legacy = PathFor("legacy.szbd");
        using (var connection = new SqliteConnection("Data Source=" + legacy))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=400";
            command.ExecuteNonQuery();
        }
        Assert.False(await shell.OpenWorkspaceAsync(legacy));
        Assert.Same(session, shell.CurrentSession);
        Assert.Equal("UnsupportedWorkspaceVersion", shell.LastError!.Code);
        Assert.Contains("v4.6.0", shell.Status);
        Assert.Contains("https://github.com/TonyHuang6666/SZU-Badminton-Draw/releases/tag/v4.6.0", shell.Status);
    }

    [Fact]
    public async Task RecentHistoryFailureIsWarningAfterSuccessfulCreationAndOpen()
    {
        var unavailable = PathFor("not-a-directory");
        File.WriteAllText(unavailable, "occupied");
        using var shell = Shell(new TournamentWorkspaceWorkflow(), Path.Combine(unavailable, "recent.json"));
        Assert.True(await shell.CreateWorkspaceAsync(Request()));
        Assert.NotNull(shell.CurrentSession);
        Assert.Null(shell.LastError);
        Assert.Contains("最近工作区", shell.Status);
        Assert.True(await shell.OpenWorkspaceAsync(Request().WorkspacePath));
        Assert.NotNull(shell.CurrentSession);
        Assert.Null(shell.LastError);
    }

    [Fact]
    public async Task ConfigurationSavePersistsProjectSelectionAndModeAndPreservesIdentity()
    {
        var workflow = new TournamentWorkspaceWorkflow();
        using var shell = Shell(workflow);
        await shell.CreateWorkspaceAsync(Request());
        var originalId = shell.CurrentSession!.Workspace.Projects[0].Id;
        var page = Assert.IsType<WorkspaceOverviewPageViewModel>(shell.CurrentPage);
        page.BeginEditCommand.Execute(null);
        page.EditableName = "改名赛事";
        page.Projects.Single(p => p.Discipline == EventDiscipline.MenSingles).CompetitionModeIndex = 1;
        page.Projects.Single(p => p.Discipline == EventDiscipline.MixedDoubles).IsSelected = true;
        await page.SaveCommand.ExecuteAsync();
        var saved = new TournamentWorkspaceWorkflow().OpenWorkspace(Request().WorkspacePath).Workspace;
        Assert.Equal("改名赛事", saved.Name);
        Assert.Equal(1, saved.Revision);
        Assert.Equal(2, saved.Projects.Count);
        Assert.Equal(originalId, saved.Projects[0].Id);
        Assert.Equal(CompetitionMode.SinglesRoundRobin, saved.Projects[0].CompetitionMode);
        Assert.Same(page, shell.CurrentPage);
        Assert.False(page.IsEditing);
    }

    [Fact]
    public async Task SessionRefreshPreservesActivePageAndUnsavedFieldsButUpdatesSnapshot()
    {
        var workflow = new TournamentWorkspaceWorkflow();
        using var shell = Shell(workflow);
        await shell.CreateWorkspaceAsync(Request());
        var page = Assert.IsType<WorkspaceOverviewPageViewModel>(shell.CurrentPage);
        page.BeginEditCommand.Execute(null);
        page.EditableName = "未保存输入";
        workflow.UpgradeToFullTournament(0);
        Assert.Same(page, shell.CurrentPage);
        Assert.Equal("未保存输入", page.EditableName);
        Assert.Equal(1, page.Session.Workspace.Revision);
        Assert.Equal(TournamentPurpose.FullTournament, shell.CurrentSession!.Workspace.Purpose);
    }

    [Fact]
    public async Task FactoryReadinessAndRefreshDoNotManufactureLaterPages()
    {
        var workflow = new TournamentWorkspaceWorkflow();
        using var shell = Shell(workflow);
        await shell.CreateWorkspaceAsync(Request());
        Assert.False(shell.CanNavigate(WorkspaceRoute.Rosters));
        Assert.False(shell.Navigate(WorkspaceRoute.Rosters));
        shell.RegisterPageFactory(WorkspaceRoute.Rosters, session => new TestPage(session));
        Assert.True(shell.Navigate(WorkspaceRoute.Rosters));
        var page = Assert.IsType<TestPage>(shell.CurrentPage);
        page.SelectedTab = "warnings";
        workflow.UpgradeToFullTournament(0);
        Assert.Same(page, shell.CurrentPage);
        Assert.Equal("warnings", page.SelectedTab);
        Assert.Equal(1, page.Session.Workspace.Revision);
        await shell.CreateWorkspaceAsync(Request("另一个"));
        Assert.IsType<WorkspaceOverviewPageViewModel>(shell.CurrentPage);
    }

    [Fact]
    public async Task SharedBusyGateRejectsWorkspaceSwitchAndStalePageCommands()
    {
        var workflow = new TournamentWorkspaceWorkflow();
        using var shell = Shell(workflow);
        await shell.CreateWorkspaceAsync(Request());
        var original = shell.CurrentSession!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var pending = shell.RunWorkspaceCommandAsync(original, (flow, revision) =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return flow.UpgradeToFullTournament(revision);
        }, "已升级");
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            Assert.True(shell.IsBusy);
            Assert.False(await shell.OpenWorkspaceAsync(PathFor("missing.szbd")));
        }
        finally { release.Set(); }
        Assert.True(await pending);
        Assert.False(shell.IsBusy);
        Assert.False(await shell.RunWorkspaceCommandAsync(original, (flow, revision) => flow.UpgradeToFullTournament(revision), "不应发生"));
        Assert.Equal("workspace.session-changed", shell.LastError!.Code);
        Assert.Equal(1, shell.CurrentSession!.Workspace.Revision);
    }

    [Fact]
    public async Task SupersededPostedNotificationsCannotReplaceTheCurrentWorkspace()
    {
        var posts = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var workflow = new TournamentWorkspaceWorkflow();
        using var shell = Shell(workflow, post: action => posts.Enqueue(action));
        await shell.CreateWorkspaceAsync(Request());
        await shell.CreateWorkspaceAsync(Request("另一个"));
        var actions = posts.ToArray();
        foreach (var action in actions.Reverse()) action();
        Assert.Equal("另一个", shell.CurrentSession!.Workspace.Name);
    }

    [Fact]
    public async Task OpeningRecentCurrentWorkspaceLeavesStartPage()
    {
        using var shell = Shell(new TournamentWorkspaceWorkflow());
        await shell.CreateWorkspaceAsync(Request());
        shell.Navigate(WorkspaceRoute.Start);
        await Assert.Single(shell.StartPage.RecentWorkspaces).OpenCommand.ExecuteAsync();
        Assert.IsType<WorkspaceOverviewPageViewModel>(shell.CurrentPage);
        Assert.Equal(WorkspaceRoute.Overview, shell.Navigator.CurrentRoute);
    }

    [Fact]
    public async Task WriteFailureRetainsCandidateBackupAndLastGoodSnapshot()
    {
        var files = new FailingPublish();
        var workflow = new TournamentWorkspaceWorkflow(new TournamentWorkspaceStore(files));
        using var shell = Shell(workflow);
        await shell.CreateWorkspaceAsync(Request());
        var session = shell.CurrentSession!;
        files.Fail = true;
        Assert.False(await shell.RunWorkspaceCommandAsync(session, (flow, revision) => flow.UpgradeToFullTournament(revision), "已升级"));
        Assert.Same(session, shell.CurrentSession);
        Assert.NotNull(shell.LastError!.CandidatePath);
        Assert.NotNull(shell.LastError.BackupPath);
        Assert.Contains(shell.LastError.CandidatePath, shell.Status);
        Assert.Contains(shell.LastError.BackupPath, shell.Status);
        Assert.True(File.Exists(shell.LastError.CandidatePath));
        Assert.True(File.Exists(shell.LastError.BackupPath));
    }

    [Fact]
    public async Task UnreadableCommittedSnapshotBlocksMutationUntilReloadButPreservesReadNavigation()
    {
        var store = new FailingReadStore();
        using var shell = Shell(new TournamentWorkspaceWorkflow(store));
        await shell.CreateWorkspaceAsync(Request());
        var session = shell.CurrentSession!;
        store.FailRead = true;
        Assert.False(await shell.RunWorkspaceCommandAsync(session, (flow, revision) => flow.UpgradeToFullTournament(revision), "已升级"));
        Assert.True(shell.CurrentSession!.RequiresReload);
        Assert.False(shell.CanMutate);
        Assert.True(shell.CanNavigate(WorkspaceRoute.Overview));
        Assert.True(shell.ReloadCommand.CanExecute(null));
        Assert.True(shell.LastError!.Committed);
        var page = Assert.IsType<WorkspaceOverviewPageViewModel>(shell.CurrentPage);
        Assert.False(page.BeginEditCommand.CanExecute(null));
        store.FailRead = false;
        await shell.ReloadCommand.ExecuteAsync();
        Assert.False(shell.CurrentSession!.RequiresReload);
        Assert.True(shell.CanMutate);
        Assert.Equal(1, shell.CurrentSession.Workspace.Revision);
    }

    private sealed class FailingPublish : WorkspaceFileOperations
    {
        public bool Fail { get; set; }
        public override void Publish(string candidate, string destination, bool overwrite)
        {
            if (Fail) throw new IOException("publication failed");
            base.Publish(candidate, destination, overwrite);
        }
    }

    private sealed class FailingReadStore : ITournamentWorkspaceStore
    {
        private readonly TournamentWorkspaceStore actual = new();
        public bool FailRead { get; set; }
        public TournamentWorkspace Create(string path, TournamentWorkspace workspace) => actual.Create(path, workspace);
        public TournamentWorkspace Read(string path) => FailRead ? throw new IOException("reread failed") : actual.Read(path);
        public WorkspaceMutationResult Mutate(string path, long revision, Func<TournamentWorkspace, TournamentWorkspace> mutation)
        {
            var result = actual.Mutate(path, revision, mutation);
            if (FailRead) throw new WorkspaceStoreException("CommittedReadFailed", "文件已保存，但重新读取失败。",
                backupPath: result.BackupPath, committed: true);
            return result;
        }
        public string CreateBackup(string path) => actual.CreateBackup(path);
        public TournamentWorkspace RestoreBackup(string path, string backupPath) => actual.RestoreBackup(path, backupPath);
        public TournamentWorkspace RecoverFromBackup(string path, string backupPath) => actual.RecoverFromBackup(path, backupPath);
    }

    private sealed class TestPage(WorkspaceSession session) : WorkspacePageViewModel(session)
    {
        public string SelectedTab { get; set; } = "roster";
    }
}
