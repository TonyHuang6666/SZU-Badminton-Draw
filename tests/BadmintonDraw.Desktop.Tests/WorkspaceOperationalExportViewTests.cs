using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Desktop;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;
using BadmintonDraw.Tests;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class WorkspaceOperationalExportViewTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Fact]
    public Task SuccessfulExportShowsActualMaterialFolderAndDescriptiveFilesWithTechnicalEvidenceFolded() => ui.Dispatch(async () =>
    {
        using var data = new WorkspaceResultImportFacadeFixture(1, 2);
        var output = data.PathFor("materials");
        var window = new AppShellWindow(data.Workflow, new RecentWorkspaceStore(data.PathFor("recent-materials.json")),
            operationalOutputPicker: () => Task.FromResult<string?>(output));
        try
        {
            window.Show();
            var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            Assert.True(shell.Navigate(WorkspaceRoute.Operations)); window.UpdateLayout();
            var page = Assert.IsType<OperationsPageViewModel>(shell.CurrentPage);
            var view = Assert.Single(window.GetVisualDescendants().OfType<OperationsPage>());
            page.Materials.ScopeConfirmed = true;
            await page.Materials.ExportCommand.ExecuteAsync();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(page.Materials.Outcome is not null, page.Materials.StateMessage);
            var outcome = Assert.IsType<OperationalPackageOutcome>(page.Materials.Outcome);
            var visible = view.FindControl<ResultImportEvidenceText>("OperationalExportOutcome")!;
            Assert.True(visible.IsEffectivelyVisible);
            Assert.Contains(Path.Combine(output, "9月13日-9月14日多项目合并材料包"), visible.Text);
            Assert.Contains("9月13日合并赛程安排表.xlsx", visible.Text);
            Assert.Contains("9月13日合并赛程安排表.pdf", visible.Text);
            Assert.Contains("男子单打带时间对阵图.xlsx", visible.Text);
            Assert.Contains("9月13日合并赛程记录表.xlsx", visible.Text);
            Assert.DoesNotContain("9月13日男子单打赛程记录表.xlsx", visible.Text);
            Assert.DoesNotContain("DailyScheduleExcel", visible.Text);
            Assert.DoesNotContain(page.Session.Workspace.Id.ToString(), visible.Text);
            Assert.DoesNotContain(page.Session.Workspace.Projects[0].Id.ToString(), visible.Text);
            var technical = view.FindControl<Expander>("OperationalExportTechnicalDetails");
            Assert.NotNull(technical); Assert.False(technical.IsExpanded);
            technical.IsExpanded = true; window.UpdateLayout();
            var evidence = view.FindControl<ResultImportEvidenceText>("OperationalExportEvidence")!;
            Assert.True(evidence.IsEffectivelyVisible);
            Assert.Contains(outcome.AuditId.ToString(), evidence.Text);
            Assert.Contains(outcome.Outputs[0].Sha256, evidence.Text);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    public void Dispose() => ui.Dispose();
}
