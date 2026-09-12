using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Workflows.Tournaments;

/// <summary>A published immutable snapshot. RequiresReload blocks writes after an unreadable committed save.</summary>
public sealed record WorkspaceSession(TournamentWorkspace Workspace, string WorkspacePath, bool RequiresReload = false);
