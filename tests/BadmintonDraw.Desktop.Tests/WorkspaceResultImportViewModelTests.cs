using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Persistence;
using BadmintonDraw.Tests;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class WorkspaceResultImportViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitOnceOnlyApplyAcceptsOwnPublicationAndShowsAuthoritativeCompletedSnapshot(bool posted)
    {
        var posts = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        using var f = new ResultImportUiFixture(post: posted ? action => posts.Enqueue(action) : action => action());
        var vm = f.ViewModel; var paths = f.Data.Session.Workspace.Projects.Select(p =>
            f.Data.Export(p.Id + ".records.xlsx", keys: p.MatchGraph!.Matches.Select(n => new WorkspaceMatchKey(p.Id, n.Id)).ToArray())).ToArray();
        var before = f.Shell.CurrentSession!; var hash = WorkspaceResultImportFacadeFixture.Hash(before.WorkspacePath);
        var diskFiles = Directory.GetFiles(f.Data.DirectoryPath).Order().ToArray(); var notices = 0;
        f.Data.Workflow.SessionChanged += (_, _) => notices++;
        await f.Choose(paths); Assert.Empty(before.Workspace.Results); Assert.False(vm.HasCurrentPreview);
        await vm.PreviewCommand.ExecuteAsync();
        Assert.True(vm.HasCurrentPreview); Assert.Equal(ResultImportEvaluationStatus.Ready, vm.PreviewStatus);
        Assert.Equal(6, vm.Counts!.AddedResultCount); Assert.Equal(2, vm.Files.Count);
        Assert.False(vm.ConfirmImportCommand.CanExecute(null)); Assert.False(vm.Confirmed);
        Assert.Equal(hash, WorkspaceResultImportFacadeFixture.Hash(before.WorkspacePath));
        Assert.Equal(diskFiles, Directory.GetFiles(f.Data.DirectoryPath).Order().ToArray()); Assert.Equal(0, notices);
        Assert.Contains(before.WorkspacePath, vm.SourceDetails);
        foreach (var path in paths) { Assert.Contains(path, vm.SourceDetails); Assert.Contains(WorkspaceResultImportFacadeFixture.Hash(path), vm.SourceDetails); }
        await f.Accept(); while (posts.TryDequeue(out var action)) action();
        Assert.Equal(1, notices); Assert.Equal(1, f.Data.Store.Mutations);
        Assert.Equal(TournamentStage.Completed, f.Shell.CurrentSession!.Workspace.Stage);
        Assert.Equal(6, f.Shell.CurrentSession.Workspace.Results.Count); Assert.NotSame(before, f.Shell.CurrentSession);
        Assert.Contains("已保存", vm.StateMessage); Assert.Contains(f.Shell.WorkspacePath, vm.OutcomeDetails);
        Assert.False(vm.HasCurrentPreview); Assert.False(vm.Confirmed); Assert.False(vm.ConfirmImportCommand.CanExecute(null));
        await vm.ConfirmImportCommand.ExecuteAsync(); Assert.Equal(1, f.Data.Store.Mutations);
        var projection = ScheduledMatchProjection.Build(f.Shell.CurrentSession.Workspace.Projects.Select(p => p.MatchGraph!).ToArray(),
            f.Shell.CurrentSession.Workspace.Schedule!, f.Shell.CurrentSession.Workspace.Results);
        Assert.DoesNotContain(projection, m => m.SideA.EndsWith("胜者", StringComparison.Ordinal));
        Assert.False(f.Shell.CanNavigate(WorkspaceRoute.Operations)); // No placeholder route was registered by this leaf.
    }

    [Fact]
    public async Task PreviouslyImportedHashNoChangesDoesNotInventSaveBackupAuditOrCoverage()
    {
        using var f = new ResultImportUiFixture(1, 2); var path = f.Data.Export(); await f.Preview(path); await f.Accept();
        var before = f.Shell.CurrentSession!; var hash = WorkspaceResultImportFacadeFixture.Hash(before.WorkspacePath);
        var diskFiles = Directory.GetFiles(f.Data.DirectoryPath).Order().ToArray(); var notices = 0; f.Data.Store.Mutations = 0;
        f.Data.Workflow.SessionChanged += (_, _) => notices++;
        await f.ViewModel.PreviewCommand.ExecuteAsync();
        Assert.Equal(ResultImportEvaluationStatus.NoChanges, f.ViewModel.PreviewStatus);
        Assert.Equal(ResultImportFileStatus.PreviouslyImported, Assert.Single(f.ViewModel.Files).Status);
        Assert.Contains("无需保存", f.ViewModel.ConfirmButtonText); await f.Accept();
        Assert.Same(before, f.Shell.CurrentSession); Assert.Equal(0, f.Data.Store.Mutations); Assert.Equal(0, notices);
        Assert.Equal(hash, WorkspaceResultImportFacadeFixture.Hash(before.WorkspacePath));
        Assert.Equal(diskFiles, Directory.GetFiles(f.Data.DirectoryPath).Order().ToArray());
        Assert.Contains("没有保存", f.ViewModel.StateMessage); Assert.DoesNotContain("已导入并保存", f.ViewModel.StateMessage);
    }

    [Fact]
    public async Task MetadataCorrectionRequiresAllConsentAndPreservesExactBeforeAfterAndSingleHistory()
    {
        using var f = new ResultImportUiFixture(1, 2); var path = f.Data.Export(); await f.Preview(path); await f.Accept();
        var old = Assert.Single(f.Shell.CurrentSession!.Workspace.Results).Value;
        var correction = f.Data.Export("correct.xlsx"); WorkspaceResultImportFacadeFixture.Edit(correction, s => s.Cell(6, 10).Value = 25);
        await f.Preview(correction); var vm = f.ViewModel;
        Assert.Equal(ResultImportEvaluationStatus.RequiresConfirmation, vm.PreviewStatus);
        var change = Assert.Single(vm.Corrections);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(old), System.Text.Json.JsonSerializer.Serialize(change.Before));
        Assert.Equal(25, change.After.DurationMinutes);
        Assert.Equal(correction, change.Source.SourcePath); Assert.Contains(vm.Diagnostics, d => d.Code == "result.correction-confirmation");
        vm.Confirmed = true; Assert.False(vm.ConfirmImportCommand.CanExecute(null));
        vm.AllowCorrections = true; Assert.False(vm.Confirmed); vm.Confirmed = true;
        Assert.False(vm.ConfirmImportCommand.CanExecute(null));
        vm.CorrectionReason = "  核对原始记录  "; Assert.False(vm.Confirmed); Assert.True(vm.HasCurrentPreview);
        vm.Confirmed = true; Assert.True(vm.ConfirmImportCommand.CanExecute(null)); await vm.ConfirmImportCommand.ExecuteAsync();
        var history = Assert.Single(f.Shell.CurrentSession!.Workspace.ResultHistory);
        Assert.Equal("核对原始记录", history.Reason); Assert.Equal(old.RecordedAt, history.Before.RecordedAt);
        Assert.Equal(25, Assert.Single(f.Shell.CurrentSession.Workspace.Results).Value.DurationMinutes);
        Assert.Contains("已保存", vm.StateMessage); Assert.Equal("  核对原始记录  ", vm.CorrectionReason);
    }

    [Fact]
    public async Task PendingOnlyReceiptIsSavedDespiteZeroAddedResults()
    {
        using var f = new ResultImportUiFixture(1, 2); var before = f.Shell.CurrentSession!;
        await f.Preview(f.Data.Export(fill: false)); Assert.NotNull(f.ViewModel.Counts); Assert.Equal(0, f.ViewModel.Counts.AddedResultCount);
        Assert.Equal(1, f.ViewModel.Counts.NewFileCount); Assert.Equal(1, f.ViewModel.Counts.PendingRowCount);
        await f.Accept(); Assert.Equal(before.Workspace.Revision + 1, f.Shell.CurrentSession!.Workspace.Revision);
        Assert.Empty(f.Shell.CurrentSession.Workspace.Results); Assert.Single(f.Shell.CurrentSession.Workspace.ImportLogs);
        Assert.Single(f.Shell.CurrentSession.Workspace.ProcessedDays); Assert.Contains("已保存", f.ViewModel.StateMessage);
    }

    [Fact]
    public async Task ByteIdenticalBatchAliasesArePreservedAndReportedThenOnlyOneReceiptIsSaved()
    {
        using var f = new ResultImportUiFixture(1, 2); var first = f.Data.Export(); var second = f.Data.PathFor("alias.xlsx");
        File.Copy(first, second); await f.Preview(first, second);
        Assert.Equal(new[] { first, second }, f.ViewModel.SelectedPaths);
        Assert.Equal(new[] { ResultImportFileStatus.New, ResultImportFileStatus.DuplicateInBatch }, f.ViewModel.Files.Select(file => file.Status));
        Assert.Equal(1, f.ViewModel.Counts!.DuplicateFileCount); await f.Accept();
        Assert.Single(f.Shell.CurrentSession!.Workspace.ImportLogs); Assert.Equal(1, f.Data.Store.Mutations);
    }

    [Fact]
    public async Task NewFileWithUnchangedResultStillSavesItsOwnReceipt()
    {
        using var f = new ResultImportUiFixture(1, 2); await f.Preview(f.Data.Export()); await f.Accept();
        var before = f.Shell.CurrentSession!; var next = f.Data.Export("another.xlsx");
        WorkspaceResultImportFacadeFixture.Edit(next, s => s.Cell(3, 1).Value = "new independent receipt bytes");
        await f.Preview(next); Assert.Equal(ResultImportEvaluationStatus.Ready, f.ViewModel.PreviewStatus);
        Assert.Equal(0, f.ViewModel.Counts!.AddedResultCount); await f.Accept();
        Assert.Equal(before.Workspace.Revision + 1, f.Shell.CurrentSession!.Workspace.Revision);
        Assert.Equal(2, f.Shell.CurrentSession.Workspace.ImportLogs.Count); Assert.Contains("已保存", f.ViewModel.StateMessage);
    }

    [Fact]
    public async Task VoidedReceiptRemainsNoChangesAndDoesNotReactivateCoverage()
    {
        using var f = new ResultImportUiFixture(1, 2); var file = f.Data.Export(fill: false); await f.Preview(file); await f.Accept();
        var schedule = f.Data.Session.Workspace.Schedule!;
        f.Data.Workflow.GenerateSchedule(schedule.Resources, schedule.Policy, f.Data.Revision);
        await f.ViewModel.PreviewCommand.ExecuteAsync();
        Assert.Equal(ResultImportFileStatus.PreviouslyVoided, Assert.Single(f.ViewModel.Files).Status);
        var before = f.Shell.CurrentSession; await f.Accept(); Assert.Same(before, f.Shell.CurrentSession);
        Assert.Empty(f.Shell.CurrentSession!.Workspace.ProcessedDays); Assert.NotNull(Assert.Single(f.Shell.CurrentSession.Workspace.ImportLogs).VoidedAt);
        Assert.Contains("没有保存", f.ViewModel.StateMessage);
    }

    [Theory]
    [InlineData("winner")]
    [InlineData("foreign")]
    [InlineData("epoch")]
    public async Task RejectedRowsCannotBeUnlockedByCorrectionConsent(string change)
    {
        using var f = new ResultImportUiFixture(1, 2); await f.Preview(f.Data.Export()); await f.Accept();
        var file = f.Data.Export("bad.xlsx");
        WorkspaceResultImportFacadeFixture.Edit(file, s =>
        {
            if (change == "winner") { s.Cell(6, 12).Value = "B"; s.Cell(6, 9).Value = "10-21"; }
            if (change == "foreign") s.Cell(6, 17).Value = Guid.NewGuid().ToString();
            if (change == "epoch") s.Cell(6, 20).Value = DateTimeOffset.UtcNow.AddYears(-1).ToString("O");
        });
        var before = f.Shell.CurrentSession!; var hash = WorkspaceResultImportFacadeFixture.Hash(before.WorkspacePath);
        await f.Preview(file); Assert.Equal(ResultImportEvaluationStatus.Rejected, f.ViewModel.PreviewStatus); Assert.NotEmpty(f.ViewModel.Diagnostics);
        var diagnostic = change switch { "winner" => "result.participants-changed", "foreign" => "result.foreign-match", _ => "result.epoch-stale" };
        Assert.Contains(f.ViewModel.Diagnostics, d => d.Code == diagnostic);
        f.ViewModel.AllowCorrections = true; f.ViewModel.CorrectionReason = "不得覆盖硬冲突"; f.ViewModel.Confirmed = true;
        Assert.False(f.ViewModel.ConfirmImportCommand.CanExecute(null)); await f.ViewModel.ConfirmImportCommand.ExecuteAsync();
        Assert.Same(before, f.Shell.CurrentSession); Assert.Equal(hash, WorkspaceResultImportFacadeFixture.Hash(before.WorkspacePath));
    }

    [Fact]
    public async Task ChangedBytesAtAcceptConsumeAuthorizationButRetainUserDrafts()
    {
        using var f = new ResultImportUiFixture(1, 2); var file = f.Data.Export(); await f.Preview(file);
        f.ViewModel.CorrectionReason = "preserved draft";
        WorkspaceResultImportFacadeFixture.Edit(file, s => s.Cell(6, 10).Value = 25);
        var hash = WorkspaceResultImportFacadeFixture.Hash(f.Shell.WorkspacePath); await f.Accept();
        Assert.Contains("results.file-changed", f.ViewModel.StateMessage); Assert.False(f.ViewModel.HasCurrentPreview);
        Assert.Equal("preserved draft", f.ViewModel.CorrectionReason); Assert.Equal(file, Assert.Single(f.ViewModel.SelectedPaths));
        Assert.Equal(hash, WorkspaceResultImportFacadeFixture.Hash(f.Shell.WorkspacePath)); Assert.Equal(0, f.Data.Store.Mutations);
    }
}
