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
    [Fact]
    public async Task SoleProjectDefaultsToItsActualIdentityAndOmitsTheGlobalReportFromScope()
    {
        using var f = new OperationsUiFixture(); var vm = f.Page.Materials;
        var projectId = Assert.Single(f.Page.Session.Workspace.Projects).Id;
        Assert.Equal(projectId, vm.SelectedProject!.ProjectId);
        Assert.Equal(projectId, Assert.Single(vm.ProjectChoices).ProjectId);
        Assert.All(vm.Days, day => Assert.True(day.IsSelected));
        Assert.DoesNotContain("检查报告", vm.ScopeSummary);
        f.PrepareExport(); await vm.ExportCommand.ExecuteAsync();
        Assert.Equal(projectId, Assert.Single(vm.Outcome!.Scope.ProjectIds));
        Assert.Equal(projectId, Assert.Single(f.Page.Session.Workspace.AuditEvents,
            audit => audit.Action == "OperationalPackageExported").ProjectId);
    }

    [Fact]
    public void MultiProjectDefaultIncludesGlobalReportButIndividualScopeDoesNot()
    {
        using var f = new OperationsUiFixture(2); var vm = f.Page.Materials;
        Assert.Null(vm.SelectedProject!.ProjectId);
        Assert.Contains("检查报告", vm.ScopeSummary);
        vm.SelectedProject = vm.ProjectChoices[1];
        Assert.DoesNotContain("检查报告", vm.ScopeSummary);
        vm.SelectedProject = vm.ProjectChoices[0];
        Assert.Contains("检查报告", vm.ScopeSummary);
    }

    [Fact]
    public void RefreshBetweenSingleAndMultipleProjectsUsesSoleProjectAndPreservesValidSpecificScope()
    {
        using var f = new OperationsUiFixture(2); var vm = f.Page.Materials;
        var original = f.Page.Session; var first = original.Workspace.Projects[0];
        vm.SelectedProject = vm.ProjectChoices[2]; vm.ScopeConfirmed = true;
        vm.RefreshSession(original with { Workspace = original.Workspace with { Projects = [first] } });
        Assert.Equal(first.Id, vm.SelectedProject!.ProjectId); Assert.False(vm.ScopeConfirmed);
        Assert.DoesNotContain("检查报告", vm.ScopeSummary);
        vm.ScopeConfirmed = true;
        vm.RefreshSession(original with { Workspace = original.Workspace with { Revision = original.Workspace.Revision + 1 } });
        Assert.Equal(first.Id, vm.SelectedProject!.ProjectId); Assert.False(vm.ScopeConfirmed);
        Assert.DoesNotContain("检查报告", vm.ScopeSummary);
        vm.SelectedProject = vm.ProjectChoices[2];
        vm.RefreshSession(original with { Workspace = original.Workspace with { Revision = original.Workspace.Revision + 2 } });
        Assert.Equal(original.Workspace.Projects[1].Id, vm.SelectedProject!.ProjectId);
    }

    [Fact]
    public void RefreshMapsAllOrMissingSelectionToSoleProjectButDoesNotExpandARemovedSpecificScope()
    {
        using var f = new OperationsUiFixture(2); var vm = f.Page.Materials;
        var original = f.Page.Session; var first = original.Workspace.Projects[0];
        vm.RefreshSession(original with { Workspace = original.Workspace with { Projects = [first] } });
        Assert.Equal(first.Id, vm.SelectedProject!.ProjectId);
        vm.SelectedProject = null;
        vm.RefreshSession(original with { Workspace = original.Workspace with { Projects = [first], Revision = original.Workspace.Revision + 1 } });
        Assert.Equal(first.Id, vm.SelectedProject!.ProjectId);
        // The fixture schedules two disciplines; this UI-only snapshot adds a distinct choice
        // to keep the refreshed scope genuinely multiple after the original project is removed.
        var second = original.Workspace.Projects[1];
        var third = second with { Id = Guid.NewGuid(), DisplayName = "另一个项目", SortOrder = 2 };
        vm.RefreshSession(original with { Workspace = original.Workspace with { Projects = [second, third] } });
        Assert.Equal(3, vm.ProjectChoices.Count);
        Assert.Null(vm.SelectedProject);
        Assert.DoesNotContain("检查报告", vm.ScopeSummary);
    }

    [Fact]
    public async Task ExportPromptsForDestinationWithoutPreselectedFolderAndUsesFreshChoiceEachTime()
    {
        using var f = new OperationsUiFixture(); var vm = f.Page.Materials;
        vm.ScopeConfirmed = true;
        Assert.True(vm.ExportCommand.CanExecute(null));
        f.NextOutput = f.Data.PathFor("first-choice");
        await vm.ExportCommand.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.NotNull(vm.Outcome);
        Assert.All(vm.Outputs, item => Assert.StartsWith(f.NextOutput + Path.DirectorySeparatorChar, item.Path));
        f.NextOutput = f.Data.PathFor("second-choice"); vm.ScopeConfirmed = true;
        await vm.ExportCommand.ExecuteAsync();
        Assert.NotNull(vm.Outcome);
        Assert.All(vm.Outputs, item => Assert.StartsWith(f.NextOutput + Path.DirectorySeparatorChar, item.Path));
        Assert.Equal(2, f.Shell.CurrentSession!.Workspace.AuditEvents.Count(a => a.Action == "OperationalPackageExported"));
    }

    [Fact]
    public async Task CancellingDestinationOnExportDoesNotCreateFilesOrChangeWorkspace()
    {
        using var f = new OperationsUiFixture(); var vm = f.Page.Materials;
        f.PrepareExport(); f.NextOutput = null;
        var before = f.Shell.CurrentSession!; var hash = WorkspaceResultImportFacadeFixture.Hash(before.WorkspacePath);
        await vm.ExportCommand.ExecuteAsync();
        Assert.Same(before, f.Shell.CurrentSession); Assert.Null(vm.Outcome); Assert.Empty(vm.Outputs);
        Assert.False(Directory.Exists(f.Data.PathFor("materials")));
        Assert.Equal(hash, WorkspaceResultImportFacadeFixture.Hash(before.WorkspacePath));
        Assert.DoesNotContain(before.Workspace.AuditEvents, a => a.Action == "OperationalPackageExported");
    }

    [Fact]
    public async Task DestinationChoiceInProgressRejectsDuplicateExportAndScopeEditsPreventPublication()
    {
        var selected = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var f = new OperationsUiFixture(outputPicker: () => selected.Task); var vm = f.Page.Materials;
        f.PrepareExport(); var before = f.Shell.CurrentSession!;
        var pending = vm.ExportCommand.ExecuteAsync();
        Assert.False(pending.IsCompleted); Assert.True(vm.IsWorking); Assert.False(vm.ExportCommand.CanExecute(null));
        await vm.ExportCommand.ExecuteAsync();
        vm.Days[0].IsSelected = false;
        selected.SetResult(f.Data.PathFor("picked")); await pending;
        Assert.Same(before, f.Shell.CurrentSession); Assert.Null(vm.Outcome); Assert.False(vm.IsWorking);
        Assert.False(Directory.Exists(f.Data.PathFor("picked")));
        Assert.DoesNotContain(before.Workspace.AuditEvents, a => a.Action == "OperationalPackageExported");
    }

    [Fact]
    public async Task ConfirmationListsOnlyActualConflictsAndDoesNotAuthorizeNewFilesAppearingAfterPreview()
    {
        string[] expected = []; string? unexpected = null; IReadOnlyList<string>? prompted = null;
        using var f = new OperationsUiFixture(confirmOverwrite: paths =>
        {
            prompted = paths;
            File.WriteAllText(unexpected!, "new file from another process");
            return Task.FromResult(true);
        });
        f.PrepareExport(); var vm = f.Page.Materials; await vm.ExportCommand.ExecuteAsync();
        var outputs = vm.Outputs.Select(output => output.Path).ToArray();
        expected = [outputs[0]]; unexpected = outputs[1];
        foreach (var path in outputs.Skip(1)) File.Delete(path);
        var original = File.ReadAllBytes(expected[0]); var before = f.Shell.CurrentSession!;
        vm.ScopeConfirmed = true; await vm.ExportCommand.ExecuteAsync();
        Assert.Equal(expected, prompted);
        Assert.Equal("export.exists", vm.Error?.Code); Assert.Null(vm.Outcome); Assert.Empty(vm.Outputs);
        Assert.Equal(original, File.ReadAllBytes(expected[0]));
        Assert.Equal("new file from another process", File.ReadAllText(unexpected));
        Assert.Same(before, f.Shell.CurrentSession);
        Assert.Single(before.Workspace.AuditEvents, audit => audit.Action == "OperationalPackageExported");
    }

    [Fact]
    public async Task ConfirmationFailureDoesNotClaimPartialPublicationOrWriteAnotherAudit()
    {
        using var f = new OperationsUiFixture(confirmOverwrite: _ => Task.FromException<bool>(new IOException("确认窗口失败")));
        f.PrepareExport(); var vm = f.Page.Materials; await vm.ExportCommand.ExecuteAsync();
        var before = f.Shell.CurrentSession; vm.ScopeConfirmed = true; await vm.ExportCommand.ExecuteAsync();
        Assert.Contains("确认窗口失败", vm.StateMessage); Assert.Contains("未发起材料导出", vm.StateMessage);
        Assert.DoesNotContain("部分发布", vm.StateMessage); Assert.Null(vm.ExportFailure);
        Assert.Same(before, f.Shell.CurrentSession);
    }

    [Fact]
    public void SameNamedProjectsRemainDistinguishableWhenChoosingExportScope()
    {
        using var f = new OperationsUiFixture(2, 2);
        var choices = f.Page.Materials.ProjectChoices.Where(choice => choice.ProjectId is not null).ToArray();
        Assert.Equal(2, choices.Select(choice => choice.Label).Distinct(StringComparer.Ordinal).Count());
        foreach (var choice in choices)
        {
            f.Page.Materials.SelectedProject = choice;
            Assert.Contains(choice.Label, f.Page.Materials.ScopeSummary);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealPackageSavesExactlyOneAuditAndDisplaysVerifiedOutputsUnderBothPublicationOrders(bool posted)
    {
        var posts = new ConcurrentQueue<Action>();
        using var f = new OperationsUiFixture(post: posted ? a => posts.Enqueue(a) : a => a());
        var vm = f.Page.Materials; var before = f.Shell.CurrentSession!;
        f.NextOutput = f.Data.PathFor("materials"); Assert.False(vm.ExportCommand.CanExecute(null));
        vm.ScopeConfirmed = true; Assert.True(vm.ExportCommand.CanExecute(null)); await vm.ExportCommand.ExecuteAsync();
        while (posts.TryDequeue(out var next)) next();
        var outcome = Assert.IsType<OperationalPackageOutcome>(vm.Outcome);
        Assert.Equal(7, outcome.Outputs.Count); Assert.Equal(7, vm.Outputs.Count); Assert.True(outcome.AuditRecorded);
        Assert.Equal(before.Workspace.Revision + 1, f.Page.Session.Workspace.Revision);
        Assert.Single(f.Page.Session.Workspace.AuditEvents, a => a.Action == "OperationalPackageExported");
        Assert.Equal(TournamentStage.ScheduleReady, f.Page.Session.Workspace.Stage); Assert.Empty(f.Page.Session.Workspace.Results);
        Assert.False(vm.ScopeConfirmed); Assert.Null(vm.ExportFailure); Assert.Null(vm.Error);
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
        f.PrepareExport(); vm.ScopeConfirmed = true;
        vm.SelectedProject = vm.ProjectChoices[1]; Assert.False(vm.ScopeConfirmed);
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
    public async Task CancelledExportRetainsScopeDraftWithoutExportingOrSaving()
    {
        using var f = new OperationsUiFixture(); f.PrepareExport(); var before = f.Shell.CurrentSession;
        var project = f.Page.Materials.SelectedProject; var days = f.Page.Materials.Days.Where(d => d.IsSelected).ToArray();
        f.NextOutput = null; await f.Page.Materials.ExportCommand.ExecuteAsync();
        Assert.Same(project, f.Page.Materials.SelectedProject); Assert.Equal(days, f.Page.Materials.Days.Where(d => d.IsSelected));
        Assert.Same(before, f.Shell.CurrentSession);
        Assert.False(Directory.Exists(f.Data.PathFor("materials"))); Assert.Null(f.Page.Materials.Outcome);
    }

    [Fact]
    public async Task CurrentPickerFailureDoesNotClaimThatMaterialPublicationWasAttempted()
    {
        using var f = new OperationsUiFixture(outputPicker: () => Task.FromException<string?>(new IOException("无法读取目录选择结果")));
        f.PrepareExport(); var vm = f.Page.Materials; var before = f.Shell.CurrentSession!;
        await vm.ExportCommand.ExecuteAsync();
        Assert.Equal("desktop.operation-failed", vm.Error?.Code); Assert.Contains("无法读取目录选择结果", vm.StateMessage);
        Assert.Contains("未发起材料导出", vm.StateMessage); Assert.DoesNotContain("部分发布", vm.StateMessage);
        Assert.Same(before, f.Shell.CurrentSession); Assert.False(Directory.Exists(f.NextOutput)); Assert.Empty(vm.Outputs);
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
        Assert.Equal("export.no-matches", vm.Error?.Code); Assert.Null(vm.ExportFailure); Assert.Null(vm.Outcome); Assert.Empty(vm.Outputs);
        Assert.False(Directory.Exists(f.NextOutput)); Assert.Same(before, f.Shell.CurrentSession);
        Assert.Contains("未发起材料导出", vm.StateMessage); Assert.DoesNotContain("部分发布", vm.StateMessage);
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
        Assert.Contains(second.Id.ToString(), vm.OutcomeDetails); Assert.Empty(vm.Outputs); Assert.False(Directory.Exists(f.NextOutput));
        Assert.Equal(hash, WorkspaceResultImportFacadeFixture.Hash(original.WorkspacePath));
    }

    [Fact]
    public async Task ExistingTargetsRequireNewExplicitOverwriteConsentAndOnlySuccessfulAttemptsRecordAudit()
    {
        var prompts = new List<IReadOnlyList<string>>(); var accept = false;
        using var f = new OperationsUiFixture(confirmOverwrite: paths => { prompts.Add(paths); return Task.FromResult(accept); });
        f.PrepareExport(); var vm = f.Page.Materials;
        await vm.ExportCommand.ExecuteAsync(); Assert.NotNull(vm.Outcome); var afterFirst = f.Shell.CurrentSession!;
        Assert.Empty(prompts);
        var expectedPaths = vm.Outputs.Select(file => file.Path).Order(StringComparer.Ordinal).ToArray();
        var hashes = expectedPaths.Select(WorkspaceResultImportFacadeFixture.Hash).ToArray();
        vm.ScopeConfirmed = true; await vm.ExportCommand.ExecuteAsync();
        Assert.Null(vm.Error); Assert.Empty(vm.Outputs); Assert.Same(afterFirst, f.Shell.CurrentSession);
        Assert.Equal(expectedPaths, Assert.Single(prompts).Order(StringComparer.Ordinal));
        Assert.Equal(hashes, expectedPaths.Select(WorkspaceResultImportFacadeFixture.Hash));
        Assert.False(vm.ScopeConfirmed); Assert.Contains("取消", vm.StateMessage);
        accept = true; vm.ScopeConfirmed = true;
        await vm.ExportCommand.ExecuteAsync(); var outcome = Assert.IsType<OperationalPackageOutcome>(vm.Outcome);
        Assert.Equal(7, outcome.Outputs.Count); Assert.Equal(afterFirst.Workspace.Revision + 1, outcome.Command.Workspace.Revision);
        Assert.Equal(2, outcome.Command.Workspace.AuditEvents.Count(a => a.Action == "OperationalPackageExported"));
        Assert.Equal(2, prompts.Count); Assert.False(vm.ScopeConfirmed);
        accept = false; vm.ScopeConfirmed = true; await vm.ExportCommand.ExecuteAsync();
        Assert.Equal(3, prompts.Count); Assert.Null(vm.Outcome);
        Assert.Equal(2, f.Shell.CurrentSession!.Workspace.AuditEvents.Count(a => a.Action == "OperationalPackageExported"));
    }

    [Fact]
    public void ReplannedDatesRetainValidDraftSelectionsButDoNotAutoSelectNewDaysOrKeepRemovedCarryTarget()
    {
        using var f = new OperationsUiFixture(); var vm = f.Page.Materials;
        vm.SelectedProject = vm.ProjectChoices[0]; vm.IncludePendingCarryover = true; vm.SelectedCarryoverDay = vm.Days[1];
        f.PrepareExport(); var projectId = vm.SelectedProject.ProjectId;
        var before = f.Shell.CurrentSession!.Workspace; var days = before.Schedule!.Resources.Days;
        // A genuine replan must also remove the old date's persisted load-target reference.
        var policy = before.Schedule.Policy with
        { DayLoadTargets = before.Schedule.Policy.DayLoadTargets.Where(t => t.DayLabel == days[0].DayLabel).ToArray() };
        f.Workflow.GenerateSchedule(before.Schedule.Resources with { Days = [days[0], days[1] with { Date = new(2026, 9, 15) }] }, policy, before.Revision);
        Assert.Same(vm, f.Page.Materials); Assert.Equal(projectId, vm.SelectedProject!.ProjectId);
        Assert.True(vm.Days.Single(d => d.Day == new DateOnly(2026, 9, 13)).IsSelected);
        Assert.False(vm.Days.Single(d => d.Day == new DateOnly(2026, 9, 15)).IsSelected);
        Assert.Null(vm.SelectedCarryoverDay); Assert.True(vm.IncludePendingCarryover); Assert.False(vm.ScopeConfirmed);
    }
}
