using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Persistence;
using BadmintonDraw.Tests;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class WorkspaceResultImportFaultTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrecommitFailuresRetainActualCandidateBackupPathsAndDraftsWithoutReusingAuthorization(bool partial)
    {
        using var f = new ResultImportUiFixture(1, 2); var file = f.Data.Export(); await f.Preview(file);
        f.ViewModel.CorrectionReason = "original reason";
        var before = f.Shell.CurrentSession!; var hash = WorkspaceResultImportFacadeFixture.Hash(before.WorkspacePath);
        var oldBackups = Directory.GetFiles(f.Data.DirectoryPath, "*.backup.szbd");
        f.Files.Armed = true; f.Files.FailPublish = !partial; f.Files.PartialBackup = partial;
        await f.Accept();
        Assert.Same(before, f.Shell.CurrentSession); Assert.Equal(hash, WorkspaceResultImportFacadeFixture.Hash(before.WorkspacePath));
        Assert.False(f.ViewModel.HasCurrentPreview); Assert.False(f.ViewModel.Confirmed); Assert.Equal(1, f.Data.Store.Mutations);
        Assert.Equal(file, Assert.Single(f.ViewModel.SelectedPaths)); Assert.Equal("original reason", f.ViewModel.CorrectionReason);
        var candidate = Assert.Single(Directory.GetFiles(f.Data.DirectoryPath, "*.candidate.szbd"));
        var backup = Assert.Single(Directory.GetFiles(f.Data.DirectoryPath, "*.backup.szbd").Except(oldBackups));
        Assert.Contains(candidate, f.ViewModel.OutcomeDetails); Assert.Contains(backup, f.ViewModel.OutcomeDetails);
        Assert.DoesNotContain("导入已保存", f.ViewModel.StateMessage);
        if (partial) { Assert.Contains("完整性未验证", f.ViewModel.StateMessage); Assert.Equal("partial backup", File.ReadAllText(backup)); }
        else Assert.Equal(hash, WorkspaceResultImportFacadeFixture.Hash(backup));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CommittedReadFailureRemainsVisibleWithItsOwnImmediateOrPostedPublishedSession(bool persistent, bool posted)
    {
        var posts = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        using var f = new ResultImportUiFixture(1, 2, post: posted ? a => posts.Enqueue(a) : a => a());
        await f.Preview(f.Data.Export()); var original = f.Shell.CurrentSession!;
        var hash = WorkspaceResultImportFacadeFixture.Hash(original.WorkspacePath);
        var oldBackups = Directory.GetFiles(f.Data.DirectoryPath, "*.backup.szbd");
        f.Files.Armed = true; f.Store.FailCommittedRead = persistent; f.Store.CommittedReadFailuresRemaining = 1;
        await f.Accept(); while (posts.TryDequeue(out var callback)) callback();
        Assert.Contains("CommittedReadFailed", f.ViewModel.StateMessage); Assert.Contains("文件已保存", f.ViewModel.StateMessage);
        Assert.DoesNotContain("本次没有提交", f.ViewModel.StateMessage); Assert.NotSame(original, f.Shell.CurrentSession);
        Assert.Equal(persistent, f.Shell.CurrentSession!.RequiresReload);
        Assert.Equal(!persistent, f.ViewModel.PreviewCommand.CanExecute(null));
        var durable = new TournamentWorkspaceStore().Read(original.WorkspacePath);
        Assert.Equal(original.Workspace.Revision + 1, durable.Revision); Assert.Equal(TournamentStage.Completed, durable.Stage);
        Assert.Single(durable.ImportLogs); Assert.Single(durable.Results); Assert.Single(durable.ProcessedDays);
        var backup = Assert.Single(Directory.GetFiles(f.Data.DirectoryPath, "*.backup.szbd").Except(oldBackups));
        Assert.Equal(hash, WorkspaceResultImportFacadeFixture.Hash(backup)); Assert.Contains(backup, f.ViewModel.OutcomeDetails);
        Assert.False(f.ViewModel.HasCurrentPreview); Assert.False(f.ViewModel.ConfirmImportCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("reason")]
    [InlineData("allow")]
    [InlineData("consent")]
    public async Task ChangingInputsWhileAcceptedMutationIsBlockedDoesNotCancelOrMisreportTheDurableCommit(string change)
    {
        using var f = new ResultImportUiFixture(1, 2); await f.Preview(f.Data.Export()); var original = f.Shell.CurrentSession!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        f.Data.Store.BeforeMutation = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); };
        var pending = f.Accept(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            switch (change)
            {
                case "clear": f.ViewModel.ClearFilesCommand.Execute(null); Assert.Empty(f.ViewModel.SelectedPaths); break;
                case "reason": f.ViewModel.CorrectionReason = "new input"; break;
                case "allow": f.ViewModel.AllowCorrections = true; break;
                case "consent": f.ViewModel.Confirmed = true; break;
            }
            Assert.Contains("不会因此回滚", f.ViewModel.StateMessage);
        }
        finally { release.Set(); }
        await pending;
        Assert.Equal(original.Workspace.Revision + 1, f.Shell.CurrentSession!.Workspace.Revision); Assert.Single(f.Shell.CurrentSession.Workspace.Results);
        Assert.False(f.ViewModel.HasCurrentPreview); Assert.DoesNotContain("导入已保存", f.ViewModel.StateMessage);
        Assert.DoesNotContain("等待", f.ViewModel.StateMessage); Assert.DoesNotContain("正在", f.ViewModel.StateMessage);
    }
}
