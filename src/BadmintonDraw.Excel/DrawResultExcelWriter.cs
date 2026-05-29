using BadmintonDraw.Core;
using ClosedXML.Excel;

namespace BadmintonDraw.Excel;

public sealed class DrawResultExcelWriter
{
    private const int BracketStartRow = 6;
    private const int SlotRowGap = 4;
    private const int PlayInFirstColumn = 1;
    private const int PlayInWinnerColumn = 3;
    private const int MainDrawFirstColumn = 5;
    private const int RoundColumnGap = 4;
    private const int MergedCellWidth = 2;

    private static readonly XLColor TitleFill = XLColor.FromHtml("#1F4E78");
    private static readonly XLColor HeaderFill = XLColor.FromHtml("#305496");
    private static readonly XLColor PlayInFill = XLColor.FromHtml("#FCE4D6");
    private static readonly XLColor PlayInWinnerFill = XLColor.FromHtml("#FFF2CC");
    private static readonly XLColor ByeFill = XLColor.FromHtml("#E2F0D9");
    private static readonly XLColor FutureFill = XLColor.FromHtml("#E7E6E6");
    private static readonly XLColor GroupFill = XLColor.FromHtml("#D9EAF7");
    private static readonly XLColor NoteFill = XLColor.FromHtml("#EEF2FF");

    public void Write(string outputPath, DrawResult result, IReadOnlyList<DrawParticipant> sourceParticipants)
    {
        using var workbook = new XLWorkbook();

        if (result.Settings.IsKnockout)
        {
            WriteUnifiedBracketSheet(workbook, result);
        }
        else
        {
            WriteGroupSheet(workbook, "总分组结果", result.Groups);
        }

        WriteAuditSheet(workbook, "抽签设置与审计信息", result);
        WriteRosterSheet(workbook, "原始名单", sourceParticipants);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        workbook.SaveAs(outputPath);
    }

    private static void WriteUnifiedBracketSheet(XLWorkbook workbook, DrawResult result)
    {
        var sheet = workbook.Worksheets.Add(BuildBracketSheetName(result.Settings.EventKind));
        var bracketSlots = BuildBracketSlots(result);
        var mainSlotCount = bracketSlots.Count;
        var roundColumns = BuildRoundColumns(mainSlotCount);
        var lastColumn = roundColumns.Count > 0
            ? roundColumns[^1] + MergedCellWidth - 1
            : MainDrawFirstColumn + MergedCellWidth - 1;
        var noteRow = BracketStartRow + mainSlotCount * SlotRowGap + 2;

        ConfigureBracketSheet(sheet, lastColumn, noteRow);
        WriteBracketTitle(sheet, result, mainSlotCount, bracketSlots.Count(slot => slot.IsPlayIn), lastColumn);
        WriteBracketHeaders(sheet, roundColumns, mainSlotCount);
        WriteGroupBands(sheet, result, bracketSlots);
        WriteFirstRoundSlots(sheet, bracketSlots, roundColumns[0]);
        WriteFutureRoundSlots(sheet, mainSlotCount, roundColumns);
        WriteBracketNote(sheet, noteRow, lastColumn);

        sheet.SheetView.FreezeRows(4);
        sheet.SheetView.FreezeColumns(4);
        sheet.ShowGridLines = false;
        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sheet.PageSetup.FitToPages(1, 0);
        sheet.PageSetup.SetRowsToRepeatAtTop(1, 4);
        sheet.PageSetup.PrintAreas.Add($"A1:{sheet.Cell(noteRow, lastColumn).Address.ToStringRelative()}");
    }

    private static string BuildBracketSheetName(EventKind eventKind)
    {
        return eventKind switch
        {
            EventKind.Doubles => "深大双打",
            EventKind.Team => "深大团体",
            _ => "深大男单"
        };
    }

    private static List<BracketSlot> BuildBracketSlots(DrawResult result)
    {
        var slots = new List<BracketSlot>();

        foreach (var group in result.Groups)
        {
            var roundOneGroup = result.RoundOneGroups.FirstOrDefault(item => item.Number == group.Number);
            var byeGroup = result.ByeGroups.FirstOrDefault(item => item.Number == group.Number);
            var roundOneParticipants = roundOneGroup?.Participants ?? Array.Empty<DrawParticipant>();
            var byeParticipants = byeGroup?.Participants ?? Array.Empty<DrawParticipant>();

            for (var i = 0; i + 1 < roundOneParticipants.Count; i += 2)
            {
                var matchNumber = i / 2 + 1;
                slots.Add(new BracketSlot(
                    group.Number,
                    $"第{group.Number}组附加赛{matchNumber}胜者",
                    true,
                    roundOneParticipants[i].DisplayName,
                    roundOneParticipants[i + 1].DisplayName));
            }

            foreach (var participant in byeParticipants)
            {
                slots.Add(new BracketSlot(group.Number, participant.DisplayName, false, "", ""));
            }
        }

        return slots;
    }

