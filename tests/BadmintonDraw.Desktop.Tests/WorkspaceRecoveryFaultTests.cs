using BadmintonDraw.Persistence;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class WorkspaceRecoveryFaultTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualCopyRemainsUsableWhenAuditFailsWithoutHidingSeparatePaths(bool committed)
    {
        using var fixture = new RecoveryUiFixture(); await fixture.CreateCurrent(); var shell = fixture.Shell; var vm = shell.Recovery;
        var originalHash = RecoveryUiFixture.Hash(shell.WorkspacePath);
        fixture.Files.FailPublish = !committed; fixture.Store.FailCommittedRead = committed;
        vm.Open(); await vm.CreateBackupCommand.ExecuteAsync();
        Assert.NotNull(vm.LastValidatedBackup); Assert.NotNull(vm.LastManualBackupPath);
        Assert.Equal(originalHash, RecoveryUiFixture.Hash(vm.LastManualBackupPath));
        Assert.NotEqual(vm.LastManualBackupPath, shell.LastError!.BackupPath);
        Assert.True(File.Exists(shell.LastError.BackupPath));
        Assert.Contains(vm.LastManualBackupPath, vm.OutputDetails); Assert.Contains(shell.LastError.BackupPath!, vm.OutputDetails);
        Assert.Equal(committed, shell.LastError.Committed); Assert.Equal(committed, shell.CurrentSession!.RequiresReload);
        if (!committed)
        {
            Assert.Equal(originalHash, RecoveryUiFixture.Hash(shell.WorkspacePath));
            Assert.True(File.Exists(shell.LastError.CandidatePath)); Assert.Contains(shell.LastError.CandidatePath!, vm.OutputDetails);
        }
        else Assert.Contains("目标文件已保存", vm.OutputDetails);
        Assert.Equal(0, new TournamentWorkspaceStore().Read(vm.LastManualBackupPath).Revision);
    }

    [Fact]
    public async Task PartialManualCopyIsRetainedButNeverShownAsValidated()
    {
        using var fixture = new RecoveryUiFixture(); await fixture.CreateCurrent(); var vm = fixture.Shell.Recovery;
        var hash = RecoveryUiFixture.Hash(fixture.Shell.WorkspacePath); fixture.Files.PartialBackup = true;
        vm.Open(); await vm.CreateBackupCommand.ExecuteAsync();
        Assert.Null(vm.LastValidatedBackup); Assert.NotNull(vm.LastManualBackupPath);
        Assert.Equal("partial copy", File.ReadAllText(vm.LastManualBackupPath));
        Assert.Contains("完整性未验证", vm.OutputDetails); Assert.DoesNotContain("已验证备份", vm.OutputDetails);
        Assert.False(fixture.Shell.LastError!.Committed); Assert.Equal(hash, RecoveryUiFixture.Hash(fixture.Shell.WorkspacePath));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RestoreFailuresExposeActualCommitAndPreservedFilesWithoutFalseSuccess(bool corrupt, bool committed)
    {
        using var fixture = new RecoveryUiFixture(); var shell = fixture.Shell; var vm = shell.Recovery;
        var (target, backup) = corrupt ? fixture.CorruptTarget() : (fixture.Request("current").WorkspacePath, await fixture.CreateCurrent());
        vm.Open(); vm.RecoverCorruptTarget = corrupt; vm.TargetPath = target; vm.BackupPath = backup; vm.Reason = "核对后恢复";
        await vm.PreviewCommand.ExecuteAsync(); Assert.True(vm.HasPreview); vm.Confirmed = true;
        var hash = RecoveryUiFixture.Hash(target); fixture.Files.FailPublish = !committed; fixture.Store.FailCommittedRead = committed;
        await vm.RestoreCommand.ExecuteAsync();
        Assert.Equal(committed, shell.LastError!.Committed); Assert.Contains(target, vm.OutputDetails);
        Assert.Equal(hash, RecoveryUiFixture.Hash(shell.LastError.BackupPath!));
        Assert.Contains(shell.LastError.BackupPath!, vm.OutputDetails);
        Assert.False(vm.HasPreview); Assert.False(vm.Confirmed);
        if (committed)
        {
            Assert.Equal("CommittedReadFailed", shell.LastError.Code); Assert.True(shell.CurrentSession!.RequiresReload);
            Assert.Equal(target, shell.WorkspacePath); Assert.Contains("目标文件已保存", vm.OutputDetails);
            Assert.False(vm.CreateBackupCommand.CanExecute(null));
            Assert.DoesNotContain("已恢复并保存", vm.OutputDetails);
        }
        else
        {
            Assert.Equal(hash, RecoveryUiFixture.Hash(target)); Assert.Contains("未提交", vm.OutputDetails);
            Assert.Contains(shell.LastError.CandidatePath!, vm.OutputDetails);
        }
    }
}
