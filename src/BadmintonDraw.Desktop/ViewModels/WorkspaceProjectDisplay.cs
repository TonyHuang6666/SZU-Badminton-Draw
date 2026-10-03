using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

internal static class WorkspaceProjectDisplay
{
    internal static string Label(TournamentWorkspace workspace, TournamentProject project) =>
        workspace.Projects.Count(candidate => string.Equals(candidate.DisplayName, project.DisplayName, StringComparison.Ordinal)) > 1
            ? $"{project.DisplayName} · {WorkspaceProjectOptionViewModel.DisciplineName(project.Discipline)}"
            : project.DisplayName;
}
