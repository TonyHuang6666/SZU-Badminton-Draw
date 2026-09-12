namespace BadmintonDraw.Core.Tournaments;
public sealed record ProjectRosterWarning(string Code, string Message, int? RowNumber = null);
public sealed record ProjectRoster(IReadOnlyList<DrawParticipant> Participants, string SourceFileName,
    string ContentHash, IReadOnlyList<ProjectRosterWarning> Warnings)
{
    private IReadOnlyList<DrawParticipant> participants = WorkspaceSnapshot.List(Participants);
    private IReadOnlyList<ProjectRosterWarning> warnings = WorkspaceSnapshot.List(Warnings);
    public IReadOnlyList<DrawParticipant> Participants { get => participants; init => participants = WorkspaceSnapshot.List(value); }
    public IReadOnlyList<ProjectRosterWarning> Warnings { get => warnings; init => warnings = WorkspaceSnapshot.List(value); }
}
