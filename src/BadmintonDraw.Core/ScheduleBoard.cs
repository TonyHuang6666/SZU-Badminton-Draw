namespace BadmintonDraw.Core;

public enum ScheduleBoardKind
{
    SingleEvent,
    CrossEvent
}

public sealed record ScheduleBoardDay(
    string DayLabel,
    TimeOnly StartTime,
    TimeOnly EndTime,
    IReadOnlyList<string> Courts,
    int SlotMinutes,
    IReadOnlyList<TimeOnly> TimeSlots)
{
    public string TimeRange => $"{StartTime:HH:mm}-{EndTime:HH:mm}";
}

public sealed record ScheduleBoardView(
    ScheduleBoardKind Kind,
    IReadOnlyList<ScheduleBoardDay> Days,
    IReadOnlyList<ScheduleBoardItem> Items,
    string EmptyDayText = "当前比赛日没有赛程。")
{
    public IReadOnlyList<string> DayLabels => Days.Select(day => day.DayLabel).ToList();

    public ScheduleBoardDay? FindDay(string? dayLabel)
    {
        if (string.IsNullOrWhiteSpace(dayLabel))
        {
            return null;
        }

        return Days.FirstOrDefault(day => string.Equals(day.DayLabel, dayLabel, StringComparison.Ordinal));
    }

    public IReadOnlyList<ScheduleBoardItem> GetItems(string dayLabel, string court, TimeOnly startTime)
    {
        return Items
            .Where(item => string.Equals(item.DayLabel, dayLabel, StringComparison.Ordinal)
                           && string.Equals(item.Court, court, StringComparison.Ordinal)
                           && item.StartTime == startTime)
            .OrderBy(item => item.SortText, StringComparer.Ordinal)
            .ThenBy(item => item.Order)
            .ThenBy(item => item.Title, StringComparer.Ordinal)
            .ToList();
    }
}

public sealed record ScheduleBoardItem(
    string Key,
    string DragPayload,
    string FocusKey,
    string DayLabel,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string Court,
    int Order,
    string Title,
    string Subtitle,
    string SideText,
    string DetailText = "",
    string Tooltip = "",
    bool IsLocked = false,
    bool IsBlocking = false,
    string SortText = "")
{
    public string TimeRange => $"{StartTime:HH:mm}-{EndTime:HH:mm}";
}

public sealed record ScheduleBoardDropTarget(
    ScheduleBoardKind Kind,
    string DayLabel,
    TimeOnly StartTime,
    string Court);

public enum ScheduleBoardMoveValidationSeverity
{
    Allowed = 0,
    Warning = 1,
    Blocked = 2
}

public sealed record ScheduleBoardMoveValidationResult(
    ScheduleBoardMoveValidationSeverity Severity,
    string Message,
    IReadOnlyList<string>? AffectedMatches = null)
{
    public IReadOnlyList<string> AffectedMatches { get; init; } =
        AffectedMatches ?? Array.Empty<string>();

    public bool CanDrop => Severity != ScheduleBoardMoveValidationSeverity.Blocked;

    public static ScheduleBoardMoveValidationResult Allowed(string message)
    {
        return new ScheduleBoardMoveValidationResult(
            ScheduleBoardMoveValidationSeverity.Allowed,
            message);
    }

    public static ScheduleBoardMoveValidationResult Warning(
        string message,
        IReadOnlyList<string>? affectedMatches = null)
    {
        return new ScheduleBoardMoveValidationResult(
            ScheduleBoardMoveValidationSeverity.Warning,
            message,
            affectedMatches);
    }

    public static ScheduleBoardMoveValidationResult Blocked(
        string message,
        IReadOnlyList<string>? affectedMatches = null)
    {
        return new ScheduleBoardMoveValidationResult(
            ScheduleBoardMoveValidationSeverity.Blocked,
            message,
            affectedMatches);
    }
}

