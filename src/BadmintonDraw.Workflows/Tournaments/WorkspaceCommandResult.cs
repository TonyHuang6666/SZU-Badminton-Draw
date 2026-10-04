using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Core.Scheduling;

namespace BadmintonDraw.Workflows.Tournaments;

public sealed record WorkspaceNotice(string Code, string Message, Guid? ProjectId = null);

public sealed record WorkspaceCommandResult(TournamentWorkspace Workspace, string WorkspacePath,
    string? BackupPath, IReadOnlyList<WorkspaceNotice> Notices)
{
    public IReadOnlyList<WorkspaceNotice> Notices { get; init; } = Array.AsReadOnly(Notices.ToArray());
    public TournamentScheduleQuality? SchedulingQuality { get; init; }
    public SchedulingRunDiagnostics? SchedulingDiagnostics { get; init; }
}
