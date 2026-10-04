using BadmintonDraw.Core;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Desktop.ViewModels;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class ScheduleTimingSemanticsTests
{
    [Fact]
    public void SavedSplitShowsEarlyDurationInMainFieldAndRoundTripsUnchanged()
    {
        using var fixture = new ScheduleUiFixture();
        var project = fixture.Workflow.CurrentSession!.Workspace.Projects[0];
        var policy = new TournamentSchedulingPolicy(ScheduleAutoSchedulingStrategy.Compact, [], false, [], [])
        { ProjectTimings = new Dictionary<Guid, ProjectMatchTiming> { [project.Id] = new(30, 16, 20) } };
        var editor = new ScheduleProjectTimingViewModel(project, policy, () => { });

        Assert.Equal("20", editor.MinutesText);
        Assert.Equal("30", editor.AfterMinutesText);
        Assert.Equal(new ProjectMatchTiming(30, 16, 20), editor.BuildTiming());
        editor.UseTimingSplit = false;
        Assert.Equal(new ProjectMatchTiming(20), editor.BuildTiming());
    }

    [Theory]
    [InlineData(32, 20)]
    [InlineData(16, 30)]
    [InlineData(8, 30)]
    [InlineData(2, 30)]
    public void OverrideStartsWithTheRoundAtBoundary(int entrants, int expectedMinutes)
    {
        using var fixture = new ScheduleUiFixture();
        var project = fixture.Workflow.CurrentSession!.Workspace.Projects[0];
        var editor = new ScheduleProjectTimingViewModel(project, null, () => { })
        { MinutesText = "20", AfterMinutesText = "30", BoundaryText = "16", UseTimingSplit = true };
        var policy = new TournamentSchedulingPolicy(ScheduleAutoSchedulingStrategy.Compact, [], false, [], [])
        { ProjectTimings = new Dictionary<Guid, ProjectMatchTiming> { [project.Id] = editor.BuildTiming() } };

        Assert.Equal(expectedMinutes, ScheduleTimingResolver.Resolve(project.MatchGraph!.Matches[0] with
        { KnockoutEntrantCount = entrants }, policy));
    }

    [Fact]
    public void RoundRobinIgnoresSplitOverrideAndUsesMainDuration()
    {
        using var fixture = new ScheduleUiFixture(roundRobin: true);
        var editor = new ScheduleProjectTimingViewModel(fixture.Workflow.CurrentSession!.Workspace.Projects[0], null, () => { })
        { MinutesText = "20", AfterMinutesText = "30", UseTimingSplit = true };
        Assert.False(editor.SupportsTimingSplit);
        Assert.Equal(new ProjectMatchTiming(20), editor.BuildTiming());
    }

    [Fact]
    public void SingleProjectDropsHiddenFinalDayConstraintOnlyFromDraftRequest()
    {
        using var fixture = new ScheduleUiFixture();
        var workspace = fixture.Workflow.CurrentSession!.Workspace;
        fixture.Workflow.GenerateSchedule(new([new(new(2026, 10, 3), new(9, 0), new(18, 0), ["B1"])], 1, 30, 4),
            new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []) { RequireChampionshipFinalsOnLastDay = true }, workspace.Revision);
        var saved = fixture.Workflow.CurrentSession!;
        var bytes = File.ReadAllBytes(saved.WorkspacePath);
        using var page = new ScheduleSetupPageViewModel(fixture.Shell, saved);

        Assert.False(page.IsMultiProject);
        Assert.True(page.RequireChampionshipFinalsOnLastDay);
        Assert.False(page.BuildSetup().Policy.RequireChampionshipFinalsOnLastDay);
        Assert.True(saved.Workspace.Schedule!.Policy.RequireChampionshipFinalsOnLastDay);
        Assert.Equal(bytes, File.ReadAllBytes(saved.WorkspacePath));
        Assert.Same(saved, fixture.Workflow.CurrentSession);
    }
}
