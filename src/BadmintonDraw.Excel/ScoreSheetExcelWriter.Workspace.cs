using System.Globalization;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Tournaments;
using ClosedXML.Excel;

namespace BadmintonDraw.Excel;

public sealed partial class ScoreSheetExcelWriter
{
    public void WriteIndividualMatchScorePdf(string outputPath, WorkspaceScheduleExportContext context,
        IReadOnlyList<WorkspaceRecordExportRow> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        using var workbook = BuildIndividualScoreSheetWorkbook(context, rows);
        WriteIndividualPdf(outputPath, workbook);
    }

    // The actual workbook handed to the public PDF renderer, not a parallel test renderer.
    internal static XLWorkbook BuildIndividualScoreSheetWorkbook(WorkspaceScheduleExportContext context,
        IReadOnlyList<WorkspaceRecordExportRow> rows)
    {
        var selected = WorkspaceMaterialPresentation.SelectRows(context, rows, "计分表");
        RequireFamily(context, TournamentKind.Individual);
        var data = selected.Select((row, index) =>
        {
            var node = context.Nodes[row.Key]; var display = WorkspaceMaterialPresentation.Coverage(context, row);
            string[] Players(EntrantSource source) => context.ResolveParticipant(row.Key.ProjectId, source)?.Players
                .Select(player => player.Name).ToArray() ?? [];
            var caption = display.IsDifferentDay || display.IsCompleted ?
                $"羽毛球比赛记分表 · 记录日期 {display.RecordDayText} · 实际日期 {ActualDay(display)} · " +
                display.OriginalPlanText : null;
            return new IndividualScoreSheetData(context.Projects[row.Key.ProjectId].DisplayName, node.Phase,
                display.RecordDayText, display.StartTimeText, display.CourtText, index + 1,
                Players(node.SideA), Players(node.SideB), caption);
        }).ToArray();
        var workbook = BuildIndividualWorkbook(data);
        try
        {
            for (var index = 0; index < selected.Length; index++)
            {
                if (data[index].Caption is null || DrawResultVisualWriter.FitsAtDeclaredFontSize(
                    workbook.Worksheet(index + 1).Range("A1:AX1"), ScoreSheetPdfLeftInset, ScoreSheetPdfRightInset))
                    continue;
                var key = selected[index].Key;
                throw new WorkspaceValidationException("export.layout",
                    $"项目“{context.Projects[key.ProjectId].DisplayName}”比赛“{context.Nodes[key].DisplayName}”的计分表日期说明无法在固定标题区清晰显示" +
                    $"（场地“{context.Placements[key].Court}”）。请缩短场地名称后重试；未生成文件，原始数据未修改。");
            }
            return workbook;
        }
        catch { workbook.Dispose(); throw; }
    }

    private static string ActualDay(WorkspaceCoverageDisplay display) =>
        display.ActualPlayedDay?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "未知";

    public void WriteTeamScoreSheets(string outputPath, WorkspaceScheduleExportContext context,
        IReadOnlyList<WorkspaceRecordExportRow> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var selected = WorkspaceMaterialPresentation.SelectRows(context, rows, "团体记分表");
        RequireFamily(context, TournamentKind.Team);
        var data = selected.Select(row =>
        {
            var node = context.Nodes[row.Key]; var display = WorkspaceMaterialPresentation.Coverage(context, row);
            string Side(EntrantSource source)
            {
                if (context.ResolveParticipant(row.Key.ProjectId, source) is { } participant) return participant.DisplayName;
                var sourceId = source switch { EntrantSource.WinnerOf w => w.MatchId, EntrantSource.LoserOf l => l.MatchId,
                    _ => throw new WorkspaceValidationException("export.source", "计分表包含不可比赛的来源。") };
                return "待定：" + context.Nodes[new(row.Key.ProjectId, sourceId)].DisplayName +
                    (source is EntrantSource.WinnerOf ? "胜者" : "负者");
            }
            var notes = new List<string>();
            if (!string.IsNullOrWhiteSpace(node.Note)) notes.Add(node.Note);
            if (display.IsDifferentDay || display.IsCompleted)
            {
                notes.Add($"本页记录日期 {display.RecordDayText}；实际比赛日期 {ActualDay(display)}。");
                notes.Add(display.OriginalPlanText + "；记录日期不改变赛程。");
            }
            return new TeamScoreSheetData(node.Phase, node.GroupName, display.RecordDayText, display.TimeRangeText,
                display.CourtText, Side(node.SideA), Side(node.SideB), string.Join("\n", notes),
                context.Workspace.Name + " · 团体赛记分表");
        }).ToArray();
        using var workbook = new XLWorkbook();
        WriteTeamBlocks(workbook.AddWorksheet("团体记分表"), data);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        workbook.SaveAs(outputPath);
    }

    private static void RequireFamily(WorkspaceScheduleExportContext context, TournamentKind kind)
    {
        if (context.Workspace.Kind != kind)
            throw new WorkspaceValidationException("export.kind", "计分材料类型与团体赛/单项赛工作区不一致。");
    }
}