    private static List<int> BuildRoundColumns(int mainSlotCount)
    {
        var columns = new List<int>();
        var roundCount = Math.Max(1, (int)Math.Ceiling(Math.Log2(Math.Max(mainSlotCount, 1))) + 1);

        for (var i = 0; i < roundCount; i++)
        {
            columns.Add(MainDrawFirstColumn + i * RoundColumnGap);
        }

        return columns;
    }

    private static void ConfigureBracketSheet(IXLWorksheet sheet, int lastColumn, int noteRow)
    {
        for (var column = 1; column <= lastColumn; column++)
        {
            sheet.Column(column).Width = column <= 4 || (column - MainDrawFirstColumn) % RoundColumnGap <= 1
                ? 13
                : 4;
        }

        for (var row = 1; row <= noteRow; row++)
        {
            sheet.Row(row).Height = 18;
        }

        sheet.Row(1).Height = 30;
        sheet.Row(2).Height = 24;
        sheet.Row(4).Height = 24;
    }

    private static void WriteBracketTitle(
        IXLWorksheet sheet,
        DrawResult result,
        int mainSlotCount,
        int playInCount,
        int lastColumn)
    {
        var participantLabel = result.Settings.EventKind switch
        {
            EventKind.Doubles => "对",
            EventKind.Team => "队",
            _ => "人"
        };

        WriteMergedCell(
            sheet,
            1,
            1,
            1,
            lastColumn,
            $"深大羽协{EventKindText(result.Settings.EventKind)}{result.Audit.ParticipantCount}{participantLabel}大表格对阵表",
            TitleFill,
            XLColor.White,
            isBold: true,
            fontSize: 16);

        WriteMergedCell(
            sheet,
            2,
            1,
            2,
            lastColumn,
            $"共{result.Audit.ParticipantCount}{participantLabel}：{playInCount}场附加赛 + {mainSlotCount}{participantLabel}主签。附加赛胜者直接进入同一行的主签位置。",
            NoteFill,
            XLColor.FromHtml("#1F2937"),
            fontSize: 11);
    }

    private static void WriteBracketHeaders(IXLWorksheet sheet, IReadOnlyList<int> roundColumns, int mainSlotCount)
    {
        WriteMergedCell(
            sheet,
            4,
            PlayInFirstColumn,
            4,
            PlayInWinnerColumn + MergedCellWidth - 1,
            "附加赛",
            HeaderFill,
            XLColor.White,
            isBold: true);

        for (var i = 0; i < roundColumns.Count; i++)
        {
            var header = i == roundColumns.Count - 1
                ? "冠军"
                : BuildRoundHeader(mainSlotCount, i);

            WriteMergedCell(
                sheet,
                4,
                roundColumns[i],
                4,
                roundColumns[i] + MergedCellWidth - 1,
                header,
                HeaderFill,
                XLColor.White,
                isBold: true);
        }
    }

    private static string BuildRoundHeader(int mainSlotCount, int roundIndex)
    {
        var from = Math.Max(1, mainSlotCount / (int)Math.Pow(2, roundIndex));
        var to = Math.Max(1, from / 2);
        if (from == 4)
        {
            return "半决赛";
        }

        if (from == 2)
        {
            return "决赛";
        }

        return $"{from}进{to}";
    }

    private static void WriteGroupBands(
        IXLWorksheet sheet,
        DrawResult result,
        IReadOnlyList<BracketSlot> bracketSlots)
    {
        foreach (var group in result.Groups)
        {
            var firstIndex = bracketSlots.ToList().FindIndex(slot => slot.GroupNumber == group.Number);
            if (firstIndex < 0)
            {
                continue;
            }

            var playInCount = bracketSlots.Count(slot => slot.GroupNumber == group.Number && slot.IsPlayIn);
            var mainSlotCount = bracketSlots.Count(slot => slot.GroupNumber == group.Number);
            var row = BracketStartRow + firstIndex * SlotRowGap - 1;

            WriteMergedCell(
                sheet,
                row,
                1,
                row,
                24,
                $"第{group.Number}组：{group.Participants.Count}人，{playInCount}场附加赛，{mainSlotCount}人主签",
                GroupFill,
                XLColor.FromHtml("#1F2937"),
                isBold: true);
        }
    }

