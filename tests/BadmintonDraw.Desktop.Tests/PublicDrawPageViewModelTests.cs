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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnifiedExportDefaultsToAllConfirmedButHonorsCurrentProjectChoice(bool currentOnly)
    {
        var (workflow, shell) = Ready(2);
        DrawExportOptionsViewModel? shown = null;
        Register(shell, output: () => Task.FromResult<string?>(PathFor("unified")), configure: options =>
        {
            shown = options;
            Assert.Null(Assert.IsType<DrawExportScope>(options.SelectedScope).ProjectId);
            if (currentOnly) options.SelectedScope = options.Scopes.Single(s => s.ProjectId is not null);
            return Task.FromResult(true);
        });
        shell.Navigate(WorkspaceRoute.PublicDraw);
        var page = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage);
        foreach (var project in page.Projects.ToArray()) { await project.PreviewDrawCommand.ExecuteAsync(); await project.ConfirmDrawCommand.ExecuteAsync(); }
        page.SelectedProject = page.Projects[1];
        var draws = JsonSerializer.Serialize(workflow.CurrentSession!.Workspace.Projects.Select(p => p.Draw));
        await page.OpenExportCommand.ExecuteAsync();
        Assert.Null(shell.LastError); Assert.NotNull(shown);
        var paths = Directory.GetFiles(PathFor("unified"), "*.xlsx");
        Assert.Equal(currentOnly ? 1 : 2, paths.Length);
        if (currentOnly) Assert.Contains(page.SelectedProject.ProjectId.ToString("N"), paths[0]);
        Assert.Equal(draws, JsonSerializer.Serialize(workflow.CurrentSession!.Workspace.Projects.Select(p => p.Draw)));
        Assert.Equal(TournamentPurpose.PublicDrawOnly, workflow.CurrentSession.Workspace.Purpose);
        Assert.Null(workflow.CurrentSession.Workspace.Schedule);
        Assert.Same(page, shell.CurrentPage);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task UnifiedExportKeepsPendingStateAndOnlyOffersValidBatchScope(bool mixed, bool chooseAll)
    {
        var (workflow, shell) = Ready(2);
        Register(shell, output: () => Task.FromResult<string?>(PathFor("pending")), configure: options =>
        {
            Assert.NotNull(Assert.IsType<DrawExportScope>(options.SelectedScope).ProjectId);
            var all = options.Scopes.Single(s => s.ProjectId is null);
            Assert.Equal(!mixed, all.CanExport);
            if (chooseAll) options.SelectedScope = all;
            return Task.FromResult(true);
        });
        shell.Navigate(WorkspaceRoute.PublicDraw);
        var page = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage);
        foreach (var project in page.Projects.ToArray()) await project.PreviewDrawCommand.ExecuteAsync();
        if (mixed) await page.Projects[0].ConfirmDrawCommand.ExecuteAsync();
        page.SelectedProject = page.Projects[1];
        await page.OpenExportCommand.ExecuteAsync();
        Assert.Null(shell.LastError);
        Assert.Equal(chooseAll ? 2 : 1, Directory.GetFiles(PathFor("pending"), "*待确认抽签结果.xlsx").Length);
        Assert.Null(workflow.CurrentSession!.Workspace.Projects[1].Draw!.ConfirmedAt);
        Assert.False(page.ContinueToScheduleCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancellingUnifiedExportKeepsFilesArchiveAndLastUsedSettings()
    {
        var (workflow, shell) = Ready(); var picked = false;
        Register(shell, output: () => { picked = true; return Task.FromResult<string?>(PathFor("cancelled")); }, configure: options =>
        {
            Assert.Single(options.Scopes);
            options.ExportFormatIndex = 4; options.PdfRowsText = "3";
            return Task.FromResult(false);
        });
        shell.Navigate(WorkspaceRoute.PublicDraw);
        var page = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage);
        Assert.False(page.OpenExportCommand.CanExecute(null));
        await page.SelectedProject!.PreviewDrawCommand.ExecuteAsync();
        var saved = workflow.CurrentSession!; var bytes = File.ReadAllBytes(saved.WorkspacePath);
        await page.OpenExportCommand.ExecuteAsync();
        Assert.False(picked); Assert.Same(saved, workflow.CurrentSession);
        Assert.Equal(bytes, File.ReadAllBytes(saved.WorkspacePath));
        Assert.Equal(0, page.ExportFormatIndex); Assert.Equal("1", page.PdfRowsText);
        Assert.False(Directory.Exists(PathFor("cancelled")));
    }

    [Theory]
    [InlineData("project", false)]
    [InlineData("reload", false)]
    [InlineData("navigate", false)]
    [InlineData("reload", true)]
    public async Task UnifiedExportOptionsCannotSurviveChangedContext(string change, bool throws)
    {
        var (workflow, shell) = Ready(2); var pickerCount = 0;
        var shown = new TaskCompletionSource<DrawExportOptionsViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Register(shell, output: () => { pickerCount++; return Task.FromResult<string?>(PathFor("stale")); },
            configure: options => { shown.SetResult(options); return decision.Task; });
        shell.Navigate(WorkspaceRoute.PublicDraw);
        var page = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage);
        foreach (var project in page.Projects.ToArray()) { await project.PreviewDrawCommand.ExecuteAsync(); await project.ConfirmDrawCommand.ExecuteAsync(); }
        var pending = page.OpenExportCommand.ExecuteAsync();
        var options = await shown.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.False(page.OpenExportCommand.CanExecute(null));
            Assert.False(page.ExportAllConfirmedCommand.CanExecute(null));
            Assert.False(page.SelectedProject!.ExportConfirmedCommand.CanExecute(null));
            var chosen = options.SelectedScope;
            switch (change)
            {
                case "project": page.SelectedProject = page.Projects[1]; break;
                case "reload": await shell.ReloadCommand.ExecuteAsync(); break;
                case "navigate": Assert.True(shell.Navigate(WorkspaceRoute.Rosters)); break;
            }
            Assert.Same(chosen, options.SelectedScope);
        }
        finally
        {
            if (throws) decision.SetException(new IOException("late options failure")); else decision.SetResult(true);
            await pending;
        }
        Assert.Equal(0, pickerCount); Assert.False(Directory.Exists(PathFor("stale")));
        Assert.DoesNotContain(workflow.CurrentSession!.Workspace.AuditEvents, e => e.Action == "DrawPackageExported");
        Assert.Null(shell.LastError);
    }

    [Fact]
    public async Task DuplicateProjectNamesAreDistinguishableWithoutRenamingProjectsOrTemplateFiles()
    {
        var (workflow, shell) = Ready(2);
        workflow.UpdateConfiguration(new("同名项目", workflow.CurrentSession!.Workspace.Projects
            .Select(p => new WorkspaceProjectRequest(p.Discipline, p.CompetitionMode, "公开组", p.Id)).ToArray()),
            workflow.CurrentSession.Workspace.Revision);
        Register(shell);
        string? suggestedName = null;
        shell.RegisterPageFactory(WorkspaceRoute.Rosters, session => new RostersPageViewModel(shell, session,
            () => Task.FromResult<string?>(null), name => { suggestedName = name; return Task.FromResult<string?>(null); }));
        shell.Navigate(WorkspaceRoute.Rosters);
        var rosters = Assert.IsType<RostersPageViewModel>(shell.CurrentPage);
        Assert.Equal(new[] { "公开组 · 男子单打", "公开组 · 女子单打" }, rosters.Projects.Select(p => p.DisplayLabel));
        Assert.All(rosters.Projects, p => Assert.Equal("公开组", p.Name));
        await rosters.Projects[0].ExportTemplateCommand.ExecuteAsync();
        Assert.Equal("公开组_名单模板", suggestedName);
        shell.Navigate(WorkspaceRoute.PublicDraw);
        var draws = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage);
        Assert.Equal(new[] { "公开组 · 男子单打", "公开组 · 女子单打" }, draws.Projects.Select(p => p.DisplayLabel));
        Assert.All(draws.Projects, p => Assert.Equal("公开组", p.Name));
        Assert.All(workflow.CurrentSession.Workspace.Projects, p => Assert.Equal("公开组", p.DisplayName));

        workflow.UpdateConfiguration(new("同名项目", workflow.CurrentSession.Workspace.Projects
            .Select((p, i) => new WorkspaceProjectRequest(p.Discipline, p.CompetitionMode, i == 0 ? "公开组" : "校友组", p.Id)).ToArray()),
            workflow.CurrentSession.Workspace.Revision);
        Assert.Equal(new[] { "公开组", "校友组" }, draws.Projects.Select(p => p.DisplayLabel));
    }

    [Fact]
    public void RosterNextActionReviewsCompletedDrawsAndReturnsToPreparationAfterReopening()
    {
        var (workflow, shell) = Ready(); Register(shell); shell.Navigate(WorkspaceRoute.Rosters);
        var rosters = Assert.IsType<RostersPageViewModel>(shell.CurrentPage);
        Assert.Equal("名单检查无误，开始公开抽签", rosters.NextLabel);
        var id = workflow.CurrentSession!.Workspace.Projects[0].Id;
        workflow.PreviewDraw(id, new(CompetitionMode.SinglesKnockout, EventKind.Singles, 1, "review"), workflow.CurrentSession.Workspace.Revision);
        workflow.ConfirmDraw(id, workflow.CurrentSession.Workspace.Revision);
        Assert.Equal("查看公开抽签", rosters.NextLabel);
        Assert.True(rosters.NextCommand.CanExecute(null));
        workflow.ReopenDraw(id, "检查名单", workflow.CurrentSession.Workspace.Revision);
        Assert.Equal("名单检查无误，开始公开抽签", rosters.NextLabel);
        Assert.True(rosters.NextCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinueToSchedulingRequiresEveryConfirmationAndNeverGeneratesSchedule(bool alreadyFullTournament)
    {
        var (workflow, shell) = Ready(2);
        if (alreadyFullTournament) workflow.UpgradeToFullTournament(workflow.CurrentSession!.Workspace.Revision);
        Register(shell);
        shell.RegisterPageFactory(WorkspaceRoute.ScheduleSetup, session => new ScheduleSetupPageViewModel(shell, session));
        shell.Navigate(WorkspaceRoute.PublicDraw);
        var page = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage);
        Assert.False(page.ContinueToScheduleCommand.CanExecute(null));
        await page.Projects[0].PreviewDrawCommand.ExecuteAsync();
        await page.Projects[0].ConfirmDrawCommand.ExecuteAsync();
        Assert.False(page.ContinueToScheduleCommand.CanExecute(null));
        Assert.Same(page.Projects[1], Assert.Single(page.PendingProjects));
        var beforeReview = workflow.CurrentSession;
        page.PendingProjects[0].ReviewDrawCommand.Execute(null);
        Assert.Same(page.Projects[1], page.SelectedProject);
        Assert.Same(beforeReview, workflow.CurrentSession);
        await page.Projects[1].PreviewDrawCommand.ExecuteAsync();
        Assert.False(page.ContinueToScheduleCommand.CanExecute(null));
        await page.Projects[1].ConfirmDrawCommand.ExecuteAsync();
        Assert.Same(page, shell.CurrentPage);
        Assert.True(page.ContinueToScheduleCommand.CanExecute(null));
        Assert.Empty(page.PendingProjects);
        var draws = JsonSerializer.Serialize(workflow.CurrentSession!.Workspace.Projects.Select(project => project.Draw));

        await page.ContinueToScheduleCommand.ExecuteAsync();

        Assert.IsType<ScheduleSetupPageViewModel>(shell.CurrentPage);
        Assert.Equal(TournamentPurpose.FullTournament, workflow.CurrentSession!.Workspace.Purpose);
        Assert.Equal(draws, JsonSerializer.Serialize(workflow.CurrentSession.Workspace.Projects.Select(project => project.Draw)));
        Assert.Null(workflow.CurrentSession.Workspace.Schedule);
    }

    [Fact]
    public async Task ReloadingReopenedDrawRestoresPendingGuidanceAndDisposedPageCannotSelectIt()
    {
        var (workflow, shell) = Ready(2); Register(shell); shell.Navigate(WorkspaceRoute.PublicDraw);
        var page = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage);
        foreach (var project in page.Projects.ToArray())
        {
            await project.PreviewDrawCommand.ExecuteAsync();
            await project.ConfirmDrawCommand.ExecuteAsync();
        }
        Assert.Empty(page.PendingProjects);
        Assert.False(page.Projects[1].ReviewDrawCommand.CanExecute(null));
        var other = new TournamentWorkspaceWorkflow(); other.OpenWorkspace(workflow.CurrentSession!.WorkspacePath);
        other.ReopenDraw(page.Projects[1].ProjectId, "重新核对", other.CurrentSession!.Workspace.Revision);

        await shell.ReloadCommand.ExecuteAsync();

        var pending = Assert.Single(page.PendingProjects);
        Assert.Same(page.Projects[1], pending);
        Assert.Contains("未抽签", pending.PendingDrawLabel);
        Assert.False(page.ContinueToScheduleCommand.CanExecute(null));
        var saved = workflow.CurrentSession;
        pending.ReviewDrawCommand.Execute(null);
        Assert.Same(pending, page.SelectedProject);
        Assert.Same(saved, workflow.CurrentSession);
        page.SelectedProject = page.Projects[0]; page.Dispose();
        Assert.False(pending.ReviewDrawCommand.CanExecute(null));
        pending.ReviewDrawCommand.Execute(null);
        Assert.Same(page.Projects[0], page.SelectedProject);
    }

    [Fact]
    public async Task ImportFromInitiallyEmptyPageEnablesSeedEditorAndSavesNewSeed()
    {
        var (workflow, shell) = Create(); Register(shell, () => Task.FromResult<string?>(WriteRoster("new.xlsx")));
        shell.Navigate(WorkspaceRoute.Rosters);
        var project = Assert.IsType<RostersPageViewModel>(shell.CurrentPage).Projects[0];
        Assert.False(project.BeginSeedEditCommand.CanExecute(null));
        await project.ImportCommand.ExecuteAsync();
        Assert.True(project.BeginSeedEditCommand.CanExecute(null)); Assert.False(project.ResetSeedsCommand.CanExecute(null));
        project.BeginSeedEditCommand.Execute(null); project.Rows[0].IsSeed = true; project.Rows[0].SeedRankText = "1";
        await project.SaveSeedsCommand.ExecuteAsync();
        Assert.True(workflow.CurrentSession!.Workspace.Projects[0].Roster!.Participants[0].IsSeed);
        Assert.Equal(1, workflow.CurrentSession.Workspace.Projects[0].Roster!.Participants[0].SeedRank);
    }

    [Fact]
    public async Task UncheckedSeedWithRetainedRankShowsLocalGuidanceAndRequiresExplicitClear()
    {
        var (workflow, shell) = Ready(); Register(shell); shell.Navigate(WorkspaceRoute.Rosters);
        var project = Assert.IsType<RostersPageViewModel>(shell.CurrentPage).Projects[0];
        project.BeginSeedEditCommand.Execute(null); var row = project.Rows[0];
        row.IsSeed = true; row.SeedRankText = "1"; row.IsSeed = false;
        Assert.NotEmpty(row.SeedIssue); Assert.Equal("1", row.SeedRankText);
        var before = workflow.CurrentSession;
        await project.SaveSeedsCommand.ExecuteAsync(); Assert.Same(before, workflow.CurrentSession);
        row.SeedRankText = ""; Assert.Empty(row.SeedIssue);
        await project.SaveSeedsCommand.ExecuteAsync();
        Assert.False(workflow.CurrentSession!.Workspace.Projects[0].Roster!.Participants[0].IsSeed);
        Assert.Null(workflow.CurrentSession.Workspace.Projects[0].Roster!.Participants[0].SeedRank);
    }

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
    public void DiscardDrawSettingsTracksEffectiveChangesAndRestoresInitialSeedWithoutSaving()
    {
        var (workflow, shell) = Ready(); Register(shell); shell.Navigate(WorkspaceRoute.PublicDraw);
        var project = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage).SelectedProject!;
        var initialSeed = project.RandomSeed;
        var saved = workflow.CurrentSession!;
        var archive = File.ReadAllBytes(saved.WorkspacePath);
        Assert.False(project.ResetSettingsCommand.CanExecute(null));
        project.GroupCountText = "invalid";
        Assert.True(project.ResetSettingsCommand.CanExecute(null));
        project.GroupCountText = "1";
        Assert.False(project.ResetSettingsCommand.CanExecute(null));
        project.KnockoutGoalIndex = 1;
        Assert.False(project.ResetSettingsCommand.CanExecute(null)); // Single-group goal is always champion.
        project.PlacementPlayoffIndex = 1;
        Assert.True(project.ResetSettingsCommand.CanExecute(null));
        project.PlacementPlayoffIndex = 0;
        Assert.False(project.ResetSettingsCommand.CanExecute(null));
        project.GenerateSeedCommand.Execute(null);
        Assert.NotEqual(initialSeed, project.RandomSeed);
        Assert.True(project.ResetSettingsCommand.CanExecute(null));
        project.ResetSettingsCommand.Execute(null);
        Assert.Equal(initialSeed, project.RandomSeed);
        Assert.Equal("1", project.GroupCountText);
        Assert.False(project.ResetSettingsCommand.CanExecute(null));
        Assert.Same(saved, workflow.CurrentSession);
        Assert.Equal(archive, File.ReadAllBytes(saved.WorkspacePath));
        Assert.Null(saved.Workspace.Projects[0].Draw);
    }

    [Fact]
    public async Task DiscardDrawSettingsUsesSavedPreviewAndSurvivesProjectSwitchAndUnrelatedRefresh()
    {
        var (workflow, shell) = Ready(2); Register(shell); shell.Navigate(WorkspaceRoute.PublicDraw);
        var page = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage);
        var project = page.SelectedProject!;
        project.GroupCountText = "4"; project.KnockoutGoalIndex = 1; project.PlacementPlayoffIndex = 1; project.RandomSeed = "saved-settings";
        await project.PreviewDrawCommand.ExecuteAsync();
        Assert.False(project.ResetSettingsCommand.CanExecute(null));
        var savedDraw = JsonSerializer.Serialize(workflow.CurrentSession!.Workspace.Projects[0].Draw);
        project.KnockoutGoalIndex = 0;
        Assert.True(project.ResetSettingsCommand.CanExecute(null));
        project.KnockoutGoalIndex = 1;
        Assert.False(project.ResetSettingsCommand.CanExecute(null));
        project.RandomSeed = "draft";
        page.SelectedProject = page.Projects[1];
        Assert.False(page.SelectedProject.ResetSettingsCommand.CanExecute(null));
        await page.UpgradeCommand.ExecuteAsync();
        page.SelectedProject = project;
        Assert.Equal("draft", project.RandomSeed);
        Assert.True(project.ResetSettingsCommand.CanExecute(null));
        var saved = workflow.CurrentSession!; var archive = File.ReadAllBytes(saved.WorkspacePath);
        project.ResetSettingsCommand.Execute(null);
        Assert.Equal("saved-settings", project.RandomSeed);
        Assert.Equal("4", project.GroupCountText);
        Assert.Equal(1, project.KnockoutGoalIndex); Assert.Equal(1, project.PlacementPlayoffIndex);
        Assert.False(project.ResetSettingsCommand.CanExecute(null));
        Assert.True(project.ConfirmDrawCommand.CanExecute(null));
        Assert.Same(saved, workflow.CurrentSession);
        Assert.Equal(archive, File.ReadAllBytes(saved.WorkspacePath));
        Assert.Equal(savedDraw, JsonSerializer.Serialize(saved.Workspace.Projects[0].Draw));
        await project.ConfirmDrawCommand.ExecuteAsync();
        Assert.False(project.ResetSettingsCommand.CanExecute(null));
    }

    [Fact]
    public async Task ConflictingDrawChangeKeepsRecoveryAfterLocalInputsReturnToOriginal()
    {
        var (workflow, shell) = Ready(); Register(shell); shell.Navigate(WorkspaceRoute.PublicDraw);
        var project = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage).SelectedProject!;
        var initialSeed = project.RandomSeed;
        project.RandomSeed = "local-draft";
        var other = new TournamentWorkspaceWorkflow(); other.OpenWorkspace(workflow.CurrentSession!.WorkspacePath);
        other.PreviewDraw(project.ProjectId, new(CompetitionMode.SinglesKnockout, EventKind.Singles, 1, "external-settings"), other.CurrentSession!.Workspace.Revision);
        await shell.ReloadCommand.ExecuteAsync();
        project.RandomSeed = initialSeed;
        Assert.True(project.HasEditorConflict);
        Assert.True(project.ResetSettingsCommand.CanExecute(null));
        var saved = workflow.CurrentSession!; var archive = File.ReadAllBytes(saved.WorkspacePath);
        project.ResetSettingsCommand.Execute(null);
        Assert.Equal("external-settings", project.RandomSeed);
        Assert.False(project.HasEditorConflict);
        Assert.False(project.ResetSettingsCommand.CanExecute(null));
        Assert.True(project.ConfirmDrawCommand.CanExecute(null));
        Assert.Same(saved, workflow.CurrentSession);
        Assert.Equal(archive, File.ReadAllBytes(saved.WorkspacePath));
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
        var prompts = new List<string[]>(); var accept = false;
        var (workflow, shell) = Ready(confirm: paths => { prompts.Add(paths.ToArray()); return Task.FromResult(accept); });
        Register(shell, output: () => Task.FromResult<string?>(directory)); shell.Navigate(WorkspaceRoute.PublicDraw);
        var page = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage);
        page.ExportFormatIndex = 0;
        var project = page.SelectedProject!;
        await project.PreviewDrawCommand.ExecuteAsync();
        await project.ExportPreviewCommand.ExecuteAsync();
        Assert.Contains(".xlsx", page.ExportDetails);
        var target = Assert.Single(Directory.GetFiles(directory, "*待确认抽签结果.xlsx"));
        Assert.Empty(prompts);
        Assert.Null(workflow.CurrentSession!.Workspace.Projects[0].Draw!.ConfirmedAt);
        var revision = workflow.CurrentSession.Workspace.Revision;
        File.WriteAllText(target, "original output");
        var archive = File.ReadAllBytes(workflow.CurrentSession.WorkspacePath);
        await project.ExportPreviewCommand.ExecuteAsync();
        Assert.Equal(revision, workflow.CurrentSession.Workspace.Revision);
        Assert.Equal(new[] { target }, Assert.Single(prompts));
        Assert.Null(shell.LastError);
        Assert.Contains("已取消", page.ExportDetails);
        Assert.Equal("original output", File.ReadAllText(target));
        Assert.Equal(archive, File.ReadAllBytes(workflow.CurrentSession.WorkspacePath));
        accept = true;
        await project.ExportPreviewCommand.ExecuteAsync();
        Assert.Null(shell.LastError);
        Assert.Equal(revision + 1, workflow.CurrentSession.Workspace.Revision);
        Assert.Equal(2, prompts.Count);
        using (var workbook = new XLWorkbook(target)) Assert.NotEmpty(workbook.Worksheets);
        accept = false;
        await project.ExportPreviewCommand.ExecuteAsync();
        Assert.Equal(3, prompts.Count);
        Assert.Equal(revision + 1, workflow.CurrentSession.Workspace.Revision);
        Assert.Null(workflow.CurrentSession.Workspace.Projects[0].Draw!.ConfirmedAt);
    }

    public static IEnumerable<object[]> DrawConfirmationChanges => new[] { "format", "layout", "project", "reload", "navigate" }
        .SelectMany(change => new[] { new object[] { change, false }, new object[] { change, true } });

    [Theory, MemberData(nameof(DrawConfirmationChanges))]
    public async Task DrawExportConsentCannotSurviveChangedInputsOrPage(string change, bool throws)
    {
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (workflow, shell) = Ready(2, confirm: _ => { shown.TrySetResult(); return decision.Task; });
        Register(shell, output: () => Task.FromResult<string?>(directory)); shell.Navigate(WorkspaceRoute.PublicDraw);
        var page = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage);
        var project = page.SelectedProject!;
        await project.PreviewDrawCommand.ExecuteAsync(); await project.ExportPreviewCommand.ExecuteAsync();
        var target = Assert.Single(Directory.GetFiles(directory, "*待确认抽签结果.xlsx"));
        var original = File.ReadAllBytes(target);
        var revision = workflow.CurrentSession!.Workspace.Revision;
        var pending = project.ExportPreviewCommand.ExecuteAsync();
        try
        {
            await shown.Task.WaitAsync(TimeSpan.FromSeconds(10));
            switch (change)
            {
                case "format": page.ExportFormatIndex = 1; break;
                case "layout": page.PdfRowsText = "2"; break;
                case "project": page.SelectedProject = page.Projects[1]; break;
                case "reload": await shell.ReloadCommand.ExecuteAsync(); break;
                case "navigate": Assert.True(shell.Navigate(WorkspaceRoute.Rosters)); break;
            }
        }
        finally
        {
            if (throws) decision.TrySetException(new IOException("late confirmation failure")); else decision.TrySetResult(true);
            await pending;
        }
        Assert.Null(shell.LastError);
        Assert.Equal(revision, workflow.CurrentSession!.Workspace.Revision);
        Assert.Equal(original, File.ReadAllBytes(target));
        Assert.Single(workflow.CurrentSession.Workspace.AuditEvents, e => e.Action == "DrawPackageExported");
    }

    [Fact]
    public async Task ChangedDrawExportInputsWhileChoosingDirectoryDoNotPublish()
    {
        var picker = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (workflow, shell) = Ready(); Register(shell, output: () => picker.Task); shell.Navigate(WorkspaceRoute.PublicDraw);
        var page = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage);
        await page.SelectedProject!.PreviewDrawCommand.ExecuteAsync();
        var before = workflow.CurrentSession;
        var pending = page.SelectedProject.ExportPreviewCommand.ExecuteAsync();
        page.ExportFormatIndex = 1;
        picker.SetResult(PathFor("never-created")); await pending;
        Assert.Same(before, workflow.CurrentSession);
        Assert.False(Directory.Exists(PathFor("never-created")));
    }

    [Fact]
    public async Task LateDrawConflictCheckCannotReplaceNewerSessionFeedbackOrPublish()
    {
        var (workflow, shell) = Ready();
        Register(shell, output: () => Task.FromResult<string?>(PathFor("never-created")));
        shell.Navigate(WorkspaceRoute.PublicDraw);
        var page = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage);
        await page.SelectedProject!.PreviewDrawCommand.ExecuteAsync();
        var deferred = new DeferredUiContext();
        var pending = deferred.Start(() => page.SelectedProject.ExportPreviewCommand.ExecuteAsync());
        await deferred.FirstPost.WaitAsync(TimeSpan.FromSeconds(10));
        workflow.OpenWorkspace(workflow.CurrentSession!.WorkspacePath);
        shell.ReportError(new IOException("newer feedback"));
        var newerError = shell.LastError; var newerStatus = shell.Status;
        var newerSession = shell.CurrentSession;
        await deferred.Complete(pending);
        Assert.Same(newerError, shell.LastError);
        Assert.Equal(newerStatus, shell.Status);
        Assert.Same(newerSession, workflow.CurrentSession);
        Assert.False(Directory.Exists(PathFor("never-created")));
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
    public async Task TemplateExportReplacesTheExistingFileAcceptedByTheSavePicker()
    {
        var (workflow, shell) = Ready(); Register(shell); shell.Navigate(WorkspaceRoute.Rosters);
        var target = WriteRoster("template.xlsx");
        var before = workflow.CurrentSession!;
        var project = Assert.IsType<RostersPageViewModel>(shell.CurrentPage).Projects[0];

        await project.ExportTemplateCommand.ExecuteAsync();

        Assert.Null(shell.LastError);
        using var workbook = new XLWorkbook(target);
        Assert.Equal("是否种子", workbook.Worksheet(1).Cell(1, 7).GetString());
        Assert.Contains(target, project.ExportDetails);
        Assert.Equal(before.Workspace.Revision + 1, workflow.CurrentSession!.Workspace.Revision);
        Assert.Equal(JsonSerializer.Serialize(before.Workspace.Projects), JsonSerializer.Serialize(workflow.CurrentSession.Workspace.Projects));
        Assert.Single(workflow.CurrentSession.Workspace.AuditEvents, e => e.Action == "RosterTemplateExported");
    }

    [Fact]
    public async Task CancelingTemplateSavePickerLeavesTheExistingFileAndWorkspaceUntouched()
    {
        var (workflow, shell) = Ready();
        var target = WriteRoster("template.xlsx");
        var original = File.ReadAllBytes(target);
        var before = workflow.CurrentSession!;
        var archive = File.ReadAllBytes(before.WorkspacePath);
        shell.RegisterPageFactory(WorkspaceRoute.Rosters, session => new RostersPageViewModel(shell, session,
            () => Task.FromResult<string?>(null), _ => Task.FromResult<string?>(null)));
        shell.Navigate(WorkspaceRoute.Rosters);

        await Assert.IsType<RostersPageViewModel>(shell.CurrentPage).Projects[0].ExportTemplateCommand.ExecuteAsync();

        Assert.Null(shell.LastError);
        Assert.Equal(original, File.ReadAllBytes(target));
        Assert.Equal(archive, File.ReadAllBytes(before.WorkspacePath));
        Assert.Same(before, workflow.CurrentSession);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("other-project-source")]
    [InlineData("workspace")]
    [InlineData("backup")]
    public async Task AcceptedTemplateSavePathStillCannotOverwriteProtectedFiles(string kind)
    {
        var (workflow, shell) = Ready(2);
        var otherSource = WriteRoster("other-project.xlsx");
        workflow.ImportRoster(workflow.CurrentSession!.Workspace.Projects[1].Id, otherSource, workflow.CurrentSession.Workspace.Revision);
        var before = workflow.CurrentSession!;
        var backup = PathFor("backup.szbd");
        File.Copy(before.WorkspacePath, backup);
        var target = kind switch
        {
            "source" => PathFor("input.xlsx"),
            "other-project-source" => otherSource,
            "workspace" => before.WorkspacePath,
            _ => backup
        };
        var original = File.ReadAllBytes(target);
        var archive = File.ReadAllBytes(before.WorkspacePath);
        shell.RegisterPageFactory(WorkspaceRoute.Rosters, session => new RostersPageViewModel(shell, session,
            () => Task.FromResult<string?>(null), _ => Task.FromResult<string?>(target)));
        shell.Navigate(WorkspaceRoute.Rosters);

        await Assert.IsType<RostersPageViewModel>(shell.CurrentPage).Projects[0].ExportTemplateCommand.ExecuteAsync();

        Assert.Equal("export.protected-path", shell.LastError!.Code);
        Assert.Equal(original, File.ReadAllBytes(target));
        Assert.Equal(archive, File.ReadAllBytes(before.WorkspacePath));
        Assert.Same(before, workflow.CurrentSession);
    }

    [Fact]
    public async Task AcceptedTemplateReplacementIsRejectedIfWorkspaceChangedWhilePickerWasOpen()
    {
        var (workflow, shell) = Ready();
        var target = WriteRoster("template.xlsx");
        var original = File.ReadAllBytes(target);
        var picker = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.RegisterPageFactory(WorkspaceRoute.Rosters, session => new RostersPageViewModel(shell, session,
            () => Task.FromResult<string?>(null), _ => picker.Task));
        shell.Navigate(WorkspaceRoute.Rosters);
        var export = Assert.IsType<RostersPageViewModel>(shell.CurrentPage).Projects[0].ExportTemplateCommand.ExecuteAsync();
        await shell.ReloadCommand.ExecuteAsync();
        var current = workflow.CurrentSession!;
        var archive = File.ReadAllBytes(current.WorkspacePath);

        picker.SetResult(target);
        await export;

        Assert.Equal("workspace.session-changed", shell.LastError!.Code);
        Assert.Equal(original, File.ReadAllBytes(target));
        Assert.Equal(archive, File.ReadAllBytes(current.WorkspacePath));
        Assert.Same(current, workflow.CurrentSession);
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
        var published = Assert.Single(Directory.GetFiles(directory, "*待确认抽签结果.xlsx"));
        Assert.Contains(published, page.ExportDetails);
        Assert.Equal(revision, workflow.CurrentSession.Workspace.Revision);
        Assert.DoesNotContain(workflow.CurrentSession.Workspace.AuditEvents, e => e.Action == "DrawPackageExported");
        if (auditFailure) { Assert.NotNull(shell.LastError.BackupPath); Assert.Contains(shell.LastError.BackupPath, shell.Status); }
    }

    [Fact]
    public async Task CommittedAuditRereadFailureShowsSavedAuditAndBlocksFurtherDrawActionsUntilReload()
    {
        var store = new FailingCommittedReadStore();
        var (workflow, shell) = Ready(suppliedWorkflow: new(store, new(new HookedOutputs(() => store.FailRead = true))));
        Register(shell, output: () => Task.FromResult<string?>(directory)); shell.Navigate(WorkspaceRoute.PublicDraw);
        var page = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage);
        await page.SelectedProject!.PreviewDrawCommand.ExecuteAsync();
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
        Assert.False(project.ResetSettingsCommand.CanExecute(null));
        project.KnockoutGoalIndex = 0; project.PlacementPlayoffIndex = 2;
        Assert.False(project.ResetSettingsCommand.CanExecute(null));
        Assert.True(project.ConfirmDrawCommand.CanExecute(null));
        await project.ConfirmDrawCommand.ExecuteAsync();
        Assert.Equal(audit, workflow.CurrentSession.Workspace.Projects[0].Draw!.Result.Audit);
        Assert.NotNull(workflow.CurrentSession.Workspace.Projects[0].Draw!.ConfirmedAt);
    }

    private (TournamentWorkspaceWorkflow Workflow, AppShellViewModel Shell) Create(int count = 1, TournamentWorkspaceWorkflow? suppliedWorkflow = null,
        Func<IReadOnlyList<string>, Task<bool>>? confirm = null)
    {
        var workflow = suppliedWorkflow ?? new TournamentWorkspaceWorkflow();
        workflow.CreateWorkspace(new("公开抽签测试", TournamentKind.Individual, TournamentPurpose.PublicDrawOnly,
            new[] { EventDiscipline.MenSingles, EventDiscipline.WomenSingles }.Take(count).Select(d => new WorkspaceProjectRequest(d, CompetitionMode.SinglesKnockout)).ToArray(), PathFor(Guid.NewGuid() + ".szbd")));
        var shell = new AppShellViewModel(workflow, () => Task.FromResult<string?>(null), _ => Task.FromResult<string?>(null),
            new RecentWorkspaceStore(PathFor("recent.json")), action => action(), confirmExportOverwrite: confirm);
        owned.Add(shell); return (workflow, shell);
    }
    private (TournamentWorkspaceWorkflow Workflow, AppShellViewModel Shell) Ready(int count = 1, TournamentWorkspaceWorkflow? suppliedWorkflow = null,
        Func<IReadOnlyList<string>, Task<bool>>? confirm = null)
    {
        var pair = Create(count, suppliedWorkflow, confirm); var input = WriteRoster("input.xlsx");
        foreach (var project in pair.Workflow.CurrentSession!.Workspace.Projects)
            pair.Workflow.ImportRoster(project.Id, input, pair.Workflow.CurrentSession!.Workspace.Revision);
        return pair;
    }
    private void Register(AppShellViewModel shell, Func<Task<string?>>? import = null, Func<Task<string?>>? output = null,
        Func<DrawExportOptionsViewModel, Task<bool>>? configure = null)
    {
        shell.RegisterPageFactory(WorkspaceRoute.Rosters, session => new RostersPageViewModel(shell, session,
            import ?? (() => Task.FromResult<string?>(null)), _ => Task.FromResult<string?>(PathFor("template.xlsx"))));
        shell.RegisterPageFactory(WorkspaceRoute.PublicDraw, session => new PublicDrawPageViewModel(shell, session,
            output ?? (() => Task.FromResult<string?>(null)), configure));
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
        public WorkspaceBackupSnapshot InspectBackup(string path) => actual.InspectBackup(path);
        public WorkspaceRecoveryInspection InspectRecovery(string path, string backup) => actual.InspectRecovery(path, backup);
        public WorkspaceMutationResult RestoreBackup(string path, WorkspaceRestoreRequest request) => actual.RestoreBackup(path, request);
        public WorkspaceMutationResult RecoverFromBackup(string path, WorkspaceRecoveryRequest request) => actual.RecoverFromBackup(path, request);
    }
}
