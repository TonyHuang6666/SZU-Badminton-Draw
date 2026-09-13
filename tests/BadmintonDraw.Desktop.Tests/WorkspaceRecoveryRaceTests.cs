using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Workflows.Tournaments;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class WorkspaceRecoveryRaceTests
{
    [Theory]
    [InlineData(true, "target")]
    [InlineData(true, "backup")]
    [InlineData(true, "close")]
    [InlineData(true, "session")]
    [InlineData(true, "dispose")]
    [InlineData(false, "target")]
    [InlineData(false, "backup")]
    [InlineData(false, "close")]
    [InlineData(false, "session")]
    [InlineData(false, "dispose")]
    public async Task LatePickerExceptionCannotOverwriteCurrentStatusErrorOrDrafts(bool target, string change)
    {
        var picked = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new RecoveryUiFixture(targetPicker: () => picked.Task, backupPicker: () => picked.Task);
        await fixture.CreateCurrent();
        var shell = fixture.Shell; var vm = shell.Recovery; var originalSession = shell.CurrentSession;
        vm.Open(); vm.TargetPath = "original target"; vm.BackupPath = "original backup"; vm.Reason = "current reason";
        shell.ReportError(new IOException("current visible error"));
        var pending = (target ? vm.PickTargetCommand : vm.PickBackupCommand).ExecuteAsync();
        Assert.False(pending.IsCompleted);
        switch (change)
        {
            case "target": vm.TargetPath = "new target"; break;
            case "backup": vm.BackupPath = "new backup"; break;
            case "close": vm.Close(); vm.Open(); break;
            case "session":
                Assert.True(await shell.CreateWorkspaceAsync(fixture.Request("replacement")));
                Assert.NotSame(originalSession, shell.CurrentSession);
                Assert.NotEqual(originalSession!.Workspace.Id, shell.CurrentSession!.Workspace.Id);
                Assert.Null(shell.LastError);
                break;
            case "dispose": shell.Dispose(); break;
        }
        var status = shell.Status; var error = shell.LastError;
        var drafts = (vm.TargetPath, vm.BackupPath, vm.Reason, vm.PreviewDetails, vm.OutputDetails, vm.IsOpen);
        picked.SetException(new IOException("obsolete picker failure")); await pending;
        Assert.Equal(status, shell.Status); Assert.Same(error, shell.LastError);
        Assert.Equal(drafts, (vm.TargetPath, vm.BackupPath, vm.Reason, vm.PreviewDetails, vm.OutputDetails, vm.IsOpen));
        Assert.False(vm.HasPreview); Assert.False(vm.Confirmed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CurrentPickerExceptionIsStillReportedWithoutChangingDrafts(bool target)
    {
        var picked = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new RecoveryUiFixture(targetPicker: () => picked.Task, backupPicker: () => picked.Task);
        var shell = fixture.Shell; var vm = shell.Recovery;
        vm.Open(); vm.TargetPath = "current target"; vm.BackupPath = "current backup"; vm.Reason = "current reason";
        var drafts = (vm.TargetPath, vm.BackupPath, vm.Reason);
        var pending = (target ? vm.PickTargetCommand : vm.PickBackupCommand).ExecuteAsync();
        Assert.False(pending.IsCompleted);
        picked.SetException(new IOException("current picker failure")); await pending;
        Assert.Equal("desktop.operation-failed", shell.LastError!.Code);
        Assert.Contains("current picker failure", shell.Status);
        Assert.Equal(drafts, (vm.TargetPath, vm.BackupPath, vm.Reason));
        Assert.True(vm.PickTargetCommand.CanExecute(null)); Assert.True(vm.PickBackupCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("target")]
    [InlineData("backup")]
    [InlineData("session")]
    [InlineData("close")]
    [InlineData("dispose")]
    public async Task LatePickerCannotChangeNewInputsOrReopenAuthorization(string change)
    {
        var picked = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new RecoveryUiFixture(backupPicker: () => picked.Task); var vm = fixture.Shell.Recovery;
        vm.Open(); vm.BackupPath = "original backup";
        var task = vm.PickBackupCommand.ExecuteAsync();
        switch (change)
        {
            case "target": vm.TargetPath = "new target"; break;
            case "backup": vm.BackupPath = "new backup"; break;
            case "session": await fixture.CreateCurrent(); break;
            case "close": vm.Close(); vm.Open(); break;
            case "dispose": fixture.Shell.Dispose(); break;
        }
        picked.SetResult("late backup"); await task;
        Assert.Equal(change == "backup" ? "new backup" : "original backup", vm.BackupPath);
        Assert.False(vm.HasPreview); Assert.False(vm.Confirmed);
    }

    [Theory]
    [InlineData(false, "input")]
    [InlineData(false, "close")]
    [InlineData(false, "session")]
    [InlineData(false, "dispose")]
    [InlineData(true, "input")]
    [InlineData(true, "close")]
    [InlineData(true, "session")]
    [InlineData(true, "dispose")]
    public async Task LateQueryIsDiscardedAndUsesSharedExclusiveBusyGate(bool corrupt, string change)
    {
        using var fixture = new RecoveryUiFixture(); var shell = fixture.Shell; var vm = shell.Recovery;
        var (target, backup) = corrupt ? fixture.CorruptTarget() : (fixture.Request("current").WorkspacePath, await fixture.CreateCurrent());
        vm.Open(); vm.RecoverCorruptTarget = corrupt; vm.TargetPath = target; vm.BackupPath = backup; vm.Reason = "preserved reason";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); var thread = 0;
        fixture.Store.BeforeRead = path =>
        {
            if (!path.EndsWith("snapshot.szbd", StringComparison.Ordinal)) return;
            thread = Environment.CurrentManagedThreadId; entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };
        var pending = vm.PreviewCommand.ExecuteAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<WorkspaceCommandResult>? switching = null;
        try
        {
            Assert.True(thread != 0); Assert.True(shell.IsBusy); Assert.False(shell.OpenCommand.CanExecute(null));
            Assert.False(shell.NewCommand.CanExecute(null)); Assert.False(vm.PreviewCommand.CanExecute(null));
            Assert.False(await shell.OpenWorkspaceAsync(target));
            Assert.False((await shell.PreviewRecoveryAsync(shell.CurrentSession, target, backup)).Succeeded);
            switch (change)
            {
                case "input": vm.BackupPath = "new backup"; break;
                case "close": vm.Close(); vm.Open(); break;
                case "session": switching = Task.Run(() => fixture.Workflow.OpenWorkspace(backup)); break;
                case "dispose": shell.Dispose(); break;
            }
        }
        finally { release.Set(); }
        await pending;
        if (switching is not null) await switching;
        Assert.False(shell.IsBusy); Assert.False(vm.HasPreview); Assert.False(vm.Confirmed); Assert.Equal("preserved reason", vm.Reason);
    }

    [Fact]
    public async Task RecoveryRunnerRejectsCapturedNullAfterAnotherWorkspaceWasOpened()
    {
        using var fixture = new RecoveryUiFixture(); var (target, backup) = fixture.CorruptTarget();
        var preview = await fixture.Shell.PreviewRecoveryAsync(null, target, backup); Assert.True(preview.Succeeded);
        await fixture.CreateCurrent(); var hash = RecoveryUiFixture.Hash(target);
        Assert.False(await fixture.Shell.RecoverFromBackupAsync(null, preview.Value!, "obsolete context"));
        Assert.Equal("workspace.session-changed", fixture.Shell.LastError!.Code); Assert.Equal(hash, RecoveryUiFixture.Hash(target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryRunnersStillRejectUnusableSessions(bool reload)
    {
        using var fixture = new RecoveryUiFixture();
        if (reload)
        {
            await fixture.CreateCurrent(); fixture.Store.FailCommittedRead = true;
            await fixture.Shell.RunWorkspaceCommandAsync(fixture.Shell.CurrentSession!, (w, r) => w.UpgradeToFullTournament(r), "upgrade");
            Assert.True(fixture.Shell.CurrentSession!.RequiresReload);
        }
        var invoked = false; var session = fixture.Shell.CurrentSession;
        Assert.False(await fixture.Shell.RunWorkspaceCommandAsync(session!, (_, _) => { invoked = true; throw new Exception(); }, "never"));
        var query = await fixture.Shell.RunWorkspaceQueryAsync(session!, (_, _) => { invoked = true; return "never"; });
        Assert.False(query.Succeeded); Assert.False(invoked);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(501)]
    public async Task UnsupportedVersionIsNotAutomaticallyClassifiedAsCorrupt(int version)
    {
        using var fixture = new RecoveryUiFixture(); var path = fixture.PathFor("unsupported.szbd");
        using (var connection = new SqliteConnection("Data Source=" + path))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = $"PRAGMA user_version={version}"; command.ExecuteNonQuery();
        }
        Assert.False(await fixture.Shell.OpenWorkspaceAsync(path));
        Assert.Equal("UnsupportedWorkspaceVersion", fixture.Shell.LastError!.Code);
        Assert.Empty(fixture.Shell.Recovery.TargetPath); Assert.False(fixture.Shell.Recovery.RecoverCorruptTarget);
    }
}
