using System.Globalization;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using ClosedXML.Excel;

namespace BadmintonDraw.Excel;

public sealed partial class ScheduleExcelWriter
{
    /// <summary>One explicit coverage day, projected from the complete global workspace.</summary>
    public void WriteDailySchedule(string outputPath, WorkspaceScheduleExportContext context,
        IReadOnlyList<WorkspaceRecordExportRow> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var selected = WorkspaceMaterialPresentation.SelectRows(context, rows, "每日赛程");
        if (selected.Select(r => r.RecordDay).Distinct().Count() != 1)
            throw new WorkspaceValidationException("export.selection", "每日赛程必须选择同一个记录日期。");
        var day = selected[0].RecordDay;
        var ordered = selected.OrderBy(r => context.Placements[r.Key].DayLabel, StringComparer.Ordinal)
            .ThenBy(r => context.Placements[r.Key].StartTime).ThenBy(r => context.Placements[r.Key].EndTime)
            .ThenBy(r => context.Projects[r.Key.ProjectId].SortOrder).ThenBy(r => context.Nodes[r.Key].Order)
            .ThenBy(r => r.Key.ProjectId).ThenBy(r => r.Key.MatchId).ToArray();
        using var book = new XLWorkbook();
        WriteWorkspaceDetail(book, context, ordered, day);
        WriteWorkspaceGrid(book, context, ordered, day);
        WriteWorkspaceSettings(book, context, ordered.Length, day);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        book.SaveAs(outputPath);
    }

    private static void WriteWorkspaceDetail(XLWorkbook book, WorkspaceScheduleExportContext context,
        IReadOnlyList<WorkspaceRecordExportRow> rows, DateOnly day)
    {
        var sheet = book.Worksheets.Add("赛程明细");
        var headers = new[] { "序号", "项目", "记录日", "计划时间", "场地", "阶段", "场次", "选手/队伍A", "选手/队伍B",
            "赛果状态", "实际比赛日", "说明 / 前序安排", "项目标识", "场次标识" };
        sheet.Range(1, 1, 1, 12).Merge().Value = $"{day:yyyy-MM-dd} 每日赛程明细";
        sheet.Range(2, 1, 2, 12).Merge().Value = $"{context.Workspace.Name}；本表 {rows.Count} 场；记录日不等于实际比赛日。";
        WriteWorkspaceHeaders(sheet, headers);
        for (var i = 0; i < rows.Count; i++)
        {
            var item = rows[i]; var key = item.Key; var match = context.Matches[key];
            var coverage = WorkspaceMaterialPresentation.Coverage(context, item); var row = i + 5;
            var notes = new List<string>();
            if (coverage.IsDifferentDay) notes.Add(coverage.OriginalPlanText);
            if (coverage.IsPendingCarryOver) notes.Add("待补打：仅纳入记录覆盖，尚未重新安排时间和场地。");
            if (!string.IsNullOrWhiteSpace(match.Note)) notes.Add(match.Note);
            notes.AddRange(WorkspacePrerequisiteNotes(context, key));
            string[] values = [match.Order.ToString(CultureInfo.InvariantCulture), WorkspaceProjectLabel(context, key.ProjectId),
                coverage.RecordDayText, coverage.TimeRangeText, coverage.CourtText, match.Phase, match.MatchName,
                match.SideA, match.SideB, coverage.IsCompleted ? "已录入" : "待比赛",
                coverage.IsCompleted ? coverage.ActualPlayedDay?.ToString("yyyy-MM-dd") ?? "未知" : "",
                string.Join("\n", notes), key.ProjectId.ToString("D"), key.MatchId.ToString("D")];
            for (var c = 0; c < values.Length; c++) sheet.Cell(row, c + 1).Value = values[c];
        }
        StyleWorkspaceTable(sheet, rows.Count + 4, 14);
        double[] widths = [6, 10, 12, 23, 10, 10, 15, 24, 24, 10, 12, 38, 38, 38];
        for (var c = 0; c < widths.Length; c++) sheet.Column(c + 1).Width = widths[c];
        sheet.Columns(13, 14).Hide();
        for (var r = 5; r <= rows.Count + 4; r++)
        {
            var lines = Enumerable.Range(1, 12).Max(c => EstimateWrappedLineCount(sheet.Cell(r, c).GetString(),
                Math.Max(5, (int)(widths[c - 1] / 1.6))));
            sheet.Row(r).Height = Math.Max(45, 8 + lines * 13);
            ApplyGridPhaseStyle(sheet.Cell(r, 6), sheet.Cell(r, 6).GetString());
        }
        SetupWorkspacePrint(sheet, rows.Count + 4, 12, XLPageOrientation.Landscape);
    }

