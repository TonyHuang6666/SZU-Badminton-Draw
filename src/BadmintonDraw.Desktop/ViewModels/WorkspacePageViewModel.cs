using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

/// <summary>Refresh the immutable snapshot without replacing selections, tabs or unfinished edits.</summary>
/// <remarks>
/// Session is the latest read snapshot, not an editor's accepted write baseline. Pages retaining mutable
/// inputs must separately compare the fields they intend to replace with their editor baseline. If those
/// fields change, preserve the inputs but block submission until the user explicitly reloads or reconciles
/// that editor. Only unrelated snapshot changes may advance the command revision without reconciliation.
/// </remarks>
public abstract class WorkspacePageViewModel(WorkspaceSession session) : ViewModelBase
{
    private WorkspaceSession session = session;
    public WorkspaceSession Session { get => session; private set => SetProperty(ref session, value); }
    public virtual void RefreshSession(WorkspaceSession next) { Session = next; RefreshAvailability(); }
    public virtual void RefreshAvailability() { }
}
