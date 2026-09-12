using System.Security.Cryptography;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Persistence;
using Xunit;

namespace BadmintonDraw.Tests;

public class TournamentWorkspaceStoreFailureTests
{
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static TournamentWorkspace Initialize(WorkspaceStoreTemp temp) => new TournamentWorkspaceStore().Create(temp.Path, TournamentWorkspaceRulesTests.Fixture(TournamentStage.Completed));

    [Theory]
    [InlineData("transaction")]
    [InlineData("semantic")]
    [InlineData("backup")]
    [InlineData("replace")]
    [InlineData("null")]
    [InlineData("identity")]
    [InlineData("resource-mismatch")]
    public void FailedMutationsPreserveFormalBytes(string failure)
    {
        using var temp = new WorkspaceStoreTemp(); Initialize(temp); var before = Hash(temp.Path);
        var store = new TournamentWorkspaceStore(new FailingFiles(failure));
        var ex = Assert.Throws<WorkspaceStoreException>(() => store.Mutate(temp.Path, 0, w => failure switch
        {
            "semantic" => w with { Name = "" },
            "null" => null!,
            "identity" => w with { Id = Guid.NewGuid() },
            "resource-mismatch" => w with { Schedule = w.Schedule! with { Resources = w.Resources! with { RefereeCount = 99 } } },
            _ => w with { Name = "changed" }
        }));
        Assert.False(ex.Committed); Assert.Equal(before, Hash(temp.Path));
        Assert.Equal(0, new TournamentWorkspaceStore().Read(temp.Path).Revision);
        Assert.NotNull(ex.CandidatePath); Assert.True(File.Exists(ex.CandidatePath));
        if (failure == "replace") { Assert.NotNull(ex.BackupPath); Assert.Equal(before, Hash(ex.BackupPath)); }
        else Assert.Null(ex.BackupPath);
    }

    [Fact]
    public void StaleRevisionAndDuplicateCreateDoNotTouchArchive()
    {
        using var temp = new WorkspaceStoreTemp(); var initial = Initialize(temp); var before = Hash(temp.Path); var store = new TournamentWorkspaceStore();
        var stale = Assert.Throws<WorkspaceStoreException>(() => store.Mutate(temp.Path, 9, _ => throw new Exception("must not run")));
        Assert.Equal("RevisionConflict", stale.Code); Assert.Null(stale.CandidatePath);
        Assert.Equal("DestinationExists", Assert.Throws<WorkspaceStoreException>(() => store.Create(temp.Path, initial)).Code);
        Assert.Equal(before, Hash(temp.Path));
    }

    [Theory]
    [InlineData("PRAGMA foreign_keys=OFF; DELETE FROM workspace")]
    [InlineData("DELETE FROM project_rosters")]
    [InlineData("DELETE FROM match_results")]
    [InlineData("UPDATE project_rosters SET json='{}'")]
    [InlineData("UPDATE match_graphs SET json=replace(json,'participant','unknown-source')")]
    [InlineData("UPDATE projects SET json='not-json'")]
    [InlineData("CREATE TABLE unknown_rows(id TEXT)")]
    public void MissingAndCorruptRowsAreRejectedWithoutModification(string sql)
    {
        using var temp = new WorkspaceStoreTemp(); Initialize(temp); WorkspaceStoreTemp.Sql(temp.Path, sql); var before = Hash(temp.Path);
        Assert.Equal("InvalidWorkspace", Assert.Throws<WorkspaceStoreException>(() => new TournamentWorkspaceStore().Read(temp.Path)).Code);
        Assert.Equal(before, Hash(temp.Path));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(501, false)]
    public void VersionCheckedBeforeSchemaParsing(int version, bool legacy)
    {
        using var temp = new WorkspaceStoreTemp(); WorkspaceStoreTemp.Sql(temp.Path, $"PRAGMA user_version={version}; CREATE TABLE metadata(schema_version INTEGER); INSERT INTO metadata VALUES(1)"); var before = Hash(temp.Path);
        var ex = Assert.Throws<WorkspaceStoreException>(() => new TournamentWorkspaceStore().Read(temp.Path));
        Assert.Equal("UnsupportedWorkspaceVersion", ex.Code);
        if (legacy) { Assert.Contains("v4.6.0", ex.Message); Assert.Contains("/releases/tag/v4.6.0", ex.Message); }
        else Assert.DoesNotContain("v4.6.0", ex.Message);
        Assert.Equal(before, Hash(temp.Path));
    }

    [Fact]
    public async Task ConcurrentWritersWithSameRevisionCannotBothCommit()
    {
        using var temp = new WorkspaceStoreTemp(); Initialize(temp);
        using var barrier = new Barrier(2);
        Task<string> Writer(string name) => Task.Run(() => { barrier.SignalAndWait(); try { new TournamentWorkspaceStore().Mutate(temp.Path, 0, w => w with { Name = name }); return "ok"; } catch (WorkspaceStoreException ex) { return ex.Code; } });
        var results = await Task.WhenAll(Writer("one"), Writer("two"));
        Assert.Single(results, r => r == "ok"); Assert.Single(results, r => r == "RevisionConflict"); Assert.Equal(1, new TournamentWorkspaceStore().Read(temp.Path).Revision);
    }