    private static void WriteWorkspaceGrid(XLWorkbook book, WorkspaceScheduleExportContext context,
        IReadOnlyList<WorkspaceRecordExportRow> rows, DateOnly day)
    {
        var settings = context.Workspace.Schedule!.Resources.Days.Single(d => d.Date == day);
        var currentDay = rows.Where(r => context.Placements[r.Key].DayLabel == day.ToString("yyyy-MM-dd")).ToArray();
        // Four courts plus the time column remain readable on A4; keep unused configured courts as well.
        var sections = settings.Courts.Chunk(4).ToArray();
        for (var i = 0; i < sections.Length; i++)
            WriteWorkspaceGridSection(book, context, currentDay, day, sections[i], i, sections.Length);
    }

    private static void WriteWorkspaceGridSection(XLWorkbook book, WorkspaceScheduleExportContext context,
        IReadOnlyList<WorkspaceRecordExportRow> currentDay, DateOnly day, string[] courts, int section, int sectionCount)
    {
        var sheet = book.Worksheets.Add(section == 0 ? "时间场地网格" : $"时间场地网格 {section + 1}");
        var lastColumn = courts.Length + 1;
        var inGrid = currentDay.Where(r => courts.Contains(context.Placements[r.Key].Court, StringComparer.OrdinalIgnoreCase)).ToArray();
        sheet.Range(1, 1, 1, lastColumn).Merge().Value = $"{day:yyyy-MM-dd} 时间场地网格" +
            (sectionCount > 1 ? $" · 第 {section + 1}/{sectionCount} 组场地" : "");
        sheet.Range(2, 1, 2, lastColumn).Merge().Value =
            "仅显示本日现有编排；异日记录见赛程明细，不代表补打已排入本日场地。";
        WriteWorkspaceHeaders(sheet, new[] { "时间" }.Concat(courts).ToArray());
        var currentRow = 5;
        foreach (var group in inGrid.GroupBy(r => (context.Placements[r.Key].StartTime, context.Placements[r.Key].EndTime))
                     .OrderBy(g => g.Key.StartTime).ThenBy(g => g.Key.EndTime))
        {
            sheet.Cell(currentRow, 1).Value = WorkspaceTimeRange(group.Key.StartTime, group.Key.EndTime);
            var occupied = new HashSet<int>(); var maxLines = 1;
            foreach (var record in group)
            {
                var key = record.Key; var match = context.Matches[key];
                var index = Array.FindIndex(courts, c => string.Equals(c, match.Court, StringComparison.OrdinalIgnoreCase));
                if (index < 0 || !occupied.Add(index))
                    throw new WorkspaceValidationException("export.grid-overlap", "时间场地网格包含未知场地或重复位置，无法无损导出。");
                var text = $"{WorkspaceProjectLabel(context, key.ProjectId)} · {match.MatchName}\n{match.SideA} vs {match.SideB}";
                var notes = WorkspacePrerequisiteNotes(context, key).ToList();
                if (!string.IsNullOrWhiteSpace(match.Note)) notes.Add(match.Note);
                if (context.Workspace.Results.ContainsKey(key)) notes.Add("已录入赛果");
                if (notes.Count > 0) text += "\n" + string.Join("\n", notes);
                sheet.Cell(currentRow, index + 2).Value = text;
                maxLines = Math.Max(maxLines, EstimateWrappedLineCount(text, GridEstimatedCharsPerLine));
            }
            sheet.Row(currentRow).Height = CalculateGridBodyRowHeight(maxLines);
            currentRow++;
        }
        if (inGrid.Length == 0)
        {
            sheet.Range(currentRow, 1, currentRow, lastColumn).Merge().Value = currentDay.Count == 0
                ? "无本日已安排场次；异日记录仅列入明细。" : "本组场地无已安排比赛；其他场地见其余网格。";
            sheet.Row(currentRow).Height = 32;
            currentRow++;
        }
        StyleWorkspaceTable(sheet, currentRow - 1, lastColumn);
        foreach (var group in inGrid.GroupBy(r => (context.Placements[r.Key].StartTime, context.Placements[r.Key].EndTime))
                     .OrderBy(g => g.Key.StartTime).ThenBy(g => g.Key.EndTime).Select((g, i) => (Rows: g, Row: i + 5)))
        foreach (var record in group.Rows)
        {
            var index = Array.FindIndex(courts, c => string.Equals(c, context.Placements[record.Key].Court, StringComparison.OrdinalIgnoreCase));
            ApplyGridPhaseStyle(sheet.Cell(group.Row, index + 2), context.Nodes[record.Key].Phase);
        }
        sheet.Columns(1, lastColumn).Width = 24; sheet.Column(1).Width = 26;
        sheet.Row(2).Height = 38;
        SetupWorkspacePrint(sheet, currentRow - 1, lastColumn, XLPageOrientation.Landscape);
    }

