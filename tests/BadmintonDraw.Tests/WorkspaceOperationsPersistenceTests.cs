using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Persistence;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceOperationsPersistenceTests
{
    [Theory]
    [InlineData("DELETE FROM processed_days")]
    [InlineData("DELETE FROM import_logs")]
    [InlineData("DELETE FROM result_history")]
    [InlineData("UPDATE workspace SET json=json_remove(json,'$.ImportLogCount')")]
    [InlineData("UPDATE workspace SET json=json_set(json,'$.ResultHistoryCount',0)")]
    [InlineData("UPDATE processed_days SET day='2030-01-01'")]
    [InlineData("UPDATE processed_days SET json='{}'")]
    [InlineData("UPDATE import_logs SET id='11111111-1111-1111-1111-111111111111'")]
    [InlineData("UPDATE import_logs SET json=json_set(json,'$.Rows[0].Key.ProjectId','11111111-1111-1111-1111-111111111111')")]
    [InlineData("UPDATE import_logs SET json=json_set(json,'$.Rows[0].RecordDay','2030-01-01')")]
    [InlineData("UPDATE import_logs SET json=json_set(json,'$.CorrectionCount',0)")]
    [InlineData("UPDATE result_history SET id='11111111-1111-1111-1111-111111111111'")]
    [InlineData("UPDATE result_history SET json=json_set(json,'$.Sequence',2)")]
    [InlineData("UPDATE result_history SET json=json_set(json,'$.ImportLogId','11111111-1111-1111-1111-111111111111')")]
    [InlineData("UPDATE match_results SET json=json_set(json,'$.Score','21-8')")]
    [InlineData("PRAGMA foreign_keys=OFF; UPDATE import_logs SET workspace_id='11111111-1111-1111-1111-111111111111'")]
    public void DamagedOperationsRowsOrHeaderCannotBeSilentlyDropped(string sql)
    {
        using var temp = new WorkspaceStoreTemp(); var store = new TournamentWorkspaceStore();
        store.Create(temp.Path, WorkspaceOperationsContractsTests.Fixture());
        WorkspaceStoreTemp.Sql(temp.Path, sql);
        var hash = WorkspaceRecoveryContractsTests.Hash(temp.Path);

        Assert.Equal("InvalidWorkspace", Assert.Throws<WorkspaceStoreException>(() => store.Read(temp.Path)).Code);

        Assert.Equal(hash, WorkspaceRecoveryContractsTests.Hash(temp.Path));
    }

    [Theory]
    [InlineData("transaction")]
    [InlineData("semantic")]
    [InlineData("backup")]
    [InlineData("publish")]
    public void PopulatedOperationStateSurvivesFailedAtomicMutation(string failure)
    {
        using var temp = new WorkspaceStoreTemp(); var initial = new TournamentWorkspaceStore().Create(temp.Path, WorkspaceOperationsContractsTests.Fixture());
        var before = WorkspaceRecoveryContractsTests.Hash(temp.Path);
        var store = new TournamentWorkspaceStore(new FaultFiles(failure));

        var error = Assert.Throws<WorkspaceStoreException>(() => store.Mutate(temp.Path, 0,
            workspace => failure == "semantic" ? workspace with { ResultHistory = [] } : workspace with { Name = "new name" }));

        Assert.False(error.Committed);
        Assert.Equal(before, WorkspaceRecoveryContractsTests.Hash(temp.Path));
        var reopened = new TournamentWorkspaceStore().Read(temp.Path);
        Assert.Equal(initial.Revision, reopened.Revision);
        Assert.Single(reopened.ImportLogs); Assert.Single(reopened.ResultHistory); Assert.Single(reopened.ProcessedDays);
        Assert.NotNull(error.CandidatePath);
    }

    [Theory]
    [InlineData(false, "backup")]
    [InlineData(false, "publish")]
    [InlineData(false, "read")]
    [InlineData(true, "backup")]
    [InlineData(true, "publish")]
    [InlineData(true, "read")]
    public void RestoreFailuresKeepTruthfulPublicationAndRecoveryPaths(bool corrupt, string failure)
    {
        using var temp = new WorkspaceStoreTemp(); var normal = new TournamentWorkspaceStore();
        normal.Create(temp.Path, WorkspaceOperationsContractsTests.Fixture());
        var backup = normal.CreateBackup(temp.Path);
        if (corrupt) File.WriteAllText(temp.Path, "damaged workspace bytes");
        var before = WorkspaceRecoveryContractsTests.Hash(temp.Path);
        var files = new FaultFiles(failure);
        var store = new PostReadFaultStore(files, temp.Path);
        var inspection = corrupt ? store.InspectRecovery(temp.Path, backup) : null;
        var snapshot = store.InspectBackup(backup);

        var error = Assert.Throws<WorkspaceStoreException>(() =>
        {
            if (corrupt) store.RecoverFromBackup(temp.Path, WorkspaceRecoveryContractsTests.Recovery(inspection!));
            else store.RestoreBackup(temp.Path, WorkspaceRecoveryContractsTests.Request(snapshot, 0));
        });

        Assert.Equal(failure == "read", error.Committed);
        if (failure == "read")
        {
            Assert.Equal("CommittedReadFailed", error.Code);
            Assert.Null(error.CandidatePath);
            var saved = normal.Read(temp.Path);
            Assert.Single(saved.AuditEvents, a => a.Action == (corrupt ? "WorkspaceRecovered" : "WorkspaceRestored"));
            Assert.Single(saved.ResultHistory); Assert.Single(saved.ImportLogs); Assert.Single(saved.ProcessedDays);
        }
        else
        {
            Assert.Equal(before, WorkspaceRecoveryContractsTests.Hash(temp.Path));
            Assert.NotNull(error.CandidatePath);
        }
        if (failure != "backup") Assert.Equal(before, WorkspaceRecoveryContractsTests.Hash(error.BackupPath!));
    }

    private sealed class FaultFiles(string failure) : WorkspaceFileOperations
    {
        public bool Published { get; private set; }
        public bool FailRead => failure == "read";
        public override void Copy(string source, string destination)
        {
            if (failure == "backup" && destination.EndsWith(".backup.szbd")) throw new IOException("backup fault");
            base.Copy(source, destination);
            if (failure == "transaction" && destination.EndsWith(".candidate.szbd"))
                WorkspaceStoreTemp.Sql(destination, "CREATE TRIGGER fail_write BEFORE DELETE ON import_logs BEGIN SELECT RAISE(ABORT,'transaction fault'); END");
        }
        public override void Publish(string candidate, string destination, bool overwrite)
        {
            if (failure == "publish") throw new IOException("publish fault");
            base.Publish(candidate, destination, overwrite); Published = true;
        }
    }
    private sealed class PostReadFaultStore(FaultFiles files, string formal) : TournamentWorkspaceStore(files)
    {
        public override TournamentWorkspace Read(string path) => files.Published && files.FailRead && path == formal
            ? throw new IOException("post-publication read fault") : base.Read(path);
    }
}