    [Fact]
    public void PostCommitReadFailureReportsCommittedAndRecoverableBackup()
    {
        using var temp = new WorkspaceStoreTemp(); Initialize(temp); var before = Hash(temp.Path);
        var files = new FailingFiles("after-commit"); var store = new FailingReadStore(files, temp.Path);
        var ex = Assert.Throws<WorkspaceStoreException>(() => store.Mutate(temp.Path, 0, w => w with { Name = "saved" }));
        Assert.True(ex.Committed); Assert.Equal("CommittedReadFailed", ex.Code); Assert.Null(ex.CandidatePath);
        Assert.Equal(before, Hash(ex.BackupPath!)); Assert.Equal("saved", new TournamentWorkspaceStore().Read(temp.Path).Name);
    }

    [Fact]
    public void ExplicitRecoveryPreservesCorruptFileAndValidatesBackup()
    {
        using var temp = new WorkspaceStoreTemp(); Initialize(temp); var store = new TournamentWorkspaceStore(); var backup = store.CreateBackup(temp.Path);
        var oldSession = store.Mutate(temp.Path, 0, w => w with { Name = "before corruption" }).Workspace;
        File.WriteAllText(temp.Path, "corrupt sqlite"); var before = Hash(temp.Path);
        Assert.Equal("InvalidWorkspace", Assert.Throws<WorkspaceStoreException>(() => store.Read(temp.Path)).Code);
        Assert.Throws<WorkspaceStoreException>(() => store.RestoreBackup(temp.Path, backup)); Assert.Equal(before, Hash(temp.Path));
        var recovered = store.RecoverFromBackup(temp.Path, backup); Assert.True(recovered.Revision > oldSession.Revision);
        Assert.Equal("RevisionConflict", Assert.Throws<WorkspaceStoreException>(() => store.Mutate(temp.Path, oldSession.Revision, w => w)).Code);
        Assert.Contains(Directory.GetFiles(temp.DirectoryPath, "*.backup.szbd"), p => Hash(p) == before);
        Assert.Equal("RecoveryNotRequired", Assert.Throws<WorkspaceStoreException>(() => store.RecoverFromBackup(temp.Path, backup)).Code);
    }

    [Fact]
    public void ForeignOrInvalidBackupCannotReplaceFormalFile()
    {
        using var temp = new WorkspaceStoreTemp(); using var other = new WorkspaceStoreTemp(); Initialize(temp); Initialize(other); var before = Hash(temp.Path); var store = new TournamentWorkspaceStore();
        Assert.Equal("WorkspaceIdentityMismatch", Assert.Throws<WorkspaceStoreException>(() => store.RestoreBackup(temp.Path, other.Path)).Code);
        File.WriteAllText(other.Path, "invalid"); Assert.Throws<WorkspaceStoreException>(() => store.RestoreBackup(temp.Path, other.Path)); Assert.Equal(before, Hash(temp.Path));
    }

    [Fact]
    public void ArchiveWithWalJournalIsRejectedBeforeCopying()
    {
        using var temp = new WorkspaceStoreTemp(); Initialize(temp); WorkspaceStoreTemp.Sql(temp.Path, "PRAGMA journal_mode=WAL");
        var before = Hash(temp.Path);
        Assert.Equal("InvalidWorkspace", Assert.Throws<WorkspaceStoreException>(() => new TournamentWorkspaceStore().Mutate(temp.Path, 0, w => w)).Code);
        Assert.Equal(before, Hash(temp.Path));
    }

    private sealed class FailingFiles(string failure) : WorkspaceFileOperations
    {
        public bool Published { get; private set; }
        public override void Copy(string source, string destination)
        {
            if (failure == "backup" && destination.EndsWith(".backup.szbd")) throw new IOException("Injected backup failure");
            base.Copy(source, destination);
            if (failure == "transaction" && destination.EndsWith(".candidate.szbd")) WorkspaceStoreTemp.Sql(destination, "CREATE TRIGGER fail_write BEFORE DELETE ON workspace BEGIN SELECT RAISE(ABORT,'injected transaction failure'); END");
        }
        public override void Publish(string candidate, string destination, bool overwrite)
        { if (failure == "replace") throw new IOException("Injected replace failure"); base.Publish(candidate, destination, overwrite); Published = true; }
    }
    private sealed class FailingReadStore(FailingFiles files, string formal) : TournamentWorkspaceStore(files)
    {
        public override TournamentWorkspace Read(string path)
        { if (files.Published && path == formal) throw new IOException("Injected post-commit read failure"); return base.Read(path); }
    }
}
