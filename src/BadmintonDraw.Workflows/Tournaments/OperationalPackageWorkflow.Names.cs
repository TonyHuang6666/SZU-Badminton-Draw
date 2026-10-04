using System.Text;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Workflows.Tournaments;

public sealed partial class OperationalPackageWorkflow
{
    // Derive labels from all projects, not just the selection, so a project's filename stays
    // the same when exporting it separately. IDs remain in the workbook and manifest.
    private static IReadOnlyDictionary<Guid, string> ProjectFileNames(TournamentWorkspace workspace)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<Guid, string>();
        foreach (var project in workspace.Projects.OrderBy(p => p.SortOrder).ThenBy(p => p.Id))
        {
            var safe = WorkflowFileNames.Sanitize(project.DisplayName).Normalize(NormalizationForm.FormC).Trim('.');
            // Leave room for dates, suffixes and extensions on Windows and macOS.
            safe = string.Concat(safe.EnumerateRunes().Take(40).Select(r => r.ToString()));
            if (safe.Length == 0) safe = "未命名项目";
            var candidate = safe;
            for (var suffix = 2; !used.Add(candidate); suffix++) candidate = $"{safe}（{suffix}）";
            names.Add(project.Id, candidate);
        }
        return names;
    }

    private static string MaterialFileName(OperationalMaterialKind kind, string? project, DateOnly? day,
        string extension, bool selectedProject, bool multipleYears)
    {
        var date = day is { } d ? DateLabel(d, multipleYears) : "";
        var title = kind switch
        {
            OperationalMaterialKind.TimedDrawExcel or OperationalMaterialKind.TimedDrawA4Pdf => project + "带时间对阵图",
            OperationalMaterialKind.ProjectRecordExcel => date + project + "赛程记录表",
            OperationalMaterialKind.DailyScheduleExcel or OperationalMaterialKind.DailySchedulePdf => date + "合并赛程安排表",
            OperationalMaterialKind.MergedRecordExcel => date + "合并赛程记录表",
            OperationalMaterialKind.IndividualScorePdf => date + "合并单场比赛计分表",
            OperationalMaterialKind.TeamScoreExcel => date + "合并团体比赛计分表",
            OperationalMaterialKind.QualityExcel => selectedProject ? "赛程检查报告" : "多项目排程检查报告",
            OperationalMaterialKind.Description => selectedProject ? "比赛材料包说明" : "合并材料包说明",
            OperationalMaterialKind.Manifest => "材料包校验清单",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return title + extension;
    }

    private static string PackageDirectory(string parent, IReadOnlyList<DateOnly> days, string? project)
    {
        var multipleYears = days[0].Year != days[^1].Year;
        var dates = DateLabel(days[0], multipleYears) + (days.Count == 1 ? "" : "-" + DateLabel(days[^1], multipleYears));
        var path = Path.Combine(Path.GetFullPath(parent), dates + (project is null ? "多项目合并材料包" : project + "比赛材料包"));
        ValidatePackageDirectory(path);
        return path;
    }

    private static string DateLabel(DateOnly day, bool includeYear) => includeYear
        ? $"{day.Year}年{day.Month}月{day.Day}日" : $"{day.Month}月{day.Day}日";

    private static void ValidatePackageDirectory(string path) => Require(
        !File.Exists(path) && new DirectoryInfo(path).LinkTarget is null,
        "export.protected-path", "材料包文件夹位置已被文件或符号链接占用，请选择其他导出位置：" + path);
}
