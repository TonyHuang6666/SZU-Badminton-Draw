using System.Globalization;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using ClosedXML.Excel;

namespace BadmintonDraw.Excel;

/// <summary>A read-only global diagnostic, not authorization to publish operational materials.</summary>
public sealed class WorkspaceScheduleQualityExcelWriter
{
    private const string Unavailable = "不可用（排程输入无效）";
    private const string Baseline = "基线：当前快照；未提供历史比较基线，历史移动次数不计算";
    private const string Prohibition = "检查不通过：存在硬约束违规；禁止作为现场执行赛程/运营材料放行依据";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public void Write(string outputPath, WorkspaceScheduleExportContext context, DateTimeOffset generatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(context);
        if (generatedAt == default) throw new ArgumentException("必须提供报告生成时间。", nameof(generatedAt));
        var workspace = context.Workspace; var schedule = workspace.Schedule!;
        var projects = workspace.Projects.OrderBy(p => p.SortOrder).ThenBy(p => p.Id).ToArray();
        var request = new TournamentSchedulingRequest(projects.Select(p => p.MatchGraph!).ToArray(), schedule.Resources, schedule.Policy)
        {
            ProjectNames = projects.ToDictionary(p => p.Id, p => p.DisplayName), Results = workspace.Results,
            BaselinePlacements = schedule.Placements, ScheduleRevision = schedule.Revision
        };
        var inputValid = new TournamentPlacementValidator(request).ValidateInput().IsValid;
        var quality = new TournamentScheduleQualityAnalyzer().Analyze(request, schedule.Placements);
        var owners = context.Nodes.Keys.ToDictionary(k => k.MatchId);
        using var book = new XLWorkbook();
        WriteOverview(book, context, generatedAt, quality, inputValid);
        WriteCards(book, context, quality);
        WriteIssues(book, context, quality, owners);
        WritePlayers(book, context, quality, inputValid, owners);
        WriteResources(book, context, quality);
        WriteSources(book, context, generatedAt, projects);
        WorkbookPrintTitles.SaveAs(book, outputPath);
    }

    private static void WriteOverview(XLWorkbook book, WorkspaceScheduleExportContext context, DateTimeOffset at,
        TournamentScheduleQuality quality, bool inputValid)
    {
        var w = context.Workspace; var s = w.Schedule!;
        var sheet = Sheet(book, "检查总览", quality.HardConstraintCount == 0 ? "未发现硬约束违规；仅代表本次全局赛程检查。" : Prohibition,
            ["检查项目", "当前快照 / 解释"], [30, 112], false);
        var row = 5;
        foreach (var pair in new (string Label, object Value)[]
        {
            ("生成时间", Instant(at)), ("赛事名称", w.Name), ("工作区 ID", w.Id), ("赛事类型", w.Kind),
            ("工作区用途", w.Purpose), ("赛事阶段", w.Stage), ("工作区修订", w.Revision), ("赛程修订", s.Revision),
            ("项目数", w.Projects.Count), ("全局场次数", context.MatchKeys.Count), ("已记录赛果", w.Results.Count),
            ("待记录场次", context.MatchKeys.Count - w.Results.Count), ("配置日期", string.Join("、", s.Resources.Days.OrderBy(d => d.Date).Select(d => d.DayLabel))),
            ("硬约束违规项数", quality.HardConstraintCount), ("全局检查结论", quality.HardConstraintCount == 0 ? "未发现硬约束违规；不等于全部赛事材料已验收。" : Prohibition),
            ("当前基线软约束评分", inputValid ? quality.SoftScore : Unavailable),
            ("评分含义", "内部软约束评分，可为负数；不是百分比、质量等级或最优性证明。"), ("比较基线", Baseline),
            ("负荷口径", "已确定出场数指计划赛程中参与路径已确定的场次，不是已完成赛果数；最大值为兼容路径的最大值。"),
            ("概率口径", $"中性分支模型 0.5；条件模型估计，不是实测胜率。P(N ≥ {s.Resources.MaxPlayerMatchesPerDay}) 为达到或超过每日上限的概率；达到上限本身不等于违规。"),
            ("未知值", "未精确枚举时，期望/概率/分布如不可用则显示未知，不视为零风险。"),
            ("材料边界", "本文件是检查诊断，不发布或修改赛程，不证明材料包导出或审计保存成功。")
        }) Row(sheet, row++, pair.Label, pair.Value);
        Finish(sheet, row - 1, 2);
        if (quality.HardConstraintCount != 0) sheet.Range(2, 1, 2, 2).Style.Font.FontColor = XLColor.DarkRed;
    }

