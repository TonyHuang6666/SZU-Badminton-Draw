using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Workflows.Tournaments;

public sealed record ResultCorrectionConfirmation(bool AllowCorrections, string? Reason);
public sealed record WorkspaceResultImportFileInfo(string FullPath, string SourceFileName, string ContentHash);
public sealed record WorkspaceResultImportOutcome(WorkspaceCommandResult Command, ResultImportEvaluation Evaluation)
{
    public bool NoChanges => Evaluation.Status == ResultImportEvaluationStatus.NoChanges;
}

/// <summary>Read-only evidence for an explicit confirmation, not a caller-constructible mutation request.</summary>
public sealed class WorkspaceResultImportPreview
{
    internal WorkspaceResultImportPreview(TournamentWorkspaceWorkflow owner, WorkspaceSession source, string identity,
        IEnumerable<WorkspaceResultImportFileInfo> files, ResultImportEvaluation evaluation)
    {
        Owner = owner; Source = source; SourceIdentity = identity;
        Files = Array.AsReadOnly(files.ToArray()); Evaluation = evaluation;
    }
    internal TournamentWorkspaceWorkflow Owner { get; }
    internal WorkspaceSession Source { get; }
    internal string SourceIdentity { get; }
    public long SourceRevision => Source.Workspace.Revision;
    public string WorkspacePath => Source.WorkspacePath;
    public IReadOnlyList<WorkspaceResultImportFileInfo> Files { get; }
    public ResultImportEvaluation Evaluation { get; }
}
