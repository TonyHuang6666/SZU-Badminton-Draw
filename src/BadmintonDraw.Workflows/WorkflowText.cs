using System.Text.RegularExpressions;
using BadmintonDraw.Core;

namespace BadmintonDraw.Workflows;

public static class WorkflowLabels
{
    public static string GetEventKindDisplay(EventKind eventKind)
    {
        return eventKind switch
        {
            EventKind.Singles => "单打",
            EventKind.Team => "团体",
            _ => "双打"
        };
    }

    public static string BuildGroupName(int groupNumber)
    {
        if (groupNumber <= 0)
        {
            return "总决赛";
        }

        var columnName = "";
        var number = groupNumber;
        while (number > 0)
        {
            number--;
            columnName = (char)('A' + number % 26) + columnName;
            number /= 26;
        }

        return $"{columnName}组";
    }
}

public static class WorkflowFileNames
{
    public static string ExtractEventName(string? inputPath)
    {
        var stem = string.IsNullOrWhiteSpace(inputPath)
            ? ""
            : Path.GetFileNameWithoutExtension(inputPath);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return "深大羽协";
        }

        var normalized = stem.Trim();
        normalized = Regex.Replace(normalized, @"\s*[-—–]\s*副本$", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"副本$", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"参赛名单模板|参赛名单|名单模板|名单|模板|抽签结果", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\d+\s*组\s*种子", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\d+\s*[人对队组]\b?", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"[_\-\s（）()]+$", "");
        normalized = Regex.Replace(normalized, @"^[_\-\s（）()]+", "");
        normalized = Regex.Replace(normalized, @"[_\-\s]+", "_");

        return string.IsNullOrWhiteSpace(normalized) ? "深大羽协" : normalized;
    }

    public static string GetCompetitionModePart(CompetitionMode competitionMode)
    {
        return competitionMode is CompetitionMode.SinglesRoundRobin or CompetitionMode.TeamRoundRobin
            ? "循环赛"
            : "淘汰赛";
    }

    public static string GetEventScalePart(EventKind eventKind, int participantCount)
    {
        return eventKind switch
        {
            EventKind.Doubles => $"双打{participantCount}对",
            EventKind.Team => $"团体{participantCount}队",
            _ => $"单打{participantCount}人"
        };
    }

    public static string? GetKnockoutGoalPart(DrawSettings settings)
    {
        if (!settings.IsKnockout)
        {
            return null;
        }

        if (settings.GroupCount == 1 || settings.KnockoutGoal == KnockoutGoal.Champion)
        {
            return "决出冠军";
        }

        return "每组出线";
    }

    public static string? GetPlacementPlayoffPart(DrawSettings settings)
    {
        return settings.PlacementPlayoff switch
        {
            PlacementPlayoff.ThirdPlace => "排3-4名",
            PlacementPlayoff.ThirdToEighth => "排3-8名",
            _ => null
        };
    }

    public static string GetSeedTail(string randomSeed)
    {
        var tail = randomSeed
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? randomSeed;
        tail = Sanitize(tail);

        if (string.IsNullOrWhiteSpace(tail))
        {
            return "custom";
        }

        return tail.Length <= 8 ? tail : tail[^8..];
    }

    public static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
        var sanitized = new string(value
            .Trim()
            .Select(ch => char.IsControl(ch) || invalid.Contains(ch) ? '_' : ch)
            .ToArray());
        sanitized = Regex.Replace(sanitized, @"_+", "_");
        sanitized = Regex.Replace(sanitized, @"\s+", "");
        return sanitized.Trim('_', '-', ' ');
    }

    public static string Limit(string stem, int maxLength = 150)
    {
        return stem.Length <= maxLength
            ? stem
            : stem[..maxLength].TrimEnd('_', '-', ' ');
    }
}
