using System.Globalization;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using ClosedXML.Excel;

namespace BadmintonDraw.Excel;

/// <summary>A read-only global diagnostic, not authorization to publish operational materials.</summary>
public sealed class WorkspaceScheduleQualityExcelWriter
{
    private const string Unavailable = "不可用（排程输入无效）";
    private const string Prohibition = "检查不通过：存在硬约束违规；禁止作为现场执行赛程/运营材料放行依据";
    private const string IncompleteValidation = "硬约束检查未完成；冲突情况未知，不能作为现场执行赛程/运营材料放行依据";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public void Write(string outputPath, WorkspaceScheduleExportContext context, DateTimeOffset generatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(context);
        if (generatedAt == default) throw new ArgumentException("必须提供报告生成时间。", nameof(generatedAt));
        var workspace = context.Workspace; var schedule = workspace.Schedule!;
        var projects = workspace.Projects.OrderBy(p => p.SortOrder).ThenBy(p => p.Id).ToArray();
        var projectLabels = new Dictionary<Guid, string>();
        var usedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < projects.Length; index++)
        {
            var project = projects[index];
            var label = projects.Count(p => string.Equals(p.DisplayName, project.DisplayName, StringComparison.OrdinalIgnoreCase)) > 1
                ? project.DisplayName + $"（项目 {index + 1}）" : project.DisplayName;
            var uniqueLabel = label;
            for (var suffix = 1; !usedLabels.Add(uniqueLabel); suffix++)
                uniqueLabel = label + $"（项目 {index + 1}，{suffix}）";
            projectLabels.Add(project.Id, uniqueLabel);
        }
        var request = new TournamentSchedulingRequest(projects.Select(p => p.MatchGraph!).ToArray(), schedule.Resources, schedule.Policy)
        {
            ProjectNames = projects.ToDictionary(p => p.Id, p => p.DisplayName), Results = workspace.Results,
            BaselinePlacements = schedule.Placements, ScheduleRevision = schedule.Revision
        };
        var quality = new TournamentScheduleQualityAnalyzer().Analyze(request, schedule.Placements);
        var inputValid = !quality.Violations.Any(v => v.Code is SchedulingConstraintCode.InvalidGraph or SchedulingConstraintCode.InvalidResources
            or SchedulingConstraintCode.InvalidPolicy or SchedulingConstraintCode.MissingDependency or SchedulingConstraintCode.CrossProjectDependency
            or SchedulingConstraintCode.CyclicDependency or SchedulingConstraintCode.InvalidResult or SchedulingConstraintCode.PlayerOnBothSides);
        var owners = context.Nodes.Keys.ToDictionary(k => k.MatchId);
        using var book = new XLWorkbook();
        WriteOverview(book, context, generatedAt, quality);
        WriteCards(book, context, quality, projectLabels);
        WriteIssues(book, context, quality, owners, projectLabels);
        WritePlayers(book, quality, inputValid);
        WriteResources(book, context, quality, projectLabels);
        WorkbookPrintTitles.SaveAs(book, outputPath);
    }

    private static void WriteOverview(XLWorkbook book, WorkspaceScheduleExportContext context, DateTimeOffset at,
        TournamentScheduleQuality quality)
    {
        var w = context.Workspace; var s = w.Schedule!;
        var conclusion = !quality.HardValidationComplete ? IncompleteValidation : quality.HardConstraintCount == 0
            ? "未发现硬约束违规；不等于全部赛事材料已验收。" : Prohibition;
        var sheet = Sheet(book, "检查总览", conclusion,
            ["检查项目", "当前快照 / 解释"], [30, 112], false);
        var row = 5;
        foreach (var pair in new (string Label, object Value)[]
        {
            ("生成时间", Instant(at)), ("赛事名称", w.Name),
            ("项目数", w.Projects.Count), ("全局场次数", context.MatchKeys.Count), ("已记录赛果", w.Results.Count),
            ("待记录场次", context.MatchKeys.Count - w.Results.Count), ("配置日期", string.Join("、", s.Resources.Days.OrderBy(d => d.Date).Select(d => d.DayLabel))),
            ("硬约束违规项数", quality.HardValidationComplete ? quality.HardConstraintCount : "未完成计算 / 未知"), ("全局检查结论", conclusion),
            ("负荷口径", "已确定出场数是当前计划中确定参赛的场次，不是已完成场次。最大可能出场数考虑选手能够实际连续参与的晋级路径；未完成计算时显示已证明的范围。"),
            ("未知值", "检查或最大值计算未完成时显示未知，不按零处理，也不将估计上限当作已证明的最大值。"),
            ("材料边界", "本文件是检查诊断，不发布或修改赛程，不证明材料包导出或审计保存成功。")
        }) Row(sheet, row++, pair.Label, pair.Value);
        Finish(sheet, row - 1, 2);
        if (!quality.HardValidationComplete || quality.HardConstraintCount != 0) sheet.Range(2, 1, 2, 2).Style.Font.FontColor = XLColor.DarkRed;
    }

    private static void WriteCards(XLWorkbook book, WorkspaceScheduleExportContext context, TournamentScheduleQuality quality,
        IReadOnlyDictionary<Guid, string> projectLabels)
    {
        var sheet = Sheet(book, "当前赛程卡片", "全部项目；计划日期与实际比赛日期分别列示，同名项目按项目顺序标注。",
            ["项目名称", "阶段 / 场次", "计划日期", "计划开始", "计划结束", "场地", "对阵 A", "对阵 B", "赛果状态", "实际比赛日期", "关联违规代码"],
            [26, 22, 14, 12, 12, 10, 22, 22, 12, 14, 26]);
        var row = 5;
        foreach (var key in context.MatchKeys.OrderBy(k => context.Placements[k].DayLabel, StringComparer.Ordinal)
            .ThenBy(k => context.Placements[k].StartTime).ThenBy(k => context.Placements[k].Court, StringComparer.Ordinal)
            .ThenBy(k => context.Projects[k.ProjectId].SortOrder).ThenBy(k => k.ProjectId).ThenBy(k => context.Nodes[k].Order).ThenBy(k => k.MatchId))
        {
            var node = context.Nodes[key]; var match = context.Matches[key]; var p = context.Placements[key];
            context.Workspace.Results.TryGetValue(key, out var result);
            var codes = quality.Violations.Where(v => v.MatchId == key.MatchId || v.RelatedMatchId == key.MatchId)
                .Select(v => v.Code.ToString()).Distinct().Order(StringComparer.Ordinal);
            Row(sheet, row++, projectLabels[key.ProjectId],
                node.Phase + " / " + node.DisplayName, p.DayLabel, Time(p.StartTime), Time(p.EndTime), p.Court,
                match.SideA, match.SideB, result is null ? "待记录" : "已记录",
                result is null ? "" : result.ActualPlayedDay?.ToString("yyyy-MM-dd", Invariant) ?? "未知", string.Join("\n", codes));
        }
        Finish(sheet, row - 1, 11);
    }

    private static void WriteIssues(XLWorkbook book, WorkspaceScheduleExportContext context, TournamentScheduleQuality quality,
        IReadOnlyDictionary<Guid, WorkspaceMatchKey> owners, IReadOnlyDictionary<Guid, string> projectLabels)
    {
        var sheet = Sheet(book, "检查明细", "赛程检查发现的问题；— 表示全局或未指定项目/场次。",
            ["违规代码", "问题说明", "项目名称", "场次名称", "关联项目名称", "关联场次名称"],
            [23, 55, 26, 22, 26, 22]);
        var row = 5;
        foreach (var issue in quality.Violations.OrderBy(v => v.Code).ThenBy(v => v.ProjectId).ThenBy(v => v.MatchId)
            .ThenBy(v => v.RelatedMatchId).ThenBy(v => v.Message, StringComparer.Ordinal))
        {
            var primary = issue.MatchId is { } id && owners.TryGetValue(id, out var owner) ? owner : (WorkspaceMatchKey?)null;
            var related = issue.RelatedMatchId is { } relatedId && owners.TryGetValue(relatedId, out var relatedOwner) ? relatedOwner : (WorkspaceMatchKey?)null;
            Row(sheet, row++, issue.Code, ReadableIssueMessage(issue.Message, context, owners, projectLabels),
                primary is { } ownerKey ? projectLabels[ownerKey.ProjectId]
                    : issue.ProjectId is { } project && projectLabels.TryGetValue(project, out var label) ? label : "—",
                primary is { } k ? context.Nodes[k].DisplayName : "—",
                related is { } r ? projectLabels[r.ProjectId] : "—",
                related is { } n ? context.Nodes[n].DisplayName : "—");
        }
        if (row == 5) Row(sheet, row++, quality.HardValidationComplete ? "未发现硬约束违规；不等于全部赛事材料已验收。" : IncompleteValidation);
        Finish(sheet, row - 1, 6);
    }

    private static void WritePlayers(XLWorkbook book, TournamentScheduleQuality quality, bool inputValid)
    {
        var sheet = Sheet(book, "选手每日负荷", "每位选手每天一行；已确定出场数与最大可能出场数按当前计划计算，尚未证明最大值时注明范围。",
            ["选手身份键", "选手姓名", "计划日期", "已确定出场数", "最大可能出场数", "未完成计算说明"],
            [26, 20, 14, 16, 18, 46]);
        var row = 5;
        if (!inputValid) Row(sheet, row++, Unavailable);
        else foreach (var load in quality.PlayerLoads)
        {
            Row(sheet, row++, load.PlayerKey, load.PlayerName, load.DayLabel, load.ConfirmedCount,
                load.MaximumCount is { } maximum ? maximum : "未完成计算",
                load.MaximumCount.HasValue ? "" : $"已证明至少 {load.MaximumLowerBound} 场；至多 {load.MaximumUpperBound} 场。最大值尚未确定。");
        }
        if (row == 5) Row(sheet, row++, quality.PlayerAnalysisComplete ? "未返回选手每日负荷记录。" : "选手分析未完成；负荷未知。");
        Finish(sheet, row - 1, 6);
    }

    private static void WriteResources(XLWorkbook book, WorkspaceScheduleExportContext context, TournamentScheduleQuality quality,
        IReadOnlyDictionary<Guid, string> projectLabels)
    {
        var s = context.Workspace.Schedule!; var resource = s.Resources; var policy = s.Policy;
        var sheet = Sheet(book, "资源与日负荷", "每日容量结合场地不可用时段及裁判人数计算；利用率为已排分钟占可用分钟的比例。",
            ["日期 / 设置", "开始 / 值", "结束 / 值", "场地 / 说明", "可用场地分钟", "已排场地分钟", "利用率 / 设置说明"],
            [30, 42, 30, 45, 20, 20, 48]);
        var row = 5;
        foreach (var day in resource.Days.OrderBy(d => d.Date))
        {
            var load = quality.DayLoads.SingleOrDefault(d => d.DayLabel == day.DayLabel);
            if (load is null)
            {
                Row(sheet, row++, day.DayLabel, Time(day.DayStart), Time(day.DayEnd), string.Join("、", day.Courts), "未完成计算", "未完成计算", "未知");
                continue;
            }
            Row(sheet, row, day.DayLabel, Time(day.DayStart), Time(day.DayEnd), string.Join("、", day.Courts), load.AvailableMatchMinutes,
                load.RequiredPlacedMinutes, load.AvailableMatchMinutes > 0 ? (double)load.RequiredPlacedMinutes / load.AvailableMatchMinutes : "无可用容量");
            sheet.Cell(row++, 7).Style.NumberFormat.Format = "0.00%";
        }
        Row(sheet, row++, "默认裁判人数", resource.RefereeCount is { } referees ? referees : "未设置（仅受场地/分时段容量限制）");
        Row(sheet, row++, "全局最小休息分钟", resource.MinimumRestMinutes);
        Row(sheet, row++, "全局选手每日上限", resource.MaxPlayerMatchesPerDay == int.MaxValue ? "不限" : resource.MaxPlayerMatchesPerDay);
        foreach (var day in resource.Days.OrderBy(d => d.Date))
        {
            foreach (var window in (day.RefereeCapacityWindows ?? []).OrderBy(w => w.StartTime).ThenBy(w => w.EndTime))
                Row(sheet, row++, "裁判窗口 " + day.DayLabel, Time(window.StartTime), Time(window.EndTime), "裁判人数", window.RefereeCount);
            foreach (var window in (day.UnavailableCourtWindows ?? []).OrderBy(w => w.StartTime).ThenBy(w => w.EndTime))
                Row(sheet, row++, "不可用场地 " + day.DayLabel, Time(window.StartTime), Time(window.EndTime), window.Courts.Count == 0 ? "全部配置场地" : string.Join("、", window.Courts));
        }
        foreach (var project in context.Projects.Values.OrderBy(p => p.SortOrder).ThenBy(p => p.Id))
        {
            policy.ProjectTimings.TryGetValue(project.Id, out var timing);
            var description = timing is null ? "按各场次预计时长安排。" : $"常规场次 {timing.MatchMinutes} 分钟。";
            if (timing?.BeforeBoundaryMinutes is { } before && timing.KnockoutTimingBoundaryEntrants is { } boundary)
                description += $"淘汰赛参赛人数多于 {boundary} 人的轮次或指定的前期轮次为 {before} 分钟；排位赛使用常规时长。";
            Row(sheet, row++, "项目时长设置", projectLabels[project.Id], "", description);
        }
        Finish(sheet, row - 1, 7);
    }

    private static string ReadableIssueMessage(string message, WorkspaceScheduleExportContext context,
        IReadOnlyDictionary<Guid, WorkspaceMatchKey> owners, IReadOnlyDictionary<Guid, string> projectLabels) =>
        System.Text.RegularExpressions.Regex.Replace(message,
            @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", match =>
            {
                var id = Guid.Parse(match.Value);
                if (owners.TryGetValue(id, out var key)) return projectLabels[key.ProjectId] + " / " + context.Nodes[key].DisplayName;
                return projectLabels.TryGetValue(id, out var label) ? label : "未识别的项目或场次";
            });

    private static IXLWorksheet Sheet(XLWorkbook book, string title, string note, string[] headers, double[] widths, bool landscape = true)
    {
        var sheet = book.Worksheets.Add(title); sheet.ShowGridLines = false;
        sheet.Style.Font.FontName = "Microsoft YaHei"; sheet.Style.Font.FontSize = 11;
        sheet.Style.Alignment.WrapText = true; sheet.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        for (var col = 1; col <= headers.Length; col++) sheet.Column(col).Width = widths[col - 1];
        for (var row = 1; row <= 3; row++) sheet.Range(row, 1, row, headers.Length).Merge();
        sheet.Cell(1, 1).Value = title; sheet.Row(1).Height = 30;
        sheet.Range(1, 1, 1, headers.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#5A3B78");
        sheet.Range(1, 1, 1, headers.Length).Style.Font.FontColor = XLColor.White;
        sheet.Range(1, 1, 1, headers.Length).Style.Font.FontSize = 18;
        sheet.Cell(2, 1).Value = note; sheet.Row(2).Height = 38;
        sheet.Cell(3, 1).Value = "全局赛程检查 · 当前快照只读诊断 · 计划数据不替代真实赛果"; sheet.Row(3).Height = 34;
        Row(sheet, 4, headers.Cast<object>().ToArray()); sheet.Row(4).Height = 36;
        sheet.Range(4, 1, 4, headers.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#EBE4F1");
        sheet.Range(4, 1, 4, headers.Length).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(4);
        sheet.PageSetup.PaperSize = landscape ? XLPaperSize.A3Paper : XLPaperSize.A4Paper;
        sheet.PageSetup.PageOrientation = landscape ? XLPageOrientation.Landscape : XLPageOrientation.Portrait;
        sheet.PageSetup.FitToPages(1, 0); sheet.PageSetup.SetRowsToRepeatAtTop(1, 4);
        sheet.PageSetup.Margins.Left = .25; sheet.PageSetup.Margins.Right = .25;
        sheet.PageSetup.Margins.Top = .3; sheet.PageSetup.Margins.Bottom = .3;
        return sheet;
    }

    private static void Row(IXLWorksheet sheet, int row, params object[] values)
    {
        for (var i = 0; i < values.Length; i++)
            sheet.Cell(row, i + 1).Value = values[i] switch
            {
                double n when !double.IsFinite(n) => "无效原值：" + n.ToString("R", Invariant),
                int n => n, long n => n, double n => n, _ => Convert.ToString(values[i], Invariant) ?? ""
            };
    }
    private static void Finish(IXLWorksheet sheet, int lastRow, int columns)
    {
        var table = sheet.Range(4, 1, lastRow, columns);
        table.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        table.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        table.Style.Border.InsideBorderColor = XLColor.FromHtml("#D6CDD9");
        for (var row = 5; row <= lastRow; row++)
        {
            var lines = sheet.Row(row).CellsUsed().Select(cell => cell.GetString().Split('\n').Sum(line =>
                Math.Max(1, (int)Math.Ceiling(line.Sum(c => c > 255 ? 2d : 1d) / Math.Max(1, sheet.Column(cell.Address.ColumnNumber).Width - 2))))).DefaultIfEmpty(1).Max();
            sheet.Row(row).Height = Math.Min(409, Math.Max(36, lines * 15 + 8));
            if (row % 2 == 1) sheet.Range(row, 1, row, columns).Style.Fill.BackgroundColor = XLColor.FromHtml("#FAF8FC");
        }
        sheet.PageSetup.PrintAreas.Add(1, 1, lastRow, columns);
    }
    private static string Time(TimeOnly value) => value.ToString("HH:mm:ss", Invariant);
    private static string Instant(DateTimeOffset value) => value.ToString("O", Invariant);
}
