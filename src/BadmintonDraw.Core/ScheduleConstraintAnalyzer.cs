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
                .GroupBy(BuildAppearanceKey, StringComparer.Ordinal)
                .Select(item => item.First())
                .OrderBy(appearance => appearance.DayLabel, StringComparer.Ordinal)
                .ThenBy(appearance => appearance.Match.StartTime)
                .ToList();
            for (var index = 1; index < ordered.Count; index++)
            {
                var current = ordered[index];
                var previous = FindLatestCompatiblePreviousAppearance(ordered, index);
                if (previous is null)
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
                var isProjected = previous.IsProjected || current.IsProjected;
                var subject = isProjected
                    ? $"{current.PlayerName} 可能在 {current.DayLabel}"
                    : $"{current.PlayerName} 在 {current.DayLabel}";
                var sourceNote = isProjected ? "（根据胜者/负者占位推断）" : "";
                var message = restMinutes < 0
                    ? $"{subject} 有重叠比赛：{previous.Match.MatchName} 与 {current.Match.MatchName}。{sourceNote}"
                    : $"{subject} 两场比赛间隔 {restMinutes} 分钟，低于{rules.ProfileName}要求的 {requiredRest} 分钟。{sourceNote}";
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
            var appearanceGroup = group
                .GroupBy(BuildAppearanceKey, StringComparer.Ordinal)
                .Select(item => item.First())
                .ToList();
            var playerName = appearanceGroup.First().PlayerName;
            var count = CountMaximumCompatibleMatches(appearanceGroup);
            if (count < dailyLimit)
            {
                continue;
            }

            var first = appearanceGroup.OrderBy(appearance => appearance.Match.StartTime).First();
            var severity = count > dailyLimit ? ScheduleConstraintSeverity.Warning : ScheduleConstraintSeverity.Notice;
            var action = count > dailyLimit ? "超过" : "达到";
            var certainty = appearanceGroup.Any(appearance => appearance.IsProjected) ? "可能" : "已";
            issues.Add(new ScheduleConstraintIssue(
                severity,
                ScheduleConstraintIssueType.DailyLoad,
                first.DayLabel,
                first.Match.StartTime,
                first.Match.Court,
                first.Match.Phase,
                first.Match.MatchName,
                playerName,
                $"{playerName} 在 {first.DayLabel} {certainty}{action}每日场次上限：{count}/{dailyLimit} 场。{rules.ProfileName}下建议裁判长确认体能安排。"));
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
        var scheduleByName = matches
            .GroupBy(match => match.MatchName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        return matches
            .SelectMany(match => ResolveMatchCandidates(match, scheduleByName, [])
                .GroupBy(candidate => $"{candidate.NormalizedPlayerName}\u001F{BuildConditionsKey(candidate.Conditions)}", StringComparer.Ordinal)
                .Select(group => group.First())
                .Select(candidate => new PlayerAppearance(
                    candidate.PlayerName,
                    candidate.NormalizedPlayerName,
                    match,
                    candidate.Conditions,
                    candidate.IsProjected)))
            .ToList();
    }

    private static PlayerAppearance? FindLatestCompatiblePreviousAppearance(
        IReadOnlyList<PlayerAppearance> ordered,
        int currentIndex)
    {
        var current = ordered[currentIndex];
        PlayerAppearance? previous = null;
        for (var index = currentIndex - 1; index >= 0; index--)
        {
            var candidate = ordered[index];
            if (!string.Equals(candidate.DayLabel, current.DayLabel, StringComparison.Ordinal))
            {
                break;
            }

            if (string.Equals(candidate.Match.MatchName, current.Match.MatchName, StringComparison.Ordinal)
                || !AreConditionsCompatible(candidate.Conditions, current.Conditions))
            {
                continue;
            }

            if (previous is null || candidate.Match.EndTime > previous.Match.EndTime)
            {
                previous = candidate;
            }
        }

        return previous;
    }

    private static IReadOnlyList<PlayerCandidate> ResolveMatchCandidates(
        ScheduledMatch match,
        IReadOnlyDictionary<string, ScheduledMatch> scheduleByName,
        HashSet<string> visiting)
    {
        if (!visiting.Add(match.MatchName))
        {
            return [];
        }

        var candidates = ResolveSideCandidates(match.SideA, scheduleByName, visiting)
            .Concat(ResolveSideCandidates(match.SideB, scheduleByName, visiting))
            .GroupBy(candidate => $"{candidate.NormalizedPlayerName}\u001F{BuildConditionsKey(candidate.Conditions)}", StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        visiting.Remove(match.MatchName);
        return candidates;
    }

    private static IReadOnlyList<PlayerCandidate> ResolveSideCandidates(
        string side,
        IReadOnlyDictionary<string, ScheduledMatch> scheduleByName,
        HashSet<string> visiting)
    {
        var text = side.Trim();
        if (string.IsNullOrWhiteSpace(text)
            || text.Contains("待定", StringComparison.Ordinal)
            || text.Contains("轮空", StringComparison.Ordinal))
        {
            return [];
        }

        if (TryParseOutcomeReference(text, out var sourceMatchName, out var outcome))
        {
            if (!scheduleByName.TryGetValue(sourceMatchName, out var sourceMatch))
            {
                return [];
            }

            return ResolveMatchCandidates(sourceMatch, scheduleByName, visiting)
                .Select(candidate => TryAddOutcomeCondition(candidate, sourceMatchName, outcome))
                .Where(candidate => candidate is not null)
                .Select(candidate => candidate!)
                .ToList();
        }

        if (text.Length >= 2 && text[0] == '[' && text[^1] == ']')
        {
            text = text[1..^1].Trim();
            return Regex.Split(text, @"\s+")
                .Select(part => part.Trim())
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(player => new PlayerCandidate(player, NormalizeName(player), [], IsProjected: false))
                .ToList();
        }

        return [new PlayerCandidate(text, NormalizeName(text), [], IsProjected: false)];
    }

    private static bool TryParseOutcomeReference(string side, out string sourceMatchName, out string outcome)
    {
        if (side.EndsWith("胜者", StringComparison.Ordinal))
        {
            sourceMatchName = side[..^"胜者".Length];
            outcome = "胜者";
            return true;
        }

        if (side.EndsWith("负者", StringComparison.Ordinal))
        {
            sourceMatchName = side[..^"负者".Length];
            outcome = "负者";
            return true;
        }

        sourceMatchName = "";
        outcome = "";
        return false;
    }

    private static PlayerCandidate? TryAddOutcomeCondition(PlayerCandidate candidate, string matchName, string outcome)
    {
        var existing = candidate.Conditions.FirstOrDefault(condition => string.Equals(condition.MatchName, matchName, StringComparison.Ordinal));
        if (existing is not null && !string.Equals(existing.Outcome, outcome, StringComparison.Ordinal))
        {
            return null;
        }

        if (existing is not null)
        {
            return candidate with { IsProjected = true };
        }

        var conditions = candidate.Conditions
            .Append(new ScheduleOutcomeCondition(matchName, outcome))
            .OrderBy(condition => condition.MatchName, StringComparer.Ordinal)
            .ThenBy(condition => condition.Outcome, StringComparer.Ordinal)
            .ToList();
        return candidate with { Conditions = conditions, IsProjected = true };
    }

    private static bool AreConditionsCompatible(
        IReadOnlyList<ScheduleOutcomeCondition> first,
        IReadOnlyList<ScheduleOutcomeCondition> second)
    {
        return !first.Any(left => second.Any(right =>
            string.Equals(left.MatchName, right.MatchName, StringComparison.Ordinal)
            && !string.Equals(left.Outcome, right.Outcome, StringComparison.Ordinal)));
    }

    private static int CountMaximumCompatibleMatches(IReadOnlyList<PlayerAppearance> appearances)
    {
        return CountMaximumCompatibleMatches(
            appearances,
            startIndex: 0,
            conditions: new Dictionary<string, string>(StringComparer.Ordinal),
            selectedMatchNames: new HashSet<string>(StringComparer.Ordinal));
    }

    private static int CountMaximumCompatibleMatches(
        IReadOnlyList<PlayerAppearance> appearances,
        int startIndex,
        IReadOnlyDictionary<string, string> conditions,
        IReadOnlySet<string> selectedMatchNames)
    {
        if (startIndex >= appearances.Count)
        {
            return 0;
        }

        var best = CountMaximumCompatibleMatches(appearances, startIndex + 1, conditions, selectedMatchNames);
        var current = appearances[startIndex];
        if (selectedMatchNames.Contains(current.Match.MatchName)
            || !TryMergeConditions(conditions, current.Conditions, out var mergedConditions))
        {
            return best;
        }

        var mergedMatchNames = selectedMatchNames.ToHashSet(StringComparer.Ordinal);
        mergedMatchNames.Add(current.Match.MatchName);
        return Math.Max(
            best,
            1 + CountMaximumCompatibleMatches(appearances, startIndex + 1, mergedConditions, mergedMatchNames));
    }

    private static bool TryMergeConditions(
        IReadOnlyDictionary<string, string> existingConditions,
        IReadOnlyList<ScheduleOutcomeCondition> candidateConditions,
        out IReadOnlyDictionary<string, string> mergedConditions)
    {
        var merged = new Dictionary<string, string>(existingConditions, StringComparer.Ordinal);
        foreach (var condition in candidateConditions)
        {
            if (merged.TryGetValue(condition.MatchName, out var existingOutcome)
                && !string.Equals(existingOutcome, condition.Outcome, StringComparison.Ordinal))
            {
                mergedConditions = existingConditions;
                return false;
            }

            merged[condition.MatchName] = condition.Outcome;
        }

        mergedConditions = merged;
        return true;
    }

    private static string BuildAppearanceKey(PlayerAppearance appearance)
    {
        return string.Join(
            '\u001F',
            appearance.DayLabel,
            appearance.Match.MatchName,
            appearance.Match.StartTime.ToString("HH:mm"),
            appearance.Match.Court,
            BuildConditionsKey(appearance.Conditions));
    }

    private static string BuildConditionsKey(IReadOnlyList<ScheduleOutcomeCondition> conditions)
    {
        return string.Join("|", conditions.Select(condition => $"{condition.MatchName}:{condition.Outcome}"));
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
        ScheduledMatch Match,
        IReadOnlyList<ScheduleOutcomeCondition> Conditions,
        bool IsProjected)
    {
        public string DayLabel => Match.DayLabel;
    }

    private sealed record PlayerCandidate(
        string PlayerName,
        string NormalizedPlayerName,
        IReadOnlyList<ScheduleOutcomeCondition> Conditions,
        bool IsProjected);

    private sealed record ScheduleOutcomeCondition(string MatchName, string Outcome);
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
