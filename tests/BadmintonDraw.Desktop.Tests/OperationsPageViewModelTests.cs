using System.Collections.Concurrent;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Tests;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class OperationsPageViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParentKeepsChildrenAndPublishesOwnImportThenNoopWithoutInventingHistory(bool posted)
    {
        var posts = new ConcurrentQueue<Action>();
        using var f = new OperationsUiFixture(2, 4, post: posted ? a => posts.Enqueue(a) : a => a());
        var page = f.Page; var import = page.ResultImport; var material = page.Materials;
        page.SelectedTabIndex = 2; material.OutputDirectory = f.Data.PathFor("retained-output");
        import.CorrectionReason = "保留人工说明";
        var path = f.Record(); await f.Import(path); while (posts.TryDequeue(out var next)) next();
        Assert.Same(page, f.Page); Assert.Same(import, page.ResultImport); Assert.Same(material, page.Materials);
        Assert.Equal(TournamentStage.Completed, page.Session.Workspace.Stage);
        Assert.Equal(6, page.CompletedMatchCount); Assert.Equal(6, page.TotalMatchCount);
        Assert.Equal(6, page.History.Results.Count); Assert.Single(page.History.Receipts);
        Assert.Contains("已保存", import.StateMessage); Assert.Equal("保留人工说明", import.CorrectionReason);
        Assert.Equal(2, page.SelectedTabIndex); Assert.EndsWith("retained-output", material.OutputDirectory);
        var history = page.History; page.RefreshSession(page.Session); Assert.Same(history, page.History);
        var before = f.Shell.CurrentSession!; var hash = WorkspaceResultImportFacadeFixture.Hash(before.WorkspacePath);
        var files = Directory.GetFiles(f.Data.DirectoryPath).Order().ToArray();
        await import.PreviewCommand.ExecuteAsync(); import.Confirmed = true; await import.ConfirmImportCommand.ExecuteAsync();
        while (posts.TryDequeue(out var next)) next();
        Assert.Same(before, f.Shell.CurrentSession); Assert.Same(history, page.History);
        Assert.Equal(hash, WorkspaceResultImportFacadeFixture.Hash(before.WorkspacePath));
        Assert.Equal(files, Directory.GetFiles(f.Data.DirectoryPath).Order().ToArray()); Assert.Contains("没有保存", import.StateMessage);
    }

    [Fact]
    public async Task RealAuditRefreshRevokesOldImportAndExportConsentButRetainsDrafts()
    {
        using var f = new OperationsUiFixture(); var page = f.Page; var file = f.Record();
        f.NextFiles = [file]; await page.ResultImport.PickFilesCommand.ExecuteAsync(); await page.ResultImport.PreviewCommand.ExecuteAsync();
        page.ResultImport.Confirmed = true; f.PrepareExport(); page.Materials.OverwriteExisting = true; page.Materials.ScopeConfirmed = true;
        f.Workflow.CreateBackup(f.Workflow.CurrentSession!.Workspace.Revision);
        Assert.Same(page, f.Page); Assert.False(page.ResultImport.HasCurrentPreview); Assert.False(page.ResultImport.Confirmed);
        Assert.False(page.Materials.ScopeConfirmed); Assert.False(page.Materials.OverwriteExisting);
        Assert.Equal(new[] { file }, page.ResultImport.SelectedPaths); Assert.EndsWith("materials", page.Materials.OutputDirectory);
        Assert.Contains(page.History.Audits, a => a.Contains("BackupCreated", StringComparison.Ordinal));
    }
}
