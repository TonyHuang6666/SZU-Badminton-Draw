using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Workflows.Tournaments;

public sealed record WorkspaceNotice(string Code, string Message, Guid? ProjectId = null);

public sealed record WorkspaceCommandResult(TournamentWorkspace Workspace, string WorkspacePath,
    string? BackupPath, IReadOnlyList<WorkspaceNotice> Notices)
{
    public IReadOnlyList<WorkspaceNotice> Notices { get; init; } = Array.AsReadOnly(Notices.ToArray());
}
