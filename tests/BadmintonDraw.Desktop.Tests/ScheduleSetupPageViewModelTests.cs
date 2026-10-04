using System.Security.Cryptography;
using System.Text.Json;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Persistence;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class ScheduleSetupPageViewModelTests
{
    [Fact]
    public async Task PlayerFailureKeepsStudentIdentityOutOfOrdinarySummaryAndShellStatus()
    {
        using var fixture = new ScheduleUiFixture(roundRobin: true, deterministicPlayerIds: true); var page = Page(fixture);
        page.DailyMaximumText = "1";
        await page.GenerateCommand.ExecuteAsync();
        Assert.StartsWith("student:ui-player-", page.Failure!.CapacityEvidence!.PlayerKey);
        Assert.Contains("男子单打", page.FailureSummary);
        Assert.DoesNotContain("ui-player-", page.FailureSummary);
        Assert.DoesNotContain("ui-player-", fixture.Shell.Status);
        Assert.Contains("ui-player-", page.FailureTechnicalDetails);
        Assert.Contains("涉及项目：男子单打", page.FailureTechnicalDetails);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailureDetailsUseCapturedSettingsAndDistinguishNotRunFromUnknown(bool contextFailure)
    {
        using var fixture = new ScheduleUiFixture(); var page = Page(fixture); var request = page.BuildSetup();
        var options = contextFailure ? TournamentSchedulingOptions.Default with { ContextWorkUnits = 0 }
            : TournamentSchedulingOptions.Default with { PreflightWorkUnits = 0, SearchWorkUnits = 0 };
        var result = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(new(
            fixture.Workflow.CurrentSession!.Workspace.Projects.Select(p => p.MatchGraph!).ToArray(), request.Resources, request.Policy), options));
        typeof(ScheduleSetupPageViewModel).GetProperty(nameof(page.Failure))!.SetValue(page, result.Detail);
        page.MinimumRestText = "47"; page.DailyMaximumText = "12"; page.RequireChampionshipFinalsOnLastDay = true;
        var details = page.FailureTechnicalDetails;
        Assert.Contains(contextFailure ? "Context" : "Search", details);
        Assert.Contains(contextFailure ? "NotRun" : "Unknown", details);
        Assert.Contains("预算拒绝支出", details);
        Assert.Contains(contextFailure ? "Context=0" : "Preflight=0", details);
        Assert.Contains("最短休息：30 分钟", details); Assert.DoesNotContain("47 分钟", details);
        Assert.Contains("每日上限：4 场", details); Assert.Contains("策略：尽快完成", details);
        Assert.Contains(request.Resources.Days[0].DayLabel, details); Assert.Contains("B1", details);
        Assert.Contains("ProjectTimings", details); // Complete captured policy, including duration overrides.
    }

    [Fact]
    public void NonBudgetSearchLimitDoesNotClaimBudgetExhaustion()
    {
        using var fixture = new ScheduleUiFixture(); var page = Page(fixture); var request = page.BuildSetup();
        var result = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(new(
            fixture.Workflow.CurrentSession!.Workspace.Projects.Select(p => p.MatchGraph!).ToArray(), request.Resources, request.Policy),
            TournamentSchedulingOptions.Default with { MaxDecisionAlternatives = 0 }));
        typeof(ScheduleSetupPageViewModel).GetProperty(nameof(page.Failure))!.SetValue(page, result.Detail);
        Assert.Contains("未记录预算拒绝支出", page.FailureTechnicalDetails);
        Assert.Contains("有界搜索", page.FailureTechnicalDetails);
        Assert.DoesNotContain("预算已用尽", page.FailureTechnicalDetails);
    }

    [Fact]
    public async Task RegenerationShowsActualMovementAndReopenDoesNotInventComparison()
    {
        using var fixture = new ScheduleUiFixture(); var page = Page(fixture);
        page.Days[0].DateText = "2026-09-13";
        await page.GenerateCommand.ExecuteAsync();
        Assert.Contains("1 场", page.SuccessSummary); Assert.Contains("策略：尽快完成", page.SuccessSummary);
        Assert.DoesNotContain("重新生成变动", page.SuccessSummary);
        page.Days[0].DateText = "2026-09-14";
        await page.GenerateCommand.ExecuteAsync();
        Assert.Contains("重新生成变动：1 场", page.SuccessSummary);
        Assert.Contains("跨日 1 场", page.SuccessSummary);
        fixture.Shell.Navigate(WorkspaceRoute.Overview); fixture.Shell.Navigate(WorkspaceRoute.ScheduleSetup);
        var reopened = Assert.IsType<ScheduleSetupPageViewModel>(fixture.Shell.CurrentPage);
        Assert.Contains("1 场", reopened.SuccessSummary); Assert.Contains("策略：尽快完成", reopened.SuccessSummary);
        Assert.DoesNotContain("重新生成变动", reopened.SuccessSummary);
    }

    [Fact]
    public async Task IncompleteRegenerationAnalysisDoesNotReportZeroMovement()
    {
        using var fixture = new ScheduleUiFixture(); var page = Page(fixture);
        await page.GenerateCommand.ExecuteAsync();
        var workspace = fixture.Workflow.CurrentSession!.Workspace; var request = page.BuildSetup();
        var result = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(new(
            workspace.Projects.Select(p => p.MatchGraph!).ToArray(), request.Resources, request.Policy)
            { BaselinePlacements = workspace.Schedule!.Placements }, TournamentSchedulingOptions.Default with { QualityWorkUnits = 0 }));
        var summary = (string)typeof(ScheduleSetupPageViewModel).GetMethod("BuildSuccessSummary", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [result.Schedule, result.Quality, result.Diagnostics])!;
        Assert.Contains("重新生成变动：分析未完成", summary);
        Assert.DoesNotContain("变动：0 场", summary);
        Assert.Contains("1 场", summary);
    }

    [Fact]
    public async Task ProvenPlayerCapacityFailureShowsCountAndUsefulAdvice()
    {
        using var fixture = new ScheduleUiFixture(roundRobin: true, deterministicPlayerIds: true); var page = Page(fixture);
        page.DailyMaximumText = "1";
        await page.GenerateCommand.ExecuteAsync();
        Assert.Equal("PlayerDailyCap", page.Failure!.CapacityEvidence!.Kind);
        var summary = page.FailureSummary; var advice = page.FailureAdvice;
        var evidence = page.Failure.CapacityEvidence;
        Assert.InRange(evidence.RequiredLowerBound, 2, 3);
        Assert.Equal(evidence.WitnessMatchIds.Count, evidence.RequiredLowerBound);
        Assert.Equal(1, evidence.CapacityUpperBound);
        Assert.Contains($"至少需 {evidence.RequiredLowerBound} 场", summary);
        Assert.Contains("1 天 × 每天 1 场 = 1 场", summary); Assert.Contains("原赛程没有改变", summary);
        Assert.Contains("比赛日", advice); Assert.DoesNotContain("增加场地", advice);
        Assert.DoesNotContain(page.Failure.CapacityEvidence.WitnessMatchIds[0].ToString(), summary);
    }

    [Fact]
    public void SearchIncompleteNeverClaimsImpossible()
    {
        using var fixture = new ScheduleUiFixture(); var page = Page(fixture); var request = page.BuildSetup();
        var result = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(new(
            fixture.Workflow.CurrentSession!.Workspace.Projects.Select(p => p.MatchGraph!).ToArray(), request.Resources, request.Policy),
            TournamentSchedulingOptions.Default with { SearchWorkUnits = 0 }));
        // Supply the real core failure to the presentation boundary without making the default desktop budget tiny.
        typeof(ScheduleSetupPageViewModel).GetProperty(nameof(page.Failure))!.SetValue(page, result.Detail);
        var summary = page.FailureSummary;
        Assert.Contains("未完成", summary); Assert.Contains("不表示", summary); Assert.Contains("原赛程没有改变", summary);
    }

    [Fact]
    public async Task FailureRetainsCurrentEditorInputs()
    {
        using var fixture = new ScheduleUiFixture(); var page = Page(fixture);
        await page.GenerateCommand.ExecuteAsync();
        Assert.NotEmpty(page.SuccessSummary);
        page.Days[0].EndText = "09:05"; page.MinimumRestText = "47";
        await page.GenerateCommand.ExecuteAsync();
        Assert.Equal("09:05", page.Days[0].EndText); Assert.Equal("47", page.MinimumRestText);
        Assert.Empty(page.SuccessSummary);
        Assert.Null(fixture.Shell.LastCommandResult);
        page.Days[0].EndText = "09:";
        await page.GenerateCommand.ExecuteAsync();
        Assert.Null(page.Failure); Assert.Empty(page.FailureSummary);
    }

    [Fact]
    public async Task SuccessfulGenerationShowsPerDayUtilization()
    {
        using var fixture = new ScheduleUiFixture(); var page = Page(fixture);
        page.Days[0].DateText = "2026-09-13"; page.Days[0].StartText = "09:00"; page.Days[0].EndText = "10:00";
        await page.GenerateCommand.ExecuteAsync();
        var summary = page.SuccessSummary;
        Assert.Contains("2026-09-13", summary); Assert.Contains("30 / 120", summary); Assert.Contains("25", summary);
        Assert.NotNull(fixture.Shell.LastCommandResult);
        fixture.Shell.Navigate(WorkspaceRoute.Overview); fixture.Shell.Navigate(WorkspaceRoute.ScheduleSetup);
        var reopened = Assert.IsType<ScheduleSetupPageViewModel>(fixture.Shell.CurrentPage);
        Assert.Contains("30 / 120", reopened.SuccessSummary);
        Assert.DoesNotContain("预算", reopened.SuccessSummary);
    }

    [Fact]
    public async Task ResultFromWorkspaceSwitchedBeforeReturnIsNeverPresentedAsSuccess()
    {
        using var fixture = new ScheduleUiFixture(); using var other = new ScheduleUiFixture(); var page = Page(fixture); var request = page.BuildSetup();
        var succeeded = await fixture.Shell.RunWorkspaceCommandAsync(fixture.Workflow.CurrentSession!, (workflow, revision) =>
        {
            var result = workflow.GenerateSchedule(request.Resources, request.Policy, revision);
            workflow.OpenWorkspace(other.Workflow.CurrentSession!.WorkspacePath);
            return result;
        }, "旧工作区生成成功");
        Assert.False(succeeded);
        Assert.Null(fixture.Shell.LastCommandResult);
        Assert.DoesNotContain("旧工作区生成成功", fixture.Shell.Status);
    }

    [Fact]
    public async Task FailureFromWorkspaceSwitchedBeforeReturnDoesNotLeakIntoCurrentSession()
    {
        using var fixture = new ScheduleUiFixture(); using var other = new ScheduleUiFixture(); var page = Page(fixture); var request = page.BuildSetup();
        var succeeded = await fixture.Shell.RunWorkspaceCommandAsync(fixture.Workflow.CurrentSession!, (workflow, revision) =>
        {
            try { return workflow.GenerateSchedule(request.Resources, request.Policy, revision,
                TournamentSchedulingOptions.Default with { SearchWorkUnits = 0 }); }
            catch
            {
                workflow.OpenWorkspace(other.Workflow.CurrentSession!.WorkspacePath);
                throw;
            }
        }, "旧工作区生成成功");
        Assert.False(succeeded); Assert.Null(fixture.Shell.LastCommandResult); Assert.Null(fixture.Shell.LastError);
        Assert.Equal(other.Workflow.CurrentSession!.Workspace.Id, fixture.Shell.CurrentSession!.Workspace.Id);
        Assert.DoesNotContain("搜索", fixture.Shell.Status);
    }

    [Fact]
    public void IncompleteQualityMarksEveryUnanalyzedDayWithoutInventingZeroLoad()
    {
        using var fixture = new ScheduleUiFixture(); var page = Page(fixture); var request = page.BuildSetup();
        var result = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(new(
            fixture.Workflow.CurrentSession!.Workspace.Projects.Select(p => p.MatchGraph!).ToArray(), request.Resources, request.Policy),
            TournamentSchedulingOptions.Default with { QualityWorkUnits = 0 }));
        var summary = (string)typeof(ScheduleSetupPageViewModel).GetMethod("BuildSuccessSummary", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [result.Schedule, result.Quality, result.Diagnostics])!;
        Assert.Contains(request.Resources.Days[0].DayLabel, summary);
        Assert.Contains("负荷分析未完成", summary);
        Assert.DoesNotContain("0 /", summary);
        Assert.Contains("不能", summary);
    }

    [Fact]
    public void CapacityEstimateUsesCourtBlocksRefereeLimitsAndEditedProjectDurations()
    {
        using var fixture = new ScheduleUiFixture(3); var page = Page(fixture);
        page.Days[0].StartText = "09:00"; page.Days[0].EndText = "11:00"; page.Days[0].CourtsText = "B1, B2";
        page.RefereeCountText = "1";
        page.Days[0].AddUnavailableCommand.Execute(null);
        var blocked = page.Days[0].Unavailable[0]; blocked.StartText = "09:00"; blocked.EndText = "10:00"; blocked.CourtsText = "";
        page.ProjectTimings[0].MinutesText = "45";
        var estimate = Assert.IsType<ScheduleCapacityEstimate>(page.CapacityEstimate);
        Assert.Equal(3, estimate.MatchCount);
        Assert.Equal(60, estimate.AvailableMinutes);
        Assert.Equal(105, estimate.RequiredMinutes);
        Assert.True(estimate.IsInsufficient);
        page.Days[0].StartText = "09:";
        Assert.Null(page.CapacityEstimate);
    }

    [Fact]
    public void NormalTimesAreReadableWhileSubMinuteTimesRoundTripExactly()
    {
        var date = new DateOnly(2026, 9, 13);
        var editor = new ScheduleDayEditorViewModel(new(date, new(9, 0), new(12, 30, 0, 123), ["B1"]), () => { }, _ => { });
        Assert.Equal("09:00", editor.StartText);
        Assert.Equal(new TimeOnly(12, 30, 0, 123), editor.Build().DayEnd);
    }

    private static ScheduleSetupPageViewModel Page(ScheduleUiFixture fixture)
    {
        fixture.Shell.RegisterPageFactory(WorkspaceRoute.ScheduleSetup, s => new ScheduleSetupPageViewModel(fixture.Shell, s, confirmDiscard: () => Task.FromResult(true)));
        fixture.Shell.Navigate(WorkspaceRoute.ScheduleSetup);
        var page = Assert.IsType<ScheduleSetupPageViewModel>(fixture.Shell.CurrentPage);
        // Resource tests explicitly choose courts; new days no longer imply an existing booking.
        page.Days[0].CourtsText = "B1, B2";
        return page;
    }
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task OneGlobalRequestSchedulesEveryConfirmedProjectAndPersistsDurationOverrides(int count)
    {
        using var fixture = new ScheduleUiFixture(count); var page = Page(fixture);
        page.Days[0].DateText = "2026-09-13"; page.Days[0].StartText = "09:00"; page.Days[0].EndText = "15:00"; page.Days[0].CourtsText = "B1, B2";
        page.MinimumRestText = "30"; page.DailyMaximumText = "4"; page.ProjectTimings[0].MinutesText = "45";
        await page.GenerateCommand.ExecuteAsync();
        var workspace = new TournamentWorkspaceStore().Read(fixture.Workflow.CurrentSession!.WorkspacePath);
        Assert.Equal(count, workspace.Schedule!.GraphRevisions.Count);
        Assert.Equal(count, workspace.Schedule.Placements.Count);
        Assert.Equal(45, (workspace.Schedule.Placements[workspace.Projects[0].MatchGraph!.Matches[0].Id].EndTime - workspace.Schedule.Placements[workspace.Projects[0].MatchGraph!.Matches[0].Id].StartTime).TotalMinutes);
        Assert.All(workspace.Schedule.Policy.FinalDayRules, r => Assert.Equal(TournamentFinalDayPreference.Flexible, r.Preference)); // Effective Compact defaults have no hidden final preference.
        Assert.Single(workspace.AuditEvents, a => a.Action == "ScheduleGenerated");
        Assert.Equal(ScheduleAutoSchedulingStrategy.Compact, page.BuildSetup().Policy.Strategy);
    }
    [Fact]
    public void DateEditKeepsResourceWindowsAttachedToEditedDay()
    {
        using var fixture = new ScheduleUiFixture(); var page = Page(fixture);
        var day = page.Days[0]; day.DateText = "2026-09-13";
        day.AddUnavailableCommand.Execute(null);
        day.Unavailable[0].StartText = "12:00"; day.Unavailable[0].EndText = "13:00";
        day.DateText = "2026-09-15";
        var request = page.BuildSetup();
        var resource = Assert.Single(request.Resources.Days);
        Assert.Equal("2026-09-15", resource.DayLabel);
        Assert.Equal(new TimeOnly(12, 0), Assert.Single(resource.UnavailableCourtWindows!).StartTime);
        day.RemoveCommand.Execute(null);
        Assert.ThrowsAny<Exception>(() => page.BuildSetup());
    }
    [Fact]
    public void ResourceWindowsAndFinalDayRequirementReachOnePolicyWithExactTimePrecision()
    {
        using var fixture = new ScheduleUiFixture(3); var page = Page(fixture);
        page.Days[0].DateText = "2026-09-13"; page.Days[0].StartText = "09:00:30.123"; page.Days[0].EndText = "15:00"; page.Days[0].CourtsText = "B1, B2";
        page.Days[0].AddUnavailableCommand.Execute(null);
        var blocked = page.Days[0].Unavailable[0]; blocked.StartText = "10:00"; blocked.EndText = "11:00"; blocked.CourtsText = "B1";
        page.Days[0].AddRefereeWindowCommand.Execute(null);
        var referees = page.Days[0].RefereeWindows[0]; referees.StartText = "11:00"; referees.EndText = "12:00"; referees.CountText = "0";
        page.RequireChampionshipFinalsOnLastDay = true;
        var setup = page.BuildSetup();
        Assert.Equal(new TimeOnly(9, 0, 30, 123), setup.Resources.Days[0].DayStart);
        Assert.Equal("B1", Assert.Single(Assert.Single(setup.Resources.Days[0].UnavailableCourtWindows!).Courts));
        Assert.Equal(0, Assert.Single(setup.Resources.Days[0].RefereeCapacityWindows!).RefereeCount);
        Assert.True(setup.Policy.RequireChampionshipFinalsOnLastDay);
        Assert.Empty(setup.Policy.FinalDayRules);
    }
    [Fact]
    public void CourtEditorTreatsRangesAsLiteralNamesAndOnlySplitsExplicitSeparators()
    {
        using var fixture = new ScheduleUiFixture(); var page = Page(fixture);
        page.Days[0].CourtsText = "B1-C8, B9，B10; B11；B12\nB13";

        var courts = page.BuildSetup().Resources.Days[0].Courts;

        Assert.Equal(["B1-C8", "B9", "B10", "B11", "B12", "B13"], courts);
    }
    [Fact]
    public async Task FailedRegenerationPreservesSavedScheduleAndTypedEditorValues()
    {
        using var fixture = new ScheduleUiFixture(); var page = Page(fixture);
        await page.GenerateCommand.ExecuteAsync();
        var path = fixture.Workflow.CurrentSession!.WorkspacePath; var bytes = SHA256.HashData(File.ReadAllBytes(path));
        var snapshot = JsonSerializer.Serialize(fixture.Workflow.CurrentSession.Workspace.Schedule);
        page.Days[0].StartText = "09:00"; page.Days[0].EndText = "09:05";
        await page.GenerateCommand.ExecuteAsync();
        Assert.NotNull(page.Failure);
        Assert.NotEmpty(page.Failure!.UnplacedMatches);
        Assert.Equal("09:05", page.Days[0].EndText);
        Assert.Equal(snapshot, JsonSerializer.Serialize(fixture.Workflow.CurrentSession.Workspace.Schedule));
        Assert.Equal(bytes, SHA256.HashData(File.ReadAllBytes(path)));
    }
    [Fact]
    public async Task ExternalRegenerationLatchesEditorConflictUntilExplicitReset()
    {
        using var fixture = new ScheduleUiFixture(); var page = Page(fixture);
        await page.GenerateCommand.ExecuteAsync(); page.MinimumRestText = "47";
        var other = new BadmintonDraw.Workflows.Tournaments.TournamentWorkspaceWorkflow(); other.OpenWorkspace(fixture.Workflow.CurrentSession!.WorkspacePath);
        var current = other.CurrentSession!.Workspace.Schedule!;
        other.GenerateSchedule(current.Resources with { MinimumRestMinutes = 15 }, current.Policy, other.CurrentSession.Workspace.Revision);
        await fixture.Shell.ReloadCommand.ExecuteAsync();
        Assert.Equal("47", page.MinimumRestText); Assert.True(page.HasEditorConflict); Assert.False(page.GenerateCommand.CanExecute(null));
        await page.ResetCommand.ExecuteAsync();
        Assert.Equal("15", page.MinimumRestText); Assert.False(page.HasEditorConflict); Assert.True(page.GenerateCommand.CanExecute(null));
    }
    [Fact]
    public async Task InvalidRestTimeNeverChangesArchive()
    {
        using var fixture = new ScheduleUiFixture(); var page = Page(fixture); var session = fixture.Workflow.CurrentSession;
        page.MinimumRestText = "NaN";
        await page.GenerateCommand.ExecuteAsync();
        Assert.NotNull(fixture.Shell.LastError); Assert.Same(session, fixture.Workflow.CurrentSession); Assert.Null(session!.Workspace.Schedule);
    }
    [Fact]
    public void AddingWindowWithUnfinishedDayTimeDoesNotThrowOrReplaceInput()
    {
        using var fixture = new ScheduleUiFixture(); var page = Page(fixture); var day = page.Days[0];
        day.StartText = "09:";
        day.AddUnavailableCommand.Execute(null); day.AddRefereeWindowCommand.Execute(null);
        Assert.Equal("09:", day.StartText); Assert.Single(day.Unavailable); Assert.Single(day.RefereeWindows);
    }
    [Fact]
    public async Task DisablingFinalDayRequirementKeepsDurationsAndResources()
    {
        using var fixture = new ScheduleUiFixture(3); var page = Page(fixture);
        page.RequireChampionshipFinalsOnLastDay = true; page.ProjectTimings[0].MinutesText = "45";
        await page.GenerateCommand.ExecuteAsync();
        Assert.True(page.BuildSetup().Policy.RequireChampionshipFinalsOnLastDay);
        page.RequireChampionshipFinalsOnLastDay = false;
        var setup = page.BuildSetup(); Assert.Empty(setup.Policy.FinalDayRules); Assert.Empty(setup.Policy.DayLoadTargets); Assert.Empty(setup.Policy.StageWaveTargets);
        Assert.False(setup.Policy.RequireChampionshipFinalsOnLastDay);
        Assert.Equal(45, setup.Policy.ProjectTimings[page.ProjectTimings[0].ProjectId].MatchMinutes); Assert.NotEmpty(setup.Resources.Days);
    }
}
