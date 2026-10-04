using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Workflows.Tournaments;

public sealed record ResultImportEvaluationOptions(bool AllowCorrections, string? CorrectionReason, DateTimeOffset ImportedAt, Guid OperationId);
public enum ResultImportEvaluationStatus { Rejected, RequiresConfirmation, NoChanges, Ready }
public enum ResultImportDiagnosticSeverity { Info, Warning, Blocking }
public enum ResultImportFileStatus { New, DuplicateInBatch, PreviouslyImported, PreviouslyVoided, Invalid }
public sealed record ResultImportSource(string ContentHash, string SourceFileName, string SourcePath, WorkspaceRecordLocation? Location = null, string? Field = null);
public sealed record ResultImportCounts(int NewFileCount, int DuplicateFileCount, int AddedResultCount, int CorrectionCount, int PendingRowCount);

public sealed class ResultImportDiagnostic
{
    public string Code { get; }
    public ResultImportDiagnosticSeverity Severity { get; }
    public string Message { get; }
    public ResultImportSource? Source { get; }
    public IReadOnlyList<ResultImportSource> RelatedSources { get; }
    internal ResultImportDiagnostic(string code, ResultImportDiagnosticSeverity severity, string message, ResultImportSource? source = null,
        IEnumerable<ResultImportSource>? relatedSources = null)
    { Code = code; Severity = severity; Message = message; Source = source; RelatedSources = Array.AsReadOnly((relatedSources ?? []).ToArray()); }
}

public sealed class ResultImportFileEvaluation
{
    public ResultImportSource Source { get; }
    public ResultImportFileStatus Status { get; }
    public int RowCount { get; }
    internal ResultImportFileEvaluation(ResultImportSource source, ResultImportFileStatus status, int rowCount)
    { Source = source; Status = status; RowCount = rowCount; }
}

public sealed class ResultImportCorrection
{
    public WorkspaceMatchKey Key { get; }
    public TournamentMatchResult Before { get; }
    public TournamentMatchResult After { get; }
    public ResultImportSource Source { get; }
    internal ResultImportCorrection(TournamentMatchResult before, TournamentMatchResult after, ResultImportSource source)
    { Key = before.Key; Before = before; After = after; Source = source; }
}

/// <summary>Only Ready exposes a complete validated candidate. This is not a session-bound apply permission.</summary>
public sealed class ResultImportEvaluation
{
    public ResultImportEvaluationStatus Status { get; }
    public IReadOnlyList<ResultImportDiagnostic> Diagnostics { get; }
    public IReadOnlyList<ResultImportFileEvaluation> Files { get; }
    public IReadOnlyList<ResultImportCorrection> Corrections { get; }
    public ResultImportCounts ProposedCounts { get; }
    public TournamentWorkspace? Candidate { get; }
    internal ResultImportEvaluation(ResultImportEvaluationStatus status, IEnumerable<ResultImportDiagnostic> diagnostics,
        IEnumerable<ResultImportFileEvaluation> files, IEnumerable<ResultImportCorrection> corrections, ResultImportCounts counts,
        TournamentWorkspace? candidate = null)
    {
        if ((status == ResultImportEvaluationStatus.Ready) != (candidate is not null)) throw new ArgumentException("Only Ready has a candidate.");
        Status = status; Diagnostics = Array.AsReadOnly(diagnostics.ToArray()); Files = Array.AsReadOnly(files.ToArray());
        Corrections = Array.AsReadOnly(corrections.ToArray()); ProposedCounts = counts; Candidate = candidate;
    }
}
