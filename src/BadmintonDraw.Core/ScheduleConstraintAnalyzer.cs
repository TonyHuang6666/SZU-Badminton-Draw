using System.Text.RegularExpressions;

namespace BadmintonDraw.Core;

public sealed class ScheduleConstraintAnalyzer
{
    public ScheduleConstraintReport Analyze(SchedulePlan schedule)
    {
        var rules = ScheduleConstraintRules.For(schedule.Settings.ConstraintProfile);
        var issues = new List<ScheduleConstraintIssue>();
        var appearances = BuildAppearances(schedule.Matches);

        AddRestIssues(issues, appearances, rules);
        AddDailyLoadIssues(issues, appearances, schedule.Settings, rules);
        AddKeyMatchTimeIssues(issues, schedule.Matches, rules);

        return new ScheduleConstraintReport(schedule.Settings.ConstraintProfile, rules, issues
            .OrderByDescending(issue => issue.Severity)
            .ThenBy(issue => issue.DayLabel, StringComparer.Ordinal)
            .ThenBy(issue => issue.StartTime ?? TimeOnly.MinValue)
            .ThenBy(issue => issue.MatchName, StringComparer.Ordinal)
            .ToList());
    }

    private static void AddRestIssues(
        List<ScheduleConstraintIssue> issues,
        IReadOnlyList<PlayerAppearance> appearances,
        ScheduleConstraintRules rules)
    {
        foreach (var group in appearances.GroupBy(appearance => appearance.NormalizedPlayerName, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group
                .OrderBy(appearance => appearance.DayLabel, StringComparer.Ordinal)
                .ThenBy(appearance => appearance.Match.StartTime)
                .ToList();
            for (var index = 1; index < ordered.Count; index++)
            {
                var previous = ordered[index - 1];
                var current = ordered[index];
                if (!string.Equals(previous.DayLabel, current.DayLabel, StringComparison.Ordinal))
                {
                    continue;
                }

                var restMinutes = (int)(current.Match.StartTime.ToTimeSpan() - previous.Match.EndTime.ToTimeSpan()).TotalMinutes;
                var requiredRest = IsKeyMatch(current.Match)
                    ? rules.KeyMatchMinimumRestMinutes
                    : rules.MinimumRestMinutes;
                if (restMinutes >= requiredRest)
                {
                    continue;
                }

                var severity = restMinutes < 0
                    ? ScheduleConstraintSeverity.Severe
                    : ScheduleConstraintSeverity.Warning;
                var message = restMinutes < 0
                    ? $"{current.PlayerName} 在 {current.DayLabel} 有重叠比赛：{previous.Match.MatchName} 与 {current.Match.MatchName}。"
                    : $"{current.PlayerName} 在 {current.DayLabel} 两场比赛间隔 {restMinutes} 分钟，低于{rules.ProfileName}要求的 {requiredRest} 分钟。";
                issues.Add(new ScheduleConstraintIssue(
                    severity,
                    ScheduleConstraintIssueType.ShortRest,
                    current.DayLabel,
                    current.Match.StartTime,
                    current.Match.Court,
                    current.Match.Phase,
                    current.Match.MatchName,
                    current.PlayerName,
                    message));
            }
        }
    }

    private static void AddDailyLoadIssues(
        List<ScheduleConstraintIssue> issues,
        IReadOnlyList<PlayerAppearance> appearances,
        ScheduleSettings settings,
        ScheduleConstraintRules rules)
    {
        var dailyLimit = Math.Max(
            settings.MaxMatchesPerEntrantPerDay,
            settings.BeforeBoundaryTiming?.MaxMatchesPerEntrantPerDay ?? 0);
        foreach (var group in appearances
                     .GroupBy(appearance => $"{appearance.NormalizedPlayerName}\u001F{appearance.DayLabel}", StringComparer.OrdinalIgnoreCase))
        {
            var playerName = group.First().PlayerName;
            var count = group.Select(appearance => appearance.Match.MatchName).Distinct(StringComparer.Ordinal).Count();
            if (count < dailyLimit)
            {
                continue;
            }

            var first = group.OrderBy(appearance => appearance.Match.StartTime).First();
            var severity = count > dailyLimit ? ScheduleConstraintSeverity.Warning : ScheduleConstraintSeverity.Notice;
            var action = count > dailyLimit ? "超过" : "达到";
            issues.Add(new ScheduleConstraintIssue(
                severity,
                ScheduleConstraintIssueType.DailyLoad,
                first.DayLabel,
                first.Match.StartTime,
                first.Match.Court,
                first.Match.Phase,
                first.Match.MatchName,
                playerName,
                $"{playerName} 在 {first.DayLabel} 已{action}每日场次上限：{count}/{dailyLimit} 场。{rules.ProfileName}下建议裁判长确认体能安排。"));
        }
    }

    private static void AddKeyMatchTimeIssues(
        List<ScheduleConstraintIssue> issues,
        IReadOnlyList<ScheduledMatch> matches,
        ScheduleConstraintRules rules)
    {
        foreach (var match in matches.Where(IsFinalMatch))
        {
            if (match.StartTime >= rules.PreferredFinalStartTime)
            {
                continue;
            }

            issues.Add(new ScheduleConstraintIssue(
                ScheduleConstraintSeverity.Notice,
                ScheduleConstraintIssueType.KeyMatchTiming,
                match.DayLabel,
                match.StartTime,
                match.Court,
                match.Phase,
                match.MatchName,
                null,
                $"{match.MatchName} 在 {match.StartTime:HH:mm} 开始，早于建议的重点场次时段 {rules.PreferredFinalStartTime:HH:mm}；如需观赏性或颁奖衔接，可人工调整。"));
        }
    }

    private static IReadOnlyList<PlayerAppearance> BuildAppearances(IReadOnlyList<ScheduledMatch> matches)
    {
        return matches
            .SelectMany(match => ExtractPlayerNames(match.SideA)
                .Concat(ExtractPlayerNames(match.SideB))
                .Select(player => new PlayerAppearance(player, NormalizeName(player), match)))
            .ToList();
    }

    private static IReadOnlyList<string> ExtractPlayerNames(string side)
    {
        var text = side.Trim();
        if (string.IsNullOrWhiteSpace(text)
            || text.EndsWith("胜者", StringComparison.Ordinal)
            || text.EndsWith("负者", StringComparison.Ordinal)
            || text.Contains("待定", StringComparison.Ordinal)
            || text.Contains("轮空", StringComparison.Ordinal))
        {
            return [];
        }

        if (text.Length >= 2 && text[0] == '[' && text[^1] == ']')
        {
            text = text[1..^1].Trim();
            return Regex.Split(text, @"\s+")
                .Select(part => part.Trim())
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return [text];
    }

    private static string NormalizeName(string value)
    {
        var text = value.Trim();
        if (text.Length >= 2 && text[0] == '[' && text[^1] == ']')
        {
            text = text[1..^1].Trim();
        }

        return Regex.Replace(text, @"\s+", " ");
    }

    private static bool IsKeyMatch(ScheduledMatch match)
    {
        return match.Phase.Contains("半决赛", StringComparison.Ordinal)
               || match.Phase.Contains("决赛", StringComparison.Ordinal)
               || match.MatchName.Contains("半决赛", StringComparison.Ordinal)
               || match.MatchName.Contains("决赛", StringComparison.Ordinal)
               || match.Phase.Contains("名赛", StringComparison.Ordinal)
               || match.MatchName.Contains("名赛", StringComparison.Ordinal);
    }

    private static bool IsFinalMatch(ScheduledMatch match)
    {
        return match.Phase.Contains("决赛", StringComparison.Ordinal)
               || match.MatchName.Contains("决赛", StringComparison.Ordinal);
    }

    private sealed record PlayerAppearance(
        string PlayerName,
        string NormalizedPlayerName,
        ScheduledMatch Match)
    {
        public string DayLabel => Match.DayLabel;
    }
}

public enum ScheduleConstraintProfile
{
    Campus = 0,
    Formal = 1
}

public enum ScheduleConstraintSeverity
{
    Notice = 0,
    Warning = 1,
    Severe = 2
}

public enum ScheduleConstraintIssueType
{
    ShortRest,
    DailyLoad,
    KeyMatchTiming
}

public sealed record ScheduleConstraintRules(
    ScheduleConstraintProfile Profile,
    string ProfileName,
    int MinimumRestMinutes,
    int KeyMatchMinimumRestMinutes,
    TimeOnly PreferredFinalStartTime)
{
    public static ScheduleConstraintRules For(ScheduleConstraintProfile profile)
    {
        return profile switch
        {
            ScheduleConstraintProfile.Formal => new ScheduleConstraintRules(
                profile,
                "正式赛",
                MinimumRestMinutes: 30,
                KeyMatchMinimumRestMinutes: 60,
                PreferredFinalStartTime: new TimeOnly(16, 0)),
            _ => new ScheduleConstraintRules(
                ScheduleConstraintProfile.Campus,
                "校园赛",
                MinimumRestMinutes: 20,
                KeyMatchMinimumRestMinutes: 30,
                PreferredFinalStartTime: new TimeOnly(16, 0))
        };
    }
}

public sealed record ScheduleConstraintReport(
    ScheduleConstraintProfile Profile,
    ScheduleConstraintRules Rules,
    IReadOnlyList<ScheduleConstraintIssue> Issues)
{
    public bool HasIssues => Issues.Count > 0;

    public int SevereCount => Issues.Count(issue => issue.Severity == ScheduleConstraintSeverity.Severe);

    public int WarningCount => Issues.Count(issue => issue.Severity == ScheduleConstraintSeverity.Warning);

    public int NoticeCount => Issues.Count(issue => issue.Severity == ScheduleConstraintSeverity.Notice);
}

public sealed record ScheduleConstraintIssue(
    ScheduleConstraintSeverity Severity,
    ScheduleConstraintIssueType Type,
    string DayLabel,
    TimeOnly? StartTime,
    string? Court,
    string Phase,
    string MatchName,
    string? PlayerName,
    string Message);
