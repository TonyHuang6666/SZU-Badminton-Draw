using System.Diagnostics;
using System.Windows.Input;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed class AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null,
    Action<Exception>? onError = null) : ICommand
{
    private int executing;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => Volatile.Read(ref executing) == 0 && (canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter) => await ExecuteAsync();
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null) || Interlocked.CompareExchange(ref executing, 1, 0) != 0) return;
        NotifyCanExecuteChanged();
        try { await execute(); }
        catch (Exception exception)
        {
            if (onError is not null) onError(exception);
            else Trace.TraceError("异步操作失败：{0}", exception);
        }
        finally { Interlocked.Exchange(ref executing, 0); NotifyCanExecuteChanged(); }
    }
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
