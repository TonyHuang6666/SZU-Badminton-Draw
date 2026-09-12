using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.Navigation;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class WorkspaceNavigatorTests
{
    [Theory]
    [InlineData(TournamentStage.Draft, TournamentPurpose.FullTournament, WorkspaceRoute.Overview)]
    [InlineData(TournamentStage.RostersReady, TournamentPurpose.FullTournament, WorkspaceRoute.PublicDraw)]
    [InlineData(TournamentStage.DrawsConfirmed, TournamentPurpose.PublicDrawOnly, WorkspaceRoute.PublicDraw)]
    [InlineData(TournamentStage.DrawsConfirmed, TournamentPurpose.FullTournament, WorkspaceRoute.ScheduleSetup)]
    [InlineData(TournamentStage.ScheduleReady, TournamentPurpose.FullTournament, WorkspaceRoute.ScheduleBoard)]
    [InlineData(TournamentStage.InProgress, TournamentPurpose.FullTournament, WorkspaceRoute.Operations)]
    [InlineData(TournamentStage.Completed, TournamentPurpose.FullTournament, WorkspaceRoute.Operations)]
    public void PicksNextActionForStage(TournamentStage stage, TournamentPurpose purpose, WorkspaceRoute route)
        => Assert.Equal(route, WorkspaceNavigator.PreferredRoute(stage, purpose));

    [Fact]
    public void GatesForwardActionsAndAllowsEarlierPagesForEveryStageAndPurpose()
    {
        foreach (var stage in Enum.GetValues<TournamentStage>())
        foreach (var purpose in Enum.GetValues<TournamentPurpose>())
        {
            var navigator = new WorkspaceNavigator();
            navigator.UpdateWorkspace(stage, purpose);
            Assert.True(navigator.CanNavigate(WorkspaceRoute.Overview));
            Assert.True(navigator.CanNavigate(WorkspaceRoute.Rosters));
            Assert.Equal(stage >= TournamentStage.RostersReady, navigator.CanNavigate(WorkspaceRoute.PublicDraw));
            Assert.Equal(purpose == TournamentPurpose.FullTournament && stage >= TournamentStage.DrawsConfirmed,
                navigator.CanNavigate(WorkspaceRoute.ScheduleSetup));
            Assert.Equal(purpose == TournamentPurpose.FullTournament && stage >= TournamentStage.ScheduleReady,
                navigator.CanNavigate(WorkspaceRoute.ScheduleBoard));
            Assert.Equal(purpose == TournamentPurpose.FullTournament && stage >= TournamentStage.ScheduleReady,
                navigator.CanNavigate(WorkspaceRoute.Operations));
        }
    }

    [Fact]
    public void RejectingRoutePreservesCurrentPage()
    {
        var navigator = new WorkspaceNavigator();
        Assert.False(navigator.Navigate(WorkspaceRoute.Overview));
        navigator.UpdateWorkspace(TournamentStage.Draft, TournamentPurpose.FullTournament);
        Assert.True(navigator.Navigate(WorkspaceRoute.Overview));
        Assert.False(navigator.Navigate(WorkspaceRoute.ScheduleSetup));
        Assert.Equal(WorkspaceRoute.Overview, navigator.CurrentRoute);
    }
}
