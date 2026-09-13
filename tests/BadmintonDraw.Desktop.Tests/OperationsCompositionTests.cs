using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using BadmintonDraw.Desktop;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Tests;
using BadmintonDraw.Desktop.Views;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Core;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class OperationsCompositionTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Fact]
    public Task RealWindowRegistersOperationalPageForAnActuallyScheduledWorkspace() => ui.Dispatch(() =>
    {
        using var data = new WorkspaceResultImportFacadeFixture(1, 2);
        var window = new AppShellWindow(data.Workflow, new RecentWorkspaceStore(data.PathFor("recent-window.json")));
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            Assert.True(shell.CanNavigate(WorkspaceRoute.Operations));
            Assert.True(shell.Navigate(WorkspaceRoute.Operations));
            window.UpdateLayout(); Assert.IsType<OperationsPageViewModel>(shell.CurrentPage);
            Assert.Single(window.GetVisualDescendants().OfType<OperationsPage>());
            Assert.Single(window.GetVisualDescendants().OfType<ResultImportPanel>());
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task RealCompiledImportAndMaterialBindingsPreserveOwnSaveEvidenceAcrossRefresh() => ui.Dispatch(async () =>
    {
        using var data = new WorkspaceResultImportFacadeFixture(1, 2); var record = data.Export(); var output = data.PathFor("window-materials");
        var window = new AppShellWindow(data.Workflow, new RecentWorkspaceStore(data.PathFor("recent-window.json")),
            resultFilesPicker: () => Task.FromResult<IReadOnlyList<string>?>(new[] { record }),
            operationalOutputPicker: () => Task.FromResult<string?>(output));
        try
        {
            window.Show(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext); shell.Navigate(WorkspaceRoute.Operations);
            window.UpdateLayout(); var page = Assert.IsType<OperationsPageViewModel>(shell.CurrentPage);
            var view = Assert.Single(window.GetVisualDescendants().OfType<OperationsPage>());
            var import = view.FindControl<ResultImportPanel>("OperationsResultImport")!;
            await Assert.IsType<AsyncCommand>(import.FindControl<Button>("PickResultFiles")!.Command).ExecuteAsync();
            await Assert.IsType<AsyncCommand>(import.FindControl<Button>("PreviewResultImport")!.Command).ExecuteAsync();
            import.FindControl<CheckBox>("ConfirmResultImport")!.IsChecked = true;
            await Assert.IsType<AsyncCommand>(import.FindControl<Button>("ApplyResultImport")!.Command).ExecuteAsync();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Assert.Same(page, shell.CurrentPage);
            Assert.Equal(TournamentStage.Completed, page.Session.Workspace.Stage); Assert.Contains("已保存", page.ResultImport.StateMessage);
            view.FindControl<TabControl>("OperationsTabs")!.SelectedIndex = 1; window.UpdateLayout();
            await Assert.IsType<AsyncCommand>(view.FindControl<Button>("PickOperationalOutput")!.Command).ExecuteAsync();
            Assert.Equal(output, view.FindControl<TextBox>("OperationalOutputDirectory")!.Text);
            var project = view.FindControl<ComboBox>("OperationalProject")!; project.SelectedIndex = 1;
            view.FindControl<NumericUpDown>("OperationalPdfRows")!.Value = 2;
            Assert.Equal(2, page.Materials.PdfRows); Assert.False(page.Materials.ScopeConfirmed);
            view.FindControl<CheckBox>("ConfirmOperationalScope")!.IsChecked = true;
            var export = view.FindControl<Button>("ExportOperationalPackage")!; Assert.True(export.IsEffectivelyEnabled);
            await Assert.IsType<AsyncCommand>(export.Command).ExecuteAsync(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(page.Materials.Outcome is not null, $"{page.Materials.StateMessage}; error={page.Materials.Error?.Code}; project={page.Materials.SelectedProject?.Label}; audit={page.Session.Workspace.AuditEvents.Count(a => a.Action == "OperationalPackageExported")}");
            Assert.Contains("已保存", view.FindControl<ResultImportEvidenceText>("OperationalExportState")!.Text);
            Assert.Contains(output, view.FindControl<ResultImportEvidenceText>("OperationalExportOutcome")!.Text);
            Assert.Contains("已记录赛果：1/1", view.FindControl<ResultImportEvidenceText>("OperationsHeader")!.Text); Assert.False(export.IsEffectivelyEnabled);
            Assert.Equal(data.Session.Workspace.Projects[0].Id, page.Materials.SelectedProject!.ProjectId);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task DefaultWindowKeepsRecoveryAccessibleWithoutAnyWorkspace() => ui.Dispatch(() =>
    {
        var window = new AppShellWindow();
        try
        {
            window.Show(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            Assert.False(shell.CanNavigate(WorkspaceRoute.Operations)); Assert.Null(shell.CurrentSession);
            Assert.True(shell.OpenRecoveryCommand.CanExecute(null)); shell.OpenRecoveryCommand.Execute(null);
            window.UpdateLayout(); Assert.True(shell.Recovery.IsOpen);
            Assert.True(window.FindControl<WorkspaceRecoveryPanel>("RecoveryPanel")!.IsVisible);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task RealRegisteredFactoryDoesNotAdmitDrawOnlyConfirmedOrFullTournamentDraft() => ui.Dispatch(() =>
    {
        using var data = new WorkspaceResultImportFacadeFixture(1, 2);
        foreach (var purpose in new[] { TournamentPurpose.PublicDrawOnly, TournamentPurpose.FullTournament })
        {
            var workflow = new TournamentWorkspaceWorkflow();
            workflow.CreateWorkspace(new("stage", TournamentKind.Individual, purpose,
                [new(EventDiscipline.MenSingles, CompetitionMode.SinglesKnockout)], data.PathFor(purpose + ".szbd")));
            if (purpose == TournamentPurpose.PublicDrawOnly)
            {
                var project = workflow.CurrentSession!.Workspace.Projects[0]; var source = data.Session.Workspace.Projects[0];
                workflow.ImportRoster(project.Id, data.PathFor(source.Id + ".xlsx"), workflow.CurrentSession.Workspace.Revision);
                workflow.PreviewDraw(project.Id, source.Draw!.Result.Settings, workflow.CurrentSession.Workspace.Revision);
                workflow.ConfirmDraw(project.Id, workflow.CurrentSession.Workspace.Revision);
                Assert.Equal(TournamentStage.DrawsConfirmed, workflow.CurrentSession.Workspace.Stage);
            }
            var window = new AppShellWindow(workflow, new RecentWorkspaceStore(data.PathFor("recent-stage.json")));
            try { window.Show(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext); Assert.False(shell.CanNavigate(WorkspaceRoute.Operations)); Assert.True(shell.OpenRecoveryCommand.CanExecute(null)); }
            finally { window.Close(); }
        }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task RealInProgressWorkspaceRoutesToTheComposedPageAndDoesNotCountTheOtherProjectComplete() => ui.Dispatch(() =>
    {
        using var data = new WorkspaceResultImportFacadeFixture(2, 2); var p = data.Session.Workspace.Projects[0];
        var file = data.Export(keys: [new(p.Id, p.MatchGraph!.Matches[0].Id)]);
        var preview = data.Workflow.PreviewResultImport([file]); data.Workflow.ImportResults(preview, new(false, null), data.Revision);
        var window = new AppShellWindow(data.Workflow, new RecentWorkspaceStore(data.PathFor("recent-progress.json")));
        try
        {
            window.Show(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext); Assert.True(shell.CanNavigate(WorkspaceRoute.Operations)); shell.Navigate(WorkspaceRoute.Operations);
            var page = Assert.IsType<OperationsPageViewModel>(shell.CurrentPage); Assert.Equal(TournamentStage.InProgress, page.Session.Workspace.Stage);
            Assert.Equal(1, page.CompletedMatchCount); Assert.Equal(2, page.TotalMatchCount); Assert.Equal(2, page.Materials.ProjectChoices.Count - 1);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task NavigatingTheRealWindowDisposesBothChildrenAndRejectsALateResultPickerException() => ui.Dispatch(async () =>
    {
        using var data = new WorkspaceResultImportFacadeFixture(1, 2);
        var picked = new TaskCompletionSource<IReadOnlyList<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new AppShellWindow(data.Workflow, new RecentWorkspaceStore(data.PathFor("recent-navigation.json")), resultFilesPicker: () => picked.Task);
        try
        {
            window.Show(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext); shell.Navigate(WorkspaceRoute.Operations);
            var page = Assert.IsType<OperationsPageViewModel>(shell.CurrentPage);
            var pending = page.ResultImport.PickFilesCommand.ExecuteAsync(); Assert.False(pending.IsCompleted);
            Assert.True(shell.Navigate(WorkspaceRoute.Overview)); var nextPage = shell.CurrentPage;
            shell.ReportError(new IOException("新的页面反馈")); var status = shell.Status; var oldFeedback = page.ResultImport.StateMessage;
            Assert.False(page.ResultImport.PickFilesCommand.CanExecute(null)); Assert.False(page.Materials.PickOutputCommand.CanExecute(null));
            picked.SetException(new IOException("迟到的文件选择异常")); await pending;
            Assert.Same(nextPage, shell.CurrentPage); Assert.Equal(status, shell.Status); Assert.Equal(oldFeedback, page.ResultImport.StateMessage);
            Assert.Empty(page.ResultImport.SelectedPaths); Assert.False(page.ResultImport.IsWorking);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task RealHistoryViewWrapsAndCopiesTheEntireUnknownAuditDetailIncludingBlankLines() => ui.Dispatch(async () =>
    {
        using var data = new WorkspaceResultImportFacadeFixture(1, 2);
        var detail = new string('长', 90) + "\r\n\r\n/synthetic/原始目录/保持完整路径.xlsx\n\n未知字段: 0; sha=" + new string('a', 64) + "\r\n尾部";
        var audit = new WorkspaceAuditEvent(Guid.NewGuid(), "FutureUnknownAction", DateTimeOffset.UtcNow, Detail: detail);
        data.Store.Mutate(data.Session.WorkspacePath, data.Revision, w => w with { AuditEvents = [.. w.AuditEvents, audit] });
        data.Workflow.OpenWorkspace(data.Session.WorkspacePath);
        var window = new AppShellWindow(data.Workflow, new RecentWorkspaceStore(data.PathFor("recent-history.json")));
        try
        {
            window.Show(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext); shell.Navigate(WorkspaceRoute.Operations);
            window.UpdateLayout(); var view = Assert.Single(window.GetVisualDescendants().OfType<OperationsPage>());
            view.FindControl<TabControl>("OperationsTabs")!.SelectedIndex = 2; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var evidence = Assert.Single(view.FindControl<ItemsControl>("OperationsAudits")!.GetVisualDescendants().OfType<ResultImportEvidenceText>(),
                e => e.Text?.Contains(audit.Id.ToString(), StringComparison.Ordinal) == true);
            Assert.EndsWith(detail, evidence.Text); Assert.Contains("FutureUnknownAction", evidence.Text);
            var lines = evidence.GetVisualDescendants().OfType<SelectableTextBlock>().ToArray(); Assert.NotEmpty(lines);
            Assert.All(lines, line => { Assert.Equal(TextWrapping.Wrap, line.TextWrapping); Assert.True(line.Bounds.Width <= evidence.Bounds.Width); });
            Assert.Equal(evidence.Text!.Replace("\r\n", "\n", StringComparison.Ordinal), string.Join("\n", lines.Select(line => line.Text)));
            await evidence.CopyAllCommand.ExecuteAsync(); var copied = await window.Clipboard!.TryGetTextAsync();
            Assert.EndsWith(detail, copied); Assert.Contains(audit.Id.ToString(), copied);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    public void Dispose() => ui.Dispose();
}