    private static IEnumerable<string> WorkspacePrerequisiteNotes(WorkspaceScheduleExportContext context, WorkspaceMatchKey key)
    {
        var node = context.Nodes[key];
        foreach (var (side, source) in new[] { ("A", node.SideA), ("B", node.SideB) })
        {
            var id = source switch { EntrantSource.WinnerOf w => w.MatchId, EntrantSource.LoserOf l => l.MatchId, _ => (Guid?)null };
            if (id is null) continue;
            var prior = new WorkspaceMatchKey(key.ProjectId, id.Value); var placement = context.Placements[prior];
            yield return $"{side} 来源：{context.Nodes[prior].DisplayName}{(source is EntrantSource.WinnerOf ? "胜者" : "负者")}；" +
                $"{placement.DayLabel} {WorkspaceTimeRange(placement.StartTime, placement.EndTime)} {placement.Court}";
        }
    }

    private static void WriteWorkspaceSettings(XLWorkbook book, WorkspaceScheduleExportContext context, int count, DateOnly day)
    {
        var workspace = context.Workspace; var schedule = workspace.Schedule!; var resources = schedule.Resources; var policy = schedule.Policy;
        var rows = new List<(string Key, string Value)>
        {
            ("赛事", workspace.Name), ("赛事标识", workspace.Id.ToString("D")),
            ("工作区 / 赛程版本", $"{workspace.Revision} / {schedule.Revision}"),
            ("本表记录日期 / 场次", $"{day:yyyy-MM-dd} / {count}"),
            ("参数范围", "以下参数来自整个赛事，不是单个项目独立排程参数；本表不改变任何编排或赛果。"),
            ("网格打印", "每组最多 4 片场地，按配置顺序分表；所有场地均保留，纵向按实际行数分页。"),
            ("裁判人数默认值", resources.RefereeCount is { } referee ? $"{referee} 人" : "按可用场地数"),
            ("全局最短休息", $"{resources.MinimumRestMinutes} 分钟"),
            ("全局选手每日上限", $"{resources.MaxPlayerMatchesPerDay} 场"),
            ("全局编排策略", policy.Strategy switch { ScheduleAutoSchedulingStrategy.Compact => "紧凑完成",
                ScheduleAutoSchedulingStrategy.BalancedRelaxed => "均衡宽松", ScheduleAutoSchedulingStrategy.FinalsDayFriendly => "决赛日友好", _ => "自定义" }),
            ("同步阶段进度", policy.SynchronizeStageWaves ? "是" : "否")
        };
        foreach (var configured in resources.Days.OrderBy(d => d.Date))
        {
            rows.Add(($"赛程日 {configured.DayLabel}", $"{WorkspaceTimeRange(configured.DayStart, configured.DayEnd)}；场地：{string.Join("、", configured.Courts)}"));
            foreach (var window in configured.RefereeCapacityWindows ?? [])
                rows.Add(($"裁判时段 {configured.DayLabel}", $"{WorkspaceTimeRange(window.StartTime, window.EndTime)}；{window.RefereeCount} 人"));
            foreach (var block in configured.UnavailableCourtWindows ?? [])
                rows.Add(($"不可用场地 {configured.DayLabel}", $"{WorkspaceTimeRange(block.StartTime, block.EndTime)}；{(block.Courts.Count == 0 ? "全部场地" : string.Join("、", block.Courts))}"));
        }
        foreach (var project in workspace.Projects.OrderBy(p => p.SortOrder).ThenBy(p => p.Id))
        {
            var caption = project.DisplayName + " " + project.Id.ToString("D");
            rows.Add(("来源项目", caption));
            rows.Add(("抽签 / 比赛图来源", $"{project.MatchGraph!.Revision}\n{project.Draw!.ConfirmedAt!.Value:O}\n{project.Roster!.SourceFileName}\n{project.Roster.ContentHash}"));
            if (policy.ProjectTimings.TryGetValue(project.Id, out var timing))
            {
                var value = $"{caption}；单场 {timing.MatchMinutes} 分钟";
                if (timing.KnockoutTimingBoundaryEntrants is { } boundary)
                    value += $"；分界线 {boundary} 强；分界线前 {timing.BeforeBoundaryMinutes} 分钟";
                rows.Add(("项目预计耗时", value));
            }
            else rows.Add(("项目预计耗时", $"{caption}；未设置覆盖，按图中各场预计耗时：{string.Join("、", project.MatchGraph.Matches.Select(n => n.ExpectedDurationMinutes).Distinct().Order())} 分钟"));
        }
        foreach (var target in policy.DayLoadTargets.OrderBy(t => t.DayLabel, StringComparer.Ordinal))
            rows.Add(($"日负荷目标 {target.DayLabel}", $"目标 {WorkspacePercent(target.TargetUtilization)}；提示线 {WorkspacePercent(target.WarningUtilization)}"));
        foreach (var target in policy.StageWaveTargets.OrderBy(t => t.DayLabel, StringComparer.Ordinal))
            rows.Add(($"累计阶段进度 {target.DayLabel}", WorkspacePercent(target.CumulativeProgress)));
        foreach (var rule in policy.FinalDayRules.OrderBy(r => r.ProjectId).ThenBy(r => r.Category))
            rows.Add(("决赛日软偏好", $"{context.Projects[rule.ProjectId].DisplayName} {rule.ProjectId:D}；{WorkspaceFinalCategory(rule.Category)}；{WorkspaceFinalPreference(rule.Preference)}"));
        if (policy.DayLoadTargets.Count == 0) rows.Add(("日负荷目标", "未配置"));
        if (policy.StageWaveTargets.Count == 0) rows.Add(("累计阶段进度目标", "未配置"));
        if (policy.FinalDayRules.Count == 0) rows.Add(("决赛日软偏好", "未配置"));
        var sheet = book.Worksheets.Add("赛程参数"); sheet.Range("A1:B1").Merge().Value = "赛事统一赛程参数与来源";
        sheet.Range("A2:B2").Merge().Value = "记录覆盖与实际比赛日分离；完整全局资源与策略如下。";
        WriteWorkspaceHeaders(sheet, ["参数", "实际配置"]);
        for (var i = 0; i < rows.Count; i++) { sheet.Cell(i + 5, 1).Value = rows[i].Key; sheet.Cell(i + 5, 2).Value = rows[i].Value; }
        StyleWorkspaceTable(sheet, rows.Count + 4, 2); sheet.Column(1).Width = 28; sheet.Column(2).Width = 86;
        for (var r = 5; r <= rows.Count + 4; r++) sheet.Row(r).Height = Math.Max(30,
            8 + 14 * Math.Max(EstimateWrappedLineCount(sheet.Cell(r, 1).GetString(), 16), EstimateWrappedLineCount(sheet.Cell(r, 2).GetString(), 48)));
        SetupWorkspacePrint(sheet, rows.Count + 4, 2, XLPageOrientation.Portrait);
    }

