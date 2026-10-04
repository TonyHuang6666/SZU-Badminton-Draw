using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Excel;

namespace BadmintonDraw.Workflows.Tournaments;

public sealed partial class OperationalPackageWorkflow
{
    private sealed record CarryoverProof(WorkspaceMatchKey Key, DateOnly RecordDay, DateOnly OriginalPlanDay,
        Guid ImportLogId, string ContentHash, DateOnly EvidenceRecordDay, WorkspaceRecordLocation Location);
    private sealed record MaterialPlan(OperationalMaterialKind Kind, Guid? ProjectId, DateOnly? RecordDay,
        string FileName, IReadOnlyList<WorkspaceRecordExportRow> Rows);
    private sealed record PackagePlan(string OutputDirectory, WorkspaceScheduleExportContext Context,
        IReadOnlyList<TournamentProject> Projects, IReadOnlyList<WorkspaceRecordExportRow> Rows,
        IReadOnlyList<CarryoverProof> CarryoverEvidence, IReadOnlyList<MaterialPlan> Materials);

    private static PackagePlan Plan(TournamentWorkspace source, string workspacePath,
        OperationalExportRequest request, ExportProgress progress)
    {
        Require(!string.IsNullOrWhiteSpace(request.OutputDirectory), "export.path", "请选择导出文件夹。");
        Require(source.Purpose == TournamentPurpose.FullTournament && source.Schedule is not null &&
            source.Stage is TournamentStage.ScheduleReady or TournamentStage.InProgress or TournamentStage.Completed,
            "export.schedule", "请先完成统一赛程编排，再导出现场材料。");
        Require(source.Projects.All(p => p.Draw?.ConfirmedAt is not null && p.MatchGraph is not null),
            "export.draw", "所有项目必须先确认抽签。");
        var layout = request.DrawLayout ?? new();
        Require(layout.PdfRows > 0 && layout.PdfColumns > 0, "export.layout", "PDF 横向和纵向分页数必须大于零。");
        var projects = source.Projects.Where(p => request.ProjectId is null || p.Id == request.ProjectId)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Id).ToArray();
        Require(projects.Length > 0, "project.not-found", "当前工作区中找不到该项目。");
        var configured = source.Schedule!.Resources.Days.Select(d => d.Date).ToArray();
        var days = request.Days?.ToArray() ?? configured;
        Require(days.Length > 0 && days.Distinct().Count() == days.Length && days.All(configured.Contains),
            "export.days", "导出日期不能为空、重复或超出已配置赛程日期。");
        Require(request.PendingCarryoverDay is null || days.Contains(request.PendingCarryoverDay.Value),
            "export.carryover-day", "补打记录目标必须是本次选择的已配置日期。");
        days = days.Order().ToArray();
        progress.Scope = new(projects.Select(p => p.Id).Order().ToArray(), days, request.PendingCarryoverDay);

