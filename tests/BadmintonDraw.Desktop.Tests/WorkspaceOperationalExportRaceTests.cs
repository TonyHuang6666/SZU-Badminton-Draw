using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Persistence;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class WorkspaceOperationalExportRaceTests
{
    public static IEnumerable<object[]> PickerCases => new[] { "output", "project", "day", "carry", "pdf", "overwrite", "consent", "reopen", "audit", "foreign", "dispose" }
        .SelectMany(change => new[] { new object[] { change, false }, new object[] { change, true } });
    [Theory, MemberData(nameof(PickerCases))]
    public async Task LatePickerSuccessAndExceptionCannotOverrideNewScopeOrFeedback(string change, bool throws)
    {
        var returned = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var f = new OperationsUiFixture(outputPicker: () => returned.Task); var vm = f.Page.Materials; vm.OutputDirectory = "original";
        var pending = vm.PickOutputCommand.ExecuteAsync(); Assert.False(pending.IsCompleted);
        Change(f, vm, change); var expected = Feedback(f, vm);
        if (throws) returned.SetException(new IOException("late picker failure")); else returned.SetResult("late-output");
        await pending; Assert.Equal(expected, Feedback(f, vm)); Assert.Null(vm.Outcome); Assert.False(vm.IsWorking);
    }
    public static IEnumerable<object[]> ExportCases => new[] { "output", "day", "reopen", "audit", "foreign", "dispose" }
        .SelectMany(change => new[] { new object[] { change, "success" }, new object[] { change, "precommit" }, new object[] { change, "committed" } });
    [Theory, MemberData(nameof(ExportCases))]
    public async Task DelayedRealExportPreservesDurabilityButCannotLeakIntoReplacementPageOrScope(string change, string failure)
    {
        var files = new ImportUiFiles(); var store = new ImportUiStore(files);
        using var f = new OperationsUiFixture(store: store); f.PrepareExport(); var vm = f.Page.Materials; var original = f.Shell.CurrentSession!;
        files.Armed = true; files.FailPublish = failure == "precommit"; store.FailCommittedRead = failure == "committed";
        var deferred = new DeferredUiContext(); var pending = deferred.Start(() => vm.ExportCommand.ExecuteAsync());
        await deferred.FirstPost.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(f.Shell.IsBusy); Assert.False(f.Shell.NewCommand.CanExecute(null)); Assert.False(f.Shell.OpenCommand.CanExecute(null));
        Assert.False(await f.Shell.OpenWorkspaceAsync(original.WorkspacePath)); Assert.False(f.Shell.Navigate(WorkspaceRoute.Start));
        files.FailPublish = false; store.FailCommittedRead = false;
        Change(f, vm, change); var expected = Feedback(f, vm);
        await deferred.Complete(pending); Assert.Equal(expected, Feedback(f, vm)); Assert.False(f.Shell.IsBusy);
        var durable = new TournamentWorkspaceStore().Read(original.WorkspacePath);
        Assert.Equal(failure == "precommit" ? 0 : 1, durable.AuditEvents.Count(a => a.Action == "OperationalPackageExported"));
        Assert.Same(f.Workflow.CurrentSession, f.Shell.CurrentSession); Assert.Null(vm.Outcome); Assert.Null(vm.ExportFailure);
        if (change is not ("dispose" or "foreign")) Assert.DoesNotContain("正在", vm.StateMessage);
    }
    [Fact]
    public async Task CapturedDatesAndDestinationDoNotChangeWhileRealMutationWaits()
    {
        using var f = new OperationsUiFixture(); var vm = f.Page.Materials; vm.Days[1].IsSelected = false; f.PrepareExport();
        var originalOutput = vm.OutputDirectory; var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        f.Data.Store.BeforeMutation = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); };
        var pending = vm.ExportCommand.ExecuteAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { vm.Days[0].IsSelected = false; vm.Days[1].IsSelected = true; vm.OutputDirectory = f.Data.PathFor("new-output"); }
        finally { release.Set(); }
        await pending;
        Assert.Equal(9, Directory.GetFiles(originalOutput).Length); Assert.False(Directory.Exists(vm.OutputDirectory));
        Assert.Single(f.Shell.CurrentSession!.Workspace.AuditEvents, a => a.Action == "OperationalPackageExported");
        Assert.Null(vm.Outcome); Assert.False(vm.ScopeConfirmed); Assert.DoesNotContain("正在", vm.StateMessage);
    }
    private static void Change(OperationsUiFixture f, WorkspaceOperationalExportViewModel vm, string change)
    {
        switch (change)
        {
            case "output": vm.OutputDirectory = f.Data.PathFor("new-output"); break;
            case "project": vm.SelectedProject = vm.ProjectChoices[1]; break;
            case "day": vm.Days[0].IsSelected = !vm.Days[0].IsSelected; break;
            case "carry": vm.IncludePendingCarryover = true; break;
            case "pdf": vm.PdfRows = 2; break;
            case "overwrite": vm.OverwriteExisting = true; break;
            case "consent": vm.ScopeConfirmed = true; break;
            case "reopen": f.Workflow.OpenWorkspace(f.Workflow.CurrentSession!.WorkspacePath); break;
            case "audit":
                if (f.Workflow.CurrentSession!.RequiresReload) f.Workflow.OpenWorkspace(f.Workflow.CurrentSession.WorkspacePath);
                f.Workflow.CreateBackup(f.Workflow.CurrentSession!.Workspace.Revision); break;
            case "foreign": f.Workflow.CreateWorkspace(new("foreign", TournamentKind.Individual, TournamentPurpose.FullTournament,
                [new(EventDiscipline.MenSingles, CompetitionMode.SinglesKnockout)], f.Data.PathFor("foreign.szbd"))); break;
            case "dispose": f.Page.Dispose(); break;
        }
        f.Shell.ReportError(new IOException("newer visible feedback"));
    }
    private static string Feedback(OperationsUiFixture f, WorkspaceOperationalExportViewModel vm) =>
        string.Join("|", f.Shell.Status, f.Shell.LastError?.Code, vm.StateMessage, vm.SourceDetails, vm.OutcomeDetails, vm.OutputDirectory, vm.ScopeSummary);
}
