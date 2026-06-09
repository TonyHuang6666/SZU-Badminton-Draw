namespace BadmintonDraw.Excel;

public sealed record MatchRecordResult(
    string MatchName,
    string DayLabel,
    string Winner,
    string Loser,
    string Score,
    string Duration);
