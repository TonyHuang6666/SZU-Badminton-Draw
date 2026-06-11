namespace BadmintonDraw.Excel;

public sealed record MatchRecordImportResult(
    IReadOnlyDictionary<string, MatchRecordResult> Results,
    IReadOnlyList<string> DayLabels,
    int ExpectedMatchCount = 0,
    IReadOnlyList<string>? MissingResultRows = null,
    IReadOnlyList<string>? ValidationIssues = null,
    IReadOnlyList<string>? PendingMatchNames = null,
    IReadOnlyList<string>? TournamentIds = null)
{
    public IReadOnlyList<string> MissingResultRows { get; init; } = MissingResultRows ?? [];

    public IReadOnlyList<string> ValidationIssues { get; init; } = ValidationIssues ?? [];

    public IReadOnlyList<string> PendingMatchNames { get; init; } = PendingMatchNames ?? [];

    public IReadOnlyList<string> TournamentIds { get; init; } = TournamentIds ?? [];

    public bool IsComplete => MissingResultRows.Count == 0 && ValidationIssues.Count == 0;

    public bool HasWarnings => !IsComplete;
}
