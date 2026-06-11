using BadmintonDraw.Core;

namespace BadmintonDraw.Excel;

public sealed record TournamentProgressSnapshot(
    string TournamentId,
    string EventName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? SourceInputPath,
    DrawResult DrawResult,
    IReadOnlyList<DrawParticipant> Participants,
    IReadOnlyList<ParticipantImportWarning> ImportWarnings,
    SchedulePlan Schedule);

public sealed record TournamentProgressState(
    TournamentProgressSnapshot Snapshot,
    IReadOnlyDictionary<string, MatchRecordResult> Results,
    IReadOnlyList<string> PendingMatchNames,
    IReadOnlyList<string> ProcessedDayLabels,
    IReadOnlyList<TournamentProgressImportLog> ImportLogs)
{
    public int RemainingMatchCount =>
        Snapshot.Schedule.Matches.Count(match => !Results.ContainsKey(match.MatchName));

    public MatchRecordImportResult BuildCumulativeImportResult()
    {
        return new MatchRecordImportResult(
            Results,
            ProcessedDayLabels,
            Results.Count + PendingMatchNames.Count,
            PendingMatchNames.Select(matchName => $"{matchName} 尚未填写胜方").ToList(),
            [],
            PendingMatchNames,
            [Snapshot.TournamentId]);
    }
}

public sealed record TournamentProgressImportLog(
    long Id,
    DateTimeOffset ImportedAt,
    string SourceFileName,
    string SourcePath,
    string SourceHash,
    int ExpectedMatchCount,
    int ImportedResultCount,
    int WarningCount,
    int CorrectionCount);

public sealed record TournamentProgressCorrection(
    string MatchName,
    MatchRecordResult Existing,
    MatchRecordResult Replacement);

public sealed record TournamentProgressImportPreview(
    MatchRecordImportResult SelectedImportResult,
    MatchRecordImportResult ProjectedCumulativeResult,
    IReadOnlyList<TournamentProgressCorrection> Corrections,
    IReadOnlyList<string> DuplicateFiles,
    IReadOnlyList<string> CompatibilityWarnings,
    int NewResultCount,
    int FilesToImport)
{
    public bool HasWarnings =>
        SelectedImportResult.HasWarnings
        || Corrections.Count > 0
        || CompatibilityWarnings.Count > 0;
}

public sealed record TournamentProgressImportOutcome(
    TournamentProgressState State,
    TournamentProgressImportPreview Preview,
    string? BackupPath);
