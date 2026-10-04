using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Persistence;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class RosterNavigationGuardTests
{
    [Theory]
    [InlineData("next")]
    [InlineData("sidebar-draw")]
    [InlineData("sidebar-overview")]
    [InlineData("sidebar-rosters")]
    [InlineData("home")]
    [InlineData("new")]
    [InlineData("navigate-new")]
    [InlineData("open")]
    [InlineData("open-direct")]
    [InlineData("recent")]
    [InlineData("create-direct")]
    [InlineData("recover")]
    public async Task LeavingDuringSeedEditingRetainsDraftAndDoesNotWriteOrDraw(string action)
    {
        using var fixture = new ScheduleUiFixture();
        var page = OpenRosters(fixture);
        var project = page.SelectedProject!;
        Edit(project);
        var session = fixture.Workflow.CurrentSession!;
        var bytes = File.ReadAllBytes(session.WorkspacePath);

        switch (action)
        {
            case "next": page.NextCommand.Execute(null); break;
            case "sidebar-draw": Sidebar(fixture.Shell, WorkspaceRoute.PublicDraw); break;
            case "sidebar-overview": Sidebar(fixture.Shell, WorkspaceRoute.Overview); break;
            case "sidebar-rosters": Sidebar(fixture.Shell, WorkspaceRoute.Rosters); break;
            case "home": fixture.Shell.HomeCommand.Execute(null); break;
            case "new": fixture.Shell.NewCommand.Execute(null); break;
            case "navigate-new": fixture.Shell.Navigate(WorkspaceRoute.NewWorkspace); break;
            case "open": await fixture.Shell.OpenCommand.ExecuteAsync(); break;
            case "open-direct": await fixture.Shell.OpenWorkspaceAsync(session.WorkspacePath); break;
            case "recent": await new RecentWorkspaceViewModel(session.WorkspacePath, fixture.Shell).OpenCommand.ExecuteAsync(); break;
            case "create-direct":
                var newPath = Path.Combine(fixture.DirectoryPath, "new.szbd");
                await fixture.Shell.CreateWorkspaceAsync(new("新比赛", session.Workspace.Kind, session.Workspace.Purpose,
                    [new(session.Workspace.Projects[0].Discipline, session.Workspace.Projects[0].CompetitionMode)], newPath));
                Assert.False(File.Exists(newPath));
                break;
            case "recover":
                using (var damaged = new RecoveryUiFixture())
                {
                    var (target, backup) = damaged.CorruptTarget();
                    var original = File.ReadAllBytes(target);
                    var preview = fixture.Workflow.PreviewRecovery(target, backup);
                    Assert.False(await fixture.Shell.RecoverFromBackupAsync(session, preview, "恢复比赛"));
                    Assert.Equal(original, File.ReadAllBytes(target));
                }
                break;
        }

        Assert.Same(page, fixture.Shell.CurrentPage);
        Assert.Equal(WorkspaceRoute.Rosters, fixture.Shell.Navigator.CurrentRoute);
        Assert.True(project.IsSeedEditing);
        Assert.True(project.Rows[0].IsSeed);
        Assert.Equal("1", project.Rows[0].SeedRankText);
        Assert.Same(session, fixture.Workflow.CurrentSession);
        Assert.Equal(bytes, File.ReadAllBytes(session.WorkspacePath));
        Assert.Contains(project.DisplayLabel, fixture.Shell.StatusSummary);
        Assert.Contains("保存", fixture.Shell.StatusSummary);
        Assert.Contains("取消", fixture.Shell.StatusSummary);
    }

    [Fact]
    public async Task SavingAllProjectEditorsEnablesNavigationWithoutImplicitlyDrawing()
    {
        using var fixture = new ScheduleUiFixture(2);
        var page = OpenRosters(fixture);
        var first = page.Projects[0]; var second = page.Projects[1];
        Edit(first); Edit(second);
        page.SelectedProject = second;

        page.NextCommand.Execute(null);
        Assert.Same(page, fixture.Shell.CurrentPage);
        Assert.Same(first, page.SelectedProject);
        await first.SaveSeedsCommand.ExecuteAsync();
        page.NextCommand.Execute(null);
        Assert.Same(page, fixture.Shell.CurrentPage);
        Assert.Same(second, page.SelectedProject);
        Assert.Equal("1", second.Rows[0].SeedRankText);
        await second.SaveSeedsCommand.ExecuteAsync();
        var saved = fixture.Workflow.CurrentSession!;

        page.NextCommand.Execute(null);

        Assert.IsType<PublicDrawPageViewModel>(fixture.Shell.CurrentPage);
        Assert.Same(saved, fixture.Workflow.CurrentSession);
        Assert.All(saved.Workspace.Projects, p => { Assert.True(p.Roster!.Participants[0].IsSeed); Assert.Null(p.Draw); });
    }

    [Fact]
    public void CancelingSeedEditingClearsLeaveWarningAndAllowsSidebarNavigationWithoutSaving()
    {
        using var fixture = new ScheduleUiFixture();
        var page = OpenRosters(fixture); var project = page.SelectedProject!;
        Edit(project); page.NextCommand.Execute(null);
        Assert.Same(page, fixture.Shell.CurrentPage);
        var session = fixture.Workflow.CurrentSession!;

        project.CancelSeedEditCommand.Execute(null);

        Assert.False(project.IsSeedEditing);
        Assert.False(project.Rows[0].IsSeed);
        Assert.Null(fixture.Shell.LastError);
        Sidebar(fixture.Shell, WorkspaceRoute.PublicDraw);
        Assert.IsType<PublicDrawPageViewModel>(fixture.Shell.CurrentPage);
        Assert.Same(session, fixture.Workflow.CurrentSession);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSeedSaveKeepsDraftAndNavigationGuard(bool writeFailure)
    {
        var files = new FailingSave();
        using var fixture = new ScheduleUiFixture(workflow: new(new TournamentWorkspaceStore(files)));
        var page = OpenRosters(fixture); var project = page.SelectedProject!;
        Edit(project);
        if (writeFailure) files.Fail = true;
        else project.Rows[0].SeedRankText = "不是数字";
        var session = fixture.Workflow.CurrentSession!;
        await project.SaveSeedsCommand.ExecuteAsync();
        Assert.NotNull(fixture.Shell.LastError);
        var saveError = fixture.Shell.LastError;

        page.NextCommand.Execute(null);

        Assert.Same(page, fixture.Shell.CurrentPage);
        Assert.Same(saveError, fixture.Shell.LastError);
        Assert.True(project.IsSeedEditing);
        Assert.Equal(writeFailure ? "1" : "不是数字", project.Rows[0].SeedRankText);
        Assert.Same(session, fixture.Workflow.CurrentSession);
    }

    [Fact]
    public async Task ReloadStillPreservesConflictingDraftAndRequiresEndingEditBeforeLeaving()
    {
        using var fixture = new ScheduleUiFixture();
        var page = OpenRosters(fixture); var project = page.SelectedProject!;
        Edit(project);
        var other = new TournamentWorkspaceWorkflow();
        other.OpenWorkspace(fixture.Workflow.CurrentSession!.WorkspacePath);
        other.UpdateRosterSeeds(project.ProjectId, [new(0, true, 2), new(1, false, null)], other.CurrentSession!.Workspace.Revision);

        await fixture.Shell.ReloadCommand.ExecuteAsync();

        Assert.Same(page, fixture.Shell.CurrentPage);
        Assert.True(project.HasEditorConflict);
        Assert.Equal("1", project.Rows[0].SeedRankText);
        page.NextCommand.Execute(null);
        Assert.Same(page, fixture.Shell.CurrentPage);
        project.ResetSeedsCommand.Execute(null);
        Assert.Equal("2", project.Rows[0].SeedRankText);
        page.NextCommand.Execute(null);
        Assert.Same(page, fixture.Shell.CurrentPage);
        project.CancelSeedEditCommand.Execute(null);
        page.NextCommand.Execute(null);
        Assert.IsType<PublicDrawPageViewModel>(fixture.Shell.CurrentPage);
    }

    private static RostersPageViewModel OpenRosters(ScheduleUiFixture fixture)
    {
        foreach (var project in fixture.Workflow.CurrentSession!.Workspace.Projects)
            fixture.Workflow.ReopenDraw(project.Id, "检查种子", fixture.Workflow.CurrentSession.Workspace.Revision);
        fixture.Shell.RegisterPageFactory(WorkspaceRoute.Rosters, session => new RostersPageViewModel(fixture.Shell, session,
            () => Task.FromResult<string?>(null), _ => Task.FromResult<string?>(null)));
        fixture.Shell.RegisterPageFactory(WorkspaceRoute.PublicDraw, session => new PublicDrawPageViewModel(fixture.Shell, session,
            () => Task.FromResult<string?>(null)));
        Assert.True(fixture.Shell.Navigate(WorkspaceRoute.Rosters));
        return Assert.IsType<RostersPageViewModel>(fixture.Shell.CurrentPage);
    }

    private static void Edit(ProjectRosterViewModel project)
    { project.BeginSeedEditCommand.Execute(null); project.Rows[0].IsSeed = true; project.Rows[0].SeedRankText = "1"; }

    private static void Sidebar(AppShellViewModel shell, WorkspaceRoute route) =>
        shell.NavigationItems.Single(item => item.Route == route).Command.Execute(null);

    private sealed class FailingSave : WorkspaceFileOperations
    {
        public bool Fail { get; set; }
        public override void Publish(string candidate, string destination, bool overwrite)
        { if (Fail) throw new IOException("测试保存失败"); base.Publish(candidate, destination, overwrite); }
    }
}
