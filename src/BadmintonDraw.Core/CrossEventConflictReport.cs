namespace BadmintonDraw.Core;

public enum CrossEventConflictSeverity
{
    Severe = 1,
    Warning = 2,
    Notice = 3
}

public sealed record CrossEventScheduleSource(
    string SourceId,
    string EventName,
    string SourcePath,
    EventKind EventKind,
    IReadOnlyList<CrossEventScheduledMatch> Matches,
    int UnresolvedSideCount = 0,
    ScheduleSettings? ScheduleSettings = null);

public sealed record CrossEventScheduledMatch(
    int Order,
    string DayLabel,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string Court,
    string GroupName,
    string Phase,
    string MatchName,
    string SideA,
    string SideB,
    IReadOnlyList<string> SideAPlayers,
    IReadOnlyList<string> SideBPlayers,
    int GroupNumber = 0,
    string Note = "",
    bool SameUnit = false,
    bool IsCompleted = false)
{
    public string TimeRange => $"{StartTime:HH:mm}-{EndTime:HH:mm}";

    public int DurationMinutes => Math.Max(1, (int)(EndTime - StartTime).TotalMinutes);
}

public sealed record CrossEventPlayerAppearance(
    string SourceId,
    string EventName,
    string SourcePath,
    EventKind EventKind,
    int MatchOrder,
    string DayLabel,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string Court,
    string GroupName,
    string Phase,
    string MatchName,
    string Side,
    string SideText,
    string OpponentText)
{
    public string TimeRange => $"{StartTime:HH:mm}-{EndTime:HH:mm}";
}

public sealed record CrossEventConflictIssue(
    CrossEventConflictSeverity Severity,
    string PlayerName,
    string NormalizedPlayerName,
    string DayLabel,
    int? RestMinutes,
    CrossEventPlayerAppearance FirstMatch,
    CrossEventPlayerAppearance SecondMatch,
    string Detail);

public sealed record CrossEventConflictSourceSummary(
    string SourceId,
    string EventName,
    string SourcePath,
    EventKind EventKind,
    int MatchCount,
    int KnownPlayerAppearanceCount,
    int UnresolvedSideCount);

public sealed record CrossEventConflictReport(
    IReadOnlyList<CrossEventConflictSourceSummary> Sources,
    IReadOnlyList<CrossEventConflictIssue> Issues,
    int MinimumRestMinutes)
{
    public int SevereCount => Issues.Count(issue => issue.Severity == CrossEventConflictSeverity.Severe);

    public int WarningCount => Issues.Count(issue => issue.Severity == CrossEventConflictSeverity.Warning);

    public int NoticeCount => Issues.Count(issue => issue.Severity == CrossEventConflictSeverity.Notice);

    public bool HasIssues => Issues.Count > 0;
}