public sealed record ScheduleBoardCascadeMovePreview(
    string MatchName,
    string DayLabel,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string Court,
    IReadOnlyList<ScheduleBoardCascadeMovePreviewItem>? AffectedMatches = null,
    IReadOnlyList<ScheduleBoardCrossEventImpactPreviewItem>? CrossEventImpacts = null,
    string CrossEventImpactNote = "")
{
    public IReadOnlyList<ScheduleBoardCascadeMovePreviewItem> AffectedMatches { get; init; } =
        AffectedMatches ?? Array.Empty<ScheduleBoardCascadeMovePreviewItem>();

    public IReadOnlyList<ScheduleBoardCrossEventImpactPreviewItem> CrossEventImpacts { get; init; } =
        CrossEventImpacts ?? Array.Empty<ScheduleBoardCrossEventImpactPreviewItem>();

    public bool HasAffectedMatches => AffectedMatches.Count > 0;

    public bool HasCrossEventImpact => CrossEventImpacts.Count > 0 || !string.IsNullOrWhiteSpace(CrossEventImpactNote);

    public bool HasPreviewItems => HasAffectedMatches || HasCrossEventImpact;

    public string TargetText => $"{DayLabel} {StartTime:HH:mm}-{EndTime:HH:mm} · {Court}";
}

public sealed record ScheduleBoardCascadeMovePreviewItem(
    int Depth,
    string EventName,
    string MatchName,
    string DayLabel,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string Court,
    string Phase,
    string DependencyText,
    int RestMinutes,
    bool IsCompleted = false)
{
    public string TimeRange => $"{StartTime:HH:mm}-{EndTime:HH:mm}";

    public string DisplayMatchName => string.IsNullOrWhiteSpace(EventName)
        ? MatchName
        : $"{EventName} · {MatchName}";
}

public sealed record ScheduleBoardCrossEventImpactPreviewItem(
    CrossEventConflictSeverity Severity,
    string PlayerName,
    string EventName,
    string MatchName,
    string DayLabel,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string Court,
    string Phase,
    int? RestMinutes,
    string Detail,
    bool IsCompleted = false)
{
    public string TimeRange => $"{StartTime:HH:mm}-{EndTime:HH:mm}";
}

public sealed record ScheduleBoardCascadeMovedItem(
    int Depth,
    string EventName,
    string MatchName,
    string FromDayLabel,
    TimeOnly FromStartTime,
    TimeOnly FromEndTime,
    string FromCourt,
    string ToDayLabel,
    TimeOnly ToStartTime,
    TimeOnly ToEndTime,
    string ToCourt,
    string Reason)
{
    public string FromText => $"{FromDayLabel} {FromStartTime:HH:mm}-{FromEndTime:HH:mm} · {FromCourt}";

    public string ToText => $"{ToDayLabel} {ToStartTime:HH:mm}-{ToEndTime:HH:mm} · {ToCourt}";
}

public sealed record ScheduleBoardCascadeMoveResult<TSchedule>(
    TSchedule Schedule,
    IReadOnlyList<ScheduleBoardCascadeMovedItem>? MovedMatches = null,
    IReadOnlyList<string>? Messages = null)
{
    public IReadOnlyList<ScheduleBoardCascadeMovedItem> MovedMatches { get; init; } =
        MovedMatches ?? Array.Empty<ScheduleBoardCascadeMovedItem>();

    public IReadOnlyList<string> Messages { get; init; } =
        Messages ?? Array.Empty<string>();
}

public static class ScheduleBoardDrag
{
    public const string SingleEventPrefix = "schedule:";

    public static string BuildSingleEventPayload(string matchName)
    {
        return $"{SingleEventPrefix}{matchName}";
    }

    public static bool TryParseSingleEventPayload(string? payload, out string matchName)
    {
        if (!string.IsNullOrWhiteSpace(payload)
            && payload.StartsWith(SingleEventPrefix, StringComparison.Ordinal))
        {
            matchName = payload[SingleEventPrefix.Length..];
            return !string.IsNullOrWhiteSpace(matchName);
        }

        matchName = "";
        return false;
    }
}

public static class ScheduleBoardLayout
{
    public const double MainMinZoom = 0.65;
    public const double WindowMinZoom = 0.25;
    public const double MaxZoom = 1.6;
    public const double ZoomStep = 0.15;

    public static double ClampMainZoom(double zoom)
    {
        return Math.Clamp(zoom, MainMinZoom, MaxZoom);
    }

    public static double ClampWindowZoom(double zoom)
    {
        return Math.Clamp(zoom, WindowMinZoom, MaxZoom);
    }

    public static double Scale(double value, double zoom)
    {
        return Math.Max(1, value * zoom);
    }

    public static double ScaleFont(double value, double zoom)
    {
        return Math.Max(8, value * zoom);
    }
}
