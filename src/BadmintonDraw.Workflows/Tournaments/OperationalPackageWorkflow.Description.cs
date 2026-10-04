using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Workflows.Tournaments;

public sealed partial class OperationalPackageWorkflow
{
    private static string Description(TournamentWorkspace source, PackagePlan plan, Guid auditId,
        DateTimeOffset exportedAt, ExportProgress progress)
    {
        var names = ProjectFileNames(source);
        var multipleProjects = plan.Projects.Count > 1;
        var lines = new List<string>
        {
            multipleProjects ? "多项目合并材料包" : names[plan.Projects[0].Id] + "比赛材料包",
            $"赛事：{source.Name}", $"导出时间：{exportedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}",
            $"项目数量：{plan.Projects.Count}", $"项目：{string.Join("、", plan.Projects.Select(p => names[p.Id]))}",
            $"日期：{string.Join("、", progress.Scope!.Days.Select(d => d.ToString("yyyy-MM-dd")))}",
            $"比赛场次：{progress.Counts!.DistinctMatchCount}",
            $"记录行数：{progress.Counts.RecordRowCount}；其中待补打场次：{progress.Counts.PendingCarryoverCount}", "",
            "每日材料："
        };
        foreach (var day in progress.Scope.Days)
        {
            var daily = plan.Materials.Where(m => m.RecordDay == day).ToArray();
            if (daily.Length == 0) { lines.Add($"{day:yyyy-MM-dd}：没有所选项目的比赛，未生成每日材料。"); continue; }
            lines.Add($"{day:yyyy-MM-dd}（{plan.Rows.Count(r => r.RecordDay == day)} 场）：");
            lines.AddRange(daily.Select(m => "  " + m.FileName));
        }
        lines.AddRange([
            "", "填写与打印：",
            "赛程记录表：比分、用时和胜方供现场填写。可选择胜方下拉，或填写 A/B；正常比赛、弃权及实际比赛日期按表内说明填写。",
            "同一记录表中，前序胜负会通过公式带入后续对阵。跨日期文件不互相引用，请先导入前一天赛果，再导出后续日期材料。",
            multipleProjects
                ? "每天只有一份合并赛程记录表，包含所选项目当天的全部场次；按比赛时间、场地顺序填写，不另生成分项目记录表。"
                : "每天只有一份赛程记录表，包含所选项目当天的全部场次；按比赛时间、场地顺序填写。",
            "历史导出文件不会自动清理；需要干净材料包时请选择新的保存文件夹。",
            "赛程安排表 PDF 是各场地的整日总览；Excel 可查看赛程明细和参数。计分表按场次打印。",
            "带时间对阵图覆盖所选项目的全部场次。打印请使用对应 PDF；Excel 自然分页可能拆开连接线或合并单元格。",
            "待补打行仅用于记录，不会移动比赛，不代表已安排新的时间和场地。",
        ]);
        if (multipleProjects) lines.Add("排程检查报告检查整场赛事，不缩小检查范围。");
        lines.AddRange(["", "文件清单："]);
        lines.AddRange(plan.Materials.Select(m => m.FileName));
        lines.AddRange([
            "", "导出核对信息：",
            "文件存在不代表整包成功或导出记录已保存。请以程序最终提示和正式赛事文件中的导出记录为准。",
            $"赛事标识：{source.Id:D}；来源修订：{source.Revision}；赛程修订：{source.Schedule!.Revision}",
            $"本次导出标识：{auditId:D}（正式存档中的 OperationalPackageExported 记录证明已保存，并保留文件大小、SHA-256 和导出范围。）"
        ]);
        if (progress.Skips.Count > 0)
        {
            lines.Add("跳过说明：");
            lines.AddRange(progress.Skips.Select(s => $"{s.RecordDay:yyyy-MM-dd} { (s.ProjectId is { } id ? names[id] : "全部项目")}：{s.Message}"));
        }
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
