using System.Text.RegularExpressions;
using BadmintonDraw.Core;
using BadmintonDraw.Excel;

namespace BadmintonDraw.Workflows;

public sealed class ScheduleWorkflow
{
    private readonly ScheduleService _scheduleService = new();
    private readonly ScheduleExcelWriter _scheduleWriter = new();

    public SchedulePlan Generate(DrawResult result, ScheduleSettings settings)
    {
        return _scheduleService.Generate(result, settings);
    }

    public void ExportExcel(string outputPath, SchedulePlan schedule)
    {
        _scheduleWriter.Write(outputPath, schedule);
    }

    public static ScheduleSettings BuildSettings(ScheduleWorkflowRequest request)
    {
        if (request.End <= request.Start)
        {
            throw new DrawValidationException("赛程结束时间必须晚于开始时间。");
        }

        var courts = ParseCourts(request.CourtsText);
        return new ScheduleSettings(
            [new ScheduleDaySettings(request.Date, request.Start, request.End, courts)],
            request.MatchMinutes,
            request.MaxMatchesPerEntrantPerDay);
    }

    public static IReadOnlyList<string> ParseCourts(string value)
    {
        var courts = Regex.Split(value, @"[\s,，、;；]+")
            .SelectMany(ExpandCourtToken)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (courts.Count == 0)
        {
            throw new DrawValidationException("请至少填写一片场地。");
        }

        return courts;
    }

    private static IReadOnlyList<string> ExpandCourtToken(string token)
    {
        token = token.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return [];
        }

        var match = Regex.Match(
            token,
            @"^([A-Za-z]+)(\d+)\s*[-~－–—]\s*([A-Za-z]+)?(\d+)$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return [token];
        }

        var startPrefix = match.Groups[1].Value.ToUpperInvariant();
        var startNumber = int.Parse(match.Groups[2].Value);
        var endPrefix = string.IsNullOrWhiteSpace(match.Groups[3].Value)
            ? startPrefix
            : match.Groups[3].Value.ToUpperInvariant();
        var endNumber = int.Parse(match.Groups[4].Value);

        if (startPrefix == endPrefix)
        {
            return ExpandNumberRange(startPrefix, startNumber, endNumber);
        }

        if (startPrefix.Length != 1 || endPrefix.Length != 1)
        {
            return [token];
        }

        var prefixStep = startPrefix[0] <= endPrefix[0] ? 1 : -1;
        var courts = new List<string>();
        for (var prefix = startPrefix[0];; prefix = (char)(prefix + prefixStep))
        {
            courts.AddRange(ExpandNumberRange(prefix.ToString(), startNumber, endNumber));
            if (prefix == endPrefix[0])
            {
                break;
            }
        }

        return courts;
    }

    private static IReadOnlyList<string> ExpandNumberRange(string prefix, int startNumber, int endNumber)
    {
        var step = startNumber <= endNumber ? 1 : -1;
        var courts = new List<string>();
        for (var number = startNumber;; number += step)
        {
            courts.Add($"{prefix}{number}");
            if (number == endNumber)
            {
                break;
            }
        }

        return courts;
    }

    public static string BuildDefaultScheduleExcelFileName(DrawResult result, string? inputPath)
    {
        var sourceName = string.IsNullOrWhiteSpace(inputPath)
            ? "深大羽协"
            : Path.GetFileNameWithoutExtension(inputPath);
        var modeName = result.Settings.IsKnockout ? "淘汰赛" : "循环赛";
        var stem = string.Join("_", new[]
        {
            WorkflowFileNames.Sanitize(sourceName),
            "赛程表",
            modeName,
            $"{WorkflowLabels.GetEventKindDisplay(result.Settings.EventKind)}{result.Audit.ParticipantCount}人",
            $"{result.Audit.GroupCount}组",
            DateTime.Now.ToString("yyyyMMdd_HHmm")
        }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return $"{stem}.xlsx";
    }
}

public sealed record ScheduleWorkflowRequest(
    DateOnly Date,
    TimeOnly Start,
    TimeOnly End,
    string CourtsText,
    int MatchMinutes,
    int MaxMatchesPerEntrantPerDay);
