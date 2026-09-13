using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Workflows.Tournaments;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class WorkspaceRecoveryRaceTests
{
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
