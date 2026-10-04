using BadmintonDraw.Persistence;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceBackupFailureTests
{
    [Theory]
    [InlineData("restore", false)]
    [InlineData("restore", true)]
    [InlineData("recovery", false)]
    [InlineData("recovery", true)]
    [InlineData("mutate", false)]
    [InlineData("mutate", true)]
    [InlineData("manual", false)]
    [InlineData("manual", true)]
    public void FailedCopyReportsItsSurvivingUnverifiedBackupWithoutPublishing(string operation, bool partial)
    {
        using var temp = new WorkspaceStoreTemp(); var normal = new TournamentWorkspaceStore();
        normal.Create(temp.Path, WorkspaceOperationsContractsTests.Fixture());
        var selectedBackup = normal.CreateBackup(temp.Path);
        if (operation == "recovery") File.WriteAllText(temp.Path, "damaged formal workspace bytes");
        var original = File.ReadAllBytes(temp.Path);
        var formalHash = WorkspaceRecoveryContractsTests.Hash(temp.Path);
        var files = new CopyThenThrowFiles(partial); var store = new TournamentWorkspaceStore(files);

        var error = Assert.Throws<WorkspaceStoreException>(() =>
        {
            switch (operation)
            {
                case "restore":
                    store.RestoreBackup(temp.Path, WorkspaceRecoveryContractsTests.Request(store.InspectBackup(selectedBackup), 0));
                    break;
                case "recovery":
                    store.RecoverFromBackup(temp.Path, WorkspaceRecoveryContractsTests.Recovery(store.InspectRecovery(temp.Path, selectedBackup)));
                    break;
                case "mutate": store.Mutate(temp.Path, 0, workspace => workspace with { Name = "must not publish" }); break;
                case "manual": store.CreateBackup(temp.Path); break;
            }
        });

        Assert.False(error.Committed); Assert.False(files.Published);
        Assert.Equal(formalHash, WorkspaceRecoveryContractsTests.Hash(temp.Path));
        Assert.NotNull(error.BackupPath); Assert.Equal(files.SurvivingBackup, error.BackupPath);
        Assert.True(File.Exists(error.BackupPath));
        Assert.Equal(partial ? original.Take(8).ToArray() : original, File.ReadAllBytes(error.BackupPath));
        Assert.Contains("完整性未验证", error.Message);
        if (partial)
            Assert.Equal("InvalidWorkspace", Assert.Throws<WorkspaceStoreException>(() => normal.Read(error.BackupPath)).Code);
        if (operation == "manual")
        {
            Assert.Equal("BackupFailed", error.Code); Assert.Null(error.CandidatePath);
        }
        else
        {
            Assert.Equal("WorkspaceWriteFailed", error.Code);
            Assert.NotNull(error.CandidatePath); Assert.True(File.Exists(error.CandidatePath));
            var inner = Assert.IsType<WorkspaceStoreException>(error.InnerException);
            Assert.Equal("BackupFailed", inner.Code); Assert.Equal(error.BackupPath, inner.BackupPath);
        }
    }

    private sealed class CopyThenThrowFiles(bool partial) : WorkspaceFileOperations
    {
        public string? SurvivingBackup { get; private set; }
        public bool Published { get; private set; }
        public override void Copy(string source, string destination)
        {
            if (!destination.EndsWith(".backup.szbd")) { base.Copy(source, destination); return; }
            if (partial) File.WriteAllBytes(destination, File.ReadAllBytes(source).Take(8).ToArray());
            else base.Copy(source, destination);
            SurvivingBackup = destination;
            throw new IOException(partial ? "injected partial write failure" : "injected failure after full durable copy");
        }
        public override void Publish(string candidate, string destination, bool overwrite)
        { Published = true; base.Publish(candidate, destination, overwrite); }
    }
}
