using System.ComponentModel;
using BadmintonDraw.Desktop.ViewModels;

namespace BadmintonDraw.Desktop.Tests;

internal static class ScheduleBoardWindowTestSync
{
    internal static async Task WaitForInitializationAsync(AppShellViewModel shell, ScheduleBoardPageViewModel board)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void CompleteWhenReady()
        {
            if (!shell.IsBusy && !board.NeedsPositionRefresh) ready.TrySetResult();
        }
        void Changed(object? sender, PropertyChangedEventArgs args) => CompleteWhenReady();
        shell.PropertyChanged += Changed;
        board.PropertyChanged += Changed;
        try
        {
            // Visual attachment may already own the initial load; a duplicate InitializeAsync
            // returns immediately, so await the observable shell/baseline readiness instead.
            await board.InitializeAsync();
            CompleteWhenReady();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            shell.PropertyChanged -= Changed;
            board.PropertyChanged -= Changed;
        }
    }
}
