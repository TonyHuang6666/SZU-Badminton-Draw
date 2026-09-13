using System.Text.Json;
using System.Text.Json.Serialization;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Workflows.Tournaments;

public sealed partial class OperationalPackageWorkflow
{
    private static readonly JsonSerializerOptions ManifestOptions = new()
    { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private static string Manifest(TournamentWorkspace source, PackagePlan plan, Guid auditId,
        DateTimeOffset exportedAt, ExportProgress progress, IEnumerable<OperationalPackageOutput> outputs) =>
        JsonSerializer.Serialize(new
        {
            FormatVersion = 1,
            Source = new { WorkspaceId = source.Id, source.Name, source.Kind, source.Revision, ScheduleRevision = source.Schedule!.Revision,
                Projects = source.Projects.OrderBy(p => p.SortOrder).ThenBy(p => p.Id).Select(p => new
                { ProjectId = p.Id, p.DisplayName, GraphRevision = p.MatchGraph!.Revision, ConfirmationEpoch = p.Draw!.ConfirmedAt }) },
            Audit = new { Id = auditId, PlannedAt = exportedAt, State = "Planned" },
            progress.Scope, progress.Counts, progress.Skips,
            ScopeNotice = "带时间对阵图覆盖所选项目的完整比赛图；质量报告检查整个赛事；每日材料仅覆盖明确选择的记录日期和项目。",
            Rows = plan.Rows.Select(r => new { r.Key, r.RecordDay, OriginalPlanDay = source.Schedule.Placements[r.Key.MatchId].DayLabel,
                IsPendingCarryover = plan.CarryoverEvidence.Any(p => p.Key == r.Key && p.RecordDay == r.RecordDay) }),
            CarryoverEvidence = plan.CarryoverEvidence,
            Files = outputs.Select(o => new { o.Kind, o.ProjectId, o.RecordDay, FileName = Path.GetFileName(o.Path), o.ByteLength, o.Sha256 })
        }, ManifestOptions);

    private static string Description(TournamentWorkspace source, Guid auditId, DateTimeOffset exportedAt, ExportProgress progress) =>
        $"赛事运营材料包：{source.Name}\n赛事标识：{source.Id:D}\n来源工作区修订：{source.Revision}；赛程修订：{source.Schedule!.Revision}\n" +
        $"生成时间：{exportedAt:O}\n计划导出审计标识：{auditId:D}\n" +
        "此说明与 manifest 中的审计仅为计划信息。文件存在不代表审计已保存；请在正式赛事工作区查找上述标识的 OperationalPackageExported 审计记录，确认本次保存。\n" +
        $"项目范围：{string.Join("、", progress.Scope!.ProjectIds)}\n记录日期：{string.Join("、", progress.Scope.Days)}\n" +
        $"补打覆盖目标：{progress.Scope.PendingCarryoverDay?.ToString("yyyy-MM-dd") ?? "未选择"}\n" +
        $"不同场次 {progress.Counts!.DistinctMatchCount}；记录行 {progress.Counts.RecordRowCount}；额外待补打 {progress.Counts.PendingCarryoverCount}；完整对阵图场次 {progress.Counts.TimedDrawMatchCount}。\n" +
        "项目记录表与合并记录表可能覆盖同一场次，不应把物理文件行数相加视为比赛总数。\n" +
        "带时间对阵图包含所选项目的完整比赛图，不限于所选记录日；质量报告始终检查整个赛事。补打行只是记录覆盖，不是重排时间或实际比赛日期。\n" +
        "对阵图打印请使用程序生成的 TimedDrawA4Pdf。XLSX 用于编辑和数据查看；Office 自然分页可能拆分连接线或合并单元格，不代表已完善的对阵图打印布局。PDF 分页使用本次明确配置，不自动更改。\n" +
        string.Join("\n", progress.Skips.Select(s => $"跳过 [{s.Code}] 项目 {s.ProjectId} 日期 {s.RecordDay}：{s.Message}")) + "\n";
}