    private static void WriteCards(XLWorkbook book, WorkspaceScheduleExportContext context, TournamentScheduleQuality quality)
    {
        var sheet = Sheet(book, "当前赛程卡片", "全部项目 / 计划日期与实际比赛日期分别列示；身份以 GUID 为准。",
            ["项目 ID", "项目名称", "场次 ID", "阶段 / 场次", "计划日期", "计划开始", "计划结束", "场地", "对阵 A", "对阵 B", "赛果状态", "实际比赛日期", "关联违规代码"],
            [28, 20, 28, 22, 14, 22, 22, 10, 22, 22, 12, 14, 26]);
        var row = 5;
        foreach (var key in context.MatchKeys.OrderBy(k => context.Placements[k].DayLabel, StringComparer.Ordinal)
            .ThenBy(k => context.Placements[k].StartTime).ThenBy(k => context.Placements[k].Court, StringComparer.Ordinal)
            .ThenBy(k => context.Projects[k.ProjectId].SortOrder).ThenBy(k => k.ProjectId).ThenBy(k => context.Nodes[k].Order).ThenBy(k => k.MatchId))
        {
            var node = context.Nodes[key]; var match = context.Matches[key]; var p = context.Placements[key];
            context.Workspace.Results.TryGetValue(key, out var result);
            var codes = quality.Violations.Where(v => v.MatchId == key.MatchId || v.RelatedMatchId == key.MatchId)
                .Select(v => v.Code.ToString()).Distinct().Order(StringComparer.Ordinal);
            Row(sheet, row++, key.ProjectId, context.Projects[key.ProjectId].DisplayName, key.MatchId,
                node.Phase + " / " + node.DisplayName, p.DayLabel, Time(p.StartTime), Time(p.EndTime), p.Court,
                match.SideA, match.SideB, result is null ? "待记录" : "已记录",
                result is null ? "" : result.ActualPlayedDay?.ToString("yyyy-MM-dd", Invariant) ?? "未知", string.Join("\n", codes));
        }
        Finish(sheet, row - 1, 13);
    }

    private static void WriteIssues(XLWorkbook book, WorkspaceScheduleExportContext context, TournamentScheduleQuality quality,
        IReadOnlyDictionary<Guid, WorkspaceMatchKey> owners)
    {
        var sheet = Sheet(book, "检查明细", "下列均为 Core 硬约束违规；— 表示全局/未指定身份。关联场次独立解析所属项目。",
            ["违规代码", "原始说明", "项目 ID", "场次 ID", "项目名称", "场次名称", "关联场次 ID", "关联项目 ID", "关联项目名称", "关联场次名称"],
            [23, 55, 28, 28, 20, 22, 28, 28, 20, 22]);
        var row = 5;
        foreach (var issue in quality.Violations.OrderBy(v => v.Code).ThenBy(v => v.ProjectId).ThenBy(v => v.MatchId)
            .ThenBy(v => v.RelatedMatchId).ThenBy(v => v.Message, StringComparer.Ordinal))
        {
            var primary = issue.MatchId is { } id && owners.TryGetValue(id, out var owner) ? owner : (WorkspaceMatchKey?)null;
            var related = issue.RelatedMatchId is { } relatedId && owners.TryGetValue(relatedId, out var relatedOwner) ? relatedOwner : (WorkspaceMatchKey?)null;
            Row(sheet, row++, issue.Code, issue.Message, issue.ProjectId?.ToString() ?? "—", issue.MatchId?.ToString() ?? "—",
                issue.ProjectId is { } project && context.Projects.TryGetValue(project, out var p) ? p.DisplayName : "—",
                primary is { } k ? context.Nodes[k].DisplayName : "—", issue.RelatedMatchId?.ToString() ?? "—",
                related?.ProjectId.ToString() ?? "—", related is { } r ? context.Projects[r.ProjectId].DisplayName : "—",
                related is { } n ? context.Nodes[n].DisplayName : "—");
        }
        if (row == 5) Row(sheet, row++, "未发现硬约束违规；不等于全部赛事材料已验收。");
        Finish(sheet, row - 1, 10);
    }

