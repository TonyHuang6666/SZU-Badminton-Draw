using System.Text.Json.Serialization;

namespace BadmintonDraw.Core;

[method: JsonConstructor]
public sealed record SchedulePlan(
    IReadOnlyList<ScheduledMatch> Matches,
    ScheduleSettings Settings,
    IReadOnlyList<UnscheduledMatchPreview> UnscheduledMatches,
    ScheduleQualityReport? QualityReport = null)
{
    public SchedulePlan(IReadOnlyList<ScheduledMatch> Matches, ScheduleSettings Settings)
        : this(Matches, Settings, Array.Empty<UnscheduledMatchPreview>())
    {
    }

    public bool IsComplete => UnscheduledMatches.Count == 0;

    public int TotalMatchCount => Matches.Count + UnscheduledMatches.Count;

    public int DayCount => Matches
        .Select(match => match.DayLabel)
        .Distinct(StringComparer.Ordinal)
        .Count();
}
