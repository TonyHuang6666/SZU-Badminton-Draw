using BadmintonDraw.Core;
using System.Text.RegularExpressions;

namespace BadmintonDraw.Excel;

internal static class ScheduleMatchText
{
    public static string ResolveSide(
        string side,
        ScheduledMatch currentMatch,
        IReadOnlyDictionary<string, ScheduledMatch> scheduleByName,
        IReadOnlyDictionary<string, MatchRecordResult>? completedResults = null)
    {
        if (!TryParseOutcomeReference(side, out var sourceMatchName, out var outcome))
        {
            return side;
        }

        if (completedResults is not null && completedResults.TryGetValue(sourceMatchName, out var result))
        {
            return outcome == "胜者" ? result.Winner : result.Loser;
        }

        if (!scheduleByName.TryGetValue(sourceMatchName, out var sourceMatch))
        {
            return side;
        }

        var sourceLabel = sourceMatch.DayLabel == currentMatch.DayLabel
            ? $"{sourceMatch.TimeRange} {sourceMatch.Court}"
            : $"{FormatShortDayLabel(sourceMatch.DayLabel)} {sourceMatch.TimeRange} {sourceMatch.Court}";
        return $"{sourceLabel}{(outcome == "胜者" ? "胜" : "负")}";
    }

    public static string NormalizeCompetitorName(string side)
    {
        var trimmed = side.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
        {
            trimmed = trimmed[1..^1].Trim();
        }

        return Regex.Replace(trimmed, @"\s+", " ");
    }

    public static string ToMultilineCompetitorName(string side)
    {
        var normalized = NormalizeCompetitorName(side);
        return normalized.Contains(' ', StringComparison.Ordinal)
            ? normalized.Replace(" ", Environment.NewLine, StringComparison.Ordinal)
            : normalized;
    }

    public static bool TryParseOutcomeReference(string side, out string sourceMatchName, out string outcome)
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

    private static string FormatShortDayLabel(string dayLabel)
    {
        return DateOnly.TryParse(dayLabel, out var date)
            ? $"{date.Month}/{date.Day}"
            : dayLabel;
    }
}