    private static void WritePlayers(XLWorkbook book, WorkspaceScheduleExportContext context, TournamentScheduleQuality quality,
        bool inputValid, IReadOnlyDictionary<Guid, WorkspaceMatchKey> owners)
    {
        var sheet = Sheet(book, "选手每日负荷", "计划赛程的条件模型估计；中性分支 0.5，兼容路径计数；续行仅展示关联键，不是新负荷。",
            ["选手身份键", "选手姓名", "计划日期", "已确定出场数", "兼容最大出场数", "期望出场数", $"P(N ≥ {context.Workspace.Schedule!.Resources.MaxPlayerMatchesPerDay})", "枚举状态", "次数:概率分布", "关联项目 ID / 场次 ID"],
            [26, 20, 14, 14, 16, 14, 16, 24, 30, 82]);
        var row = 5;
        if (!inputValid) Row(sheet, row++, Unavailable);
        else foreach (var load in quality.PlayerLoads.OrderBy(p => p.DayLabel, StringComparer.Ordinal).ThenBy(p => p.PlayerKey, StringComparer.Ordinal))
        {
            var keys = load.MatchIds.Select(id => owners[id]).OrderBy(k => k.ProjectId).ThenBy(k => k.MatchId)
                .Select(k => $"{k.ProjectId} / {k.MatchId}").Chunk(8).ToArray();
            Row(sheet, row, load.PlayerKey, load.PlayerName, load.DayLabel, load.ConfirmedCount, load.MaximumCount,
                load.ExpectedCount is { } expected ? expected : "未知", load.ProbabilityAtOrAboveLimit is { } probability ? probability : "未知",
                load.IsExact ? "精确枚举（条件模型）" : "未精确枚举 / 未知",
                load.Distribution.Count == 0 ? "未知" : string.Join("; ", load.Distribution.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value.ToString("0.########", Invariant)}")),
                keys.Length == 0 ? "—" : string.Join("\n", keys[0]));
            sheet.Cell(row, 6).Style.NumberFormat.Format = "0.########";
            sheet.Cell(row++, 7).Style.NumberFormat.Format = "0.00%";
            foreach (var chunk in keys.Skip(1)) Row(sheet, row++, "", $"关联场次（续）\n{load.PlayerKey}\n{load.DayLabel}", "", "", "", "", "", "", "", string.Join("\n", chunk));
        }
        if (row == 5) Row(sheet, row++, "未返回选手每日负荷记录。");
        Finish(sheet, row - 1, 10);
    }

