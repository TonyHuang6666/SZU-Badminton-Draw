namespace BadmintonDraw.Excel;

public sealed record MatchRecordImportResult(
    IReadOnlyDictionary<string, MatchRecordResult> Results,
    IReadOnlyList<string> DayLabels);
