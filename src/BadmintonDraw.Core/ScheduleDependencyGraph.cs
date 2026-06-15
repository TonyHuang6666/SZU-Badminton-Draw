namespace BadmintonDraw.Core;

public sealed class ScheduleDependencyGraph
{
    private readonly IReadOnlyDictionary<string, int> _dayNumbers;

    private ScheduleDependencyGraph(
        IReadOnlyList<ScheduleDependencyEdge> edges,
        IReadOnlyDictionary<string, int> dayNumbers)
    {
        Edges = edges;
        _dayNumbers = dayNumbers;
    }

    public IReadOnlyList<ScheduleDependencyEdge> Edges { get; }

    public static ScheduleDependencyGraph Build(SchedulePlan schedule)
    {
        var matchesById = schedule.Matches
            .Where(match => !string.IsNullOrWhiteSpace(match.MatchId))
            .GroupBy(match => match.MatchId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var edges = schedule.Matches
            .SelectMany(dependent => dependent.Dependencies.Select(dependency => (Dependent: dependent, Dependency: dependency)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Dependency.SourceMatchId))
            .Select(item => matchesById.TryGetValue(item.Dependency.SourceMatchId, out var source)
                ? new ScheduleDependencyEdge(source, item.Dependent, item.Dependency)
                : null)
            .Where(edge => edge is not null)
            .Select(edge => edge!)
            .ToList();

        return new ScheduleDependencyGraph(edges, BuildDayNumberLookup(schedule.Settings, schedule.Matches));
    }

    public IReadOnlyList<ScheduleDependencyOrderViolation> FindOrderViolations()
    {
        return Edges
            .Select(edge => new ScheduleDependencyOrderViolation(edge, GetRestMinutes(edge)))
            .Where(violation => violation.RestMinutes < 0)
            .OrderBy(violation => violation.Edge.Dependent.DayLabel, StringComparer.Ordinal)
            .ThenBy(violation => violation.Edge.Dependent.StartTime)
            .ThenBy(violation => violation.Edge.Dependent.MatchName, StringComparer.Ordinal)
            .ToList();
    }

    public void EnsureDependencyOrder()
    {
        var violation = FindOrderViolations().FirstOrDefault();
        if (violation is null)
        {
            return;
        }

        throw new DrawValidationException(BuildOrderViolationMessage(violation.Edge));
    }

    public int GetRestMinutes(ScheduleDependencyEdge edge)
    {
        return BuildComparableMinute(edge.Dependent.DayLabel, edge.Dependent.StartTime, _dayNumbers)
            - BuildComparableMinute(edge.Source.DayLabel, edge.Source.EndTime, _dayNumbers);
    }

    public static string FormatOutcome(ScheduleMatchDependencyOutcome outcome)
    {
        return outcome == ScheduleMatchDependencyOutcome.Loser ? "负者" : "胜者";
    }

    private static string BuildOrderViolationMessage(ScheduleDependencyEdge edge)
    {
        return $"赛程顺序错误：{edge.Dependent.MatchName} 依赖 {edge.Source.MatchName} 的{FormatOutcome(edge.Dependency.Outcome)}，但后续场次在前序场次结束前开始（{edge.Source.DayLabel} {edge.Source.TimeRange} → {edge.Dependent.DayLabel} {edge.Dependent.TimeRange}）。请先调整前序场次或后续场次。";
    }

    private static int BuildComparableMinute(
        string dayLabel,
        TimeOnly time,
        IReadOnlyDictionary<string, int> dayNumbers)
    {
        var dayNumber = dayNumbers.TryGetValue(dayLabel, out var value) ? value : 0;
        return (dayNumber * 24 * 60) + (time.Hour * 60) + time.Minute;
    }

    private static IReadOnlyDictionary<string, int> BuildDayNumberLookup(
        ScheduleSettings settings,
        IReadOnlyList<ScheduledMatch> matches)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var day in settings.Days)
        {
            result[day.DayLabel] = day.Date.DayNumber;
        }

        foreach (var match in matches)
        {
            if (result.ContainsKey(match.DayLabel))
            {
                continue;
            }

            if (DateOnly.TryParseExact(match.DayLabel, "yyyy-MM-dd", out var date))
            {
                result[match.DayLabel] = date.DayNumber;
            }
        }

        var fallbackStart = result.Count == 0 ? 1_000_000 : result.Values.Max() + 1;
        foreach (var dayLabel in matches
                     .Select(match => match.DayLabel)
                     .Where(dayLabel => !result.ContainsKey(dayLabel))
                     .Distinct(StringComparer.Ordinal))
        {
            result[dayLabel] = fallbackStart++;
        }

        return result;
    }
}

public sealed record ScheduleDependencyEdge(
    ScheduledMatch Source,
    ScheduledMatch Dependent,
    ScheduleMatchDependency Dependency);

public sealed record ScheduleDependencyOrderViolation(
    ScheduleDependencyEdge Edge,
    int RestMinutes);