    private static void WriteResources(XLWorkbook book, WorkspaceScheduleExportContext context, TournamentScheduleQuality quality)
    {
        var s = context.Workspace.Schedule!; var resource = s.Resources; var policy = s.Policy;
        var sheet = Sheet(book, "资源与日负荷", "全局资源原值；容量按 Core 合并重叠不可用区间及裁判窗口计算。时长为项目专属设置。",
            ["日期 / 设置", "开始 / 值", "结束 / 值", "场地 / 说明", "可用场地分钟", "已排场地分钟", "利用率 / 设置说明"],
            [30, 42, 30, 45, 20, 20, 48]);
        var row = 5;
        foreach (var day in resource.Days.OrderBy(d => d.Date))
        {
            var load = quality.DayLoads.Single(d => d.DayLabel == day.DayLabel);
            Row(sheet, row, day.DayLabel, Time(day.DayStart), Time(day.DayEnd), string.Join("、", day.Courts), load.AvailableMatchMinutes,
                load.RequiredPlacedMinutes, load.AvailableMatchMinutes > 0 ? (double)load.RequiredPlacedMinutes / load.AvailableMatchMinutes : "无可用容量");
            sheet.Cell(row++, 7).Style.NumberFormat.Format = "0.00%";
        }
        Row(sheet, row++, "默认裁判人数", resource.RefereeCount is { } referees ? referees : "未设置（仅受场地/分时段容量限制）");
        Row(sheet, row++, "全局最小休息分钟", resource.MinimumRestMinutes);
        Row(sheet, row++, "全局选手每日上限", resource.MaxPlayerMatchesPerDay);
        Row(sheet, row++, "编排策略", policy.Strategy);
        Row(sheet, row++, "同步阶段进度", policy.SynchronizeStageWaves ? "启用" : "关闭");
        foreach (var day in resource.Days.OrderBy(d => d.Date))
        {
            foreach (var window in (day.RefereeCapacityWindows ?? []).OrderBy(w => w.StartTime).ThenBy(w => w.EndTime))
                Row(sheet, row++, "裁判窗口 " + day.DayLabel, Time(window.StartTime), Time(window.EndTime), "裁判人数", window.RefereeCount);
            foreach (var window in (day.UnavailableCourtWindows ?? []).OrderBy(w => w.StartTime).ThenBy(w => w.EndTime))
                Row(sheet, row++, "不可用场地 " + day.DayLabel, Time(window.StartTime), Time(window.EndTime), window.Courts.Count == 0 ? "全部配置场地" : string.Join("、", window.Courts));
        }
        Row(sheet, row++, "项目时长", "项目 ID", "项目名称", "常规分钟", "分界人数", "分界前分钟", "计时口径");
        foreach (var project in context.Projects.Values.OrderBy(p => p.SortOrder).ThenBy(p => p.Id))
        {
            policy.ProjectTimings.TryGetValue(project.Id, out var timing);
            Row(sheet, row++, "项目时长设置", project.Id, project.DisplayName, timing is null ? "逐场使用节点预计分钟" : timing.MatchMinutes,
                timing?.KnockoutTimingBoundaryEntrants is { } boundary ? boundary : "未设置", timing?.BeforeBoundaryMinutes is { } before ? before : "未设置",
                "BeforeBoundaryMinutes：非排位赛且人数大于分界或显式分界前标记时使用；否则常规分钟。未配置时使用各节点预计分钟。");
            foreach (var node in project.MatchGraph!.Matches.OrderBy(n => n.Order).ThenBy(n => n.Id))
                Row(sheet, row++, "节点计时来源", project.Id, node.Id, node.DisplayName, node.ExpectedDurationMinutes,
                    node.KnockoutEntrantCount is { } entrants ? entrants : "未设置", $"显式分界前={node.ForceBeforeTimingBoundary}; 排位赛={node.IsPlacementPlayoff}");
        }
        Row(sheet, row++, "每日负荷目标", "日期", "目标利用率", "预警利用率");
        foreach (var target in policy.DayLoadTargets.OrderBy(t => t.DayLabel, StringComparer.Ordinal))
            Row(sheet, row++, "负荷目标", target.DayLabel, target.TargetUtilization, target.WarningUtilization);
        Row(sheet, row++, "阶段进度目标", "日期", "累计进度");
        foreach (var target in policy.StageWaveTargets.OrderBy(t => t.DayLabel, StringComparer.Ordinal))
            Row(sheet, row++, "阶段目标", target.DayLabel, target.CumulativeProgress);
        Row(sheet, row++, "决赛日偏好", "项目 ID", "项目名称", "场次类别", "偏好（软约束）");
        foreach (var rule in policy.FinalDayRules.OrderBy(r => r.ProjectId).ThenBy(r => r.Category))
            Row(sheet, row++, "决赛日规则", rule.ProjectId, context.Projects.TryGetValue(rule.ProjectId, out var project) ? project.DisplayName : "未知项目（无效输入）", rule.Category, rule.Preference);
        Finish(sheet, row - 1, 7);
    }

    private static void WriteSources(XLWorkbook book, WorkspaceScheduleExportContext context, DateTimeOffset at, IReadOnlyList<TournamentProject> projects)
    {
        var w = context.Workspace;
        var sheet = Sheet(book, "来源项目", $"工作区 {w.Id} · 工作区修订 {w.Revision} · 赛程修订 {w.Schedule!.Revision}",
            ["项目 ID", "项目名称", "单项", "赛制", "对阵图修订", "抽签确认时间", "名单来源文件名", "名单内容 SHA-256", "场次数", "已记录赛果"],
            [28, 22, 20, 25, 42, 42, 32, 44, 10, 12]);
        sheet.Cell(3, 1).Value = $"快照更新时间 {Instant(w.UpdatedAt)} · 生成时间 {Instant(at)} · 单一工作区来源；未声明文件导出审计已保存。";
        var row = 5;
        foreach (var project in projects)
            Row(sheet, row++, project.Id, project.DisplayName, project.Discipline, project.CompetitionMode, project.MatchGraph!.Revision,
                Instant(project.Draw!.ConfirmedAt!.Value), project.Roster!.SourceFileName, project.Roster.ContentHash, project.MatchGraph.Matches.Count,
                w.Results.Keys.Count(k => k.ProjectId == project.Id));
        Finish(sheet, row - 1, 10);
    }

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
        sheet.Cell(3, 1).Value = "Core 全局检查 · 当前快照只读诊断 · 计划数据不替代真实赛果"; sheet.Row(3).Height = 34;
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
    private static string Time(TimeOnly value) => value.ToString("HH:mm:ss.fffffff", Invariant);
    private static string Instant(DateTimeOffset value) => value.ToString("O", Invariant);
}
