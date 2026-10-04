using System.Security.Cryptography;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Persistence;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceRecoveryWorkflowFaultTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedPublicationLeavesSessionAndFormalBytesUntouched(bool corrupt)
    {
        using var temp = new WorkspaceStoreTemp(); var normal = new TournamentWorkspaceStore();
        normal.Create(temp.Path, WorkspaceOperationsContractsTests.Fixture()); var backup = normal.CreateBackup(temp.Path);
        var files = new FaultFiles { FailPublish = true };
        var workflow = new TournamentWorkspaceWorkflow(new TournamentWorkspaceStore(files));
        if (corrupt) File.WriteAllText(temp.Path, "damaged target"); else workflow.OpenWorkspace(temp.Path);
        var before = workflow.CurrentSession; var hash = Hash(temp.Path); var published = 0;
        workflow.SessionChanged += (_, _) => published++;
        var error = corrupt
            ? Assert.Throws<WorkspaceCommandException>(() => workflow.RecoverFromBackup(workflow.PreviewRecovery(temp.Path, backup), "恢复"))
            : Assert.Throws<WorkspaceCommandException>(() => workflow.RestoreBackup(workflow.PreviewRestoreBackup(backup), "恢复", 0));
        Assert.False(error.Error.Committed);
        Assert.True(File.Exists(error.Error.CandidatePath));
        Assert.Equal(hash, Hash(error.Error.BackupPath!));
        Assert.Equal(hash, Hash(temp.Path));
        Assert.Same(before, workflow.CurrentSession); Assert.Equal(0, published);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CommittedReadFailureRefreshesTargetOrMarksReloadWithoutClaimingRollback(bool corrupt, bool persistent)
    {
        using var temp = new WorkspaceStoreTemp(); var normal = new TournamentWorkspaceStore();
        normal.Create(temp.Path, WorkspaceOperationsContractsTests.Fixture()); var backup = normal.CreateBackup(temp.Path);
        var files = new FaultFiles();
        var workflow = new TournamentWorkspaceWorkflow(new FailingReadStore(files, temp.Path, persistent));
        if (corrupt) File.WriteAllText(temp.Path, "damaged target"); else workflow.OpenWorkspace(temp.Path);
        var hash = Hash(temp.Path); var publications = 0; bool? observerUndo = null;
        workflow.SessionChanged += (_, _) => { publications++; observerUndo = workflow.CanUndoScheduleEdit; };
        var error = corrupt
            ? Assert.Throws<WorkspaceCommandException>(() => workflow.RecoverFromBackup(workflow.PreviewRecovery(temp.Path, backup), "恢复"))
            : Assert.Throws<WorkspaceCommandException>(() => workflow.RestoreBackup(workflow.PreviewRestoreBackup(backup), "恢复", 0));

        Assert.True(error.Error.Committed);
        Assert.Equal("CommittedReadFailed", error.Error.Code);
        Assert.Equal(hash, Hash(error.Error.BackupPath!));
        Assert.Equal(1, publications);
        Assert.Equal(false, observerUndo);
        Assert.Equal(temp.Path, workflow.CurrentSession!.WorkspacePath);
        Assert.Equal(persistent, workflow.CurrentSession.RequiresReload);
        var durable = normal.Read(temp.Path);
        Assert.Single(durable.AuditEvents, a => a.Action == (corrupt ? "WorkspaceRecovered" : "WorkspaceRestored"));
        if (!persistent) Assert.Equal(durable.Revision, workflow.CurrentSession.Workspace.Revision);
        else Assert.Throws<WorkspaceCommandException>(() => workflow.PreviewRestoreBackup(backup));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommittedReadFailureInvalidatesEvenAnIdenticalOldScheduleEditToken(bool corrupt)
    {
        using var temp = new WorkspaceStoreTemp(); var normal = new TournamentWorkspaceStore();
        normal.Create(temp.Path, TournamentWorkspaceRulesTests.Fixture(TournamentStage.ScheduleReady) with
            { Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult>() });
        var files = new FaultFiles(); var store = new FailingReadStore(files, temp.Path, false);
        var workflow = new TournamentWorkspaceWorkflow(store); workflow.OpenWorkspace(temp.Path);
        var node = workflow.CurrentSession!.Workspace.Projects[0].MatchGraph!.Matches[0];
        var baseline = workflow.CaptureScheduleEditBaseline();
        var request = new MoveMatchRequest(new(node.ProjectId, node.Id), "2026-09-13", new(11, 0), "1", baseline);
        var backup = normal.CreateBackup(temp.Path);
        if (corrupt) File.WriteAllText(temp.Path, "corrupt after editing preview");

        if (corrupt) Assert.Throws<WorkspaceCommandException>(() => workflow.RecoverFromBackup(workflow.PreviewRecovery(temp.Path, backup), "恢复"));
        else Assert.Throws<WorkspaceCommandException>(() => workflow.RestoreBackup(workflow.PreviewRestoreBackup(backup), "恢复", 0));

        Assert.False(workflow.CurrentSession!.RequiresReload);
        var error = Assert.Throws<WorkspaceCommandException>(() => workflow.PreviewMove(request, workflow.CurrentSession.Workspace.Revision));
        Assert.Equal("schedule.edit-session-changed", error.Error.Code);
        Assert.Equal(new TimeOnly(9, 0), normal.Read(temp.Path).Schedule!.Placements[node.Id].StartTime);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulManualCopySurvivesLaterAuditFailureWithBothPathsReported(bool committed)
    {
        using var temp = new WorkspaceStoreTemp(); var normal = new TournamentWorkspaceStore();
        normal.Create(temp.Path, WorkspaceOperationsContractsTests.Fixture());
        var files = new FaultFiles { FailPublish = !committed };
        var workflow = new TournamentWorkspaceWorkflow(new FailingReadStore(files, temp.Path, true));
        workflow.OpenWorkspace(temp.Path); var hash = Hash(temp.Path);

        var error = Assert.Throws<WorkspaceBackupException>(() => workflow.CreateBackup(0));

        Assert.NotNull(error.Backup);
        Assert.Equal(error.Backup.FullPath, error.ManualBackupPath);
        Assert.Equal(hash, Hash(error.ManualBackupPath!));
        Assert.Equal(0, normal.Read(error.ManualBackupPath!).Revision);
        Assert.True(File.Exists(error.Error.BackupPath));
        Assert.NotEqual(error.ManualBackupPath, error.Error.BackupPath);
        Assert.Equal(committed, error.Error.Committed);
        Assert.Equal(committed ? 1 : 0, normal.Read(temp.Path).Revision);
        Assert.Equal(committed, workflow.CurrentSession!.RequiresReload);
        if (!committed) Assert.Equal(hash, Hash(temp.Path));
    }

    [Fact]
    public void PartialManualCopyIsReportedButNeverPresentedAsValidatedBackup()
    {
        using var temp = new WorkspaceStoreTemp(); var normal = new TournamentWorkspaceStore();
        normal.Create(temp.Path, WorkspaceOperationsContractsTests.Fixture());
        var workflow = new TournamentWorkspaceWorkflow(new TournamentWorkspaceStore(new PartialBackupFiles()));
        workflow.OpenWorkspace(temp.Path); var hash = Hash(temp.Path);
        var error = Assert.Throws<WorkspaceBackupException>(() => workflow.CreateBackup(0));
        Assert.Null(error.Backup);
        Assert.True(File.Exists(error.ManualBackupPath));
        Assert.Equal(error.Error.BackupPath, error.ManualBackupPath);
        Assert.Equal("partial copy", File.ReadAllText(error.ManualBackupPath!));
        Assert.Contains("完整性未验证", error.Error.Message);
        Assert.False(error.Error.Committed);
        Assert.Equal(hash, Hash(temp.Path));
        Assert.Throws<WorkspaceStoreException>(() => normal.Read(error.ManualBackupPath!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManualBackupSourceRacePreservesActualCopyAndCannotAuditNewRevision(bool beforeCopy)
    {
        using var temp = new WorkspaceStoreTemp(); var normal = new TournamentWorkspaceStore();
        var original = normal.Create(temp.Path, WorkspaceOperationsContractsTests.Fixture());
        var replacement = Path.Combine(temp.DirectoryPath, "external.szbd");
        normal.Create(replacement, original with { Name = "外部新修订", Revision = 1 });
        var workflow = new TournamentWorkspaceWorkflow(new TournamentWorkspaceStore(new ReplaceSourceFiles(temp.Path, replacement, beforeCopy)));
        workflow.OpenWorkspace(temp.Path);
        var error = Assert.Throws<WorkspaceBackupException>(() => workflow.CreateBackup(0));
        Assert.NotNull(error.Backup);
        Assert.Equal(beforeCopy ? 1 : 0, error.Backup.Workspace.Revision);
        Assert.Equal(error.Backup.ContentHash, Hash(error.ManualBackupPath!));
        Assert.Equal(1, normal.Read(temp.Path).Revision);
        Assert.DoesNotContain(normal.Read(temp.Path).AuditEvents, a => a.Action == "WorkspaceBackupCreated");
        Assert.False(error.Error.Committed);
        Assert.Equal(0, workflow.CurrentSession!.Workspace.Revision);
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private sealed class FaultFiles : WorkspaceFileOperations
    {
        public bool FailPublish { get; init; }
        public bool Published { get; private set; }
        public override void Publish(string candidate, string destination, bool overwrite)
        { if (FailPublish) throw new IOException("replace failed"); base.Publish(candidate, destination, overwrite); Published = true; }
    }
    private sealed class FailingReadStore(FaultFiles files, string target, bool persistent) : TournamentWorkspaceStore(files)
    {
        private bool failed;
        public override TournamentWorkspace Read(string path)
        {
            if (files.Published && path == target && (!failed || persistent))
            { failed = true; throw new WorkspaceStoreException("InvalidWorkspace", "post-publication read failed"); }
            return base.Read(path);
        }
    }
    private sealed class PartialBackupFiles : WorkspaceFileOperations
    {
        public override void Copy(string source, string destination)
        {
            if (destination.EndsWith(".backup.szbd", StringComparison.Ordinal))
            { File.WriteAllText(destination, "partial copy"); throw new IOException("copy failed"); }
            base.Copy(source, destination);
        }
    }
    private sealed class ReplaceSourceFiles(string formal, string replacement, bool beforeCopy) : WorkspaceFileOperations
    {
        public override void Copy(string source, string destination)
        {
            var replace = source == formal && destination.EndsWith(".backup.szbd", StringComparison.Ordinal);
            if (replace && beforeCopy) File.Copy(replacement, formal, true);
            base.Copy(source, destination);
            if (replace && !beforeCopy) File.Copy(replacement, formal, true);
        }
    }
}
