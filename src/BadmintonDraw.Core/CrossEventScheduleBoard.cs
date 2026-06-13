namespace BadmintonDraw.Core;

public sealed record CrossEventScheduleBoard(
    IReadOnlyList<CrossEventScheduleSource> Sources,
    IReadOnlyList<CrossEventScheduleBoardDay> Days,
    IReadOnlyList<CrossEventScheduleBoardItem> Items,
    IReadOnlyList<CrossEventPlayerMultiEntry> PlayerDetails,
    CrossEventConflictReport Report,
    int MinimumRestMinutes,
    bool HasUnsavedChanges)
{
    public int BlockingConflictItemCount => Items.Count(item => item.IsBlockingConflict);

    public int MultiEventPlayerCount => PlayerDetails.Count;
}

public sealed record CrossEventScheduleBoardDay(
    string DayLabel,
    TimeOnly StartTime,
    TimeOnly EndTime,
    IReadOnlyList<string> Courts,
    int SlotMinutes,
    IReadOnlyList<TimeOnly> TimeSlots)
{
    public string TimeRange => $"{StartTime:HH:mm}-{EndTime:HH:mm}";
}

public sealed record CrossEventScheduleBoardItem(
    string Key,
    string SourceId,
    string EventName,
    string SourcePath,
    EventKind EventKind,
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
    string Note,
    int DurationMinutes,
    bool IsCompleted,
    CrossEventConflictSeverity? ConflictSeverity,
    string ConflictSummary)
{
    public string TimeRange => $"{StartTime:HH:mm}-{EndTime:HH:mm}";

    public bool IsBlockingConflict => ConflictSeverity is CrossEventConflictSeverity.Severe or CrossEventConflictSeverity.Warning;

    public string Status => IsCompleted ? "已完成" : IsBlockingConflict ? "需调整" : "可用";

    public string MatchLabel => $"{EventName} · {MatchName}";
}

public sealed record CrossEventPlayerMultiEntry(
    string PlayerName,
    string NormalizedPlayerName,
    IReadOnlyList<string> EventNames,
    int MatchCount,
    int CompletedMatchCount,
    int PendingMatchCount,
    int SevereIssueCount,
    int WarningIssueCount,
    int? ShortestRestMinutes,
    string NextMatchText,
    IReadOnlyList<CrossEventPlayerScheduleAppearance> Appearances)
{
    public int EventCount => EventNames.Count;

    public bool HasBlockingIssues => SevereIssueCount > 0 || WarningIssueCount > 0;
}

public sealed record CrossEventPlayerScheduleAppearance(
    string ItemKey,
    string EventName,
    string DayLabel,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string Court,
    string Phase,
    string MatchName,
    string Side,
    string SideText,
    string OpponentText,
    bool IsCompleted,
    CrossEventConflictSeverity? ConflictSeverity,
    string ConflictSummary)
{
    public string TimeRange => $"{StartTime:HH:mm}-{EndTime:HH:mm}";

    public string Status => IsCompleted
        ? "已完成"
        : ConflictSeverity is CrossEventConflictSeverity.Severe
            ? "严重冲突"
            : ConflictSeverity is CrossEventConflictSeverity.Warning
                ? "间隔过短"
                : "可用";
}

public sealed record CrossEventScheduleAutoAdjustResult(
    CrossEventScheduleBoard Board,
    int MovedCount,
    int RemainingBlockingConflictItemCount,
    IReadOnlyList<string> Messages);

public sealed record CrossEventScheduleSaveResult(
    IReadOnlyList<string> UpdatedPaths,
    IReadOnlyList<string> BackupPaths);
