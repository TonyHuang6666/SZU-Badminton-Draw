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
        var isQualifierBracket = !IsPowerOfTwo(result.Groups.Count);
        var qualifierHeaders = isQualifierBracket
            ? BuildQualifierRoundHeaders(result, bracketSlots)
            : null;
        var roundColumns = qualifierHeaders is not null
            ? BuildRoundColumnsByCount(qualifierHeaders.Count)
            : BuildRoundColumns(mainSlotCount);
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
            isQualifierBracket ? result.Groups.Count : null);
        WriteBracketHeaders(sheet, roundColumns, mainSlotCount, qualifierHeaders);
        WriteGroupBands(sheet, result, bracketSlots, lastColumn, isQualifierBracket, roundColumns);
        WriteFirstRoundSlots(sheet, bracketSlots, roundColumns[0]);
        if (isQualifierBracket)
        {
            WriteQualifierRoundSlots(sheet, result, bracketSlots, roundColumns);
            WriteQualifierBracketConnectors(sheet, result, bracketSlots, roundColumns);
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
                    $"第{group.Number}组附加赛{matchNumber}胜者",
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
        var protectedPositions = BuildProtectedSlotOrder(slots.Count);
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

    private static IReadOnlyList<int> BuildProtectedSlotOrder(int slotCount)
    {
        var order = new List<int>(slotCount);
        AddProtectedSlotPositions(order, 0, slotCount - 1);
        return order;
    }

    private static void AddProtectedSlotPositions(ICollection<int> order, int first, int last)
    {
        if (first > last)
        {
            return;
        }

        if (order.Count == 0)
        {
            AddSlotPosition(order, first);
            AddSlotPosition(order, last);
        }

        if (last - first <= 1)
        {
            return;
        }

        var middleLeft = (first + last) / 2;
        var middleRight = middleLeft + 1;
        AddSlotPosition(order, middleLeft);
        AddSlotPosition(order, middleRight);
        AddProtectedSlotPositions(order, first, middleLeft);
        AddProtectedSlotPositions(order, middleRight, last);
    }

    private static void AddSlotPosition(ICollection<int> order, int position)
    {
        if (!order.Contains(position))
        {
            order.Add(position);
        }
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
            .ToList();
        var headers = new List<string>();

        while (groupSlotCounts.Any(count => count > 1))
        {
            var from = groupSlotCounts.Sum();
            groupSlotCounts = groupSlotCounts
                .Select(count => count > 1 ? count / 2 : 1)
                .ToList();
            var to = groupSlotCounts.Sum();
            headers.Add($"{from}进{to}");
        }

        headers.Add("出线");
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
        int? qualifierCount = null)
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
            qualifierCount.HasValue
                ? $"共{result.Audit.ParticipantCount}{participantLabel}：{playInCount}场附加赛；附加赛后{mainSlotCount}{participantLabel}进入正赛，最终决出{qualifierCount.Value}个小组出线名额。"
                : $"共{result.Audit.ParticipantCount}{participantLabel}：{playInCount}场附加赛；附加赛后{mainSlotCount}{participantLabel}进入正赛。附加赛胜者直接进入同一行的正赛位置。",
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
            "附加赛",
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
            var title = $"第{group.Number}组：{group.Participants.Count}人，{playInCount}场附加赛，附加赛后{mainSlotCount}人进入正赛";

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
        IReadOnlyList<int> roundColumns)
    {
        var finalColumn = roundColumns[^1];

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
                var column = roundColumns[Math.Min(roundIndex, roundColumns.Count - 1)];
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

            if (roundCount < roundColumns.Count - 1)
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
        IReadOnlyList<int> roundColumns)
    {
        var finalColumn = roundColumns[^1];

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
                var targetColumn = roundColumns[Math.Min(roundIndex, roundColumns.Count - 1)];
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

            if (roundCount < roundColumns.Count - 1)
            {
                var sourceColumn = roundColumns[Math.Max(0, roundCount)];
                var sourceRow = roundCount == 0
                    ? groupStartRow
                    : GetGroupRoundRow(groupStartRow, roundCount, 0);
                var targetRow = GetGroupCenterRow(groupStartRow, initialCount);
                DrawSingleConnector(sheet, sourceColumn, finalColumn, sourceRow, targetRow);
            }
        }
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
            "填写说明：左侧附加赛完成后，将胜者姓名填入黄色正赛位置；绿色为直接进入正赛的选手；灰色格用于逐轮填写胜者；红色加粗姓名为种子选手。",
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
        var lastColumn = Math.Max(7, maxGroupSize + 4);
        var row = 5;

        ConfigureRoundRobinSheet(sheet, lastColumn, maxGroupSize);
        WriteRoundRobinTitle(sheet, result, lastColumn);

        foreach (var group in result.Groups)
        {
            row = WriteRoundRobinGroup(sheet, group, row);
            row++;
        }

        sheet.SheetView.FreezeRows(4);
        sheet.ShowGridLines = false;
        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sheet.PageSetup.FitToPages(1, 0);
        sheet.PageSetup.SetRowsToRepeatAtTop(1, 4);
        sheet.PageSetup.PrintAreas.Add($"A1:{sheet.Cell(Math.Max(row - 1, 4), lastColumn).Address.ToStringRelative()}");
    }

    private static void ConfigureRoundRobinSheet(IXLWorksheet sheet, int lastColumn, int maxGroupSize)
    {
        var participantColumnWidth = maxGroupSize switch
        {
            <= 5 => 18,
            <= 7 => 16,
            <= 8 => 15.5,
            <= 10 => 13,
            _ => 10
        };
        var summaryColumnWidth = maxGroupSize >= 8 ? 7 : 8;

        for (var column = 1; column <= lastColumn; column++)
        {
            sheet.Column(column).Width = column >= lastColumn - 2
                ? summaryColumnWidth
                : participantColumnWidth;
        }

        sheet.Row(1).Height = 26;
        sheet.Row(2).Height = 26;
        sheet.Row(3).Height = 24;
        sheet.Row(4).Height = 30;
    }

    private static void WriteRoundRobinTitle(IXLWorksheet sheet, DrawResult result, int lastColumn)
    {
        var participantLabel = RoundRobinParticipantLabel(result.Settings.EventKind);
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
            "交叉格填写赛程/比分/结果；对角线为轮空；右侧填胜场、净胜、名次。",
            NoteFill,
            fontSize: 9);
    }

    private static int WriteRoundRobinGroup(IXLWorksheet sheet, DrawGroup group, int startRow)
    {
        var participants = group.Participants;
        var groupSize = participants.Count;
        var groupColumnCount = Math.Max(4, groupSize + 4);
        var lastRow = startRow + Math.Max(groupSize, 1);

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
                        cell.Value = "";
                    }
                }
            }
        }

        ApplyRoundRobinGroupStyle(sheet, startRow, lastRow, groupColumnCount, groupSize);
        return lastRow + 1;
    }

    private static void ApplyRoundRobinGroupStyle(IXLWorksheet sheet, int firstRow, int lastRow, int lastColumn, int groupSize)
    {
        var range = sheet.Range(firstRow, 1, lastRow, lastColumn);
        range.Style.Font.FontName = "Microsoft YaHei";
        range.Style.Font.FontSize = 10;
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
            sheet.Row(row).Height = row == firstRow ? 36 : 30;
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
            ("随机种子", result.Audit.RandomSeed),
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

    private enum ConnectorBorderSide
    {
        Top,
        Bottom
    }
}
