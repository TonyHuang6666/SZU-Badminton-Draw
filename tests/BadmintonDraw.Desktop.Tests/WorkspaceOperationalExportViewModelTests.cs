using System.Collections.Concurrent;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Core.Scheduling;
using ClosedXML.Excel;
using BadmintonDraw.Tests;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class WorkspaceOperationalExportViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealPackageSavesExactlyOneAuditAndDisplaysVerifiedOutputsUnderBothPublicationOrders(bool posted)
    {
        var posts = new ConcurrentQueue<Action>();
        using var f = new OperationsUiFixture(post: posted ? a => posts.Enqueue(a) : a => a());
        var vm = f.Page.Materials; var before = f.Shell.CurrentSession!;
        vm.OutputDirectory = f.Data.PathFor("materials"); Assert.False(vm.ExportCommand.CanExecute(null));
        vm.ScopeConfirmed = true; Assert.True(vm.ExportCommand.CanExecute(null)); await vm.ExportCommand.ExecuteAsync();
        while (posts.TryDequeue(out var next)) next();
        var outcome = Assert.IsType<OperationalPackageOutcome>(vm.Outcome);
        Assert.Equal(9, outcome.Outputs.Count); Assert.Equal(9, vm.Outputs.Count); Assert.True(outcome.AuditRecorded);
        Assert.Equal(before.Workspace.Revision + 1, f.Page.Session.Workspace.Revision);
        Assert.Single(f.Page.Session.Workspace.AuditEvents, a => a.Action == "OperationalPackageExported");
        Assert.Equal(TournamentStage.ScheduleReady, f.Page.Session.Workspace.Stage); Assert.Empty(f.Page.Session.Workspace.Results);
        Assert.False(vm.ScopeConfirmed); Assert.False(vm.OverwriteExisting); Assert.Null(vm.ExportFailure); Assert.Null(vm.Error);
        foreach (var file in outcome.Outputs)
        {
            Assert.Equal(file.Sha256, WorkspaceResultImportFacadeFixture.Hash(file.Path));
            Assert.Equal(file.ByteLength, new FileInfo(file.Path).Length); Assert.Contains(file.Path, vm.OutcomeDetails); Assert.Contains(file.Sha256, vm.OutcomeDetails);
        }
        Assert.Contains(outcome.AuditId.ToString(), vm.OutcomeDetails); Assert.Contains("已保存", vm.StateMessage);
        var history = f.Page.History; f.Page.RefreshSession(f.Page.Session); Assert.Same(history, f.Page.History); Assert.Same(outcome, vm.Outcome);
    }

    [Fact]
    public void ScopeEditsCannotTurnEmptyDatesOrInvalidLayoutIntoAllOrReuseOverwriteConsent()
    {
        using var f = new OperationsUiFixture(2); var vm = f.Page.Materials;
        Assert.Equal(3, vm.ProjectChoices.Count); Assert.Null(vm.SelectedProject!.ProjectId);
        Assert.False(vm.IncludePendingCarryover); Assert.Null(vm.SelectedCarryoverDay);
        f.PrepareExport(); vm.OverwriteExisting = true; vm.ScopeConfirmed = true;
        vm.SelectedProject = vm.ProjectChoices[1]; Assert.False(vm.OverwriteExisting); Assert.False(vm.ScopeConfirmed);
        foreach (var day in vm.Days) day.IsSelected = false;
        vm.ScopeConfirmed = true; Assert.False(vm.ExportCommand.CanExecute(null));
        vm.Days[0].IsSelected = true; vm.IncludePendingCarryover = true; vm.ScopeConfirmed = true;
        Assert.False(vm.ExportCommand.CanExecute(null)); vm.SelectedCarryoverDay = vm.Days[0]; vm.ScopeConfirmed = true;
        Assert.True(vm.ExportCommand.CanExecute(null)); vm.Days[0].IsSelected = false;
        Assert.Null(vm.SelectedCarryoverDay); Assert.False(vm.ScopeConfirmed);
        vm.Days[0].IsSelected = true; vm.IncludePendingCarryover = false; vm.PdfRows = 0; vm.ScopeConfirmed = true;
        Assert.False(vm.ExportCommand.CanExecute(null)); vm.PdfRows = 1; vm.PdfColumns = -1; vm.ScopeConfirmed = true;
        Assert.False(vm.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancelledOutputPickerRetainsDraftWithoutExportingOrSaving()
    {
        using var f = new OperationsUiFixture(); f.PrepareExport(); var before = f.Shell.CurrentSession;
        await f.Page.Materials.PickOutputCommand.ExecuteAsync();
        Assert.EndsWith("materials", f.Page.Materials.OutputDirectory); Assert.Same(before, f.Shell.CurrentSession);
        Assert.False(Directory.Exists(f.Page.Materials.OutputDirectory)); Assert.Null(f.Page.Materials.Outcome);
    }

    [Fact]
    public async Task CurrentPickerFailureDoesNotClaimThatMaterialPublicationWasAttempted()
    {
        using var f = new OperationsUiFixture(outputPicker: () => Task.FromException<string?>(new IOException("无法读取目录选择结果")));
        f.PrepareExport(); var vm = f.Page.Materials; var before = f.Shell.CurrentSession!;
        await vm.PickOutputCommand.ExecuteAsync();
        Assert.Equal("desktop.operation-failed", vm.Error?.Code); Assert.Contains("无法读取目录选择结果", vm.StateMessage);
        Assert.Contains("未发起材料导出", vm.StateMessage); Assert.DoesNotContain("部分发布", vm.StateMessage);
        Assert.Same(before, f.Shell.CurrentSession); Assert.False(Directory.Exists(vm.OutputDirectory)); Assert.Empty(vm.Outputs);
    }

    [Fact]
    public async Task ExplicitPendingTargetUsesRealReceiptWithoutMovingTheScheduledMatch()
    {
        using var f = new OperationsUiFixture(); await f.Import(f.Record(fill: false)); var vm = f.Page.Materials;
        var original = f.Shell.CurrentSession!.Workspace; var placement = Assert.Single(original.Schedule!.Placements).Value;
        vm.Days[0].IsSelected = false; vm.IncludePendingCarryover = true; vm.SelectedCarryoverDay = vm.Days[1]; f.PrepareExport();
        await vm.ExportCommand.ExecuteAsync(); var outcome = Assert.IsType<OperationalPackageOutcome>(vm.Outcome);
        Assert.Equal(1, outcome.Counts.PendingCarryoverCount); Assert.Equal(1, outcome.Counts.DistinctMatchCount);
        Assert.Equal(new DateOnly(2026, 9, 14), outcome.Scope.PendingCarryoverDay); Assert.Equal(placement, Assert.Single(f.Shell.CurrentSession.Workspace.Schedule!.Placements).Value);
        var record = Assert.Single(outcome.Outputs, o => o.Kind == OperationalMaterialKind.MergedRecordExcel);
        using var workbook = new XLWorkbook(record.Path); var sheet = workbook.Worksheet("对阵记录表");
        Assert.Equal("2026-09-14", sheet.Cell("B6").GetString()); Assert.Equal("待安排", sheet.Cell("C6").GetString()); Assert.Equal("待安排", sheet.Cell("K6").GetString());
        Assert.Contains(placement.DayLabel, sheet.Cell("M6").GetString()); Assert.Empty(f.Shell.CurrentSession.Workspace.Results);
    }

    [Fact]
    public async Task EmptySelectedDayReturnsRealNoMatchesFailureRatherThanAnExampleWorkbookSuccess()
    {
        using var f = new OperationsUiFixture(); var vm = f.Page.Materials; vm.Days[0].IsSelected = false; f.PrepareExport();
        var before = f.Shell.CurrentSession; await vm.ExportCommand.ExecuteAsync();
        Assert.Equal("export.no-matches", vm.Error?.Code); Assert.NotNull(vm.ExportFailure); Assert.Null(vm.Outcome); Assert.Empty(vm.Outputs);
        Assert.False(Directory.Exists(vm.OutputDirectory)); Assert.Same(before, f.Shell.CurrentSession); Assert.Contains("未保存", vm.StateMessage);
    }

    [Fact]
    public async Task InvalidUnselectedProjectStillBlocksTheGlobalPackageAndDisplaysQualifiedViolations()
    {
        using var f = new OperationsUiFixture(2); var original = f.Shell.CurrentSession!;
        var first = original.Workspace.Projects[0].MatchGraph!.Matches[0]; var second = original.Workspace.Projects[1].MatchGraph!.Matches[0];
        f.Data.Store.Mutate(original.WorkspacePath, original.Workspace.Revision, w =>
        {
            var placements = w.Schedule!.Placements.ToDictionary(); var a = placements[first.Id];
            placements[second.Id] = placements[second.Id] with { DayLabel = a.DayLabel, StartTime = a.StartTime, EndTime = a.EndTime, Court = a.Court };
            return w with { Schedule = w.Schedule with { Placements = placements } };
        });
        f.Workflow.OpenWorkspace(original.WorkspacePath); var vm = f.Page.Materials; vm.SelectedProject = vm.ProjectChoices[1]; f.PrepareExport();
        var hash = WorkspaceResultImportFacadeFixture.Hash(original.WorkspacePath); await vm.ExportCommand.ExecuteAsync();
        Assert.Equal("export.hard-violations", vm.Error?.Code); Assert.Contains(vm.Error!.SchedulingFailure!.Violations, v => v.Code == SchedulingConstraintCode.CourtOverlap);
        Assert.Contains(second.Id.ToString(), vm.OutcomeDetails); Assert.Empty(vm.Outputs); Assert.False(Directory.Exists(vm.OutputDirectory));
        Assert.Equal(hash, WorkspaceResultImportFacadeFixture.Hash(original.WorkspacePath));
    }

    [Fact]
    public async Task ExistingTargetsRequireNewExplicitOverwriteConsentAndOnlySuccessfulAttemptsRecordAudit()
    {
        using var f = new OperationsUiFixture(); f.PrepareExport(); var vm = f.Page.Materials;
        await vm.ExportCommand.ExecuteAsync(); Assert.NotNull(vm.Outcome); var afterFirst = f.Shell.CurrentSession!;
        vm.ScopeConfirmed = true; await vm.ExportCommand.ExecuteAsync();
        Assert.Equal("export.exists", vm.Error?.Code); Assert.Empty(vm.Outputs); Assert.Same(afterFirst, f.Shell.CurrentSession);
        vm.OverwriteExisting = true; Assert.False(vm.ScopeConfirmed); vm.ScopeConfirmed = true;
        await vm.ExportCommand.ExecuteAsync(); var outcome = Assert.IsType<OperationalPackageOutcome>(vm.Outcome);
        Assert.Equal(9, outcome.Outputs.Count); Assert.Equal(afterFirst.Workspace.Revision + 1, outcome.Command.Workspace.Revision);
        Assert.Equal(2, outcome.Command.Workspace.AuditEvents.Count(a => a.Action == "OperationalPackageExported"));
        Assert.False(vm.OverwriteExisting); Assert.False(vm.ScopeConfirmed);
    }

    [Fact]
    public void ReplannedDatesRetainValidDraftSelectionsButDoNotAutoSelectNewDaysOrKeepRemovedCarryTarget()
    {
        using var f = new OperationsUiFixture(); var vm = f.Page.Materials;
        vm.SelectedProject = vm.ProjectChoices[1]; vm.IncludePendingCarryover = true; vm.SelectedCarryoverDay = vm.Days[1];
        vm.OverwriteExisting = true; f.PrepareExport(); var projectId = vm.SelectedProject.ProjectId;
        var before = f.Shell.CurrentSession!.Workspace; var days = before.Schedule!.Resources.Days;
        // A genuine replan must also remove the old date's persisted load-target reference.
        var policy = before.Schedule.Policy with
        { DayLoadTargets = before.Schedule.Policy.DayLoadTargets.Where(t => t.DayLabel == days[0].DayLabel).ToArray() };
        f.Workflow.GenerateSchedule(before.Schedule.Resources with { Days = [days[0], days[1] with { Date = new(2026, 9, 15) }] }, policy, before.Revision);
        Assert.Same(vm, f.Page.Materials); Assert.Equal(projectId, vm.SelectedProject!.ProjectId);
        Assert.True(vm.Days.Single(d => d.Day == new DateOnly(2026, 9, 13)).IsSelected);
        Assert.False(vm.Days.Single(d => d.Day == new DateOnly(2026, 9, 15)).IsSelected);
        Assert.Null(vm.SelectedCarryoverDay); Assert.True(vm.IncludePendingCarryover); Assert.False(vm.ScopeConfirmed); Assert.False(vm.OverwriteExisting);
        Assert.EndsWith("materials", vm.OutputDirectory);
    }
}
