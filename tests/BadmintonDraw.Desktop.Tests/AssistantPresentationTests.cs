using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class AssistantPresentationTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("assistant-presentation-").FullName;
    private string PathFor(string name) => Path.Combine(directory, name);
    private AppShellViewModel Shell(TournamentWorkspaceWorkflow workflow) => new(workflow,
        () => Task.FromResult<string?>(null), _ => Task.FromResult<string?>(null),
        new RecentWorkspaceStore(PathFor("recent.json")), action => action());
    private CreateWorkspaceRequest Request() => new("校长杯", TournamentKind.Individual,
        TournamentPurpose.PublicDrawOnly, [new(EventDiscipline.MenSingles, CompetitionMode.SinglesKnockout)], PathFor("校长杯.szbd"));
    public void Dispose() => Directory.Delete(directory, true);

    [Theory]
    [InlineData(WorkspaceRoute.Start)]
    [InlineData(WorkspaceRoute.NewWorkspace)]
    public async Task HomeAndNewHidePreviousTournamentContextWithoutDiscardingItsSession(WorkspaceRoute route)
    {
        using var shell = Shell(new());
        await shell.CreateWorkspaceAsync(Request());
        var session = shell.CurrentSession;
        Assert.True(shell.ShowWorkspaceContext);
        Assert.True(shell.Navigate(route));
        Assert.False(shell.ShowWorkspaceContext);
        Assert.False(shell.ShowDetails);
        Assert.False(shell.HasNextAction);
        Assert.DoesNotContain("校长杯", shell.Title);
        Assert.DoesNotContain("校长杯", shell.StatusSummary);
        Assert.Same(session, shell.CurrentSession);
        Assert.True(shell.Navigate(WorkspaceRoute.Overview));
        Assert.True(shell.ShowWorkspaceContext);
        Assert.Equal("校长杯", shell.Title);
    }

    [Fact]
    public async Task RemovingRecentEntryDoesNotDeleteTheTournament()
    {
        using var shell = Shell(new());
        await shell.CreateWorkspaceAsync(Request());
        Assert.Single(shell.StartPage.RecentWorkspaces).RemoveCommand.Execute(null);
        Assert.Empty(shell.StartPage.RecentWorkspaces);
        Assert.True(shell.StartPage.HasNoRecentWorkspaces);
        Assert.Empty(new RecentWorkspaceStore(PathFor("recent.json")).Read());
        Assert.True(File.Exists(Request().WorkspacePath));
        Assert.Equal("校长杯", new TournamentWorkspaceWorkflow().OpenWorkspace(Request().WorkspacePath).Workspace.Name);
    }

    [Theory]
    [InlineData(WorkspaceRoute.Start)]
    [InlineData(WorkspaceRoute.NewWorkspace)]
    public async Task LandingPageDoesNotOfferReloadForAnErrorFromThePreviousTournament(WorkspaceRoute route)
    {
        using var shell = Shell(new());
        await shell.CreateWorkspaceAsync(Request());
        shell.ReportError(new WorkspaceCommandException(new("RevisionConflict", "旧赛事异常", BackupPath: "old.szbd")));
        Assert.True(shell.NeedsReload);
        Assert.True(shell.Navigate(route));
        Assert.False(shell.NeedsReload);
        Assert.False(shell.ShowDetails);
        Assert.Null(shell.LastError);
        Assert.DoesNotContain("旧赛事", shell.StatusSummary);
        // A new error while creating is still visible and actionable, but cannot reload the previous tournament.
        shell.ReportError(new IOException("新文件无法保存"));
        Assert.True(shell.ShowDetails);
        Assert.Contains("新文件无法保存", shell.Status);
        Assert.False(shell.NeedsReload);
    }

    [Fact]
    public void ErrorSummaryKeepsRawEvidenceInDetails()
    {
        using var shell = Shell(new());
        shell.ReportError(new WorkspaceCommandException(new("desktop.operation-failed", "低层异常 C:/private/file",
            CandidatePath: "candidate.szbd", BackupPath: "backup.szbd", Committed: true)));
        Assert.Contains("文件已经保存", shell.StatusSummary);
        Assert.DoesNotContain("C:/private", shell.StatusSummary);
        Assert.Contains("低层异常", shell.Status);
        Assert.Contains("candidate.szbd", shell.Status);
        Assert.Contains("backup.szbd", shell.Status);
        Assert.True(shell.ShowDetails);
        Assert.Equal("请核对操作结果", shell.SaveStateText);
    }

    [Fact]
    public async Task RevisionConflictProvidesVisibleReloadAndNeverReportsSaved()
    {
        using var shell = Shell(new());
        await shell.CreateWorkspaceAsync(Request());
        shell.ReportError(new WorkspaceCommandException(new("RevisionConflict", "stale")));
        Assert.True(shell.NeedsReload);
        Assert.Contains("重新读取", shell.SaveStateText);
        Assert.Contains("另一个窗口", shell.StatusSummary);
        Assert.True(shell.ReloadCommand.CanExecute(null));
    }

    [Fact]
    public async Task ArchiveIsUnavailableForDraftAndPublicDrawIsNotStartedByNavigation()
    {
        using var shell = Shell(new());
        await shell.CreateWorkspaceAsync(Request());
        Assert.False(shell.CanNavigate(WorkspaceRoute.Archive));
        Assert.False(shell.Navigate(WorkspaceRoute.Archive));
        Assert.Equal(6, shell.NavigationItems.Count);
        Assert.Contains("先检查", shell.NavigationItems.Single(n => n.Route == WorkspaceRoute.PublicDraw).Hint);
        Assert.Contains("只抽签", shell.NavigationItems.Single(n => n.Route == WorkspaceRoute.ScheduleSetup).Hint);
        Assert.Null(shell.CurrentSession!.Workspace.Projects[0].Draw);
    }

    [Fact]
    public void ArchiveShowsPendingMatchesAndOnlyRecordedBackupEvidence()
    {
        using var fixture = new ScheduleUiFixture(2);
        Assert.True(fixture.Shell.Navigate(WorkspaceRoute.Archive));
        var page = Assert.IsType<WorkspaceArchivePageViewModel>(fixture.Shell.CurrentPage);
        Assert.False(page.IsCompleted);
        Assert.Equal(2, page.PendingMatches);
        Assert.Contains("尚未完成", page.Guidance);
        Assert.Contains("还没有", page.BackupSummary);
        Assert.False(page.MaterialsCommand.CanExecute(null)); // no schedule/operations factory yet
        fixture.Workflow.CreateBackup(fixture.Workflow.CurrentSession!.Workspace.Revision);
        Assert.Same(page, fixture.Shell.CurrentPage);
        Assert.Contains("最近一次", page.BackupSummary);
        Assert.Contains("重新核对文件", page.BackupSummary);
        Assert.False(page.IsCompleted);
    }

    [Fact]
    public void ArchiveForDrawOnlyOffersDrawMaterialsWithoutRequiringSchedule()
    {
        using var fixture = new ScheduleUiFixture();
        var drawOnly = fixture.Workflow.CurrentSession! with
        { Workspace = fixture.Workflow.CurrentSession!.Workspace with { Purpose = TournamentPurpose.PublicDrawOnly } };
        var page = new WorkspaceArchivePageViewModel(fixture.Shell, drawOnly);
        Assert.True(page.IsDrawOnly);
        Assert.Contains("正式抽签结果已经确认", page.Guidance);
        Assert.Equal("查看并导出抽签结果", page.MaterialsLabel);
        Assert.Contains("1 个项目已确认抽签", page.ProgressLabel);
        Assert.False(page.IsCompleted);
    }
}
