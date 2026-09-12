using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.ViewModels;

namespace BadmintonDraw.Desktop.Navigation;

public sealed class WorkspaceNavigator : ViewModelBase
{
    private TournamentStage? stage;
    private TournamentPurpose purpose;
    private WorkspaceRoute currentRoute = WorkspaceRoute.Start;
    public WorkspaceRoute CurrentRoute { get => currentRoute; private set => SetProperty(ref currentRoute, value); }
    public void UpdateWorkspace(TournamentStage stage, TournamentPurpose purpose) { this.stage = stage; this.purpose = purpose; }
    public bool CanNavigate(WorkspaceRoute route) => route switch
    {
        WorkspaceRoute.Start or WorkspaceRoute.NewWorkspace => true,
        WorkspaceRoute.Overview or WorkspaceRoute.Rosters => stage is not null,
        WorkspaceRoute.PublicDraw => stage >= TournamentStage.RostersReady,
        WorkspaceRoute.ScheduleSetup => purpose == TournamentPurpose.FullTournament && stage >= TournamentStage.DrawsConfirmed,
        WorkspaceRoute.ScheduleBoard or WorkspaceRoute.Operations => purpose == TournamentPurpose.FullTournament && stage >= TournamentStage.ScheduleReady,
        _ => false
    };
    public bool Navigate(WorkspaceRoute route)
    {
        if (!CanNavigate(route)) return false;
        CurrentRoute = route;
        return true;
    }
    public static WorkspaceRoute PreferredRoute(TournamentStage stage, TournamentPurpose purpose) => stage switch
    {
        TournamentStage.Draft => WorkspaceRoute.Overview,
        TournamentStage.RostersReady => WorkspaceRoute.PublicDraw,
        TournamentStage.DrawsConfirmed => purpose == TournamentPurpose.PublicDrawOnly ? WorkspaceRoute.PublicDraw : WorkspaceRoute.ScheduleSetup,
        TournamentStage.ScheduleReady => WorkspaceRoute.ScheduleBoard,
        _ => WorkspaceRoute.Operations
    };
}
