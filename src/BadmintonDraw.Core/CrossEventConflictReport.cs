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

public sealed record CrossEventPlayerIdentity(
    string Name,
    string StudentId = "",
    bool IsTeam = false)
{
    public string DisplayName => string.IsNullOrWhiteSpace(StudentId)
        ? Name.Trim()
        : $"{Name.Trim()}（{StudentId.Trim()}）";

    public string IdentityKey
    {
        get
        {
            var studentId = NormalizeStudentId(StudentId);
            if (!IsTeam && !string.IsNullOrWhiteSpace(studentId))
            {
                return $"student:{studentId}";
            }

            var normalizedName = NormalizeName(Name);
            return $"{(IsTeam ? "team" : "name")}:{normalizedName}";
        }
    }

    public static CrossEventPlayerIdentity FromName(string name, bool isTeam = false)
    {
        return new CrossEventPlayerIdentity(name, "", isTeam);
    }

    private static string NormalizeName(string value)
    {
        return string.Concat(value.Trim().Where(character => !char.IsWhiteSpace(character)));
    }

    private static string NormalizeStudentId(string value)
    {
        return string.Concat(value.Trim().Where(character => !char.IsWhiteSpace(character)));
    }
}

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
    bool IsCompleted = false,
    string MatchId = "",
    IReadOnlyList<ScheduleMatchDependency>? Dependencies = null,
    IReadOnlyList<CrossEventPlayerIdentity>? SideAPlayerIdentities = null,
    IReadOnlyList<CrossEventPlayerIdentity>? SideBPlayerIdentities = null)
{
    public string MatchId { get; init; } = string.IsNullOrWhiteSpace(MatchId) ? MatchName : MatchId;

    public IReadOnlyList<ScheduleMatchDependency> Dependencies { get; init; } = Dependencies ?? Array.Empty<ScheduleMatchDependency>();

    public IReadOnlyList<CrossEventPlayerIdentity> SideAPlayerIdentities { get; init; } = NormalizeIdentities(SideAPlayerIdentities, SideAPlayers);

    public IReadOnlyList<CrossEventPlayerIdentity> SideBPlayerIdentities { get; init; } = NormalizeIdentities(SideBPlayerIdentities, SideBPlayers);

    public string TimeRange => $"{StartTime:HH:mm}-{EndTime:HH:mm}";

    public int DurationMinutes => Math.Max(1, (int)(EndTime - StartTime).TotalMinutes);

    private static IReadOnlyList<CrossEventPlayerIdentity> NormalizeIdentities(
        IReadOnlyList<CrossEventPlayerIdentity>? identities,
        IReadOnlyList<string> fallbackNames)
    {
        var source = identities is { Count: > 0 }
            ? identities
            : fallbackNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => CrossEventPlayerIdentity.FromName(name))
                .ToList();
        return source
            .Where(identity => !string.IsNullOrWhiteSpace(identity.Name))
            .GroupBy(identity => identity.IdentityKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }
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
