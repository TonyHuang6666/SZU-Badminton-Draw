using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Persistence;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class WorkspaceOperationalExportRaceTests
{
    public static IEnumerable<object[]> PickerCases => new[] { "project", "day", "carry", "pdf", "consent", "reopen", "audit", "foreign", "dispose", "navigate" }
        .SelectMany(change => new[] { new object[] { change, false }, new object[] { change, true } });
    [Theory, MemberData(nameof(PickerCases))]
    public async Task LatePickerSuccessAndExceptionCannotOverrideNewScopeOrFeedback(string change, bool throws)
    {
        var returned = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var f = new OperationsUiFixture(outputPicker: () => returned.Task); var vm = f.Page.Materials; f.PrepareExport();
        var original = f.Shell.CurrentSession!;
        var pending = vm.ExportCommand.ExecuteAsync(); Assert.False(pending.IsCompleted);
        if (change == "navigate") Assert.True(f.Shell.Navigate(WorkspaceRoute.Start));
        else Change(f, vm, change);
        var expected = Feedback(f, vm);
        var output = f.Data.PathFor("late-output");
        if (throws) returned.SetException(new IOException("late picker failure")); else returned.SetResult(output);
        await pending; Assert.Equal(expected, Feedback(f, vm)); Assert.Null(vm.Outcome); Assert.False(vm.IsWorking);
        Assert.False(Directory.Exists(output));
        Assert.DoesNotContain(new TournamentWorkspaceStore().Read(original.WorkspacePath).AuditEvents,
            audit => audit.Action == "OperationalPackageExported");
    }

    public static IEnumerable<object[]> ConfirmationCases => new[] { "project", "day", "carry", "pdf", "consent", "reopen", "audit", "foreign", "dispose", "navigate" }
        .SelectMany(change => new[] { new object[] { change, false }, new object[] { change, true } });

    [Theory]
    [InlineData("pdf")]
    [InlineData("day")]
    [InlineData("reopen")]
    [InlineData("foreign")]
    [InlineData("dispose")]
    [InlineData("navigate")]
    public async Task LateConflictPreviewCannotStartExportForStaleInputsOrReplacementPage(string change)
    {
        var prompts = 0;
        using var f = new OperationsUiFixture(confirmOverwrite: _ => { prompts++; return Task.FromResult(true); });
        f.PrepareExport(); var vm = f.Page.Materials; var original = f.Shell.CurrentSession!;
        var output = f.NextOutput!;
        var deferred = new DeferredUiContext(); var pending = deferred.Start(() => vm.ExportCommand.ExecuteAsync());
        await deferred.FirstPost.WaitAsync(TimeSpan.FromSeconds(20));
        if (change == "navigate") Assert.True(f.Shell.Navigate(WorkspaceRoute.Start));
        else Change(f, vm, change);
        var expected = Feedback(f, vm);
        await deferred.Complete(pending);
        Assert.Equal(expected, Feedback(f, vm)); Assert.False(Directory.Exists(output));
        Assert.Equal(0, prompts); Assert.Null(vm.Outcome); Assert.Null(vm.ExportFailure);
        Assert.DoesNotContain(new TournamentWorkspaceStore().Read(original.WorkspacePath).AuditEvents,
            audit => audit.Action == "OperationalPackageExported");
    }

    [Theory, MemberData(nameof(ConfirmationCases))]
    public async Task LateConfirmationCannotExportChangedInputsOrReplacementPage(string change, bool throws)
    {
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var f = new OperationsUiFixture(confirmOverwrite: _ => { shown.TrySetResult(); return returned.Task; });
        f.PrepareExport(); var vm = f.Page.Materials; await vm.ExportCommand.ExecuteAsync();
        var original = f.Shell.CurrentSession!;
        var paths = vm.Outputs.Select(file => file.Path).ToArray();
        var hashes = paths.Select(BadmintonDraw.Tests.WorkspaceResultImportFacadeFixture.Hash).ToArray();
        vm.ScopeConfirmed = true; var pending = vm.ExportCommand.ExecuteAsync();
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(f.Shell.IsBusy); Assert.True(vm.IsWorking);
        if (change == "navigate") Assert.True(f.Shell.Navigate(WorkspaceRoute.Start));
        else Change(f, vm, change);
        var expected = Feedback(f, vm);
        if (throws) returned.SetException(new IOException("late confirmation failure")); else returned.SetResult(true);
        await pending;
        Assert.Equal(expected, Feedback(f, vm)); Assert.False(vm.IsWorking); Assert.Null(vm.Outcome); Assert.Null(vm.ExportFailure);
        Assert.Equal(hashes, paths.Select(BadmintonDraw.Tests.WorkspaceResultImportFacadeFixture.Hash));
        var durable = new TournamentWorkspaceStore().Read(original.WorkspacePath);
        Assert.Single(durable.AuditEvents, audit => audit.Action == "OperationalPackageExported");
    }
    public static IEnumerable<object[]> ExportCases => new[] { "pdf", "day", "reopen", "audit", "foreign", "dispose" }
        .SelectMany(change => new[] { new object[] { change, "success" }, new object[] { change, "precommit" }, new object[] { change, "committed" } });
    [Theory, MemberData(nameof(ExportCases))]
    public async Task DelayedRealExportPreservesDurabilityButCannotLeakIntoReplacementPageOrScope(string change, string failure)
    {
        var files = new ImportUiFiles(); var store = new ImportUiStore(files);
        using var f = new OperationsUiFixture(store: store); f.PrepareExport(); var vm = f.Page.Materials; var original = f.Shell.CurrentSession!;
        files.Armed = true; files.FailPublish = failure == "precommit"; store.FailCommittedRead = failure == "committed";
        var deferred = new DeferredUiContext(); var pending = deferred.Start(() => vm.ExportCommand.ExecuteAsync());
        await deferred.WaitForPostAfter(() => files.PublicationAttempted);
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
        var originalOutput = f.NextOutput!; var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        f.Data.Store.BeforeMutation = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); };
        var pending = vm.ExportCommand.ExecuteAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { vm.Days[0].IsSelected = false; vm.Days[1].IsSelected = true; f.NextOutput = f.Data.PathFor("new-output"); }
        finally { release.Set(); }
        await pending;
        var capturedPackage = Assert.Single(Directory.GetDirectories(originalOutput));
        Assert.Equal(Path.Combine(originalOutput, "9月13日多项目合并材料包"), capturedPackage);
        Assert.Equal(9, Directory.GetFiles(capturedPackage).Length); Assert.False(Directory.Exists(f.NextOutput));
        Assert.Single(f.Shell.CurrentSession!.Workspace.AuditEvents, a => a.Action == "OperationalPackageExported");
        Assert.Null(vm.Outcome); Assert.False(vm.ScopeConfirmed); Assert.DoesNotContain("正在", vm.StateMessage);
    }
    private static void Change(OperationsUiFixture f, WorkspaceOperationalExportViewModel vm, string change)
    {
        switch (change)
        {
            case "project": vm.SelectedProject = vm.ProjectChoices[1]; break;
            case "day": vm.Days[0].IsSelected = !vm.Days[0].IsSelected; break;
            case "carry": vm.IncludePendingCarryover = true; break;
            case "pdf": vm.PdfRows = 2; break;
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
        string.Join("|", f.Shell.Status, f.Shell.LastError?.Code, vm.StateMessage, vm.SourceDetails, vm.OutcomeDetails, vm.ScopeSummary);
}
