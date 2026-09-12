using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

/// <summary>Refresh the immutable snapshot without replacing selections, tabs or unfinished edits.</summary>
public abstract class WorkspacePageViewModel(WorkspaceSession session) : ViewModelBase
{
    private WorkspaceSession session = session;
    public WorkspaceSession Session { get => session; private set => SetProperty(ref session, value); }
    public virtual void RefreshSession(WorkspaceSession next) { Session = next; RefreshAvailability(); }
    public virtual void RefreshAvailability() { }
}
