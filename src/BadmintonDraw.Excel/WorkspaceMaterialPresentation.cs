using System.Globalization;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Excel;

/// <summary>Derived display values only; selecting carryover and changing placements are not renderer responsibilities.</summary>
internal static class WorkspaceMaterialPresentation
{
    internal static WorkspaceRecordExportRow[] SelectRows(WorkspaceScheduleExportContext context,
        IReadOnlyList<WorkspaceRecordExportRow> rows, string materialName)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rows);
        var selected = rows.ToArray();
        if (selected.Length == 0)
            throw new WorkspaceValidationException("export.no-matches", $"所选范围没有比赛，未生成{materialName}。");
        if (selected.Any(r => r is null) || selected.Select(r => r.Key).Distinct().Count() != selected.Length)
            throw new WorkspaceValidationException("export.selection", $"{materialName}不能重复选择同一场比赛。");
        var knownDays = context.Workspace.Schedule!.Resources.Days.Select(d => d.Date).ToHashSet();
        if (selected.Any(r => !context.Nodes.ContainsKey(r.Key) || !knownDays.Contains(r.RecordDay)))
            throw new WorkspaceValidationException("export.selection", $"{materialName}选择包含未知比赛或记录日期。");
        return selected;
    }

    internal static string TimeText(TimeOnly time) => time.Ticks % TimeSpan.TicksPerMinute == 0
        ? time.ToString("HH:mm", CultureInfo.InvariantCulture)
        : time.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');

    internal static WorkspaceCoverageDisplay Coverage(WorkspaceScheduleExportContext context, WorkspaceRecordExportRow row)
    {
        var placement = context.Placements[row.Key];
        context.Workspace.Results.TryGetValue(row.Key, out var result);
        var recordDay = row.RecordDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var differentDay = recordDay != placement.DayLabel;
        var carryover = differentDay && result is null;
        var start = TimeText(placement.StartTime);
        var time = start + "–" + TimeText(placement.EndTime);
        return new(recordDay, carryover ? "待安排" : start,
            carryover ? "待安排" : differentDay ? $"原计划 {placement.DayLabel}\n{time}" : time,
            carryover ? "待安排" : placement.Court, $"原计划 {placement.DayLabel} {time} {placement.Court}",
            differentDay, carryover, result is not null, result?.ActualPlayedDay);
    }
}

internal sealed record WorkspaceCoverageDisplay(string RecordDayText, string StartTimeText, string TimeRangeText,
    string CourtText, string OriginalPlanText, bool IsDifferentDay, bool IsPendingCarryOver, bool IsCompleted,
    DateOnly? ActualPlayedDay);
