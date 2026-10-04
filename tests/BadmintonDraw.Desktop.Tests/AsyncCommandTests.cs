using BadmintonDraw.Desktop.ViewModels;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class AsyncCommandTests
{
    [Fact]
    public async Task ConcurrentExecuteDoesNotRunTwiceAndRestoresAvailability()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var command = new AsyncCommand(async () => { calls++; await completion.Task; });
        var pending = command.ExecuteAsync();
        Assert.False(command.CanExecute(null));
        await command.ExecuteAsync();
        Assert.Equal(1, calls);
        completion.SetResult();
        await pending;
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task FailureIsReportedAndDoesNotLeaveCommandBusy()
    {
        Exception? reported = null;
        var command = new AsyncCommand(() => throw new IOException("picker failed"), onError: e => reported = e);
        await command.ExecuteAsync();
        Assert.IsType<IOException>(reported);
        Assert.True(command.CanExecute(null));
    }
}
