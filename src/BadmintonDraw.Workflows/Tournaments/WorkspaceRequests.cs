using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Workflows.Tournaments;

/// <summary>Omit ProjectId for new projects; retained projects keep their identity and discipline.</summary>
public sealed record WorkspaceProjectRequest(EventDiscipline Discipline, CompetitionMode CompetitionMode,
    string? DisplayName = null, Guid? ProjectId = null);

public sealed record CreateWorkspaceRequest(string Name, TournamentKind Kind, TournamentPurpose Purpose,
    IReadOnlyList<WorkspaceProjectRequest> Projects, string WorkspacePath)
{
    public IReadOnlyList<WorkspaceProjectRequest> Projects { get; init; } = Array.AsReadOnly(Projects.ToArray());
}

public sealed record UpdateWorkspaceConfigurationRequest(string Name, IReadOnlyList<WorkspaceProjectRequest> Projects)
{
    public IReadOnlyList<WorkspaceProjectRequest> Projects { get; init; } = Array.AsReadOnly(Projects.ToArray());
}