    private static void WriteFirstRoundSlots(
        IXLWorksheet sheet,
        IReadOnlyList<BracketSlot> bracketSlots,
        int firstRoundColumn)
    {
        for (var i = 0; i < bracketSlots.Count; i++)
        {
            var slot = bracketSlots[i];
            var row = BracketStartRow + i * SlotRowGap;

            if (slot.IsPlayIn)
            {
                WriteMergedCell(sheet, row, PlayInFirstColumn, row, PlayInFirstColumn + 1, slot.PlayInFirstName, PlayInFill);
                WriteMergedCell(sheet, row + 1, PlayInFirstColumn, row + 1, PlayInFirstColumn + 1, "vs", PlayInFill, fontSize: 9);
                WriteMergedCell(sheet, row + 2, PlayInFirstColumn, row + 2, PlayInFirstColumn + 1, slot.PlayInSecondName, PlayInFill);
                WriteMergedCell(sheet, row, PlayInWinnerColumn, row + 2, PlayInWinnerColumn + 1, "胜者入主签", PlayInWinnerFill, fontSize: 9);
                WriteMergedCell(sheet, row, firstRoundColumn, row, firstRoundColumn + 1, slot.MainDrawName, PlayInWinnerFill);
            }
            else
            {
                WriteMergedCell(sheet, row, firstRoundColumn, row, firstRoundColumn + 1, slot.MainDrawName, ByeFill);
            }
        }
    }

    private static void WriteFutureRoundSlots(
        IXLWorksheet sheet,
        int mainSlotCount,
        IReadOnlyList<int> roundColumns)
    {
        for (var roundIndex = 1; roundIndex < roundColumns.Count; roundIndex++)
        {
            var matchCount = Math.Max(1, (int)Math.Ceiling(mainSlotCount / Math.Pow(2, roundIndex)));
            var step = SlotRowGap * (int)Math.Pow(2, roundIndex);
            var offset = Math.Max(1, step / 2 - SlotRowGap / 2);

            for (var matchIndex = 0; matchIndex < matchCount; matchIndex++)
            {
                var row = BracketStartRow + matchIndex * step + offset;
                if (BuildRoundHeader(mainSlotCount, roundIndex) == "半决赛")
                {
                    row++;
                }

                var value = roundIndex == roundColumns.Count - 1
                    ? "冠军"
                    : $"胜者{matchIndex + 1}";

                WriteMergedCell(
                    sheet,
                    row,
                    roundColumns[roundIndex],
                    row,
                    roundColumns[roundIndex] + 1,
                    value,
                    FutureFill);
            }
        }
    }

    private static void WriteBracketNote(IXLWorksheet sheet, int noteRow, int lastColumn)
    {
        WriteMergedCell(
            sheet,
            noteRow,
            1,
            noteRow,
            lastColumn,
            "填写说明：左侧附加赛完成后，将胜者姓名填入黄色主签位；绿色为直接进入主签的选手；灰色格用于逐轮填写胜者。",
            NoteFill,
            XLColor.FromHtml("#1F2937"));
    }

    private static void WriteMergedCell(
        IXLWorksheet sheet,
        int firstRow,
        int firstColumn,
        int lastRow,
        int lastColumn,
        string value,
        XLColor fill,
        XLColor? fontColor = null,
        bool isBold = false,
        double fontSize = 10)
    {
        var range = sheet.Range(firstRow, firstColumn, lastRow, lastColumn);
        range.Merge();
        range.Value = value;
        range.Style.Fill.BackgroundColor = fill;
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.OutsideBorderColor = XLColor.FromHtml("#808080");
        range.Style.Border.InsideBorderColor = XLColor.FromHtml("#808080");
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        range.Style.Alignment.WrapText = true;
        range.Style.Font.FontName = "Microsoft YaHei";
        range.Style.Font.FontSize = fontSize;
        range.Style.Font.Bold = isBold;

        if (fontColor is not null)
        {
            range.Style.Font.FontColor = fontColor;
        }
    }

    private static string EventKindText(EventKind eventKind)
    {
        return eventKind switch
        {
            EventKind.Doubles => "双打",
            EventKind.Team => "团体",
            _ => "男单"
        };
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
                sheet.Cell(row, 1).Value = $"第{group.Number}组";
                sheet.Cell(row, 2).Value = 0;
                row++;
                continue;
            }

            for (var i = 0; i < group.Participants.Count; i++)
            {
                var participant = group.Participants[i];
                sheet.Cell(row, 1).Value = $"第{group.Number}组";
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

    private sealed record BracketSlot(
        int GroupNumber,
        string MainDrawName,
        bool IsPlayIn,
        string PlayInFirstName,
        string PlayInSecondName);
}
