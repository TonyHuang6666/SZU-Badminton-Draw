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
    private static readonly XLColor QualifiedFill = XLColor.FromHtml("#00B050");
    private static readonly XLColor GroupFill = XLColor.FromHtml("#D9EAF7");
    private static readonly XLColor NoteFill = XLColor.FromHtml("#EEF2FF");
    private static readonly XLColor SeedFontColor = XLColor.FromHtml("#C00000");

    public void Write(string outputPath, DrawResult result, IReadOnlyList<DrawParticipant> sourceParticipants)
    {
        using var workbook = new XLWorkbook();

        if (result.Settings.IsKnockout)
        {
            WriteUnifiedBracketSheet(workbook, result);
        }
        else
        {
            WriteRoundRobinSheet(workbook, result);
        }

        WriteAuditSheet(workbook, "抽签设置与审计信息", result);
        WriteRosterSheet(workbook, "原始名单", sourceParticipants);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        workbook.SaveAs(outputPath);
    }

    private static void WriteUnifiedBracketSheet(XLWorkbook workbook, DrawResult result)
    {
        var sheet = workbook.Worksheets.Add("对阵表");
        var bracketSlots = BuildBracketSlots(result);
        var mainSlotCount = bracketSlots.Count;
        var isGroupedChampionBracket = result.Settings.KnockoutGoal == KnockoutGoal.Champion
            && result.Groups.Count > 1
            && IsPowerOfTwo(result.Groups.Count);
        var isQualifierBracket = result.Groups.Count > 1 && !isGroupedChampionBracket;
        var qualifierHeaders = isQualifierBracket || isGroupedChampionBracket
            ? BuildQualifierRoundHeaders(result, bracketSlots)
            : null;
        var championHeaders = isGroupedChampionBracket
            ? BuildChampionRoundHeaders(result.Groups.Count)
            : null;
        var roundColumns = qualifierHeaders is not null
            ? BuildRoundColumnsByCount(qualifierHeaders.Count + (championHeaders?.Count ?? 0))
            : BuildRoundColumns(mainSlotCount);
        var customHeaders = qualifierHeaders is null
            ? null
            : qualifierHeaders.Concat(championHeaders ?? []).ToList();
        var lastColumn = roundColumns.Count > 0
            ? roundColumns[^1] + MergedCellWidth - 1
            : MainDrawFirstColumn + MergedCellWidth - 1;
        var noteRow = BracketStartRow + mainSlotCount * SlotRowGap + 2;

        ConfigureBracketSheet(sheet, lastColumn, noteRow);
        WriteBracketTitle(
            sheet,
            result,
            mainSlotCount,
            bracketSlots.Count(slot => slot.IsPlayIn),
            lastColumn,
            isQualifierBracket ? result.Groups.Count : null,
            isGroupedChampionBracket);
        WriteBracketHeaders(sheet, roundColumns, mainSlotCount, customHeaders);
        WriteGroupBands(sheet, result, bracketSlots, lastColumn, isQualifierBracket || isGroupedChampionBracket, roundColumns);
        WriteFirstRoundSlots(sheet, bracketSlots, roundColumns[0]);
        if (isQualifierBracket || isGroupedChampionBracket)
        {
            var qualifierFinalColumnIndex = qualifierHeaders!.Count - 1;
            WriteQualifierRoundSlots(sheet, result, bracketSlots, roundColumns, qualifierFinalColumnIndex);
            WriteQualifierBracketConnectors(sheet, result, bracketSlots, roundColumns, qualifierFinalColumnIndex);
            if (isGroupedChampionBracket)
            {
                WriteGroupedChampionSlotsAndConnectors(sheet, result, bracketSlots, roundColumns, qualifierFinalColumnIndex);
            }
        }
        else
        {
            WriteFutureRoundSlots(sheet, mainSlotCount, roundColumns);
            WriteFutureRoundConnectors(sheet, mainSlotCount, roundColumns);
        }
        WriteBracketNote(sheet, noteRow, lastColumn);

        sheet.SheetView.FreezeRows(4);
        sheet.SheetView.FreezeColumns(4);
        sheet.ShowGridLines = false;
        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sheet.PageSetup.FitToPages(1, 0);
        sheet.PageSetup.SetRowsToRepeatAtTop(1, 4);
        sheet.PageSetup.PrintAreas.Add($"A1:{sheet.Cell(noteRow, lastColumn).Address.ToStringRelative()}");
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
                var first = roundOneParticipants[i];
                var second = roundOneParticipants[i + 1];
                slots.Add(new BracketSlot(
                    group.Number,
                    $"第{group.Number}组首轮赛{matchNumber}胜者",
                    false,
                    MinSeedRank(first, second),
                    true,
                    first.DisplayName,
                    first.IsSeed,
                    second.DisplayName,
                    second.IsSeed));
            }

            foreach (var participant in byeParticipants)
            {
                slots.Add(new BracketSlot(
                    group.Number,
                    participant.DisplayName,
                    participant.IsSeed,
                    participant.SeedRank,
                    false,
                    "",
                    false,
                    "",
                    false));
            }
        }

        return slots
            .GroupBy(slot => slot.GroupNumber)
            .SelectMany(group => ArrangeBracketSlotsBySeedProtection(group.ToList()))
            .ToList();
    }

    private static int? MinSeedRank(DrawParticipant first, DrawParticipant second)
    {
        return new[] { first.SeedRank, second.SeedRank }
            .Where(rank => rank.HasValue)
            .Min();
    }

    private static IReadOnlyList<BracketSlot> ArrangeBracketSlotsBySeedProtection(IReadOnlyList<BracketSlot> slots)
    {
        if (slots.Count == 0)
        {
            return slots;
        }

        var arranged = new BracketSlot?[slots.Count];
        var protectedPositions = OfficialDrawRules.GetSeedPositionOrder(slots.Count);
        var seededSlots = slots
            .Where(slot => slot.ProtectedSeedRank.HasValue)
            .OrderBy(slot => slot.ProtectedSeedRank!.Value)
            .ThenBy(slot => slot.MainDrawName, StringComparer.Ordinal)
            .ToList();
        var regularSlots = new Queue<BracketSlot>(slots.Where(slot => !slot.ProtectedSeedRank.HasValue));

        for (var i = 0; i < seededSlots.Count; i++)
        {
            arranged[protectedPositions[i % protectedPositions.Count]] = seededSlots[i];
        }

        for (var i = 0; i < arranged.Length; i++)
        {
            arranged[i] ??= regularSlots.Dequeue();
        }

        return arranged.Cast<BracketSlot>().ToList();
    }

    private static List<int> BuildRoundColumns(int mainSlotCount)
    {
        var roundCount = Math.Max(1, (int)Math.Ceiling(Math.Log2(Math.Max(mainSlotCount, 1))) + 1);
        return BuildRoundColumnsByCount(roundCount);
    }

    private static List<int> BuildRoundColumnsByCount(int roundCount)
    {
        var columns = new List<int>();

        for (var i = 0; i < roundCount; i++)
        {
            columns.Add(MainDrawFirstColumn + i * RoundColumnGap);
        }

        return columns;
    }

    private static List<string> BuildQualifierRoundHeaders(
        DrawResult result,
        IReadOnlyList<BracketSlot> bracketSlots)
    {
        var groupSlotCounts = result.Groups
            .Select(group => bracketSlots.Count(slot => slot.GroupNumber == group.Number))
            .Where(count => count > 0)
            .Select(count => Math.Max(1, count))
            .ToList();
        var headers = new List<string>();

        while (groupSlotCounts.Any(count => count > 1))
        {
            var from = groupSlotCounts.Sum();
            groupSlotCounts = groupSlotCounts
                .Select(count => Math.Max(1, count / 2))
                .ToList();
            var to = groupSlotCounts.Sum();
            headers.Add($"{from}进{to}");
        }

        headers.Add("出线");
        return headers;
    }

    private static List<string> BuildChampionRoundHeaders(int qualifierCount)
    {
        var headers = new List<string>();
        var from = Math.Max(1, qualifierCount);

        while (from > 1)
        {
            var to = Math.Max(1, from / 2);
            headers.Add(to == 1 ? "冠军" : $"{from}进{to}");
            from = to;
        }

        return headers;
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
        int lastColumn,
        int? qualifierCount = null,
        bool isGroupedChampionBracket = false)
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
            $"{result.Audit.ParticipantCount}{participantLabel}对阵表",
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
            isGroupedChampionBracket
                ? $"共{result.Audit.ParticipantCount}{participantLabel}，{playInCount}场首轮赛；分为{result.Groups.Count}个小组，每组出线后进入{result.Groups.Count}强淘汰赛，最终决出冠军。"
                : qualifierCount.HasValue
                ? $"共{result.Audit.ParticipantCount}{participantLabel}：{playInCount}场首轮赛；首轮赛后{mainSlotCount}{participantLabel}进入正赛，最终决出{qualifierCount.Value}个小组出线名额。"
                : $"共{result.Audit.ParticipantCount}{participantLabel}：{playInCount}场首轮赛；首轮赛后{mainSlotCount}{participantLabel}进入正赛。首轮赛胜者直接进入同一行的正赛位置。",
            NoteFill,
            XLColor.FromHtml("#1F2937"),
            fontSize: 11);
    }

    private static void WriteBracketHeaders(
        IXLWorksheet sheet,
        IReadOnlyList<int> roundColumns,
        int mainSlotCount,
        IReadOnlyList<string>? customHeaders = null)
    {
        WriteMergedCell(
            sheet,
            4,
            PlayInFirstColumn,
            4,
            PlayInWinnerColumn + MergedCellWidth - 1,
            "首轮赛",
            HeaderFill,
            XLColor.White,
            isBold: true);

        for (var i = 0; i < roundColumns.Count; i++)
        {
            var header = customHeaders is not null
                ? customHeaders[i]
                : i == roundColumns.Count - 1
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
        IReadOnlyList<BracketSlot> bracketSlots,
        int lastColumn,
        bool isQualifierBracket,
        IReadOnlyList<int> roundColumns)
    {
        var totalMainSlotCount = bracketSlots.Count;

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
            var title = $"第{group.Number}组：{group.Participants.Count}人，{playInCount}场首轮赛，首轮赛后{mainSlotCount}人进入正赛";

            if (!isQualifierBracket)
            {
                var blockedSpans = GetFutureRoundColumnSpansForRow(row, totalMainSlotCount, roundColumns);
                if (blockedSpans.Count > 0)
                {
                    WriteSegmentedGroupBand(sheet, row, lastColumn, title, blockedSpans);
                    continue;
                }
            }

            WriteMergedCell(
                sheet,
                row,
                1,
                row,
                lastColumn,
                title,
                GroupFill,
                XLColor.FromHtml("#1F2937"),
                isBold: true);
        }
    }

    private static List<ColumnSpan> GetFutureRoundColumnSpansForRow(
        int row,
        int mainSlotCount,
        IReadOnlyList<int> roundColumns)
    {
        var spans = new List<ColumnSpan>();

        for (var roundIndex = 1; roundIndex < roundColumns.Count; roundIndex++)
        {
            var matchCount = Math.Max(1, (int)Math.Ceiling(mainSlotCount / Math.Pow(2, roundIndex)));
            var step = SlotRowGap * (int)Math.Pow(2, roundIndex);
            var offset = Math.Max(1, step / 2 - SlotRowGap / 2);

            for (var matchIndex = 0; matchIndex < matchCount; matchIndex++)
            {
                var futureRow = BracketStartRow + matchIndex * step + offset;
                if (BuildRoundHeader(mainSlotCount, roundIndex) == "半决赛")
                {
                    futureRow++;
                }

                if (futureRow == row)
                {
                    spans.Add(new ColumnSpan(roundColumns[roundIndex], roundColumns[roundIndex] + MergedCellWidth - 1));
                }
            }
        }

        return spans
            .OrderBy(span => span.FirstColumn)
            .ToList();
    }

    private static void WriteSegmentedGroupBand(
        IXLWorksheet sheet,
        int row,
        int lastColumn,
        string title,
        IReadOnlyList<ColumnSpan> blockedSpans)
    {
        var nextColumn = 1;
        var wroteTitle = false;

        foreach (var blockedSpan in blockedSpans)
        {
            if (nextColumn < blockedSpan.FirstColumn)
            {
                WriteGroupBandSegment(
                    sheet,
                    row,
                    nextColumn,
                    blockedSpan.FirstColumn - 1,
                    wroteTitle ? "" : title,
                    !wroteTitle);
                wroteTitle = true;
            }

            nextColumn = Math.Max(nextColumn, blockedSpan.LastColumn + 1);
        }

        if (nextColumn <= lastColumn)
        {
            WriteGroupBandSegment(
                sheet,
                row,
                nextColumn,
                lastColumn,
                wroteTitle ? "" : title,
                !wroteTitle);
        }
    }

    private static void WriteGroupBandSegment(
        IXLWorksheet sheet,
        int row,
        int firstColumn,
        int lastColumn,
        string value,
        bool isTitleSegment)
    {
        WriteMergedCell(
            sheet,
            row,
            firstColumn,
            row,
            lastColumn,
            value,
            GroupFill,
            XLColor.FromHtml("#1F2937"),
            isBold: isTitleSegment);
    }

    private static void WriteQualifierRoundSlots(
        IXLWorksheet sheet,
        DrawResult result,
        IReadOnlyList<BracketSlot> bracketSlots,
        IReadOnlyList<int> roundColumns,
        int qualifierFinalColumnIndex)
    {
        var finalColumn = roundColumns[qualifierFinalColumnIndex];

        foreach (var group in result.Groups)
        {
            var firstIndex = bracketSlots.ToList().FindIndex(slot => slot.GroupNumber == group.Number);
            if (firstIndex < 0)
            {
                continue;
            }

            var initialCount = bracketSlots.Count(slot => slot.GroupNumber == group.Number);
            var roundCount = (int)Math.Log2(initialCount);
            var groupStartRow = BracketStartRow + firstIndex * SlotRowGap;

            for (var roundIndex = 1; roundIndex <= roundCount; roundIndex++)
            {
                var column = roundColumns[Math.Min(roundIndex, qualifierFinalColumnIndex)];
                var survivorCount = Math.Max(1, initialCount / (int)Math.Pow(2, roundIndex));
                var step = SlotRowGap * (int)Math.Pow(2, roundIndex);
                var offset = Math.Max(1, step / 2 - SlotRowGap / 2);
                var isGroupFinal = survivorCount == 1;

                for (var matchIndex = 0; matchIndex < survivorCount; matchIndex++)
                {
                    if (isGroupFinal && column != finalColumn)
                    {
                        continue;
                    }

                    var row = groupStartRow + matchIndex * step + offset;
                    var value = isGroupFinal
                        ? $"第{group.Number}组出线"
                        : $"胜者{matchIndex + 1}";
                    var fill = isGroupFinal ? QualifiedFill : FutureFill;
                    var fontColor = isGroupFinal ? XLColor.White : null;

                    WriteMergedCell(
                        sheet,
                        row,
                        column,
                        row,
                        column + 1,
                        value,
                        fill,
                        fontColor,
                        isBold: isGroupFinal);
                }
            }

            if (roundCount < qualifierFinalColumnIndex)
            {
                var finalRow = GetGroupCenterRow(groupStartRow, initialCount);
                WriteMergedCell(
                    sheet,
                    finalRow,
                    finalColumn,
                    finalRow,
                    finalColumn + 1,
                    $"第{group.Number}组出线",
                    QualifiedFill,
                    XLColor.White,
                    isBold: true);
            }
        }
    }

    private static void WriteQualifierBracketConnectors(
        IXLWorksheet sheet,
        DrawResult result,
        IReadOnlyList<BracketSlot> bracketSlots,
        IReadOnlyList<int> roundColumns,
        int qualifierFinalColumnIndex)
    {
        var finalColumn = roundColumns[qualifierFinalColumnIndex];

        foreach (var group in result.Groups)
        {
            var firstIndex = bracketSlots.ToList().FindIndex(slot => slot.GroupNumber == group.Number);
            if (firstIndex < 0)
            {
                continue;
            }

            var initialCount = bracketSlots.Count(slot => slot.GroupNumber == group.Number);
            var roundCount = (int)Math.Log2(initialCount);
            var groupStartRow = BracketStartRow + firstIndex * SlotRowGap;

            for (var roundIndex = 1; roundIndex <= roundCount; roundIndex++)
            {
                var targetColumn = roundColumns[Math.Min(roundIndex, qualifierFinalColumnIndex)];
                var survivorCount = Math.Max(1, initialCount / (int)Math.Pow(2, roundIndex));
                var isGroupFinal = survivorCount == 1;

                if (isGroupFinal && targetColumn != finalColumn)
                {
                    continue;
                }

                var sourceColumn = roundColumns[roundIndex - 1];

                for (var matchIndex = 0; matchIndex < survivorCount; matchIndex++)
                {
                    var upperRow = GetGroupRoundRow(groupStartRow, roundIndex - 1, matchIndex * 2);
                    var lowerRow = GetGroupRoundRow(groupStartRow, roundIndex - 1, matchIndex * 2 + 1);
                    var targetRow = GetGroupRoundRow(groupStartRow, roundIndex, matchIndex);
                    DrawMatchConnector(sheet, sourceColumn, targetColumn, upperRow, lowerRow, targetRow);
                }
            }

            if (roundCount < qualifierFinalColumnIndex)
            {
                var targetRow = GetGroupCenterRow(groupStartRow, initialCount);
                if (initialCount == 1)
                {
                    DrawSingleConnector(sheet, roundColumns[0], finalColumn, groupStartRow, targetRow);
                }
                else
                {
                    var sourceColumn = roundColumns[Math.Max(0, roundCount - 1)];
                    var upperRow = GetGroupRoundRow(groupStartRow, roundCount - 1, 0);
                    var lowerRow = GetGroupRoundRow(groupStartRow, roundCount - 1, 1);
                    DrawMatchConnector(sheet, sourceColumn, finalColumn, upperRow, lowerRow, targetRow);
                }
            }
        }
    }

    private static void WriteGroupedChampionSlotsAndConnectors(
        IXLWorksheet sheet,
        DrawResult result,
        IReadOnlyList<BracketSlot> bracketSlots,
        IReadOnlyList<int> roundColumns,
        int qualifierFinalColumnIndex)
    {
        var sourceColumn = roundColumns[qualifierFinalColumnIndex];
        var sourceRows = result.Groups
            .Select(group => GetGroupQualifierRow(result, bracketSlots, group.Number))
            .ToList();

        for (var roundColumnIndex = qualifierFinalColumnIndex + 1; roundColumnIndex < roundColumns.Count; roundColumnIndex++)
        {
            var targetColumn = roundColumns[roundColumnIndex];
            var targetRows = new List<int>();
            var isChampionColumn = roundColumnIndex == roundColumns.Count - 1;

            for (var matchIndex = 0; matchIndex + 1 < sourceRows.Count; matchIndex += 2)
            {
                var upperRow = sourceRows[matchIndex];
                var lowerRow = sourceRows[matchIndex + 1];
                var targetRow = (upperRow + lowerRow) / 2;
                targetRows.Add(targetRow);

                WriteMergedCell(
                    sheet,
                    targetRow,
                    targetColumn,
                    targetRow,
                    targetColumn + 1,
                    isChampionColumn ? "冠军" : $"胜者{matchIndex / 2 + 1}",
                    isChampionColumn ? QualifiedFill : FutureFill,
                    isChampionColumn ? XLColor.White : null,
                    isBold: isChampionColumn);
                DrawMatchConnector(sheet, sourceColumn, targetColumn, upperRow, lowerRow, targetRow);
            }

            sourceColumn = targetColumn;
            sourceRows = targetRows;
        }
    }

    private static int GetGroupQualifierRow(
        DrawResult result,
        IReadOnlyList<BracketSlot> bracketSlots,
        int groupNumber)
    {
        var firstIndex = bracketSlots.ToList().FindIndex(slot => slot.GroupNumber == groupNumber);
        if (firstIndex < 0)
        {
            throw new InvalidOperationException($"找不到第 {groupNumber} 组的签位。");
        }

        var initialCount = bracketSlots.Count(slot => slot.GroupNumber == groupNumber);
        var groupStartRow = BracketStartRow + firstIndex * SlotRowGap;
        var roundCount = (int)Math.Log2(initialCount);
        return roundCount == 0
            ? groupStartRow
            : GetGroupRoundRow(groupStartRow, roundCount, 0);
    }

    private static int GetGroupCenterRow(int groupStartRow, int groupSlotCount)
    {
        var step = SlotRowGap * groupSlotCount;
        var offset = Math.Max(1, step / 2 - SlotRowGap / 2);
        return groupStartRow + offset;
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
                WriteSeedAwareMergedCell(sheet, row, PlayInFirstColumn, row, PlayInFirstColumn + 1, slot.PlayInFirstName, PlayInFill, slot.PlayInFirstIsSeed);
                WriteMergedCell(sheet, row + 1, PlayInFirstColumn, row + 1, PlayInFirstColumn + 1, "vs", PlayInFill, fontSize: 9);
                WriteSeedAwareMergedCell(sheet, row + 2, PlayInFirstColumn, row + 2, PlayInFirstColumn + 1, slot.PlayInSecondName, PlayInFill, slot.PlayInSecondIsSeed);
                WriteMergedCell(sheet, row, PlayInWinnerColumn, row + 2, PlayInWinnerColumn + 1, "胜者入正赛", PlayInWinnerFill, fontSize: 9);
                WriteMergedCell(sheet, row, firstRoundColumn, row, firstRoundColumn + 1, slot.MainDrawName, PlayInWinnerFill);
            }
            else
            {
                WriteSeedAwareMergedCell(sheet, row, firstRoundColumn, row, firstRoundColumn + 1, slot.MainDrawName, ByeFill, slot.MainDrawIsSeed);
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

    private static void WriteFutureRoundConnectors(
        IXLWorksheet sheet,
        int mainSlotCount,
        IReadOnlyList<int> roundColumns)
    {
        for (var roundIndex = 1; roundIndex < roundColumns.Count; roundIndex++)
        {
            var sourceColumn = roundColumns[roundIndex - 1];
            var targetColumn = roundColumns[roundIndex];
            var matchCount = Math.Max(1, (int)Math.Ceiling(mainSlotCount / Math.Pow(2, roundIndex)));

            for (var matchIndex = 0; matchIndex < matchCount; matchIndex++)
            {
                var upperSourceIndex = matchIndex * 2;
                var lowerSourceIndex = upperSourceIndex + 1;
                var previousMatchCount = Math.Max(1, (int)Math.Ceiling(mainSlotCount / Math.Pow(2, roundIndex - 1)));

                if (lowerSourceIndex >= previousMatchCount)
                {
                    continue;
                }

                var upperRow = roundIndex == 1
                    ? BracketStartRow + upperSourceIndex * SlotRowGap
                    : GetFutureRoundRow(mainSlotCount, roundIndex - 1, upperSourceIndex);
                var lowerRow = roundIndex == 1
                    ? BracketStartRow + lowerSourceIndex * SlotRowGap
                    : GetFutureRoundRow(mainSlotCount, roundIndex - 1, lowerSourceIndex);
                var targetRow = GetFutureRoundRow(mainSlotCount, roundIndex, matchIndex);

                DrawMatchConnector(sheet, sourceColumn, targetColumn, upperRow, lowerRow, targetRow);
            }
        }
    }

    private static int GetFutureRoundRow(int mainSlotCount, int roundIndex, int matchIndex)
    {
        var step = SlotRowGap * (int)Math.Pow(2, roundIndex);
        var offset = Math.Max(1, step / 2 - SlotRowGap / 2);
        var row = BracketStartRow + matchIndex * step + offset;
        if (BuildRoundHeader(mainSlotCount, roundIndex) == "半决赛")
        {
            row++;
        }

        return row;
    }

    private static int GetGroupRoundRow(int groupStartRow, int roundIndex, int matchIndex)
    {
        if (roundIndex == 0)
        {
            return groupStartRow + matchIndex * SlotRowGap;
        }

        var step = SlotRowGap * (int)Math.Pow(2, roundIndex);
        var offset = Math.Max(1, step / 2 - SlotRowGap / 2);
        return groupStartRow + matchIndex * step + offset;
    }

    private static void DrawMatchConnector(
        IXLWorksheet sheet,
        int sourceColumn,
        int targetColumn,
        int upperRow,
        int lowerRow,
        int targetRow)
    {
        if (targetColumn <= sourceColumn + MergedCellWidth)
        {
            return;
        }

        var firstGapColumn = sourceColumn + MergedCellWidth;
        var trunkColumn = targetColumn - 1;
        DrawHorizontalConnector(sheet, upperRow, firstGapColumn, trunkColumn, ConnectorBorderSide.Bottom);
        DrawHorizontalConnector(sheet, lowerRow, firstGapColumn, trunkColumn, ConnectorBorderSide.Top);
        DrawVerticalConnector(sheet, trunkColumn, Math.Min(upperRow, lowerRow), Math.Max(upperRow, lowerRow));
    }

    private static void DrawSingleConnector(
        IXLWorksheet sheet,
        int sourceColumn,
        int targetColumn,
        int sourceRow,
        int targetRow)
    {
        if (targetColumn <= sourceColumn + MergedCellWidth)
        {
            return;
        }

        var firstGapColumn = sourceColumn + MergedCellWidth;
        var lastGapColumn = targetColumn - 1;
        var side = sourceRow <= targetRow ? ConnectorBorderSide.Bottom : ConnectorBorderSide.Top;
        DrawHorizontalConnector(sheet, sourceRow, firstGapColumn, lastGapColumn, side);

        if (sourceRow != targetRow)
        {
            DrawVerticalConnector(sheet, lastGapColumn, Math.Min(sourceRow, targetRow), Math.Max(sourceRow, targetRow));
        }
    }

    private static void DrawHorizontalConnector(
        IXLWorksheet sheet,
        int row,
        int firstColumn,
        int lastColumn,
        ConnectorBorderSide side)
    {
        for (var column = firstColumn; column <= lastColumn; column++)
        {
            var border = sheet.Cell(row, column).Style.Border;
            if (side == ConnectorBorderSide.Top)
            {
                border.TopBorder = XLBorderStyleValues.Thin;
                border.TopBorderColor = XLColor.FromHtml("#808080");
            }
            else
            {
                border.BottomBorder = XLBorderStyleValues.Thin;
                border.BottomBorderColor = XLColor.FromHtml("#808080");
            }
        }
    }

    private static void DrawVerticalConnector(IXLWorksheet sheet, int column, int firstRow, int lastRow)
    {
        var firstConnectorRow = firstRow + 1;
        var lastConnectorRow = lastRow - 1;
        for (var row = firstConnectorRow; row <= lastConnectorRow; row++)
        {
            var border = sheet.Cell(row, column).Style.Border;
            border.RightBorder = XLBorderStyleValues.Thin;
            border.RightBorderColor = XLColor.FromHtml("#808080");
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
            "填写说明：左侧首轮赛完成后，将胜者姓名填入黄色正赛位置；绿色为直接进入正赛的选手；灰色格用于逐轮填写胜者；红色加粗姓名为种子选手。",
            NoteFill,
            XLColor.FromHtml("#1F2937"));
    }

    private static void WriteSeedAwareMergedCell(
        IXLWorksheet sheet,
        int firstRow,
        int firstColumn,
        int lastRow,
        int lastColumn,
        string value,
        XLColor fill,
        bool isSeed)
    {
        WriteMergedCell(
            sheet,
            firstRow,
            firstColumn,
            lastRow,
            lastColumn,
            value,
            fill,
            isSeed ? SeedFontColor : null,
            isBold: isSeed);
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

    private static bool IsPowerOfTwo(int value)
    {
        return value > 0 && (value & (value - 1)) == 0;
    }

    private static void WriteRoundRobinSheet(XLWorkbook workbook, DrawResult result)
    {
        var sheet = workbook.Worksheets.Add("对阵表");
        var maxGroupSize = Math.Max(1, result.Groups.Select(group => group.Participants.Count).DefaultIfEmpty(0).Max());
        var maxDisplayNameLength = result.Groups
            .SelectMany(group => group.Participants)
            .Select(participant => participant.DisplayName.Length)
            .DefaultIfEmpty(0)
            .Max();
        var layout = BuildRoundRobinLayout(maxGroupSize, maxDisplayNameLength);
        var lastColumn = Math.Max(7, maxGroupSize + 4);
        var row = 5;

        ConfigureRoundRobinSheet(sheet, lastColumn, layout);
        WriteRoundRobinTitle(sheet, result, lastColumn);

        foreach (var group in result.Groups)
        {
            row = WriteRoundRobinGroup(sheet, group, row, layout);
            row++;
        }

        sheet.SheetView.FreezeRows(4);
        sheet.ShowGridLines = false;
        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sheet.PageSetup.FitToPages(1, 0);
        sheet.PageSetup.SetRowsToRepeatAtTop(1, 4);
        sheet.PageSetup.PrintAreas.Add($"A1:{sheet.Cell(Math.Max(row - 1, 4), lastColumn).Address.ToStringRelative()}");
    }

    private static RoundRobinLayout BuildRoundRobinLayout(int maxGroupSize, int maxDisplayNameLength)
    {
        var participantColumnWidth = maxGroupSize switch
        {
            <= 5 => 20,
            <= 7 => 18,
            <= 8 => 17,
            <= 10 => 14.5,
            _ => 12
        };
        var summaryColumnWidth = maxGroupSize >= 8 ? 7.5 : 8.5;
        var fontSize = maxDisplayNameLength switch
        {
            >= 11 => 8.2,
            >= 9 => 8.8,
            _ => 9.5
        };
        var estimatedCharsPerLine = Math.Max(4, participantColumnWidth * 0.52);
        var estimatedLines = Math.Clamp(
            (int)Math.Ceiling(Math.Max(1, maxDisplayNameLength) / estimatedCharsPerLine),
            1,
            3);
        var headerHeight = Math.Max(36, 22 + estimatedLines * (fontSize + 5));
        var rowHeight = Math.Max(30, 16 + estimatedLines * (fontSize + 4));

        return new RoundRobinLayout(
            participantColumnWidth,
            summaryColumnWidth,
            fontSize,
            headerHeight,
            rowHeight);
    }

    private static void ConfigureRoundRobinSheet(IXLWorksheet sheet, int lastColumn, RoundRobinLayout layout)
    {
        for (var column = 1; column <= lastColumn; column++)
        {
            sheet.Column(column).Width = column >= lastColumn - 2
                ? layout.SummaryColumnWidth
                : layout.ParticipantColumnWidth;
        }

        sheet.Row(1).Height = 26;
        sheet.Row(2).Height = 26;
        sheet.Row(3).Height = 24;
        sheet.Row(4).Height = 30;
    }

    private static void WriteRoundRobinTitle(IXLWorksheet sheet, DrawResult result, int lastColumn)
    {
        var participantLabel = RoundRobinParticipantLabel(result.Settings.EventKind);
        var noteText = result.Settings.EventKind == EventKind.Team
            ? "交叉格显示场次编号，可填写比分/结果；对角线为轮空；赛程顺序按轮转法生成。"
            : "交叉格显示场次编号，可填写比分/结果；对角线为轮空；同单位对阵已优先排入赛程。";
        WriteRoundRobinMergedCell(
            sheet,
            1,
            1,
            2,
            lastColumn,
            $"{EventKindText(result.Settings.EventKind)}循环赛对阵表",
            XLColor.White,
            isBold: true,
            fontSize: 18);
        WriteRoundRobinMergedCell(
            sheet,
            3,
            1,
            3,
            lastColumn,
            $"共{result.Audit.ParticipantCount}个{participantLabel}，分为{result.Groups.Count}个小组。",
            XLColor.White,
            isBold: true,
            fontSize: 11);
        WriteRoundRobinMergedCell(
            sheet,
            4,
            1,
            4,
            lastColumn,
            noteText,
            NoteFill,
            fontSize: 9);
    }

    private static int WriteRoundRobinGroup(
        IXLWorksheet sheet,
        DrawGroup group,
        int startRow,
        RoundRobinLayout layout)
    {
        var participants = group.Participants;
        var groupSize = participants.Count;
        var groupColumnCount = Math.Max(4, groupSize + 4);
        var lastRow = startRow + Math.Max(groupSize, 1);
        var schedule = BuildRoundRobinSchedule(participants);
        var scheduleByPair = schedule.ToDictionary(
            match => BuildPairKey(match.FirstIndex, match.SecondIndex),
            match => match.Order);

        sheet.Cell(startRow, 1).Value = BuildRoundRobinGroupLabel(group.Number);
        for (var i = 0; i < groupSize; i++)
        {
            var participant = participants[i];
            var headerCell = sheet.Cell(startRow, i + 2);
            headerCell.Value = participant.DisplayName;
            HighlightSeedCell(headerCell, participant);
        }

        sheet.Cell(startRow, groupSize + 2).Value = "胜场";
        sheet.Cell(startRow, groupSize + 3).Value = "净胜";
        sheet.Cell(startRow, groupSize + 4).Value = "名次";

        if (groupSize == 0)
        {
            sheet.Cell(startRow + 1, 1).Value = "暂无参赛对象";
            sheet.Range(startRow + 1, 1, startRow + 1, groupColumnCount).Merge();
        }
        else
        {
            for (var rowIndex = 0; rowIndex < groupSize; rowIndex++)
            {
                var participant = participants[rowIndex];
                var row = startRow + rowIndex + 1;
                var nameCell = sheet.Cell(row, 1);
                nameCell.Value = participant.DisplayName;
                HighlightSeedCell(nameCell, participant);

                for (var columnIndex = 0; columnIndex < groupSize; columnIndex++)
                {
                    var cell = sheet.Cell(row, columnIndex + 2);
                    if (rowIndex == columnIndex)
                    {
                        cell.Value = "—";
                        cell.Style.Fill.BackgroundColor = FutureFill;
                    }
                    else
                    {
                        cell.Value = rowIndex < columnIndex
                            && scheduleByPair.TryGetValue(BuildPairKey(rowIndex, columnIndex), out var matchOrder)
                            ? $"第{matchOrder}场"
                            : "";
                    }
                }
            }
        }

        ApplyRoundRobinGroupStyle(sheet, startRow, lastRow, groupColumnCount, groupSize, layout);
        var scheduleLastRow = WriteRoundRobinSchedule(sheet, participants, schedule, lastRow + 2, groupColumnCount);
        return Math.Max(lastRow, scheduleLastRow) + 1;
    }

    private static IReadOnlyList<RoundRobinMatch> BuildRoundRobinSchedule(IReadOnlyList<DrawParticipant> participants)
    {
        if (participants.Count <= 1)
        {
            return [];
        }

        var slots = participants
            .Select((_, index) => (int?)index)
            .ToList();
        if (slots.Count % 2 == 1)
        {
            slots.Add(null);
        }

        var matches = new List<RoundRobinMatch>();
        var roundCount = slots.Count - 1;
        var matchesPerRound = slots.Count / 2;

        for (var round = 1; round <= roundCount; round++)
        {
            for (var matchIndex = 0; matchIndex < matchesPerRound; matchIndex++)
            {
                var first = slots[matchIndex];
                var second = slots[slots.Count - 1 - matchIndex];
                if (first.HasValue && second.HasValue)
                {
                    var sameUnit = OfficialDrawRules.HaveSameUnit(participants[first.Value], participants[second.Value]);
                    matches.Add(new RoundRobinMatch(0, round, matchIndex + 1, first.Value, second.Value, sameUnit));
                }
            }

            RotateRoundRobinSlots(slots);
        }

        return matches
            .OrderBy(match => match.SameUnit ? 0 : 1)
            .ThenBy(match => match.Round)
            .ThenBy(match => match.RoundMatchNumber)
            .Select((match, index) => match with { Order = index + 1 })
            .ToList();
    }

    private static void RotateRoundRobinSlots(IList<int?> slots)
    {
        if (slots.Count <= 2)
        {
            return;
        }

        var last = slots[^1];
        for (var index = slots.Count - 1; index > 1; index--)
        {
            slots[index] = slots[index - 1];
        }

        slots[1] = last;
    }

    private static int WriteRoundRobinSchedule(
        IXLWorksheet sheet,
        IReadOnlyList<DrawParticipant> participants,
        IReadOnlyList<RoundRobinMatch> schedule,
        int startRow,
        int lastColumn)
    {
        if (schedule.Count == 0)
        {
            return startRow - 1;
        }

        var opponentFirstColumn = 3;
        var noteColumn = Math.Max(4, lastColumn);
        var opponentLastColumn = Math.Max(opponentFirstColumn, noteColumn - 1);
        var headerRow = startRow + 1;
        var lastRow = headerRow + schedule.Count;

        var titleRange = sheet.Range(startRow, 1, startRow, noteColumn);
        titleRange.Merge();
        titleRange.Value = "赛程顺序";
        titleRange.Style.Fill.BackgroundColor = HeaderFill;
        titleRange.Style.Font.FontColor = XLColor.White;
        titleRange.Style.Font.Bold = true;

        sheet.Cell(headerRow, 1).Value = "场次";
        sheet.Cell(headerRow, 2).Value = "轮次";
        WriteRoundRobinScheduleMergedCell(sheet, headerRow, opponentFirstColumn, opponentLastColumn, "对阵");
        sheet.Cell(headerRow, noteColumn).Value = "备注";

        for (var i = 0; i < schedule.Count; i++)
        {
            var match = schedule[i];
            var row = headerRow + i + 1;
            sheet.Cell(row, 1).Value = match.Order;
            sheet.Cell(row, 2).Value = $"第{match.Round}轮";
            WriteRoundRobinScheduleMergedCell(
                sheet,
                row,
                opponentFirstColumn,
                opponentLastColumn,
                $"{participants[match.FirstIndex].DisplayName}  vs  {participants[match.SecondIndex].DisplayName}");
            sheet.Cell(row, noteColumn).Value = match.SameUnit ? "同单位优先" : "";
        }

        ApplyRoundRobinScheduleStyle(sheet, startRow, lastRow, noteColumn);
        return lastRow;
    }

    private static void WriteRoundRobinScheduleMergedCell(
        IXLWorksheet sheet,
        int row,
        int firstColumn,
        int lastColumn,
        string value)
    {
        var range = sheet.Range(row, firstColumn, row, lastColumn);
        if (firstColumn < lastColumn)
        {
            range.Merge();
        }

        range.Value = value;
    }

    private static void ApplyRoundRobinScheduleStyle(
        IXLWorksheet sheet,
        int firstRow,
        int lastRow,
        int lastColumn)
    {
        var range = sheet.Range(firstRow, 1, lastRow, lastColumn);
        range.Style.Font.FontName = "Microsoft YaHei";
        range.Style.Font.FontSize = 9;
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        range.Style.Alignment.WrapText = true;
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.OutsideBorderColor = XLColor.FromHtml("#808080");
        range.Style.Border.InsideBorderColor = XLColor.FromHtml("#808080");

        sheet.Range(firstRow + 1, 1, firstRow + 1, lastColumn).Style.Font.Bold = true;
        sheet.Range(firstRow + 1, 1, firstRow + 1, lastColumn).Style.Fill.BackgroundColor = GroupFill;

        for (var row = firstRow; row <= lastRow; row++)
        {
            sheet.Row(row).Height = row == firstRow ? 22 : 20;
        }
    }

    private static string BuildPairKey(int firstIndex, int secondIndex)
    {
        var first = Math.Min(firstIndex, secondIndex);
        var second = Math.Max(firstIndex, secondIndex);
        return $"{first}:{second}";
    }

    private static void ApplyRoundRobinGroupStyle(
        IXLWorksheet sheet,
        int firstRow,
        int lastRow,
        int lastColumn,
        int groupSize,
        RoundRobinLayout layout)
    {
        var range = sheet.Range(firstRow, 1, lastRow, lastColumn);
        range.Style.Font.FontName = "Microsoft YaHei";
        range.Style.Font.FontSize = layout.FontSize;
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        range.Style.Alignment.WrapText = true;
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.OutsideBorderColor = XLColor.FromHtml("#808080");
        range.Style.Border.InsideBorderColor = XLColor.FromHtml("#808080");

        var headerRange = sheet.Range(firstRow, 1, firstRow, lastColumn);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = GroupFill;

        var summaryStartColumn = Math.Max(2, groupSize + 2);
        sheet.Range(firstRow, summaryStartColumn, lastRow, lastColumn).Style.Fill.BackgroundColor = NoteFill;
        sheet.Range(firstRow, summaryStartColumn, firstRow, lastColumn).Style.Font.Bold = true;

        for (var row = firstRow; row <= lastRow; row++)
        {
            sheet.Row(row).Height = row == firstRow ? layout.HeaderRowHeight : layout.BodyRowHeight;
        }
    }

    private static void WriteRoundRobinMergedCell(
        IXLWorksheet sheet,
        int firstRow,
        int firstColumn,
        int lastRow,
        int lastColumn,
        string value,
        XLColor fill,
        bool isBold = false,
        double fontSize = 10)
    {
        var range = sheet.Range(firstRow, firstColumn, lastRow, lastColumn);
        range.Merge();
        range.Value = value;
        range.Style.Fill.BackgroundColor = fill;
        range.Style.Font.FontName = "Microsoft YaHei";
        range.Style.Font.FontSize = fontSize;
        range.Style.Font.Bold = isBold;
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        range.Style.Alignment.WrapText = true;
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        range.Style.Border.OutsideBorderColor = XLColor.FromHtml("#808080");
    }

    private static void HighlightSeedCell(IXLCell cell, DrawParticipant participant)
    {
        if (!participant.IsSeed)
        {
            return;
        }

        cell.Style.Font.FontColor = SeedFontColor;
        cell.Style.Font.Bold = true;
    }

    private static string RoundRobinParticipantLabel(EventKind eventKind)
    {
        return eventKind switch
        {
            EventKind.Team => "参赛单位",
            EventKind.Doubles => "参赛组合",
            _ => "参赛选手"
        };
    }

    private static string BuildRoundRobinGroupLabel(int groupNumber)
    {
        return $"{ToExcelColumnName(groupNumber)}组";
    }

    private static string ToExcelColumnName(int number)
    {
        var value = Math.Max(1, number);
        var chars = new Stack<char>();
        while (value > 0)
        {
            value--;
            chars.Push((char)('A' + value % 26));
            value /= 26;
        }

        return new string(chars.ToArray());
    }

    private static void WriteAuditSheet(XLWorkbook workbook, string sheetName, DrawResult result)
    {
        var sheet = workbook.Worksheets.Add(sheetName);
        var rows = new (string Key, string Value)[]
        {
            ("比赛模式", result.Settings.CompetitionMode.ToString()),
            ("项目类型", result.Settings.EventKind.ToString()),
            ("随机数种子", result.Audit.RandomSeed),
            ("输入哈希", result.Audit.InputHash),
            ("生成时间", result.Audit.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss zzz")),
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
        var headers = new[] { "姓名", "学院/学部", "搭档姓名", "搭档学院/学部", "是否种子", "种子序号", "备注" };

        WriteHeader(sheet, headers);

        for (var i = 0; i < participants.Count; i++)
        {
            var row = i + 2;
            var participant = participants[i];
            sheet.Cell(row, 1).Value = participant.PrimaryName ?? participant.DisplayName;
            sheet.Cell(row, 2).Value = participant.TeamName ?? "";
            sheet.Cell(row, 3).Value = participant.PartnerName ?? "";
            sheet.Cell(row, 4).Value = participant.PartnerTeamName ?? "";
            sheet.Cell(row, 5).Value = participant.IsSeed ? "是" : "";
            sheet.Cell(row, 6).Value = participant.SeedRank.HasValue ? participant.SeedRank.Value : "";
            sheet.Cell(row, 7).Value = participant.Note ?? "";
        }

        ApplyTableStyle(sheet, headers.Length, participants.Count + 1);
        HighlightSeedRows(sheet, headers.Length, participants.Count + 1, seedColumn: 5);
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

    private static void HighlightSeedRows(IXLWorksheet sheet, int columnCount, int lastRow, int seedColumn = 4)
    {
        for (var row = 2; row <= lastRow; row++)
        {
            if (sheet.Cell(row, seedColumn).GetString() != "是")
            {
                continue;
            }

            var range = sheet.Range(row, 1, row, columnCount);
            range.Style.Font.FontColor = SeedFontColor;
            range.Style.Font.Bold = true;
        }
    }

    private sealed record BracketSlot(
        int GroupNumber,
        string MainDrawName,
        bool MainDrawIsSeed,
        int? ProtectedSeedRank,
        bool IsPlayIn,
        string PlayInFirstName,
        bool PlayInFirstIsSeed,
        string PlayInSecondName,
        bool PlayInSecondIsSeed);

    private sealed record ColumnSpan(int FirstColumn, int LastColumn);

    private sealed record RoundRobinMatch(
        int Order,
        int Round,
        int RoundMatchNumber,
        int FirstIndex,
        int SecondIndex,
        bool SameUnit);

    private sealed record RoundRobinLayout(
        double ParticipantColumnWidth,
        double SummaryColumnWidth,
        double FontSize,
        double HeaderRowHeight,
        double BodyRowHeight);

    private enum ConnectorBorderSide
    {
        Top,
        Bottom
    }
}
