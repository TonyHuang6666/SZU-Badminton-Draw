using System.Text.RegularExpressions;

namespace BadmintonDraw.Core;

public sealed class ScheduleConstraintAnalyzer
{
    public ScheduleConstraintReport Analyze(SchedulePlan schedule)
    {
        var rules = ScheduleConstraintRules.For(schedule.Settings.ConstraintProfile);
        var issues = new List<ScheduleConstraintIssue>();
        var appearances = BuildAppearances(schedule.Matches, rules.MaxProjectedDepth);

        AddDependencyOrderIssues(issues, schedule, rules);
        AddRestIssues(issues, appearances, rules);
        AddDailyLoadIssues(issues, appearances, schedule.Settings, rules);
        AddKeyMatchTimeIssues(issues, schedule.Matches, rules);

        return new ScheduleConstraintReport(schedule.Settings.ConstraintProfile, rules, issues
            .OrderBy(issue => issue.Scope)
            .ThenByDescending(issue => issue.Severity)
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
        var projectedCandidates = new List<ProjectedRestCandidate>();
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
                if (isProjected)
                {
                    projectedCandidates.Add(new ProjectedRestCandidate(
                        previous,
                        current,
                        restMinutes,
                        requiredRest,
                        severity,
                        IsDirectDependency(previous, current)
                            ? ScheduleConstraintIssueScope.DirectDependency
                            : ScheduleConstraintIssueScope.Speculative));
                    continue;
                }

                var subject = $"{current.PlayerName} 在 {current.DayLabel}";
                var message = restMinutes < 0
                    ? $"{subject} 有重叠比赛：{previous.Match.MatchName} 与 {current.Match.MatchName}。"
                    : $"{subject} 两场比赛间隔 {restMinutes} 分钟，低于{rules.ProfileName}要求的 {requiredRest} 分钟。";
                issues.Add(new ScheduleConstraintIssue(
                    severity,
                    ScheduleConstraintIssueType.ShortRest,
                    ScheduleConstraintIssueScope.Confirmed,
                    current.DayLabel,
                    current.Match.StartTime,
                    current.Match.Court,
                    current.Match.Phase,
                    current.Match.MatchName,
                    current.PlayerName,
                    message));
            }
        }

