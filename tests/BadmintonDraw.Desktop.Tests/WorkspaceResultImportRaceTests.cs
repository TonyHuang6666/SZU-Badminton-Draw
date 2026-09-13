using System.Collections.Concurrent;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Tests;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class WorkspaceResultImportRaceTests
{
    public static IEnumerable<object[]> StaleCases => new[] { "clear", "reason", "allow", "consent", "reopen", "audit", "foreign", "dispose" }
        .SelectMany(change => new[] { new object[] { change, false }, new object[] { change, true } });

    [Theory, MemberData(nameof(StaleCases))]
    public async Task LatePickerSuccessAndFailureCannotOverwriteChangedInputsOrFeedback(string change, bool throws)
    {
        var selected = new List<string> { "original.xlsx" }; var result = new TaskCompletionSource<IReadOnlyList<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var f = new ResultImportUiFixture(1, 2, picker: () => ++calls == 1 ? Task.FromResult<IReadOnlyList<string>?>(selected) : result.Task);
        await f.ViewModel.PickFilesCommand.ExecuteAsync(); selected[0] = "caller changed";
        Assert.Equal("original.xlsx", Assert.Single(f.ViewModel.SelectedPaths));
        var pending = f.ViewModel.PickFilesCommand.ExecuteAsync(); Assert.False(pending.IsCompleted);
        Change(f, change); var expected = Feedback(f);
        if (throws) result.SetException(new IOException("late picker failure")); else result.SetResult(new[] { "late.xlsx" });
        await pending; Assert.Equal(expected, Feedback(f)); Assert.False(f.ViewModel.HasCurrentPreview);
    }

    [Theory, MemberData(nameof(StaleCases))]
    public async Task LateRealPreviewSuccessAndFailureUseExclusiveBusyAndCannotLeakStaleFeedback(string change, bool throws)
    {
        using var f = new ResultImportUiFixture(1, 2); await f.Choose(f.Data.Export());
        if (throws) f.Store.BeforeRead = _ => throw new IOException("late source read failure");
        var deferred = new DeferredUiContext(); var pending = deferred.Start(() => f.ViewModel.PreviewCommand.ExecuteAsync());
        await deferred.FirstPost.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(f.Shell.IsBusy); Assert.True(f.ViewModel.IsWorking);
        Assert.False(f.Shell.NewCommand.CanExecute(null)); Assert.False(f.Shell.OpenCommand.CanExecute(null));
        Assert.False(await f.Shell.OpenWorkspaceAsync(f.Shell.WorkspacePath));
        var competing = await f.Shell.RunWorkspaceQueryAsync(f.Shell.CurrentSession!, (_, _) => "never");
        Assert.False(competing.Succeeded); Assert.Equal("desktop.query-busy", competing.Error!.Code);
        f.Store.BeforeRead = null; Change(f, change); var expected = Feedback(f);
        await deferred.Complete(pending); Assert.Equal(expected, Feedback(f)); Assert.False(f.ViewModel.HasCurrentPreview);
        Assert.False(f.Shell.IsBusy); Assert.False(f.ViewModel.IsWorking);
        if (change != "dispose") Assert.DoesNotContain("正在", f.ViewModel.StateMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StillCurrentErrorsAreVisibleAndDoNotDiscardDraftSelection(bool picker)
    {
        using var f = new ResultImportUiFixture(1, 2, picker: picker ? () => Task.FromException<IReadOnlyList<string>?>(new IOException("current picker error")) : null);
        f.ViewModel.CorrectionReason = "reason draft";
        if (picker) await f.ViewModel.PickFilesCommand.ExecuteAsync();
        else { await f.Choose(f.Data.PathFor("missing.xlsx")); await f.ViewModel.PreviewCommand.ExecuteAsync(); }
        Assert.Contains(picker ? "current picker error" : "results.read", f.ViewModel.StateMessage);
        Assert.Equal("reason draft", f.ViewModel.CorrectionReason);
        if (!picker) Assert.Contains("missing.xlsx", Assert.Single(f.ViewModel.SelectedPaths));
        Assert.False(f.ViewModel.HasCurrentPreview);
    }

    [Fact]
    public async Task SameReferenceRefreshAndPickerCancellationKeepTheExistingPreview()
    {
        using var f = new ResultImportUiFixture(1, 2); await f.Preview(f.Data.Export()); var vm = f.ViewModel;
        vm.Confirmed = true; var details = vm.SourceDetails; vm.RefreshSession(f.Shell.CurrentSession!);
        Assert.True(vm.HasCurrentPreview); Assert.True(vm.Confirmed);
        f.NextPick = null; await vm.PickFilesCommand.ExecuteAsync();
        Assert.True(vm.HasCurrentPreview); Assert.True(vm.Confirmed); Assert.Equal(details, vm.SourceDetails);
    }

    [Theory]
    [InlineData("clear", "success")]
    [InlineData("reason", "success")]
    [InlineData("dispose", "success")]
    [InlineData("reopen", "success")]
    [InlineData("foreign", "success")]
    [InlineData("clear", "precommit")]
    [InlineData("reason", "precommit")]
    [InlineData("dispose", "precommit")]
    [InlineData("reopen", "precommit")]
    [InlineData("foreign", "precommit")]
    [InlineData("clear", "committed")]
    [InlineData("reason", "committed")]
    [InlineData("dispose", "committed")]
    [InlineData("reopen", "committed")]
    [InlineData("foreign", "committed")]
    public async Task DelayedApplyCannotOverwriteNewerFeedbackButItsRealPublicationRemainsAuthoritative(string change, string failure)
    {
        using var f = new ResultImportUiFixture(1, 2); await f.Preview(f.Data.Export());
        var original = f.Shell.CurrentSession!; f.Files.Armed = true;
        f.Files.FailPublish = failure == "precommit"; f.Store.FailCommittedRead = failure == "committed";
        f.ViewModel.Confirmed = true; var deferred = new DeferredUiContext();
        var pending = deferred.Start(() => f.ViewModel.ConfirmImportCommand.ExecuteAsync());
        Assert.False(f.ViewModel.HasCurrentPreview); Assert.False(f.ViewModel.Confirmed);
        await deferred.FirstPost.WaitAsync(TimeSpan.FromSeconds(10));
        f.Files.FailPublish = false; f.Store.FailCommittedRead = false;
        Change(f, change); var expected = Feedback(f);
        await deferred.Complete(pending);
        Assert.Equal(expected, Feedback(f)); Assert.False(f.ViewModel.HasCurrentPreview);
        if (change is "reopen" or "foreign") Assert.DoesNotContain("等待", f.ViewModel.StateMessage);
        var durable = new BadmintonDraw.Persistence.TournamentWorkspaceStore().Read(original.WorkspacePath);
        Assert.Equal(failure == "precommit" ? 0 : 1, durable.Results.Count);
        Assert.Equal(original.Workspace.Revision + (failure == "precommit" ? 0 : 1), durable.Revision);
        Assert.Same(f.Data.Workflow.CurrentSession, f.Shell.CurrentSession);
    }

    private static void Change(ResultImportUiFixture f, string change)
    {
        switch (change)
        {
            case "clear": f.ViewModel.ClearFilesCommand.Execute(null); break;
            case "reason": f.ViewModel.CorrectionReason = "new reason"; break;
            case "allow": f.ViewModel.AllowCorrections = true; break;
            case "consent": f.ViewModel.Confirmed = true; break;
            case "reopen": f.Data.Workflow.OpenWorkspace(f.Shell.WorkspacePath); break;
            case "audit": f.Data.Workflow.CreateBackup(f.Data.Revision); break;
            case "foreign": f.Data.Workflow.CreateWorkspace(new("foreign", TournamentKind.Individual, TournamentPurpose.FullTournament,
                [new(EventDiscipline.MenSingles, CompetitionMode.SinglesKnockout)], f.Data.PathFor("foreign.szbd"))); break;
            case "dispose": f.ViewModel.Dispose(); break;
        }
        f.Shell.ReportError(new IOException("newer visible feedback"));
    }
    private static string Feedback(ResultImportUiFixture f) => string.Join("|", f.Shell.Status, f.Shell.LastError?.Code,
        f.ViewModel.StateMessage, f.ViewModel.SourceDetails, f.ViewModel.OutcomeDetails, f.ViewModel.CorrectionReason,
        string.Join(";", f.ViewModel.SelectedPaths), f.ViewModel.Files.Count, f.ViewModel.Diagnostics.Count, f.ViewModel.Corrections.Count);
}

// Hold the real await continuation, not a fake workflow return or fabricated opaque preview.
internal sealed class DeferredUiContext : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> jobs = new();
    private readonly TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task FirstPost => first.Task;
    public override void Post(SendOrPostCallback callback, object? state) { jobs.Enqueue((callback, state)); first.TrySetResult(); }
    internal Task Start(Func<Task> action)
    { var old = Current; SetSynchronizationContext(this); try { return action(); } finally { SetSynchronizationContext(old); } }
    internal async Task Complete(Task operation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!operation.IsCompleted)
        {
            while (jobs.TryDequeue(out var job))
            {
                var old = Current; SetSynchronizationContext(this);
                try { job.Callback(job.State); } finally { SetSynchronizationContext(old); }
            }
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Deferred import continuation did not finish.");
            if (!operation.IsCompleted) await Task.Delay(5);
        }
        await operation;
    }
}
