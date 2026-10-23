using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Workflows.Tournaments;

public sealed record WorkspaceDayConfirmationRequest(DateOnly Day, IReadOnlyList<WorkspaceMatchPostponement> Postponements);

public sealed class WorkspaceDayConfirmationPreview
{
    internal WorkspaceDayConfirmationPreview(object owner, Guid session, string identity, TournamentWorkspace candidate,
        DateOnly day, IReadOnlyList<ScheduleEditChange> changes, IReadOnlyList<string> errors, int completed, int postponed)
    {
        Owner = owner; Session = session; SourceIdentity = identity; Candidate = candidate; Day = day;
        Changes = Array.AsReadOnly(changes.ToArray()); Errors = Array.AsReadOnly(errors.ToArray());
        CompletedCount = completed; PostponedCount = postponed;
    }
    internal object Owner { get; }
    internal Guid Session { get; }
    internal string SourceIdentity { get; }
    internal TournamentWorkspace Candidate { get; }
    public DateOnly Day { get; }
    public IReadOnlyList<ScheduleEditChange> Changes { get; }
    public IReadOnlyList<string> Errors { get; }
    public IReadOnlyList<string> Warnings { get; } = Array.Empty<string>();
    public int CompletedCount { get; }
    public int PostponedCount { get; }
    public bool CanConfirm => Errors.Count == 0;
}