        AddProjectedRestIssues(issues, projectedCandidates, rules);
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
            var hasProjectedAppearance = appearanceGroup.Any(appearance => appearance.IsProjected);
            var certainty = hasProjectedAppearance ? "可能" : "已";
            issues.Add(new ScheduleConstraintIssue(
                severity,
                ScheduleConstraintIssueType.DailyLoad,
                hasProjectedAppearance ? ScheduleConstraintIssueScope.Speculative : ScheduleConstraintIssueScope.Confirmed,
                first.DayLabel,
                first.Match.StartTime,
                first.Match.Court,
                first.Match.Phase,
                first.Match.MatchName,
                playerName,
                $"{playerName} 在 {first.DayLabel} {certainty}{action}每日场次上限：{count}/{dailyLimit} 场。{rules.ProfileName}下建议裁判长确认体能安排。"));
        }
    }

    private static void AddProjectedRestIssues(
        List<ScheduleConstraintIssue> issues,
        IReadOnlyList<ProjectedRestCandidate> candidates,
        ScheduleConstraintRules rules)
    {
        foreach (var group in candidates
                     .Where(candidate => candidate.Scope != ScheduleConstraintIssueScope.DirectDependency)
                     .GroupBy(BuildProjectedRestKey, StringComparer.Ordinal))
        {
            var first = group.First();
            var current = first.Current;
            var previous = first.Previous;
            var players = group
                .Select(candidate => candidate.Current.PlayerName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            var playerText = FormatProjectedPlayerSummary(players);
            var outcome = DescribeOutcomeReference(current.Conditions, previous.Match.MatchId);
            var scopePrefix = first.Scope == ScheduleConstraintIssueScope.DirectDependency
                ? "场次接续风险"
                : "推演风险";
            var timeText = first.RestMinutes < 0
                ? $"两场时间重叠 {Math.Abs(first.RestMinutes)} 分钟"
                : $"两场间隔 {first.RestMinutes} 分钟";
            var message =
                $"{scopePrefix}：{previous.Match.MatchName} 的{outcome}可能进入 {current.Match.MatchName}，{timeText}，低于{rules.ProfileName}要求的 {first.RequiredRestMinutes} 分钟。{playerText}";
            issues.Add(new ScheduleConstraintIssue(
                first.Severity,
                ScheduleConstraintIssueType.ShortRest,
                first.Scope,
                current.DayLabel,
                current.Match.StartTime,
                current.Match.Court,
                current.Match.Phase,
                current.Match.MatchName,
                null,
                message));
        }
    }

    private static void AddDependencyOrderIssues(
        List<ScheduleConstraintIssue> issues,
        SchedulePlan schedule,
        ScheduleConstraintRules rules)
    {
        var graph = ScheduleDependencyGraph.Build(schedule);
        var scheduleById = schedule.Matches
            .GroupBy(match => match.MatchId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var edge in graph.Edges)
        {
            var dependent = edge.Dependent;
            var source = edge.Source;
            var restMinutes = graph.GetRestMinutes(edge);
            var requiredRest = IsKeyMatch(dependent)
                ? rules.KeyMatchMinimumRestMinutes
                : rules.MinimumRestMinutes;
            if (restMinutes >= requiredRest)
            {
                continue;
            }

            var severity = restMinutes < 0
                ? ScheduleConstraintSeverity.Severe
                : ScheduleConstraintSeverity.Warning;
            var outcome = ScheduleDependencyGraph.FormatOutcome(edge.Dependency.Outcome);
            var playerText = FormatProjectedPlayerSummary(
                ResolveMatchCandidates(source, scheduleById, [], rules.MaxProjectedDepth)
                    .Select(candidate => candidate.PlayerName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToList());
            var message = restMinutes < 0
                ? $"赛程顺序错误：{dependent.MatchName} 依赖 {source.MatchName} 的{outcome}，但后续场次在前序场次结束前开始（{source.DayLabel} {source.TimeRange} → {dependent.DayLabel} {dependent.TimeRange}）。请先安排前序场次。{playerText}"
                : $"场次接续风险：{source.MatchName} 的{outcome}进入 {dependent.MatchName}，两场间隔 {restMinutes} 分钟，低于{rules.ProfileName}要求的 {requiredRest} 分钟。{playerText}";
            issues.Add(new ScheduleConstraintIssue(
                severity,
                ScheduleConstraintIssueType.DependencyOrder,
                ScheduleConstraintIssueScope.DirectDependency,
                dependent.DayLabel,
                dependent.StartTime,
                dependent.Court,
                dependent.Phase,
                dependent.MatchName,
                null,
                message));
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
                ScheduleConstraintIssueScope.Confirmed,
                match.DayLabel,
                match.StartTime,
                match.Court,
                match.Phase,
                match.MatchName,
                null,
                $"{match.MatchName} 在 {match.StartTime:HH:mm} 开始，早于建议的重点场次时段 {rules.PreferredFinalStartTime:HH:mm}；如需观赏性或颁奖衔接，可人工调整。"));
        }
    }

    private static IReadOnlyList<PlayerAppearance> BuildAppearances(IReadOnlyList<ScheduledMatch> matches, int maxProjectedDepth)
    {
        var scheduleById = matches
            .GroupBy(match => match.MatchId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        return matches
            .SelectMany(match => ResolveMatchCandidates(match, scheduleById, [], maxProjectedDepth)
                .GroupBy(candidate => $"{candidate.NormalizedPlayerName}\u001F{BuildConditionsKey(candidate.Conditions)}", StringComparer.Ordinal)
                .Select(group => group.First())
                .Select(candidate => new PlayerAppearance(
                    candidate.PlayerName,
                    candidate.NormalizedPlayerName,
                    match,
                    candidate.Conditions,
                    candidate.IsProjected,
                    candidate.DirectSourceMatchId,
                    candidate.ProjectionDepth)))
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

            if (string.Equals(candidate.Match.MatchId, current.Match.MatchId, StringComparison.Ordinal)
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
        IReadOnlyDictionary<string, ScheduledMatch> scheduleById,
        HashSet<string> visiting,
        int maxProjectedDepth)
    {
        if (!visiting.Add(match.MatchId))
        {
            return [];
        }

        var candidates = ResolveSideCandidates(match, ScheduleMatchSide.SideA, scheduleById, visiting, maxProjectedDepth)
            .Concat(ResolveSideCandidates(match, ScheduleMatchSide.SideB, scheduleById, visiting, maxProjectedDepth))
            .GroupBy(candidate => $"{candidate.NormalizedPlayerName}\u001F{BuildConditionsKey(candidate.Conditions)}", StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        visiting.Remove(match.MatchId);
        return candidates;
    }

    private static IReadOnlyList<PlayerCandidate> ResolveSideCandidates(
        ScheduledMatch match,
        ScheduleMatchSide side,
        IReadOnlyDictionary<string, ScheduledMatch> scheduleById,
        HashSet<string> visiting,
        int maxProjectedDepth)
    {
        var sideDependencies = match.Dependencies
            .Where(dependency => dependency.TargetSide == side)
            .ToList();
        if (sideDependencies.Count > 0)
        {
            return sideDependencies
                .SelectMany(dependency =>
                {
                    if (!scheduleById.TryGetValue(dependency.SourceMatchId, out var sourceMatch))
                    {
                        return [];
                    }

                    return ResolveMatchCandidates(sourceMatch, scheduleById, visiting, maxProjectedDepth)
                        .Select(candidate => TryAddOutcomeCondition(candidate, dependency.SourceMatchId, dependency.Outcome, maxProjectedDepth))
                        .Where(candidate => candidate is not null)
                        .Select(candidate => candidate!);
                })
                .ToList();
        }

        var sideIdentities = side == ScheduleMatchSide.SideA
            ? match.SideAPlayerIdentities
            : match.SideBPlayerIdentities;
        if (sideIdentities.Count > 0)
        {
            return sideIdentities
                .Select(identity => new PlayerCandidate(
                    identity.DisplayName,
                    identity.IdentityKey,
                    [],
                    IsProjected: false,
                    null,
                    ProjectionDepth: 0))
                .ToList();
        }

        var sideText = side == ScheduleMatchSide.SideA ? match.SideA : match.SideB;
        var text = sideText.Trim();
        if (string.IsNullOrWhiteSpace(text)
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
                .Select(player => new PlayerCandidate(player, NormalizeName(player), [], IsProjected: false, null, ProjectionDepth: 0))
                .ToList();
        }

        return [new PlayerCandidate(text, NormalizeName(text), [], IsProjected: false, null, ProjectionDepth: 0)];
    }

    private static PlayerCandidate? TryAddOutcomeCondition(
        PlayerCandidate candidate,
        string matchId,
        ScheduleMatchDependencyOutcome outcome,
        int maxProjectedDepth)
    {
        if (candidate.ProjectionDepth >= maxProjectedDepth)
        {
            return null;
        }

        var existing = candidate.Conditions.FirstOrDefault(condition => string.Equals(condition.MatchId, matchId, StringComparison.Ordinal));
        if (existing is not null && existing.Outcome != outcome)
        {
            return null;
        }

        if (existing is not null)
        {
            return candidate with
            {
                IsProjected = true,
                DirectSourceMatchId = matchId,
                ProjectionDepth = candidate.ProjectionDepth + 1
            };
        }

        var conditions = candidate.Conditions
            .Append(new ScheduleOutcomeCondition(matchId, outcome))
            .OrderBy(condition => condition.MatchId, StringComparer.Ordinal)
            .ThenBy(condition => condition.Outcome)
            .ToList();
        return candidate with
        {
            Conditions = conditions,
            IsProjected = true,
            DirectSourceMatchId = matchId,
            ProjectionDepth = candidate.ProjectionDepth + 1
        };
    }

    private static bool AreConditionsCompatible(
        IReadOnlyList<ScheduleOutcomeCondition> first,
        IReadOnlyList<ScheduleOutcomeCondition> second)
    {
        return !first.Any(left => second.Any(right =>
            string.Equals(left.MatchId, right.MatchId, StringComparison.Ordinal)
            && left.Outcome != right.Outcome));
    }

    private static bool IsDirectDependency(PlayerAppearance previous, PlayerAppearance current)
    {
        return string.Equals(current.DirectSourceMatchId, previous.Match.MatchId, StringComparison.Ordinal);
    }

    private static string BuildProjectedRestKey(ProjectedRestCandidate candidate)
    {
        return string.Join(
            '\u001F',
            candidate.Scope,
            candidate.Severity,
            candidate.Previous.Match.MatchName,
            candidate.Current.Match.MatchName,
            candidate.RestMinutes,
            candidate.RequiredRestMinutes);
    }

    private static string DescribeOutcomeReference(
        IReadOnlyList<ScheduleOutcomeCondition> conditions,
        string sourceMatchId)
    {
        var outcomes = conditions
            .Where(condition => string.Equals(condition.MatchId, sourceMatchId, StringComparison.Ordinal))
            .Select(condition => condition.Outcome)
            .Distinct()
            .OrderBy(outcome => outcome)
            .Select(ScheduleDependencyGraph.FormatOutcome)
            .ToList();
        return outcomes.Count switch
        {
            1 => outcomes[0],
            > 1 => string.Join("/", outcomes),
            _ => "胜者/负者"
        };
    }

    private static string FormatProjectedPlayerSummary(IReadOnlyList<string> players)
    {
        if (players.Count == 0)
        {
            return "";
        }

        var shown = string.Join("、", players.Take(6));
        var suffix = players.Count > 6 ? $"等 {players.Count} 人" : "";
        return $"涉及可能选手：{shown}{suffix}。";
    }

    private static int CountMaximumCompatibleMatches(IReadOnlyList<PlayerAppearance> appearances)
    {
        return CountMaximumCompatibleMatches(
            appearances,
            startIndex: 0,
            conditions: new Dictionary<string, ScheduleMatchDependencyOutcome>(StringComparer.Ordinal),
            selectedMatchIds: new HashSet<string>(StringComparer.Ordinal));
    }

    private static int CountMaximumCompatibleMatches(
        IReadOnlyList<PlayerAppearance> appearances,
        int startIndex,
        IReadOnlyDictionary<string, ScheduleMatchDependencyOutcome> conditions,
        IReadOnlySet<string> selectedMatchIds)
    {
        if (startIndex >= appearances.Count)
        {
            return 0;
        }

        var best = CountMaximumCompatibleMatches(appearances, startIndex + 1, conditions, selectedMatchIds);
        var current = appearances[startIndex];
        if (selectedMatchIds.Contains(current.Match.MatchId)
            || !TryMergeConditions(conditions, current.Conditions, out var mergedConditions))
        {
            return best;
        }

        var mergedMatchIds = selectedMatchIds.ToHashSet(StringComparer.Ordinal);
        mergedMatchIds.Add(current.Match.MatchId);
        return Math.Max(
            best,
            1 + CountMaximumCompatibleMatches(appearances, startIndex + 1, mergedConditions, mergedMatchIds));
    }

    private static bool TryMergeConditions(
        IReadOnlyDictionary<string, ScheduleMatchDependencyOutcome> existingConditions,
        IReadOnlyList<ScheduleOutcomeCondition> candidateConditions,
        out IReadOnlyDictionary<string, ScheduleMatchDependencyOutcome> mergedConditions)
    {
        var merged = new Dictionary<string, ScheduleMatchDependencyOutcome>(existingConditions, StringComparer.Ordinal);
        foreach (var condition in candidateConditions)
        {
            if (merged.TryGetValue(condition.MatchId, out var existingOutcome)
                && existingOutcome != condition.Outcome)
            {
                mergedConditions = existingConditions;
                return false;
            }

            merged[condition.MatchId] = condition.Outcome;
        }

        mergedConditions = merged;
        return true;
    }

    private static string BuildAppearanceKey(PlayerAppearance appearance)
    {
        return string.Join(
            '\u001F',
            appearance.DayLabel,
            appearance.Match.MatchId,
            appearance.Match.StartTime.ToString("HH:mm"),
            appearance.Match.Court,
            BuildConditionsKey(appearance.Conditions));
    }

    private static string BuildConditionsKey(IReadOnlyList<ScheduleOutcomeCondition> conditions)
    {
        return string.Join("|", conditions.Select(condition => $"{condition.MatchId}:{(int)condition.Outcome}"));
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
        bool IsProjected,
        string? DirectSourceMatchId,
        int ProjectionDepth)
    {
        public string DayLabel => Match.DayLabel;
    }

    private sealed record PlayerCandidate(
        string PlayerName,
        string NormalizedPlayerName,
        IReadOnlyList<ScheduleOutcomeCondition> Conditions,
        bool IsProjected,
        string? DirectSourceMatchId,
        int ProjectionDepth);

    private sealed record ScheduleOutcomeCondition(string MatchId, ScheduleMatchDependencyOutcome Outcome);

    private sealed record ProjectedRestCandidate(
        PlayerAppearance Previous,
        PlayerAppearance Current,
        int RestMinutes,
        int RequiredRestMinutes,
        ScheduleConstraintSeverity Severity,
        ScheduleConstraintIssueScope Scope);
}

public enum ScheduleConstraintProfile
{
    Campus = 0,
    Formal = 1,
    Audit = 2
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
    KeyMatchTiming,
    DependencyOrder
}

public enum ScheduleConstraintIssueScope
{
    Confirmed = 0,
    DirectDependency = 1,
    Speculative = 2
}

public sealed record ScheduleConstraintRules(
    ScheduleConstraintProfile Profile,
    string ProfileName,
    int MinimumRestMinutes,
    int KeyMatchMinimumRestMinutes,
    TimeOnly PreferredFinalStartTime,
    int MaxProjectedDepth)
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
                PreferredFinalStartTime: new TimeOnly(16, 0),
                MaxProjectedDepth: 2),
            ScheduleConstraintProfile.Audit => new ScheduleConstraintRules(
                profile,
                "审计模式",
                MinimumRestMinutes: 20,
                KeyMatchMinimumRestMinutes: 30,
                PreferredFinalStartTime: new TimeOnly(16, 0),
                MaxProjectedDepth: int.MaxValue),
            _ => new ScheduleConstraintRules(
                ScheduleConstraintProfile.Campus,
                "校园赛",
                MinimumRestMinutes: 20,
                KeyMatchMinimumRestMinutes: 30,
                PreferredFinalStartTime: new TimeOnly(16, 0),
                MaxProjectedDepth: 1)
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

    public int ConfirmedCount => Issues.Count(issue => issue.Scope == ScheduleConstraintIssueScope.Confirmed);

    public int DirectDependencyCount => Issues.Count(issue => issue.Scope == ScheduleConstraintIssueScope.DirectDependency);

    public int SpeculativeCount => Issues.Count(issue => issue.Scope == ScheduleConstraintIssueScope.Speculative);
}

public sealed record ScheduleConstraintIssue(
    ScheduleConstraintSeverity Severity,
    ScheduleConstraintIssueType Type,
    ScheduleConstraintIssueScope Scope,
    string DayLabel,
    TimeOnly? StartTime,
    string? Court,
    string Phase,
    string MatchName,
    string? PlayerName,
    string Message);
