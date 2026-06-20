using System.Text.RegularExpressions;

namespace BadmintonDraw.Core;

public sealed class PlayerLoadForecastAnalyzer
{
    private const int MaxExactOutcomeVariables = 18;

    public IReadOnlyList<PlayerDailyLoadForecast> Analyze(
        SchedulePlan schedule,
        int maxProjectedDepth,
        int dailyLimit,
        double winProbability = 0.5)
    {
        var projectionDepth = maxProjectedDepth == int.MaxValue
            ? MaxExactOutcomeVariables
            : Math.Max(0, maxProjectedDepth);
        var appearances = BuildAppearances(schedule.Matches, projectionDepth);
        return BuildForecasts(appearances, dailyLimit, winProbability);
    }

    private static IReadOnlyList<PlayerDailyLoadForecast> BuildForecasts(
        IReadOnlyList<PlayerLoadForecastAppearance> appearances,
        int dailyLimit,
        double winProbability)
    {
        var probability = double.IsFinite(winProbability)
            ? Math.Clamp(winProbability, 0.01, 0.99)
            : 0.5;
        return appearances
            .GroupBy(appearance => $"{appearance.NormalizedPlayerName}\u001F{appearance.DayLabel}", StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var appearanceGroup = group
                    .GroupBy(BuildAppearanceKey, StringComparer.Ordinal)
                    .Select(item => item.First())
                    .OrderBy(item => item.StartTime)
                    .ThenBy(item => item.Court, StringComparer.Ordinal)
                    .ThenBy(item => item.MatchName, StringComparer.Ordinal)
                    .ToList();
                var distribution = BuildDistribution(appearanceGroup, probability);
                var expected = distribution.Sum(pair => pair.Key * pair.Value);
                return new PlayerDailyLoadForecast(
                    appearanceGroup[0].PlayerName,
                    appearanceGroup[0].NormalizedPlayerName,
                    appearanceGroup[0].DayLabel,
                    appearanceGroup.Count(appearance => appearance.Conditions.Count == 0),
                    distribution.Keys.DefaultIfEmpty(0).Max(),
                    expected,
                    distribution.Where(pair => pair.Key >= dailyLimit).Sum(pair => pair.Value),
                    distribution,
                    appearanceGroup);
            })
            .OrderBy(forecast => forecast.DayLabel, StringComparer.Ordinal)
            .ThenByDescending(forecast => forecast.MaximumCount)
            .ThenBy(forecast => forecast.PlayerName, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyDictionary<int, double> BuildDistribution(
        IReadOnlyList<PlayerLoadForecastAppearance> appearances,
        double winProbability)
    {
        var variables = appearances
            .SelectMany(appearance => appearance.Conditions.Select(condition => condition.MatchId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(matchId => matchId, StringComparer.Ordinal)
            .ToList();
        if (variables.Count == 0)
        {
            return new Dictionary<int, double> { [appearances.Count] = 1.0 };
        }

        if (variables.Count > MaxExactOutcomeVariables)
        {
            return BuildApproximateDistribution(appearances);
        }

        var variableIndex = variables
            .Select((matchId, index) => (matchId, index))
            .ToDictionary(item => item.matchId, item => item.index, StringComparer.Ordinal);
        var outcomeCount = 1 << variables.Count;
        var distribution = new Dictionary<int, double>();
        for (var mask = 0; mask < outcomeCount; mask++)
        {
            var scenarioProbability = 1.0;
            for (var index = 0; index < variables.Count; index++)
            {
                var winner = (mask & (1 << index)) != 0;
                scenarioProbability *= winner ? winProbability : 1.0 - winProbability;
            }

            var count = 0;
            foreach (var appearance in appearances)
            {
                if (IsSatisfied(appearance.Conditions, mask, variableIndex))
                {
                    count++;
                }
            }

            distribution[count] = distribution.TryGetValue(count, out var existing)
                ? existing + scenarioProbability
                : scenarioProbability;
        }

        return distribution
            .OrderBy(pair => pair.Key)
            .ToDictionary(pair => pair.Key, pair => Math.Round(pair.Value, 8));
    }

    private static IReadOnlyDictionary<int, double> BuildApproximateDistribution(
        IReadOnlyList<PlayerLoadForecastAppearance> appearances)
    {
        var confirmed = appearances.Count(appearance => appearance.Conditions.Count == 0);
        var maximum = CountMaximumCompatibleMatches(appearances);
        return maximum == confirmed
            ? new Dictionary<int, double> { [maximum] = 1.0 }
            : new Dictionary<int, double>
            {
                [confirmed] = 0.5,
                [maximum] = 0.5
            };
    }

    private static bool IsSatisfied(
        IReadOnlyList<PlayerLoadForecastCondition> conditions,
        int mask,
        IReadOnlyDictionary<string, int> variableIndex)
    {
        foreach (var condition in conditions)
        {
            if (!variableIndex.TryGetValue(condition.MatchId, out var index))
            {
                return false;
            }

            var winner = (mask & (1 << index)) != 0;
            if ((winner && condition.Outcome != ScheduleMatchDependencyOutcome.Winner)
                || (!winner && condition.Outcome != ScheduleMatchDependencyOutcome.Loser))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<PlayerLoadForecastAppearance> BuildAppearances(
        IReadOnlyList<ScheduledMatch> matches,
        int maxProjectedDepth)
    {
        var scheduleById = matches
            .GroupBy(match => match.MatchId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        return matches
            .SelectMany(match => ResolveMatchCandidates(match, scheduleById, [], maxProjectedDepth)
                .GroupBy(candidate => $"{candidate.NormalizedPlayerName}\u001F{BuildConditionsKey(candidate.Conditions)}", StringComparer.Ordinal)
                .Select(group => group.First())
                .Select(candidate => new PlayerLoadForecastAppearance(
                    match.MatchId,
                    match.MatchName,
                    match.Phase,
                    match.Court,
                    match.DayLabel,
                    match.StartTime,
                    match.EndTime,
                    candidate.PlayerName,
                    candidate.NormalizedPlayerName,
                    candidate.Conditions
                        .Select(condition => new PlayerLoadForecastCondition(condition.MatchId, condition.Outcome))
                        .ToList(),
                    candidate.IsProjected,
                    candidate.DirectSourceMatchId,
                    candidate.ProjectionDepth)))
            .ToList();
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
                        .Select(candidate => TryAddOutcomeCondition(
                            candidate,
                            dependency.SourceMatchId,
                            dependency.Outcome,
                            maxProjectedDepth))
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
            .Append(new InternalLoadForecastCondition(matchId, outcome))
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

    private static int CountMaximumCompatibleMatches(IReadOnlyList<PlayerLoadForecastAppearance> appearances)
    {
        return CountMaximumCompatibleMatches(
            appearances,
            startIndex: 0,
            conditions: new Dictionary<string, ScheduleMatchDependencyOutcome>(StringComparer.Ordinal),
            selectedMatchIds: new HashSet<string>(StringComparer.Ordinal));
    }

    private static int CountMaximumCompatibleMatches(
        IReadOnlyList<PlayerLoadForecastAppearance> appearances,
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
        if (selectedMatchIds.Contains(current.MatchId)
            || !TryMergeConditions(conditions, current.Conditions, out var mergedConditions))
        {
            return best;
        }

        var mergedMatchIds = selectedMatchIds.ToHashSet(StringComparer.Ordinal);
        mergedMatchIds.Add(current.MatchId);
        return Math.Max(
            best,
            1 + CountMaximumCompatibleMatches(appearances, startIndex + 1, mergedConditions, mergedMatchIds));
    }

    private static bool TryMergeConditions(
        IReadOnlyDictionary<string, ScheduleMatchDependencyOutcome> existingConditions,
        IReadOnlyList<PlayerLoadForecastCondition> candidateConditions,
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

    private static string BuildAppearanceKey(PlayerLoadForecastAppearance appearance)
    {
        return string.Join(
            '\u001F',
            appearance.DayLabel,
            appearance.MatchId,
            appearance.StartTime.ToString("HH:mm"),
            appearance.Court,
            BuildConditionsKey(appearance.Conditions));
    }

    private static string BuildConditionsKey(IReadOnlyList<PlayerLoadForecastCondition> conditions)
    {
        return string.Join("|", conditions.Select(condition => $"{condition.MatchId}:{(int)condition.Outcome}"));
    }

    private static string BuildConditionsKey(IReadOnlyList<InternalLoadForecastCondition> conditions)
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

    private sealed record PlayerCandidate(
        string PlayerName,
        string NormalizedPlayerName,
        IReadOnlyList<InternalLoadForecastCondition> Conditions,
        bool IsProjected,
        string? DirectSourceMatchId,
        int ProjectionDepth);

    private sealed record InternalLoadForecastCondition(string MatchId, ScheduleMatchDependencyOutcome Outcome);
}

public sealed record PlayerDailyLoadForecast(
    string PlayerName,
    string NormalizedPlayerName,
    string DayLabel,
    int ConfirmedCount,
    int MaximumCount,
    double ExpectedCount,
    double ProbabilityAtOrAboveLimit,
    IReadOnlyDictionary<int, double> Distribution,
    IReadOnlyList<PlayerLoadForecastAppearance> Appearances)
{
    public bool HasProjectedAppearances => Appearances.Any(appearance => appearance.IsProjected);
}

public sealed record PlayerLoadForecastAppearance(
    string MatchId,
    string MatchName,
    string Phase,
    string Court,
    string DayLabel,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string PlayerName,
    string NormalizedPlayerName,
    IReadOnlyList<PlayerLoadForecastCondition> Conditions,
    bool IsProjected,
    string? DirectSourceMatchId,
    int ProjectionDepth);

public sealed record PlayerLoadForecastCondition(string MatchId, ScheduleMatchDependencyOutcome Outcome);
