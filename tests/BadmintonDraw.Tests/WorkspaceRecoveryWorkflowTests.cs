using System.Security.Cryptography;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Persistence;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceRecoveryWorkflowTests
{
    [Fact]
    public void HealthyRestorePublishesAllEvidenceOnceAndPreservesReplacedBytes()
    {
        using var temp = new WorkspaceStoreTemp(); var store = new TournamentWorkspaceStore();
        var original = store.Create(temp.Path, WorkspaceOperationsContractsTests.Fixture());
        var backup = store.CreateBackup(temp.Path);
        store.Mutate(temp.Path, 0, w => w with { Name = "现场修改" });
        var workflow = new TournamentWorkspaceWorkflow(store); workflow.OpenWorkspace(temp.Path);
        var hash = Hash(temp.Path); var preview = workflow.PreviewRestoreBackup(backup);
        Assert.Equal(hash, Hash(temp.Path));
        Assert.Equal(original.Id, preview.Backup.Workspace.Id);
        Assert.Equal(1, preview.SourceRevision);
        var published = 0;
        workflow.SessionChanged += (_, _) => { published++; throw new IOException("界面监听失败"); };

        var restored = workflow.RestoreBackup(preview, "核对后恢复赛果", 1);

        Assert.Equal(1, published);
        Assert.Equal(2, restored.Workspace.Revision);
        Assert.Equal(original.Name, restored.Workspace.Name);
        Assert.Single(restored.Workspace.Results);
        Assert.Single(restored.Workspace.ImportLogs);
        Assert.Single(restored.Workspace.ResultHistory);
        Assert.Single(restored.Workspace.ProcessedDays);
        Assert.Single(restored.Workspace.AuditEvents, a => a.Action == "WorkspaceRestored");
        Assert.Equal(hash, Hash(restored.BackupPath!));
        Assert.Same(restored.Workspace, workflow.CurrentSession!.Workspace);
        Assert.Equal(2, store.Read(temp.Path).Revision);
    }

    [Theory]
    [InlineData("reopen")]
    [InlineData("foreign-owner")]
    [InlineData("revision")]
    [InlineData("backup")]
    [InlineData("reason")]
    public void StaleOrUnconfirmedHealthyPreviewCannotPublish(string change)
    {
        using var temp = new WorkspaceStoreTemp(); var store = new TournamentWorkspaceStore();
        store.Create(temp.Path, WorkspaceOperationsContractsTests.Fixture());
        var backup = store.CreateBackup(temp.Path);
        var workflow = new TournamentWorkspaceWorkflow(store); workflow.OpenWorkspace(temp.Path);
        var preview = workflow.PreviewRestoreBackup(backup);
        if (change == "reopen") workflow.OpenWorkspace(temp.Path);
        if (change == "foreign-owner") { workflow = new(store); workflow.OpenWorkspace(temp.Path); }
        if (change == "revision") store.Mutate(temp.Path, 0, w => w with { Name = "其他进程修改" });
        if (change == "backup") store.Mutate(backup, 0, w => w with { Name = "替换了备份" });
        var before = workflow.CurrentSession; var hash = Hash(temp.Path);

        var error = Assert.Throws<WorkspaceCommandException>(() => workflow.RestoreBackup(preview,
            change == "reason" ? " " : "恢复", 0)).Error;

        Assert.False(error.Committed);
        Assert.Equal(hash, Hash(temp.Path));
        Assert.Same(before, workflow.CurrentSession);
        Assert.DoesNotContain(store.Read(temp.Path).AuditEvents, a => a.Action == "WorkspaceRestored");
    }

    [Fact]
    public void RestorePreviewRejectsForeignWorkspaceBeforeConfirmation()
    {
        using var temp = new WorkspaceStoreTemp(); var store = new TournamentWorkspaceStore();
        store.Create(temp.Path, WorkspaceOperationsContractsTests.Fixture());
        var other = Path.Combine(temp.DirectoryPath, "other.szbd");
        store.Create(other, WorkspaceOperationsContractsTests.Fixture());
        var workflow = new TournamentWorkspaceWorkflow(store); workflow.OpenWorkspace(temp.Path);
        var hash = Hash(temp.Path);
        Assert.Equal("WorkspaceIdentityMismatch", Assert.Throws<WorkspaceCommandException>(() => workflow.PreviewRestoreBackup(other)).Error.Code);
        Assert.Equal(hash, Hash(temp.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorruptRecoveryWorksAfterFailedOpenWithNoUsableCurrentSession(bool otherOpen)
    {
        using var temp = new WorkspaceStoreTemp(); var store = new TournamentWorkspaceStore();
        var original = store.Create(temp.Path, WorkspaceOperationsContractsTests.Fixture());
        var backup = store.CreateBackup(temp.Path);
        File.WriteAllText(temp.Path, "operator reviewed damaged archive");
        var workflow = new TournamentWorkspaceWorkflow(store);
        if (otherOpen)
        {
            var other = Path.Combine(temp.DirectoryPath, "other.szbd");
            store.Create(other, WorkspaceOperationsContractsTests.Fixture()); workflow.OpenWorkspace(other);
        }
        Assert.Throws<WorkspaceCommandException>(() => workflow.OpenWorkspace(temp.Path));
        var preview = workflow.PreviewRecovery(temp.Path, backup); var hash = Hash(temp.Path);
        Assert.Equal(hash, preview.CorruptContentHash);
        var publications = 0; workflow.SessionChanged += (_, _) => publications++;

        var result = workflow.RecoverFromBackup(preview, "已核对选定备份赛事身份");

        Assert.Equal(1, publications);
        Assert.Equal(temp.Path, workflow.CurrentSession!.WorkspacePath);
        Assert.Equal(original.Id, result.Workspace.Id);
        Assert.True(result.Workspace.Revision > original.Revision);
        Assert.Equal(hash, Hash(result.BackupPath!));
        Assert.Single(result.Workspace.AuditEvents, a => a.Action == "WorkspaceRecovered");
        Assert.Single(result.Workspace.ResultHistory);
    }

    [Theory]
    [InlineData("readable")]
    [InlineData("changed")]
    [InlineData("session")]
    [InlineData("owner")]
    public void CorruptConfirmationDoesNotFollowChangedBytesOrSessions(string change)
    {
        using var temp = new WorkspaceStoreTemp(); var store = new TournamentWorkspaceStore();
        store.Create(temp.Path, WorkspaceOperationsContractsTests.Fixture());
        var backup = store.CreateBackup(temp.Path); File.WriteAllText(temp.Path, "broken");
        var workflow = new TournamentWorkspaceWorkflow(store);
        var preview = workflow.PreviewRecovery(temp.Path, backup);
        if (change == "readable") File.Copy(backup, temp.Path, true);
        if (change == "changed") File.AppendAllText(temp.Path, " other bytes");
        if (change == "session") workflow.OpenWorkspace(backup);
        if (change == "owner") workflow = new(store);
        var hash = Hash(temp.Path); var before = workflow.CurrentSession;
        Assert.False(Assert.Throws<WorkspaceCommandException>(() => workflow.RecoverFromBackup(preview, "恢复")).Error.Committed);
        Assert.Equal(hash, Hash(temp.Path)); Assert.Same(before, workflow.CurrentSession);
    }

    [Fact]
    public void SamePlacementRestoreInvalidatesUndoAndOldEditBaselineBeforeObservers()
    {
        using var temp = new WorkspaceStoreTemp(); var store = new TournamentWorkspaceStore();
        store.Create(temp.Path, TournamentWorkspaceRulesTests.Fixture(TournamentStage.ScheduleReady) with
            { Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult>() });
        var workflow = new TournamentWorkspaceWorkflow(store); workflow.OpenWorkspace(temp.Path);
        var node = workflow.CurrentSession!.Workspace.Projects[0].MatchGraph!.Matches[0];
        var request = new MoveMatchRequest(new(node.ProjectId, node.Id), "2026-09-13", new(11, 0), "1", workflow.CaptureScheduleEditBaseline());
        workflow.MoveMatch(request, 0);
        Assert.True(workflow.CanUndoScheduleEdit);
        var baseline = workflow.CaptureScheduleEditBaseline();
        var backup = store.CreateBackup(temp.Path);
        var preview = workflow.PreviewRestoreBackup(backup);
        bool? observerCanUndo = null;
        workflow.SessionChanged += (_, _) => observerCanUndo = workflow.CanUndoScheduleEdit;

        var restored = workflow.RestoreBackup(preview, "恢复相同位置的备份", 1);

        Assert.Equal(false, observerCanUndo);
        Assert.False(workflow.CanUndoScheduleEdit);
        Assert.Equal(new TimeOnly(11, 0), restored.Workspace.Schedule!.Placements[node.Id].StartTime);
        var error = Assert.Throws<WorkspaceCommandException>(() => workflow.PreviewMove(request with { Baseline = baseline }, 2));
        Assert.Equal("schedule.edit-session-changed", error.Error.Code);
    }

    [Fact]
    public void ManualBackupReturnsTheActualCopySeparatelyFromAuditSaveBackup()
    {
        using var temp = new WorkspaceStoreTemp(); var store = new TournamentWorkspaceStore();
        store.Create(temp.Path, WorkspaceOperationsContractsTests.Fixture());
        var workflow = new TournamentWorkspaceWorkflow(store); workflow.OpenWorkspace(temp.Path);
        var hash = Hash(temp.Path);

        var outcome = workflow.CreateBackup(0);

        Assert.Equal(hash, Hash(outcome.Backup.FullPath));
        Assert.Equal(hash, outcome.Backup.ContentHash);
        Assert.Equal(0, outcome.Backup.Workspace.Revision);
        Assert.Equal(1, outcome.Command.Workspace.Revision);
        Assert.NotEqual(outcome.Backup.FullPath, outcome.Command.BackupPath);
        Assert.True(File.Exists(outcome.Command.BackupPath));
        Assert.Single(outcome.Command.Workspace.AuditEvents, a => a.Action == "WorkspaceBackupCreated");
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
