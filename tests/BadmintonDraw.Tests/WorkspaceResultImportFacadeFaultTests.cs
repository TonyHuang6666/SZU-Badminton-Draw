using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Persistence;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;
using static BadmintonDraw.Tests.WorkspaceResultImportFacadeFixture;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceResultImportFacadeFaultTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailureBeforePublicationPreservesTheCompleteArchiveAndReportsActualFiles(bool partialBackup)
    {
        var files = new ImportFaultFiles();
        using var f = new WorkspaceResultImportFacadeFixture(1, 2, new TournamentWorkspaceStore(files));
        var path = f.Export(); var preview = f.Workflow.PreviewResultImport([path]);
        var before = f.Session; var hash = Hash(before.WorkspacePath); var notifications = 0;
        f.Workflow.SessionChanged += (_, _) => notifications++;
        files.Armed = true; files.PartialBackup = partialBackup; files.FailPublish = !partialBackup;

        var error = Assert.Throws<WorkspaceCommandException>(() => f.Workflow.ImportResults(preview, new(false, null), f.Revision)).Error;

        Assert.False(error.Committed); Assert.Equal(1, f.Store.Mutations);
        Assert.Equal(0, notifications); Assert.Same(before, f.Session); Assert.Equal(hash, Hash(before.WorkspacePath));
        Assert.True(File.Exists(error.CandidatePath)); Assert.True(File.Exists(error.BackupPath));
        var durable = new TournamentWorkspaceStore().Read(before.WorkspacePath);
        Assert.Empty(durable.Results); Assert.Empty(durable.ImportLogs); Assert.Empty(durable.ResultHistory); Assert.Empty(durable.ProcessedDays);
        Assert.DoesNotContain(durable.AuditEvents, a => a.Action == "ResultsImported");
        if (partialBackup)
        {
            Assert.Contains("完整性未验证", error.Message);
            Assert.Equal("partial import backup", File.ReadAllText(error.BackupPath!));
        }
        else Assert.Equal(hash, Hash(error.BackupPath!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PostPublicationReadFailureIsNotReportedAsRollback(bool persistent)
    {
        var files = new ImportFaultFiles(); var store = new ImportFailingReadStore(files, persistent);
        using var f = new WorkspaceResultImportFacadeFixture(1, 2, store);
        store.Target = f.Session.WorkspacePath;
        var path = f.Export(); var preview = f.Workflow.PreviewResultImport([path]);
        var before = f.Session; var hash = Hash(before.WorkspacePath); var notices = 0;
        f.Workflow.SessionChanged += (_, _) => notices++;
        files.Armed = true;

        var error = Assert.Throws<WorkspaceCommandException>(() => f.Workflow.ImportResults(preview, new(false, null), f.Revision)).Error;

        Assert.True(error.Committed); Assert.Equal("CommittedReadFailed", error.Code);
        Assert.Equal(hash, Hash(error.BackupPath!)); Assert.Equal(1, notices); Assert.Equal(1, f.Store.Mutations);
        Assert.Equal(persistent, f.Session.RequiresReload);
        var durable = new TournamentWorkspaceStore().Read(before.WorkspacePath);
        Assert.Equal(before.Workspace.Revision + 1, durable.Revision); Assert.Equal(TournamentStage.Completed, durable.Stage);
        Assert.Single(durable.Results); Assert.Single(durable.ImportLogs); Assert.Single(durable.ProcessedDays);
        Assert.Single(durable.AuditEvents, a => a.Action == "ResultsImported");
        if (persistent) Assert.Equal("workspace.reload-required", Assert.Throws<WorkspaceCommandException>(
            () => f.Workflow.PreviewResultImport([path])).Error.Code);
        else Assert.Equal(durable.Revision, f.Revision);
    }

    [Fact]
    public void ARevisionRaceAtMutationBoundaryCannotPublishAValidatedCandidateOntoNewData()
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 2); var path = f.Export();
        var preview = f.Workflow.PreviewResultImport([path]); string? externalHash = null;
        f.Store.BeforeMutation = () =>
        {
            new TournamentWorkspaceStore().Mutate(f.Session.WorkspacePath, f.Revision, w => w with { Name = "已由其他进程保存" });
            externalHash = Hash(f.Session.WorkspacePath);
        };
        var error = Assert.Throws<WorkspaceCommandException>(() => f.Workflow.ImportResults(preview, new(false, null), f.Revision)).Error;
        Assert.Equal("RevisionConflict", error.Code); Assert.False(error.Committed); Assert.Equal(1, f.Store.Mutations);
        Assert.NotNull(externalHash); Assert.Equal(externalHash, Hash(f.Session.WorkspacePath));
        var durable = new TournamentWorkspaceStore().Read(f.Session.WorkspacePath);
        Assert.Equal("已由其他进程保存", durable.Name); Assert.Empty(durable.ImportLogs); Assert.Empty(durable.Results);
    }

    [Fact]
    public void SameIdentityAndRevisionReplacementIsRecheckedInsideTheMutation()
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 2); var path = f.Export();
        var preview = f.Workflow.PreviewResultImport([path]); var before = f.Session;
        string? replacementHash = null;
        f.Store.BeforeMutation = () =>
        {
            var replacement = f.PathFor("外部替换.szbd");
            new TournamentWorkspaceStore().Create(replacement, before.Workspace with { Name = "同身份同修订但内容不同" });
            File.Move(replacement, before.WorkspacePath, true); replacementHash = Hash(before.WorkspacePath);
        };
        var error = Assert.Throws<WorkspaceCommandException>(() => f.Workflow.ImportResults(preview, new(false, null), f.Revision)).Error;
        Assert.Equal("results.source-changed", error.Code); Assert.False(error.Committed);
        Assert.Equal(1, f.Store.Mutations); Assert.Same(before, f.Session);
        Assert.NotNull(replacementHash); Assert.Equal(replacementHash, Hash(before.WorkspacePath));
        var durable = new TournamentWorkspaceStore().Read(before.WorkspacePath);
        Assert.Equal(before.Workspace.Revision, durable.Revision); Assert.Empty(durable.Results);
        Assert.DoesNotContain(durable.AuditEvents, a => a.Action == "ResultsImported");
    }

    private sealed class ImportFaultFiles : WorkspaceFileOperations
    {
        internal bool Armed { get; set; }
        internal bool FailPublish { get; set; }
        internal bool PartialBackup { get; set; }
        internal bool Published { get; private set; }
        public override void Copy(string source, string destination)
        {
            if (Armed && PartialBackup && destination.EndsWith(".backup.szbd", StringComparison.Ordinal))
            { File.WriteAllText(destination, "partial import backup"); throw new IOException("backup write failed"); }
            base.Copy(source, destination);
        }
        public override void Publish(string candidate, string destination, bool overwrite)
        {
            if (Armed && FailPublish) throw new IOException("publish failed");
            base.Publish(candidate, destination, overwrite); if (Armed) Published = true;
        }
    }
    private sealed class ImportFailingReadStore(ImportFaultFiles files, bool persistent) : TournamentWorkspaceStore(files)
    {
        internal string? Target { get; set; }
        private bool failed;
        public override TournamentWorkspace Read(string path)
        {
            if (files.Published && path == Target && (!failed || persistent))
            { failed = true; throw new WorkspaceStoreException("InvalidWorkspace", "injected post-publication read failure"); }
            return base.Read(path);
        }
    }
}