        var schedule = source.Schedule;
        var schedulingRequest = new TournamentSchedulingRequest(source.Projects.Select(p => p.MatchGraph!).ToArray(),
            schedule.Resources, schedule.Policy)
        {
            Results = source.Results, BaselinePlacements = schedule.Placements, ScheduleRevision = schedule.Revision,
            ProjectNames = source.Projects.ToDictionary(p => p.Id, p => p.DisplayName)
        };
        var validation = new TournamentPlacementValidator(schedulingRequest).ValidateSchedule(schedule.Placements);
        if (!validation.IsValid)
        {
            const string message = "完整赛事赛程仍有硬约束冲突，不能导出现场材料包。请先修正统一编排。";
            throw new WorkspaceCommandException(new("export.hard-violations", message,
                SchedulingFailure: new(message, [], validation.Violations, [], [])));
        }
        var context = new WorkspaceScheduleExportContext(source);
        var selectedIds = projects.Select(p => p.Id).ToHashSet();
        var keys = context.MatchKeys.Where(k => selectedIds.Contains(k.ProjectId)).ToArray();
        var proofs = new List<CarryoverProof>();
        if (request.PendingCarryoverDay is { } target)
        foreach (var key in keys.Where(k => !source.Results.ContainsKey(k)))
        {
            var original = DateOnly.Parse(schedule.Placements[key.MatchId].DayLabel);
            if (original >= target) continue;
            foreach (var log in source.ImportLogs.Where(l => l.VoidedAt is null).OrderBy(l => l.Id))
            foreach (var row in log.Rows.Where(r => r.Key == key && !r.HadResult && r.RecordDay < target)
                         .OrderBy(r => r.RecordDay).ThenBy(r => r.Location.SheetName, StringComparer.Ordinal).ThenBy(r => r.Location.RowNumber))
                proofs.Add(new(key, target, original, log.Id, log.ContentHash, row.RecordDay, row.Location));
        }
        var carryKeys = proofs.Select(p => p.Key).ToHashSet();
        var nodeOrders = source.Projects.SelectMany(p => p.MatchGraph!.Matches).ToDictionary(n => n.Id, n => n.Order);
        var projectOrders = projects.ToDictionary(p => p.Id, p => p.SortOrder);
        var courtOrders = schedule.Resources.Days.SelectMany(day => day.Courts.Select((court, order) =>
            (Day: day.Date.ToString("yyyy-MM-dd"), Court: court, Order: order)))
            .ToDictionary(item => (item.Day, item.Court), item => item.Order);
        var rows = days.SelectMany(day => keys.Where(key => schedule.Placements[key.MatchId].DayLabel == day.ToString("yyyy-MM-dd") ||
                (day == request.PendingCarryoverDay && carryKeys.Contains(key))).Select(key => new WorkspaceRecordExportRow(key, day)))
            .OrderBy(r => r.RecordDay).ThenBy(r => schedule.Placements[r.Key.MatchId].DayLabel, StringComparer.Ordinal)
            .ThenBy(r => schedule.Placements[r.Key.MatchId].StartTime)
            .ThenBy(r => courtOrders[(schedule.Placements[r.Key.MatchId].DayLabel, schedule.Placements[r.Key.MatchId].Court)])
            .ThenBy(r => projectOrders[r.Key.ProjectId]).ThenBy(r => r.Key.ProjectId)
            .ThenBy(r => nodeOrders[r.Key.MatchId]).ThenBy(r => r.Key.MatchId).ToArray();
        var materials = new List<MaterialPlan>();
        var projectNames = ProjectFileNames(source);
        var singleProject = projects.Length == 1;
        var selectedProjectName = singleProject ? projectNames[projects[0].Id] : null;
        var multipleYears = days[0].Year != days[^1].Year;
        void Add(OperationalMaterialKind kind, Guid? projectId, DateOnly? day, string extension,
            IReadOnlyList<WorkspaceRecordExportRow>? selectedRows = null) => materials.Add(new(kind, projectId, day,
                MaterialFileName(kind, projectId is { } id ? projectNames[id] : selectedProjectName, day, extension,
                    singleProject, multipleYears), selectedRows ?? []));
        foreach (var project in projects)
        { Add(OperationalMaterialKind.TimedDrawExcel, project.Id, null, ".xlsx"); Add(OperationalMaterialKind.TimedDrawA4Pdf, project.Id, null, ".pdf"); }
        foreach (var day in days)
        {
            var dayRows = rows.Where(r => r.RecordDay == day).ToArray();
            if (dayRows.Length == 0)
            { progress.Skips.Add(new("export.empty-day", "该记录日期无场次，跳过每日材料。", RecordDay: day)); continue; }
            Add(OperationalMaterialKind.DailyScheduleExcel, null, day, ".xlsx", dayRows);
            Add(OperationalMaterialKind.DailySchedulePdf, null, day, ".pdf", dayRows);
            Add(OperationalMaterialKind.MergedRecordExcel, null, day, ".xlsx", dayRows);
            Add(source.Kind == TournamentKind.Team ? OperationalMaterialKind.TeamScoreExcel : OperationalMaterialKind.IndividualScorePdf,
                null, day, source.Kind == TournamentKind.Team ? ".xlsx" : ".pdf", dayRows);
        }
        Require(rows.Length > 0, "export.no-matches", "所选范围没有可导出的记录场次；未生成任何材料。");
        if (!singleProject) Add(OperationalMaterialKind.QualityExcel, null, null, ".xlsx");
        Add(OperationalMaterialKind.Description, null, null, ".txt");
        progress.Counts = new(rows.Select(r => r.Key).Distinct().Count(), rows.Length, carryKeys.Count,
            projects.Sum(p => p.MatchGraph!.Matches.Count), materials.Count);
        var outputDirectory = PackageDirectory(request.OutputDirectory, days, selectedProjectName);
        Require(materials.Select(m => m.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == materials.Count,
            "export.duplicate-path", "材料文件名重复，无法安全导出。");
        foreach (var material in materials)
            WorkspaceExportPublication.ValidateDestination(Path.Combine(outputDirectory, material.FileName), source, workspacePath, true);
        return new(outputDirectory, context, projects, rows, proofs, materials);
    }

    private static void Require(bool condition, string code, string message)
    { if (!condition) throw new WorkspaceCommandException(new(code, message)); }
}
