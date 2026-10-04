using BadmintonDraw.Desktop.ViewModels;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class WorkspaceQueryTests
{
    [Fact]
    public async Task ReadonlyQueryRunsOffCallerThreadWithoutSavingAndExclusiveQueryBlocksCommands()
    {
        using var fixture = new ScheduleUiFixture(); var before = fixture.Workflow.CurrentSession!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var task = fixture.Shell.RunWorkspaceQueryAsync(before, (_, revision) => { entered.SetResult(); release.Wait(TimeSpan.FromSeconds(5)); return revision.ToString(); });
        await entered.Task;
        Assert.False(task.IsCompleted); Assert.True(fixture.Shell.IsBusy); Assert.False(fixture.Shell.OpenCommand.CanExecute(null));
        release.Set(); var result = await task;
        Assert.True(result.Succeeded); Assert.Equal(before.Workspace.Revision.ToString(), result.Value);
        Assert.Same(before, fixture.Workflow.CurrentSession); Assert.False(fixture.Shell.IsBusy);
        Assert.DoesNotContain("已保存", fixture.Shell.Status);
    }
    [Fact]
    public async Task BackgroundQueryDoesNotDisableDragAndRejectsResultAfterSessionChange()
    {
        using var fixture = new ScheduleUiFixture(); var before = fixture.Workflow.CurrentSession!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var task = fixture.Shell.RunWorkspaceQueryAsync(before, (_, _) => { entered.SetResult(); release.Wait(TimeSpan.FromSeconds(5)); return "old preview"; }, background: true);
        await entered.Task; Assert.False(fixture.Shell.IsBusy);
        fixture.Workflow.OpenWorkspace(before.WorkspacePath);
        release.Set(); var result = await task;
        Assert.False(result.Succeeded); Assert.Null(result.Value); Assert.Equal("workspace.session-changed", result.Error!.Code);
    }
}