    private static string WorkspaceTimeRange(TimeOnly start, TimeOnly end) =>
        WorkspaceMaterialPresentation.TimeText(start) + "-" + WorkspaceMaterialPresentation.TimeText(end);
    private static string WorkspaceProjectLabel(WorkspaceScheduleExportContext context, Guid projectId)
    {
        var project = context.Projects[projectId];
        if (context.Projects.Values.Count(p => p.DisplayName == project.DisplayName) < 2) return project.DisplayName;
        var discipline = project.Discipline switch
        {
            EventDiscipline.MenSingles => "男单", EventDiscipline.WomenSingles => "女单",
            EventDiscipline.MenDoubles => "男双", EventDiscipline.WomenDoubles => "女双",
            EventDiscipline.MixedDoubles => "混双", _ => "团体"
        };
        return $"{project.DisplayName}（{discipline}）";
    }
    private static string WorkspacePercent(double value) => (value * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";
    private static string WorkspaceFinalCategory(TournamentFinalDayMatchCategory value) => value switch
    { TournamentFinalDayMatchCategory.Final => "决赛", TournamentFinalDayMatchCategory.Semifinal => "半决赛", TournamentFinalDayMatchCategory.Bronze => "季军赛", _ => "5–8名排位赛" };
    private static string WorkspaceFinalPreference(TournamentFinalDayPreference value) => value switch
    { TournamentFinalDayPreference.Flexible => "灵活安排", TournamentFinalDayPreference.AvoidFinalDay => "避开决赛日",
        TournamentFinalDayPreference.PreferFinalDay => "偏好决赛日", _ => "强烈偏好决赛日（仍服从硬约束）" };
    private static void WriteWorkspaceHeaders(IXLWorksheet sheet, IReadOnlyList<string> headers)
    { for (var c = 0; c < headers.Count; c++) sheet.Cell(4, c + 1).Value = headers[c]; }
    private static void StyleWorkspaceTable(IXLWorksheet sheet, int lastRow, int lastColumn)
    {
        ApplySheetTitleStyle(sheet, lastColumn); ApplyTableStyle(sheet.Range(4, 1, lastRow, lastColumn));
        sheet.Range(4, 1, 4, lastColumn).Style.Fill.BackgroundColor = HeaderFill;
        sheet.Range(4, 1, 4, lastColumn).Style.Font.FontColor = XLColor.White;
        sheet.Range(4, 1, 4, lastColumn).Style.Font.Bold = true; sheet.Row(4).Height = 30;
        sheet.SheetView.FreezeRows(4); sheet.ShowGridLines = false;
    }
    private static void SetupWorkspacePrint(IXLWorksheet sheet, int lastRow, int lastColumn, XLPageOrientation orientation)
    {
        sheet.PageSetup.PaperSize = XLPaperSize.A4Paper; sheet.PageSetup.PageOrientation = orientation;
        sheet.PageSetup.FitToPages(1, 0); sheet.PageSetup.SetRowsToRepeatAtTop(1, 4);
        sheet.PageSetup.PrintAreas.Add(1, 1, lastRow, lastColumn);
        sheet.PageSetup.Margins.Left = .25; sheet.PageSetup.Margins.Right = .25;
        sheet.PageSetup.Margins.Top = .35; sheet.PageSetup.Margins.Bottom = .35;
    }
}
