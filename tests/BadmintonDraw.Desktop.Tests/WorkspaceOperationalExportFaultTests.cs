using System.Collections.Concurrent;
using BadmintonDraw.Persistence;
using BadmintonDraw.Tests;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class WorkspaceOperationalExportFaultTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PartialPublicationReportsVerifiedPrefixAndUnknownAttemptSeparately(bool afterMove, bool cleanup)
    {
        var files = new OperationalUiFiles { FailAt = 2, ThrowAfterMove = afterMove, FailCleanup = cleanup };
        using var f = new OperationsUiFixture(files: files); f.PrepareExport(); var vm = f.Page.Materials;
        var original = f.Shell.CurrentSession!; var hash = WorkspaceResultImportFacadeFixture.Hash(original.WorkspacePath);
        await vm.ExportCommand.ExecuteAsync(); var failure = Assert.IsType<OperationalPackageExportException>(vm.ExportFailure);
        Assert.Same(failure.Error, vm.Error); Assert.False(failure.AuditRecorded); Assert.Null(vm.Outcome); Assert.Same(original, f.Shell.CurrentSession);
        var success = Assert.Single(vm.Outputs); Assert.Equal(success.Sha256, WorkspaceResultImportFacadeFixture.Hash(success.Path));
        Assert.Contains(success.Path, vm.OutcomeDetails); Assert.Contains(success.Sha256, vm.OutcomeDetails);
        Assert.NotEqual(success.Path, failure.AttemptedOutputPath); Assert.Contains(failure.AttemptedOutputPath!, vm.OutcomeDetails);
        Assert.Contains("最终状态未确认", vm.OutcomeDetails); Assert.Equal(afterMove, File.Exists(failure.AttemptedOutputPath));
        Assert.DoesNotContain(vm.Outputs, o => o.Path == failure.AttemptedOutputPath);
        if (cleanup) { Assert.True(Directory.Exists(failure.RetainedStagingDirectory)); Assert.Contains(failure.RetainedStagingDirectory!, vm.OutcomeDetails); }
        Assert.Equal(hash, WorkspaceResultImportFacadeFixture.Hash(original.WorkspacePath)); Assert.False(vm.ScopeConfirmed);
        Assert.Contains(failure.Error.CandidatePath!, vm.OutcomeDetails); Assert.True(File.Exists(failure.Error.CandidatePath));
        Assert.DoesNotContain(f.Data.Store.Read(original.WorkspacePath).AuditEvents, a => a.Id == failure.AuditId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuditSaveFailureDoesNotHideAllPublishedFilesOrRealCandidateAndBackup(bool partialBackup)
    {
        var files = new ImportUiFiles(); var store = new ImportUiStore(files);
        using var f = new OperationsUiFixture(store: store); f.PrepareExport(); var vm = f.Page.Materials;
        var original = f.Shell.CurrentSession!; var hash = WorkspaceResultImportFacadeFixture.Hash(original.WorkspacePath);
        files.Armed = true; files.FailPublish = !partialBackup; files.PartialBackup = partialBackup;
        await vm.ExportCommand.ExecuteAsync(); var failure = Assert.IsType<OperationalPackageExportException>(vm.ExportFailure);
        Assert.Equal(9, vm.Outputs.Count); Assert.False(failure.AuditRecorded); Assert.False(failure.Error.Committed);
        Assert.Equal(hash, WorkspaceResultImportFacadeFixture.Hash(original.WorkspacePath)); Assert.Same(original, f.Shell.CurrentSession);
        Assert.True(File.Exists(failure.Error.BackupPath)); Assert.True(File.Exists(failure.Error.CandidatePath));
        Assert.Contains(failure.Error.BackupPath!, vm.OutcomeDetails); Assert.Contains(failure.Error.CandidatePath!, vm.OutcomeDetails);
        foreach (var output in vm.Outputs) Assert.Equal(output.Sha256, WorkspaceResultImportFacadeFixture.Hash(output.Path));
        if (partialBackup) { Assert.Equal("partial backup", File.ReadAllText(failure.Error.BackupPath!)); Assert.Contains("完整性", vm.OutcomeDetails); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CommittedRereadFailurePreservesAuditTruthAndOwnPublicationWithoutClaimingRollback(bool persistent, bool posted)
    {
        var files = new ImportUiFiles(); var store = new ImportUiStore(files); var posts = new ConcurrentQueue<Action>();
        using var f = new OperationsUiFixture(store: store, post: posted ? a => posts.Enqueue(a) : a => a()); f.PrepareExport();
        var vm = f.Page.Materials; var original = f.Shell.CurrentSession!;
        files.Armed = true; store.FailCommittedRead = persistent; store.CommittedReadFailuresRemaining = 1;
        await vm.ExportCommand.ExecuteAsync(); while (posts.TryDequeue(out var next)) next();
        var failure = Assert.IsType<OperationalPackageExportException>(vm.ExportFailure);
        Assert.Equal("CommittedReadFailed", failure.Error.Code); Assert.True(failure.Error.Committed); Assert.True(failure.AuditRecorded);
        Assert.Contains("审计已保存", vm.StateMessage); Assert.Contains("不代表回滚", vm.StateMessage); Assert.Equal(9, vm.Outputs.Count);
        Assert.Equal(persistent, f.Shell.CurrentSession!.RequiresReload); Assert.False(vm.ExportCommand.CanExecute(null));
        vm.ScopeConfirmed = true; Assert.Equal(!persistent, vm.ExportCommand.CanExecute(null));
        var durable = new TournamentWorkspaceStore().Read(original.WorkspacePath);
        Assert.Equal(original.Workspace.Revision + 1, durable.Revision); Assert.Single(durable.AuditEvents, a => a.Id == failure.AuditId);
        Assert.Contains(failure.Error.BackupPath!, vm.OutcomeDetails);
    }
}

internal sealed class OperationalUiFiles : OperationalPackageFileOperations
{
    internal int FailAt { get; set; }
    internal bool ThrowAfterMove { get; set; }
    internal bool FailCleanup { get; set; }
    private int publishes;
    public override void Publish(string stagedPath, string destination, bool overwrite)
    {
        var fails = ++publishes == FailAt;
        if (fails && !ThrowAfterMove) throw new IOException("injected publication failure");
        base.Publish(stagedPath, destination, overwrite);
        if (fails) throw new IOException("injected post-publication failure");
    }
    public override void DeleteStagingDirectory(string path)
    { if (FailCleanup) throw new IOException("injected cleanup failure"); base.DeleteStagingDirectory(path); }
}
