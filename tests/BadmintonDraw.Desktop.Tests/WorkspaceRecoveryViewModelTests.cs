using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Persistence;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class WorkspaceRecoveryViewModelTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("other")]
    [InlineData("reload")]
    public async Task CorruptRecoveryUsesExplicitTargetWithoutRequiringAHealthySession(string context)
    {
        using var fixture = new RecoveryUiFixture(); var shell = fixture.Shell;
        if (context != "none") await fixture.CreateCurrent();
        if (context == "reload")
        {
            fixture.Store.FailCommittedRead = true;
            await shell.RunWorkspaceCommandAsync(shell.CurrentSession!, (w, r) => w.UpgradeToFullTournament(r), "upgraded");
            Assert.True(shell.CurrentSession!.RequiresReload);
            fixture.Store.FailCommittedRead = false;
        }
        var (target, backup) = fixture.CorruptTarget(); var oldSession = shell.CurrentSession;
        Assert.Same(shell.OpenRecoveryCommand, shell.StartPage.OpenRecoveryCommand);
        shell.StartPage.OpenRecoveryCommand.Execute(null); var vm = shell.Recovery;
        Assert.True(vm.IsOpen); Assert.False(shell.CanNavigate(WorkspaceRoute.Operations));
        vm.RecoverCorruptTarget = true; vm.TargetPath = target; vm.BackupPath = backup; vm.Reason = "核对公开抽签备份后恢复";
        var hash = RecoveryUiFixture.Hash(target);
        await vm.PreviewCommand.ExecuteAsync();
        Assert.Same(oldSession, shell.CurrentSession); Assert.Equal(hash, RecoveryUiFixture.Hash(target));
        Assert.Contains("无法验证原赛事身份", vm.PreviewDetails);
        Assert.Contains(backup, vm.PreviewDetails); Assert.Contains(target, vm.PreviewDetails);
        Assert.False(vm.RestoreCommand.CanExecute(null));
        vm.Confirmed = true; await vm.RestoreCommand.ExecuteAsync();
        Assert.Null(shell.LastError); Assert.Equal(target, shell.WorkspacePath);
        Assert.Equal("damaged", shell.CurrentSession!.Workspace.Name);
        Assert.Equal(target, shell.StartPage.RecentWorkspaces[0].Path);
        Assert.False(vm.Confirmed); Assert.False(vm.HasPreview);
        Assert.Single(new TournamentWorkspaceStore().Read(target).AuditEvents, a => a.Action == "WorkspaceRecovered");
    }

    [Theory]
    [InlineData("backup")]
    [InlineData("target")]
    [InlineData("mode")]
    [InlineData("reopen")]
    [InlineData("audit")]
    [InlineData("close")]
    public async Task HealthyPreviewRequiresReasonAndFreshExplicitConfirmationAndPreservesDrafts(string change)
    {
        using var fixture = new RecoveryUiFixture(); var backup = await fixture.CreateCurrent(); var vm = fixture.Shell.Recovery;
        vm.Open(); vm.BackupPath = backup;
        await vm.PreviewCommand.ExecuteAsync();
        Assert.True(vm.HasPreview); Assert.Contains(fixture.Workflow.CurrentSession!.Workspace.Id.ToString(), vm.PreviewDetails);
        Assert.Contains(RecoveryUiFixture.Hash(backup), vm.PreviewDetails);
        vm.Confirmed = true; Assert.False(vm.RestoreCommand.CanExecute(null));
        vm.Reason = "恢复原因草稿"; Assert.False(vm.Confirmed);
        vm.Confirmed = true; Assert.True(vm.RestoreCommand.CanExecute(null));
        switch (change)
        {
            case "backup": vm.BackupPath = backup + "other"; break;
            case "target": vm.TargetPath = fixture.PathFor("other.szbd"); break;
            case "mode": vm.RecoverCorruptTarget = true; break;
            case "reopen": await fixture.Shell.ReloadCommand.ExecuteAsync(); break;
            case "audit": fixture.Workflow.CreateBackup(fixture.Workflow.CurrentSession!.Workspace.Revision); break;
            case "close": vm.Close(); vm.Open(); break;
        }
        Assert.False(vm.HasPreview); Assert.False(vm.Confirmed); Assert.False(vm.RestoreCommand.CanExecute(null));
        Assert.Equal("恢复原因草稿", vm.Reason);
        Assert.Equal(change == "backup" ? backup + "other" : backup, vm.BackupPath);
    }

    [Fact]
    public async Task HealthyDraftBackupAndRestoreUseActualFacadeAndSeparateManualAndAuditPaths()
    {
        using var fixture = new RecoveryUiFixture(); await fixture.CreateCurrent(); var shell = fixture.Shell; var vm = shell.Recovery;
        vm.Open(); await vm.CreateBackupCommand.ExecuteAsync();
        Assert.Null(shell.LastError); Assert.NotNull(vm.LastValidatedBackup);
        Assert.Equal(vm.LastValidatedBackup.FullPath, vm.LastManualBackupPath);
        Assert.Contains(vm.LastManualBackupPath!, vm.OutputDetails);
        Assert.Contains("自动审计备份", vm.OutputDetails);
        Assert.Equal(0, new TournamentWorkspaceStore().Read(vm.LastManualBackupPath!).Revision);
        Assert.Equal(1, shell.CurrentSession!.Workspace.Revision);
        vm.BackupPath = vm.LastManualBackupPath!; vm.Reason = "恢复至备份";
        await vm.PreviewCommand.ExecuteAsync(); vm.Confirmed = true; await vm.RestoreCommand.ExecuteAsync();
        Assert.Null(shell.LastError); Assert.Equal(2, shell.CurrentSession.Workspace.Revision);
        Assert.Single(shell.CurrentSession.Workspace.AuditEvents, a => a.Action == "WorkspaceRestored");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOpenPrefillsMalformedPathFromBothEntryPointsWithoutAdoptingIt(bool picker)
    {
        string? selected = null;
        using var fixture = new RecoveryUiFixture(openPicker: () => Task.FromResult(selected));
        var (target, _) = fixture.CorruptTarget(); selected = target;
        if (picker) await fixture.Shell.OpenCommand.ExecuteAsync(); else await fixture.Shell.OpenWorkspaceAsync(target);
        Assert.Null(fixture.Shell.CurrentSession); Assert.Equal("InvalidWorkspace", fixture.Shell.LastError!.Code);
        Assert.Equal(target, fixture.Shell.Recovery.TargetPath); Assert.True(fixture.Shell.Recovery.RecoverCorruptTarget);
        Assert.False(fixture.Shell.Recovery.Confirmed);
    }

    [Fact]
    public async Task CancelledPickersPreserveInputsWithoutErrors()
    {
        using var fixture = new RecoveryUiFixture(); var vm = fixture.Shell.Recovery;
        vm.Open(); vm.TargetPath = "target draft"; vm.BackupPath = "backup draft"; vm.Reason = "reason draft";
        await vm.PickTargetCommand.ExecuteAsync(); await vm.PickBackupCommand.ExecuteAsync();
        Assert.Equal("target draft", vm.TargetPath); Assert.Equal("backup draft", vm.BackupPath); Assert.Equal("reason draft", vm.Reason);
        Assert.Null(fixture.Shell.LastError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedBackupBytesAreRejectedByOpaqueApplyAndRequireNewPreview(bool corrupt)
    {
        using var fixture = new RecoveryUiFixture(); var shell = fixture.Shell; var vm = shell.Recovery;
        var (target, backup) = corrupt ? fixture.CorruptTarget() : (fixture.Request("current").WorkspacePath, await fixture.CreateCurrent());
        vm.Open(); vm.RecoverCorruptTarget = corrupt; vm.TargetPath = target; vm.BackupPath = backup; vm.Reason = "核对";
        await vm.PreviewCommand.ExecuteAsync(); vm.Confirmed = true;
        var hash = RecoveryUiFixture.Hash(target); File.WriteAllText(backup, "changed backup bytes");
        await vm.RestoreCommand.ExecuteAsync();
        Assert.NotNull(shell.LastError); Assert.False(shell.LastError.Committed); Assert.Equal(hash, RecoveryUiFixture.Hash(target));
        Assert.False(vm.HasPreview); Assert.False(vm.Confirmed); Assert.Equal(backup, vm.BackupPath); Assert.Equal("核对", vm.Reason);
    }
}
