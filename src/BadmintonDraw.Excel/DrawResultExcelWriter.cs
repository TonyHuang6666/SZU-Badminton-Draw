using BadmintonDraw.Core;
using ClosedXML.Excel;

namespace BadmintonDraw.Excel;

public sealed class DrawResultExcelWriter
{
    public void Write(string outputPath, DrawResult result, IReadOnlyList<DrawParticipant> sourceParticipants)
    {
        using var workbook = new XLWorkbook();

        WriteGroupSheet(workbook, "总分组结果", result.Groups);
        if (result.Settings.IsKnockout)
        {
            WriteGroupSheet(workbook, "第一轮对阵名单", result.RoundOneGroups);
            WriteGroupSheet(workbook, "轮空或直接晋级", result.ByeGroups);
        }

        WriteExcelLayoutSheet(workbook, "Excel排表格式", result.Groups);
        WriteAuditSheet(workbook, "抽签设置与审计信息", result);
        WriteRosterSheet(workbook, "原始名单", sourceParticipants);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        workbook.SaveAs(outputPath);
    }

    private static void WriteGroupSheet(XLWorkbook workbook, string sheetName, IReadOnlyList<DrawGroup> groups)
    {
        var sheet = workbook.Worksheets.Add(sheetName);
        var headers = new[] { "组别", "组内序号", "名称", "是否种子", "种子序号", "备注" };

        WriteHeader(sheet, headers);
        var row = 2;

        foreach (var group in groups)
        {
            if (group.Participants.Count == 0)
            {
                sheet.Cell(row, 1).Value = $"第 {group.Number} 组";
                sheet.Cell(row, 2).Value = 0;
                row++;
                continue;
            }

            for (var i = 0; i < group.Participants.Count; i++)
            {
                var participant = group.Participants[i];
                sheet.Cell(row, 1).Value = $"第 {group.Number} 组";
                sheet.Cell(row, 2).Value = i + 1;
                sheet.Cell(row, 3).Value = participant.DisplayName;
                sheet.Cell(row, 4).Value = participant.IsSeed ? "是" : "";
                sheet.Cell(row, 5).Value = participant.SeedRank.HasValue ? participant.SeedRank.Value : "";
                sheet.Cell(row, 6).Value = participant.Note ?? "";
                row++;
            }
        }

        ApplyTableStyle(sheet, headers.Length, row - 1);
    }

    private static void WriteExcelLayoutSheet(XLWorkbook workbook, string sheetName, IReadOnlyList<DrawGroup> groups)
    {
        var sheet = workbook.Worksheets.Add(sheetName);
        var row = 1;

        foreach (var group in groups)
        {
            sheet.Cell(row, 1).Value = $"第 {group.Number} 组";
            sheet.Cell(row, 2).Value = $"({group.Participants.Count})";
            sheet.Range(row, 1, row, 2).Style.Font.Bold = true;
            row++;

            foreach (var participant in group.Participants)
            {
                sheet.Cell(row, 1).Value = participant.DisplayName;
                row += 2;
            }

            row++;
        }

        sheet.Columns().AdjustToContents();
        sheet.SheetView.FreezeRows(1);
    }

    private static void WriteAuditSheet(XLWorkbook workbook, string sheetName, DrawResult result)
    {
        var sheet = workbook.Worksheets.Add(sheetName);
        var rows = new (string Key, string Value)[]
        {
            ("比赛模式", result.Settings.CompetitionMode.ToString()),
            ("项目类型", result.Settings.EventKind.ToString()),
            ("算法版本", result.Audit.AlgorithmVersion.ToString()),
            ("随机种子", result.Audit.RandomSeed),
            ("输入哈希", result.Audit.InputHash),
            ("生成时间 UTC", result.Audit.GeneratedAtUtc.ToString("yyyy-MM-dd HH:mm:ss zzz")),
            ("参赛数量", result.Audit.ParticipantCount.ToString()),
            ("种子数量", result.Audit.SeedCount.ToString()),
            ("小组数量", result.Audit.GroupCount.ToString())
        };

        sheet.Cell(1, 1).Value = "项目";
        sheet.Cell(1, 2).Value = "值";

        for (var i = 0; i < rows.Length; i++)
        {
            sheet.Cell(i + 2, 1).Value = rows[i].Key;
            sheet.Cell(i + 2, 2).Value = rows[i].Value;
        }

        ApplyTableStyle(sheet, 2, rows.Length + 1);
    }

    private static void WriteRosterSheet(XLWorkbook workbook, string sheetName, IReadOnlyList<DrawParticipant> participants)
    {
        var sheet = workbook.Worksheets.Add(sheetName);
        var headers = new[] { "名称", "姓名", "搭档", "队伍", "是否种子", "种子序号", "备注" };

        WriteHeader(sheet, headers);

        for (var i = 0; i < participants.Count; i++)
        {
            var row = i + 2;
            var participant = participants[i];
            sheet.Cell(row, 1).Value = participant.DisplayName;
            sheet.Cell(row, 2).Value = participant.PrimaryName ?? "";
            sheet.Cell(row, 3).Value = participant.PartnerName ?? "";
            sheet.Cell(row, 4).Value = participant.TeamName ?? "";
            sheet.Cell(row, 5).Value = participant.IsSeed ? "是" : "";
            sheet.Cell(row, 6).Value = participant.SeedRank.HasValue ? participant.SeedRank.Value : "";
            sheet.Cell(row, 7).Value = participant.Note ?? "";
        }

        ApplyTableStyle(sheet, headers.Length, participants.Count + 1);
    }

    private static void WriteHeader(IXLWorksheet sheet, IReadOnlyList<string> headers)
    {
        for (var i = 0; i < headers.Count; i++)
        {
            sheet.Cell(1, i + 1).Value = headers[i];
        }
    }

    private static void ApplyTableStyle(IXLWorksheet sheet, int columnCount, int lastRow)
    {
        var range = sheet.Range(1, 1, Math.Max(lastRow, 1), columnCount);
        range.CreateTable();
        sheet.Row(1).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents();
    }
}
