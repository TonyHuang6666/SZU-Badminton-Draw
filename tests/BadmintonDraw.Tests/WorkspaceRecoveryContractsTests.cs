using System.Security.Cryptography;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Persistence;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceRecoveryContractsTests
{
    [Fact]
    public void HealthyRestoreChecksReviewedRevisionAndRestoresAllFactsWithOneAuditAndBackup()
    {
        using var temp = new WorkspaceStoreTemp(); var store = new TournamentWorkspaceStore();
        var original = store.Create(temp.Path, WorkspaceOperationsContractsTests.Fixture());
        var backup = store.InspectBackup(store.CreateBackup(temp.Path));
        store.Mutate(temp.Path, 0, w => w with { Name = "现场新名称" });
        var before = Hash(temp.Path);
        var stale = Request(backup, 0);
        Assert.Equal("RevisionConflict", Assert.Throws<WorkspaceStoreException>(() => store.RestoreBackup(temp.Path, stale)).Code);
        Assert.Equal(before, Hash(temp.Path));

        var result = store.RestoreBackup(temp.Path, Request(backup, 1));

        Assert.Equal(original.Id, result.Workspace.Id);
        Assert.Equal(original.Name, result.Workspace.Name);
        Assert.Equal(2, result.Workspace.Revision);
        Assert.Single(result.Workspace.Results);
        Assert.Single(result.Workspace.ResultHistory);
        Assert.Single(result.Workspace.ImportLogs);
        Assert.Single(result.Workspace.ProcessedDays);
        Assert.Single(result.Workspace.AuditEvents, a => a.Action == "WorkspaceRestored");
        Assert.Equal(before, Hash(result.BackupPath));
        Assert.Equal(2, store.Read(temp.Path).Revision);
    }

    [Theory]
    [InlineData("formal-changed")]
    [InlineData("formal-readable")]
    [InlineData("backup-changed")]
    [InlineData("backup-identity")]
    public void CorruptRecoveryRejectsStaleByteOrIdentityConfirmation(string change)
    {
        using var temp = new WorkspaceStoreTemp(); var store = new TournamentWorkspaceStore();
        store.Create(temp.Path, WorkspaceOperationsContractsTests.Fixture());
        var backup = store.CreateBackup(temp.Path);
        File.WriteAllText(temp.Path, "corrupt bytes reviewed by operator");
        var inspection = store.InspectRecovery(temp.Path, backup);
        var request = Recovery(inspection);
        if (change == "formal-changed") File.AppendAllText(temp.Path, " changed");
        if (change == "formal-readable") File.Copy(backup, temp.Path, true);
        if (change == "backup-changed") store.Mutate(backup, 0, w => w with { Name = "changed backup" });
        if (change == "backup-identity") request = request with { BackupWorkspaceId = Guid.NewGuid() };
        var before = Hash(temp.Path);

        var error = Assert.Throws<WorkspaceStoreException>(() => store.RecoverFromBackup(temp.Path, request));

        Assert.False(error.Committed);
        Assert.Equal(before, Hash(temp.Path));
    }

    [Fact]
    public void CorruptRecoveryPreservesExactBytesAndUsesHighRevisionWithoutPretendingSourceIdentity()
    {
        using var temp = new WorkspaceStoreTemp(); var store = new TournamentWorkspaceStore();
        var original = store.Create(temp.Path, WorkspaceOperationsContractsTests.Fixture());
        var backup = store.CreateBackup(temp.Path);
        var old = store.Mutate(temp.Path, 0, w => w with { Name = "现场" }).Workspace;
        File.WriteAllText(temp.Path, "corrupt sqlite");
        var inspection = store.InspectRecovery(temp.Path, backup);
        Assert.Equal(Hash(temp.Path), inspection.CorruptContentHash);
        Assert.Equal(Hash(backup), inspection.BackupSnapshot.ContentHash);

        var result = store.RecoverFromBackup(temp.Path, Recovery(inspection));

        Assert.Equal(original.Id, result.Workspace.Id);
        Assert.True(result.Workspace.Revision > old.Revision);
        Assert.Equal(inspection.CorruptContentHash, Hash(result.BackupPath));
        Assert.Single(result.Workspace.AuditEvents, a => a.Action == "WorkspaceRecovered" && a.Detail.Contains(inspection.CorruptContentHash));
        Assert.Single(result.Workspace.ResultHistory);
        Assert.Equal("RevisionConflict", Assert.Throws<WorkspaceStoreException>(() => store.Mutate(temp.Path, old.Revision, w => w)).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrepublicationSourceChangeCannotBeOverwrittenByAReviewedRestore(bool corrupt)
    {
        using var temp = new WorkspaceStoreTemp(); var normal = new TournamentWorkspaceStore();
        normal.Create(temp.Path, WorkspaceOperationsContractsTests.Fixture());
        var backup = normal.CreateBackup(temp.Path);
        if (corrupt) File.WriteAllText(temp.Path, "initial corrupt bytes");
        var files = new ChangeDuringBackup(temp.Path);
        var store = new TournamentWorkspaceStore(files);
        var inspected = corrupt ? store.InspectRecovery(temp.Path, backup) : null;
        var backupSnapshot = store.InspectBackup(backup);
        files.Enabled = true;

        Assert.Throws<WorkspaceStoreException>(() =>
        {
            if (corrupt) store.RecoverFromBackup(temp.Path, Recovery(inspected!));
            else store.RestoreBackup(temp.Path, Request(backupSnapshot, 0));
        });

        Assert.Equal("externally changed bytes", File.ReadAllText(temp.Path));
        Assert.False(files.Published);
    }

    [Fact]
    public void HealthySourceChangingDuringItsRevisionReadIsNotOverwritten()
    {
        using var temp = new WorkspaceStoreTemp(); var normal = new TournamentWorkspaceStore();
        normal.Create(temp.Path, WorkspaceOperationsContractsTests.Fixture());
        var backup = normal.InspectBackup(normal.CreateBackup(temp.Path));
        var store = new ChangedAfterReadStore(temp.Path);
        Assert.Throws<WorkspaceStoreException>(() => store.RestoreBackup(temp.Path, Request(backup, 0)));
        Assert.Equal("changed after revision read", File.ReadAllText(temp.Path));
    }

    [Fact]
    public void BackupInspectionHashesAndParsesItsCapturedBytesNotTheChangingOriginalPath()
    {
        using var temp = new WorkspaceStoreTemp(); var normal = new TournamentWorkspaceStore();
        var original = normal.Create(temp.Path, WorkspaceOperationsContractsTests.Fixture());
        var backup = normal.CreateBackup(temp.Path); var hash = Hash(backup);
        var snapshot = new TournamentWorkspaceStore(new ChangeSnapshotSource(backup)).InspectBackup(backup);
        Assert.Equal(hash, snapshot.ContentHash);
        Assert.Equal(original.Name, snapshot.Workspace.Name);
        Assert.NotEqual(hash, Hash(backup));
    }

    internal static WorkspaceRestoreRequest Request(WorkspaceBackupSnapshot snapshot, long revision) =>
        new(snapshot.FullPath, snapshot.ContentHash, snapshot.Workspace.Id, revision, "确认恢复选定备份");
    internal static WorkspaceRecoveryRequest Recovery(WorkspaceRecoveryInspection inspection) =>
        new(inspection.BackupSnapshot.FullPath, inspection.BackupSnapshot.ContentHash, inspection.BackupSnapshot.Workspace.Id,
            inspection.CorruptContentHash, "已核对损坏文件和备份身份");
    internal static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class ChangeDuringBackup(string formal) : WorkspaceFileOperations
    {
        public bool Enabled { get; set; }
        public bool Published { get; private set; }
        public override void Copy(string source, string destination)
        {
            base.Copy(source, destination);
            if (Enabled && source == formal && destination.EndsWith(".backup.szbd")) File.WriteAllText(formal, "externally changed bytes");
        }
        public override void Publish(string candidate, string destination, bool overwrite)
        { Published = true; base.Publish(candidate, destination, overwrite); }
    }
    private sealed class ChangedAfterReadStore(string formal) : TournamentWorkspaceStore
    {
        public override TournamentWorkspace Read(string path)
        {
            var workspace = base.Read(path);
            if (path == formal) File.WriteAllText(formal, "changed after revision read");
            return workspace;
        }
    }
    private sealed class ChangeSnapshotSource(string sourcePath) : WorkspaceFileOperations
    {
        public override void Copy(string source, string destination)
        {
            base.Copy(source, destination);
            if (source == sourcePath) File.WriteAllText(sourcePath, "changed after snapshot copy");
        }
    }
}
