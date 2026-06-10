using ClosedXML.Excel;
using SkiaSharp;

namespace BadmintonDraw.Excel;

public sealed class DrawResultVisualWriter
{
    private const string DefaultSheetName = "对阵表";
    private const long MaxPngBytes = 20L * 1024L * 1024L;
    private const long MaxRasterPixels = 90L * 1000L * 1000L;
    private const int MaxRasterDimension = 32000;
    private const float PointsToPixels = 96f / 72f;
    private const float ExcelColumnWidthToPixels = 8.3f;
    private const float CanvasMargin = 18;
    private const float PdfScale = 72f / 96f;
    private const float A4LandscapeWidth = 841.89f;
    private const float A4LandscapeHeight = 595.28f;
    private const float A4PortraitWidth = 595.28f;
    private const float A4PortraitHeight = 841.89f;
    private const float A4Margin = 2f;
    private const float RoundRobinPdfHorizontalSafetyInset = 24f;
    private const float TextWidthSafetyFactor = 0.9f;
    private const float DefaultFontSize = 10f * PointsToPixels;
    private const string DefaultFontName = "Microsoft YaHei";

    private static readonly float[] PngScaleCandidates = [4f, 3.75f, 3.5f, 3.25f, 3f, 2.75f, 2.5f, 2.25f, 2f, 1.75f, 1.5f, 1.25f, 1f];

    private static readonly SKColor White = SKColors.White;
    private static readonly SKColor Black = SKColors.Black;
    private static readonly SKColor DefaultBorder = SKColor.Parse("#808080");

    public void Write(
        string outputPath,
        string workbookPath,
        DrawResultVisualFormat format,
        DrawResultVisualOptions? options = null)
    {
        Write(outputPath, workbookPath, DefaultSheetName, format, options);
    }

    public void Write(
        string outputPath,
        string workbookPath,
        string sheetName,
        DrawResultVisualFormat format,
        DrawResultVisualOptions? options = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

        using var workbook = new XLWorkbook(workbookPath);
        var sheet = workbook.Worksheet(sheetName);
        options ??= new DrawResultVisualOptions();

        switch (format)
        {
            case DrawResultVisualFormat.Png:
                WriteTransparentPng(outputPath, BuildLayout(sheet));
                break;
            case DrawResultVisualFormat.Jpeg:
                WriteRaster(outputPath, BuildLayout(sheet), SKEncodedImageFormat.Jpeg, 95, scale: 1f, transparentBackground: false);
                break;
            case DrawResultVisualFormat.A4Pdf:
                if (TryBuildRoundRobinPageLayouts(sheet, out var pageLayouts))
                {
                    WriteRoundRobinA4Pdf(outputPath, pageLayouts);
                }
                else if (TryBuildScheduleGridPageLayouts(sheet, out pageLayouts))
                {
                    WriteScheduleGridA4Pdf(outputPath, pageLayouts);
                }
                else
                {
                    WriteA4Pdf(outputPath, BuildLayout(sheet), options.PdfRows, options.PdfColumns);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "不支持的导出格式。");
        }
    }

    private static WorksheetLayout BuildLayout(IXLWorksheet sheet)
    {
        var usedRange = sheet.RangeUsed(XLCellsUsedOptions.All)
            ?? throw new InvalidOperationException($"工作表“{sheet.Name}”没有可导出的内容。");
        var firstRow = usedRange.RangeAddress.FirstAddress.RowNumber;
        var firstColumn = usedRange.RangeAddress.FirstAddress.ColumnNumber;
        var lastRow = usedRange.RangeAddress.LastAddress.RowNumber;
        var lastColumn = usedRange.RangeAddress.LastAddress.ColumnNumber;
        return BuildLayout(sheet, firstRow, firstColumn, lastRow, lastColumn);
    }

    private static WorksheetLayout BuildLayout(IXLWorksheet sheet, int firstRow, int firstColumn, int lastRow, int lastColumn)
    {
        var metrics = GridMetrics.Create(sheet, firstRow, firstColumn, lastRow, lastColumn);
        var mergedRanges = sheet.MergedRanges
            .Where(range => Intersects(range, firstRow, firstColumn, lastRow, lastColumn))
            .ToList();
        var cells = new List<VisualCell>();
        var mergedAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mergedRange in mergedRanges)
        {
            var first = mergedRange.RangeAddress.FirstAddress;
            var last = mergedRange.RangeAddress.LastAddress;
            var cellFirstRow = Math.Max(first.RowNumber, firstRow);
            var cellFirstColumn = Math.Max(first.ColumnNumber, firstColumn);
            var cellLastRow = Math.Min(last.RowNumber, lastRow);
            var cellLastColumn = Math.Min(last.ColumnNumber, lastColumn);
            var cell = mergedRange.FirstCell();

            cells.Add(CreateMergedVisualCell(
                mergedRange,
                cell,
                metrics.GetRect(cellFirstRow, cellFirstColumn, cellLastRow, cellLastColumn)));

            foreach (var mergedCell in mergedRange.Cells())
            {
                var row = mergedCell.Address.RowNumber;
                var column = mergedCell.Address.ColumnNumber;
                if (row < firstRow || row > lastRow || column < firstColumn || column > lastColumn)
                {
                    continue;
                }

                mergedAddresses.Add(mergedCell.Address.ToStringRelative());
            }
        }

        foreach (var cell in sheet.Range(firstRow, firstColumn, lastRow, lastColumn).CellsUsed(XLCellsUsedOptions.All))
        {
            if (mergedAddresses.Contains(cell.Address.ToStringRelative()))
            {
                continue;
            }

            cells.Add(CreateVisualCell(
                cell,
                metrics.GetRect(
                    cell.Address.RowNumber,
                    cell.Address.ColumnNumber,
                    cell.Address.RowNumber,
                    cell.Address.ColumnNumber)));
        }

        return new WorksheetLayout(metrics.Width, metrics.Height, cells);
    }

    private static bool Intersects(IXLRange range, int firstRow, int firstColumn, int lastRow, int lastColumn)
    {
        var address = range.RangeAddress;
        return address.FirstAddress.RowNumber <= lastRow
            && address.LastAddress.RowNumber >= firstRow
            && address.FirstAddress.ColumnNumber <= lastColumn
            && address.LastAddress.ColumnNumber >= firstColumn;
    }

    private static bool TryBuildRoundRobinPageLayouts(
        IXLWorksheet sheet,
        out IReadOnlyList<WorksheetLayout> pageLayouts)
    {
        pageLayouts = [];
        var usedRange = sheet.RangeUsed(XLCellsUsedOptions.All);
        if (usedRange is null
            || !sheet.Cell(1, 1).GetString().Contains("循环赛对阵表", StringComparison.Ordinal))
        {
            return false;
        }

        var usedLastRow = usedRange.RangeAddress.LastAddress.RowNumber;
        var usedLastColumn = usedRange.RangeAddress.LastAddress.ColumnNumber;
        var sections = new List<RoundRobinSection>();
        for (var row = 1; row <= usedLastRow; row++)
        {
            if (TryGetRoundRobinSection(sheet, row, usedLastColumn, out var section))
            {
                sections.Add(section);
            }
        }

        if (sections.Count == 0)
        {
            return false;
        }

        var layouts = new List<WorksheetLayout>
        {
            BuildRoundRobinCoverLayout(sheet, sections, usedLastRow),
        };

        for (var index = 0; index < sections.Count; index++)
        {
            var section = sections[index];
            var sectionLastRow = GetRoundRobinSectionLastRow(sheet, sections, index, usedLastRow);
            layouts.Add(BuildLayout(sheet, section.HeaderRow, 1, sectionLastRow, section.LastColumn));
        }

        pageLayouts = layouts;
        return true;
    }

    private static bool TryBuildScheduleGridPageLayouts(
        IXLWorksheet sheet,
        out IReadOnlyList<WorksheetLayout> pageLayouts)
    {
        pageLayouts = [];
        var usedRange = sheet.RangeUsed(XLCellsUsedOptions.All);
        if (usedRange is null || !string.Equals(sheet.Name, "时间场地网格", StringComparison.Ordinal))
        {
            return false;
        }

        var usedLastRow = usedRange.RangeAddress.LastAddress.RowNumber;
        var usedLastColumn = usedRange.RangeAddress.LastAddress.ColumnNumber;
        var sectionStartRows = new List<int>();
        for (var row = 1; row < usedLastRow; row++)
        {
            var title = sheet.Cell(row, 1).GetString().Trim();
            var nextRowFirstCell = sheet.Cell(row + 1, 1).GetString().Trim();
            if (title.EndsWith("赛程", StringComparison.Ordinal)
                && string.Equals(nextRowFirstCell, "时间", StringComparison.Ordinal))
            {
                sectionStartRows.Add(row);
            }
        }

        if (sectionStartRows.Count == 0)
        {
            return false;
        }

        var layouts = new List<WorksheetLayout>();
        for (var index = 0; index < sectionStartRows.Count; index++)
        {
            var startRow = sectionStartRows[index];
            var endRow = index + 1 < sectionStartRows.Count
                ? sectionStartRows[index + 1] - 1
                : usedLastRow;
            while (endRow > startRow && IsEmptyRow(sheet, endRow, 1, usedLastColumn))
            {
                endRow--;
            }

            var lastColumn = GetLastContentColumn(sheet, startRow, endRow, usedLastColumn);
            layouts.Add(BuildLayout(sheet, startRow, 1, endRow, lastColumn));
        }

        pageLayouts = layouts;
        return true;
    }

    private static WorksheetLayout BuildRoundRobinCoverLayout(
        IXLWorksheet sourceSheet,
        IReadOnlyList<RoundRobinSection> sections,
        int usedLastRow)
    {
        using var workbook = new XLWorkbook();
        workbook.Style.Font.FontName = DefaultFontName;
        var sheet = workbook.Worksheets.Add("PDF封面");
        const int lastColumn = 8;

        var title = sourceSheet.Cell(1, 1).GetString().Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "循环赛对阵表";
        }

        var summary = sourceSheet.Cell(3, 1).GetString().Trim();
        var note = sourceSheet.Cell(4, 1).GetString().Trim();
        var groupSummaries = sections
            .Select((section, index) =>
            {
                var sectionLastRow = GetRoundRobinSectionLastRow(sourceSheet, sections, index, usedLastRow);
                var groupName = sourceSheet.Cell(section.HeaderRow, 1).GetString().Trim();
                return new RoundRobinGroupSummary(
                    string.IsNullOrWhiteSpace(groupName) ? $"第{index + 1}组" : groupName,
                    CountRoundRobinParticipants(sourceSheet, section, sectionLastRow),
                    index + 2);
            })
            .ToArray();

        for (var column = 1; column <= lastColumn; column++)
        {
            sheet.Column(column).Width = 12;
        }

        sheet.Row(1).Height = 34;
        sheet.Row(2).Height = 18;
        sheet.Row(3).Height = 24;
        sheet.Row(4).Height = 28;
        sheet.Row(5).Height = 10;
        sheet.Row(6).Height = 24;
        sheet.Row(7).Height = 30;
        sheet.Row(8).Height = 30;
        sheet.Row(9).Height = 30;
        sheet.Row(10).Height = 38;
        sheet.Row(11).Height = 10;
        sheet.Row(12).Height = 24;

        var titleRange = MergeAndSet(sheet, 1, 1, 2, lastColumn, title);
        titleRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F4E78");
        titleRange.Style.Font.FontColor = XLColor.White;
        titleRange.Style.Font.Bold = true;
        titleRange.Style.Font.FontSize = 20;

        var summaryRange = MergeAndSet(sheet, 3, 1, 3, lastColumn, summary);
        summaryRange.Style.Font.Bold = true;
        summaryRange.Style.Font.FontSize = 12;

        var noteRange = MergeAndSet(sheet, 4, 1, 4, lastColumn, note);
        noteRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#EEF2FF");
        noteRange.Style.Font.FontSize = 10;

        var infoTitle = MergeAndSet(sheet, 6, 1, 6, lastColumn, "打印总览");
        infoTitle.Style.Fill.BackgroundColor = XLColor.FromHtml("#305496");
        infoTitle.Style.Font.FontColor = XLColor.White;
        infoTitle.Style.Font.Bold = true;

        WriteCoverInfoRow(sheet, 7, "PDF结构", "第 1 页为总览说明；后续每个小组单独一页，适合直接横向打印。", lastColumn);
        WriteCoverInfoRow(sheet, 8, "小组数量", $"{sections.Count} 个小组", lastColumn);
        WriteCoverInfoRow(sheet, 9, "小组人数", string.Join("；", groupSummaries.Select(group => $"{group.Name}：{group.ParticipantCount}")), lastColumn);
        WriteCoverInfoRow(sheet, 10, "填写提示", "矩阵交叉格显示场次编号；右侧填写胜场、净胜、名次；每组下方为赛程顺序。", lastColumn);

        var groupTitle = MergeAndSet(sheet, 12, 1, 12, lastColumn, "小组页码索引");
        groupTitle.Style.Fill.BackgroundColor = XLColor.FromHtml("#D9EAF7");
        groupTitle.Style.Font.Bold = true;

        var tableHeaderRow = 13;
        sheet.Cell(tableHeaderRow, 1).Value = "小组";
        sheet.Range(tableHeaderRow, 2, tableHeaderRow, 3).Merge().Value = "参赛单位/选手数";
        sheet.Range(tableHeaderRow, 4, tableHeaderRow, lastColumn).Merge().Value = "PDF页码";
        sheet.Range(tableHeaderRow, 1, tableHeaderRow, lastColumn).Style.Fill.BackgroundColor = XLColor.FromHtml("#E7F3FB");
        sheet.Range(tableHeaderRow, 1, tableHeaderRow, lastColumn).Style.Font.Bold = true;
        sheet.Row(tableHeaderRow).Height = 24;

        var row = tableHeaderRow + 1;
        foreach (var group in groupSummaries)
        {
            sheet.Cell(row, 1).Value = group.Name;
            sheet.Range(row, 2, row, 3).Merge().Value = group.ParticipantCount;
            sheet.Range(row, 4, row, lastColumn).Merge().Value = $"第 {group.PageNumber} 页";
            sheet.Row(row).Height = 24;
            row++;
        }

        var finalRow = Math.Max(row - 1, tableHeaderRow + 1);
        var footer = MergeAndSet(
            sheet,
            finalRow + 1,
            1,
            finalRow + 2,
            lastColumn,
            "建议打印方式：A4 横向、窄边距、按实际大小或适合页面打印；如需现场记录，可先打印本封面作为抽签结果目录。");
        footer.Style.Fill.BackgroundColor = XLColor.FromHtml("#F8FAFC");
        footer.Style.Font.FontColor = XLColor.FromHtml("#1F2937");
        footer.Style.Font.FontSize = 10;
        sheet.Row(finalRow + 1).Height = 24;
        sheet.Row(finalRow + 2).Height = 24;

        var usedRange = sheet.Range(1, 1, finalRow + 2, lastColumn);
        usedRange.Style.Font.FontName = DefaultFontName;
        usedRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        usedRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        usedRange.Style.Alignment.WrapText = true;
        usedRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        usedRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        usedRange.Style.Border.OutsideBorderColor = XLColor.FromHtml("#808080");
        usedRange.Style.Border.InsideBorderColor = XLColor.FromHtml("#808080");

        return BuildLayout(sheet, 1, 1, finalRow + 2, lastColumn);
    }

    private static IXLRange MergeAndSet(
        IXLWorksheet sheet,
        int firstRow,
        int firstColumn,
        int lastRow,
        int lastColumn,
        string value)
    {
        var range = sheet.Range(firstRow, firstColumn, lastRow, lastColumn);
        range.Merge();
        range.FirstCell().Value = value;
        return range;
    }

    private static void WriteCoverInfoRow(IXLWorksheet sheet, int row, string label, string value, int lastColumn)
    {
        sheet.Cell(row, 1).Value = label;
        sheet.Range(row, 2, row, lastColumn).Merge().Value = value;
        sheet.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#D9EAF7");
        sheet.Cell(row, 1).Style.Font.Bold = true;
        sheet.Range(row, 2, row, lastColumn).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
    }

    private static int GetRoundRobinSectionLastRow(
        IXLWorksheet sheet,
        IReadOnlyList<RoundRobinSection> sections,
        int index,
        int usedLastRow)
    {
        var section = sections[index];
        var sectionLastRow = index + 1 < sections.Count
            ? sections[index + 1].HeaderRow - 2
            : usedLastRow;

        while (sectionLastRow > section.HeaderRow
            && IsEmptyRow(sheet, sectionLastRow, 1, section.LastColumn))
        {
            sectionLastRow--;
        }

        return sectionLastRow;
    }

    private static int CountRoundRobinParticipants(IXLWorksheet sheet, RoundRobinSection section, int sectionLastRow)
    {
        var count = 0;
        for (var row = section.HeaderRow + 1; row <= sectionLastRow; row++)
        {
            var label = sheet.Cell(row, 1).GetString().Trim();
            if (string.IsNullOrWhiteSpace(label) || label == "赛程顺序")
            {
                break;
            }

            count++;
        }

        return count;
    }

    private static bool TryGetRoundRobinSection(
        IXLWorksheet sheet,
        int row,
        int searchLastColumn,
        out RoundRobinSection section)
    {
        section = default;
        var groupLabel = sheet.Cell(row, 1).GetString().Trim();
        if (!groupLabel.EndsWith("组", StringComparison.Ordinal))
        {
            return false;
        }

        var winColumn = 0;
        var netColumn = 0;
        var rankColumn = 0;
        for (var column = 2; column <= searchLastColumn; column++)
        {
            var text = sheet.Cell(row, column).GetString().Trim();
            if (text == "胜场")
            {
                winColumn = column;
            }
            else if (text == "净胜")
            {
                netColumn = column;
            }
            else if (text == "名次")
            {
                rankColumn = column;
            }
        }

        if (winColumn == 0 || netColumn == 0 || rankColumn == 0)
        {
            return false;
        }

        section = new RoundRobinSection(row, rankColumn);
        return true;
    }

    private static bool IsEmptyRow(IXLWorksheet sheet, int row, int firstColumn, int lastColumn)
    {
        return Enumerable.Range(firstColumn, lastColumn - firstColumn + 1)
            .All(column => string.IsNullOrWhiteSpace(sheet.Cell(row, column).GetString()));
    }

    private static int GetLastContentColumn(IXLWorksheet sheet, int firstRow, int lastRow, int maxColumn)
    {
        var lastColumn = 1;
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = maxColumn; column >= 1; column--)
            {
                if (!string.IsNullOrWhiteSpace(sheet.Cell(row, column).GetString()))
                {
                    lastColumn = Math.Max(lastColumn, column);
                    break;
                }
            }
        }

        return lastColumn;
    }

    private static VisualCell CreateMergedVisualCell(IXLRange mergedRange, IXLCell cell, SKRect bounds)
    {
        var rangeBorder = mergedRange.Style.Border;
        var topLeftBorder = mergedRange.FirstCell().Style.Border;
        var topRightBorder = mergedRange.Cell(1, mergedRange.ColumnCount()).Style.Border;
        var bottomLeftBorder = mergedRange.Cell(mergedRange.RowCount(), 1).Style.Border;
        var bottomRightBorder = mergedRange.LastCell().Style.Border;

        return CreateVisualCell(
            cell,
            bounds,
            FirstVisibleBorder(
                GetBorder(rangeBorder.TopBorder, rangeBorder.TopBorderColor),
                GetBorder(topLeftBorder.TopBorder, topLeftBorder.TopBorderColor),
                GetBorder(topRightBorder.TopBorder, topRightBorder.TopBorderColor)),
            FirstVisibleBorder(
                GetBorder(rangeBorder.RightBorder, rangeBorder.RightBorderColor),
                GetBorder(topRightBorder.RightBorder, topRightBorder.RightBorderColor),
                GetBorder(bottomRightBorder.RightBorder, bottomRightBorder.RightBorderColor)),
            FirstVisibleBorder(
                GetBorder(rangeBorder.BottomBorder, rangeBorder.BottomBorderColor),
                GetBorder(bottomLeftBorder.BottomBorder, bottomLeftBorder.BottomBorderColor),
                GetBorder(bottomRightBorder.BottomBorder, bottomRightBorder.BottomBorderColor)),
            FirstVisibleBorder(
                GetBorder(rangeBorder.LeftBorder, rangeBorder.LeftBorderColor),
                GetBorder(topLeftBorder.LeftBorder, topLeftBorder.LeftBorderColor),
                GetBorder(bottomLeftBorder.LeftBorder, bottomLeftBorder.LeftBorderColor)));
    }

    private static VisualCell CreateVisualCell(IXLCell cell, SKRect bounds)
    {
        var border = cell.Style.Border;
        return CreateVisualCell(
            cell,
            bounds,
            GetBorder(border.TopBorder, border.TopBorderColor),
            GetBorder(border.RightBorder, border.RightBorderColor),
            GetBorder(border.BottomBorder, border.BottomBorderColor),
            GetBorder(border.LeftBorder, border.LeftBorderColor));
    }

    private static VisualCell CreateVisualCell(
        IXLCell cell,
        SKRect bounds,
        CellBorder topBorder,
        CellBorder rightBorder,
        CellBorder bottomBorder,
        CellBorder leftBorder)
    {
        var style = cell.Style;
        var text = cell.GetFormattedString();
        var fill = ToSkColor(style.Fill.BackgroundColor, White);
        var fontColor = ToSkColor(style.Font.FontColor, Black);
        var fontName = GetPlatformFontName(style.Font.FontName);
        var fontSize = (float)Math.Max(6, style.Font.FontSize * PointsToPixels);

        return new VisualCell(
            bounds,
            text,
            fill,
            fontColor,
            topBorder,
            rightBorder,
            bottomBorder,
            leftBorder,
            fontName,
            style.Font.Bold,
            fontSize,
            style.Alignment.Horizontal,
            style.Alignment.Vertical,
            style.Alignment.WrapText);
    }

    private static CellBorder GetBorder(XLBorderStyleValues style, XLColor color)
    {
        if (style == XLBorderStyleValues.None)
        {
            return CellBorder.None;
        }

        return new CellBorder(ToSkColor(color, DefaultBorder), GetBorderWidth(style));
    }

    private static CellBorder FirstVisibleBorder(params CellBorder[] borders)
    {
        return borders.FirstOrDefault(border => border.Width > 0) ?? CellBorder.None;
    }

    private static float GetBorderWidth(XLBorderStyleValues style)
    {
        return style switch
        {
            XLBorderStyleValues.Medium => 2f,
            XLBorderStyleValues.Thick => 3f,
            XLBorderStyleValues.Double => 2f,
            _ => 1f
        };
    }

    private static SKColor ToSkColor(XLColor color, SKColor fallback)
    {
        try
        {
            var drawingColor = color.Color;
            if (drawingColor.A == 0)
            {
                return fallback;
            }

            return new SKColor(drawingColor.R, drawingColor.G, drawingColor.B, drawingColor.A);
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static string GetPlatformFontName(string fontName)
    {
        var normalized = string.IsNullOrWhiteSpace(fontName)
            ? DefaultFontName
            : fontName.Trim();
        if (OperatingSystem.IsMacOS() && IsMicrosoftYaHei(normalized))
        {
            return "PingFang SC";
        }

        if (OperatingSystem.IsLinux() && IsMicrosoftYaHei(normalized))
        {
            return "Noto Sans CJK SC";
        }

        return normalized;
    }

    private static bool IsMicrosoftYaHei(string fontName)
    {
        return fontName.Contains("Microsoft YaHei", StringComparison.OrdinalIgnoreCase)
            || fontName.Contains("微软雅黑", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteRaster(
        string outputPath,
        WorksheetLayout layout,
        SKEncodedImageFormat imageFormat,
        int quality,
        float scale,
        bool transparentBackground)
    {
        var width = Math.Max(1, (int)Math.Ceiling(layout.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(layout.Height * scale));
        if (!IsRasterSizeAllowed(width, height))
        {
            throw new InvalidOperationException($"导出图片尺寸过大：{width}×{height}。请改用 Excel/PDF，或减少对阵表分页范围。");
        }

        var imageInfo = new SKImageInfo(
            width,
            height,
            SKColorType.Bgra8888,
            transparentBackground ? SKAlphaType.Premul : SKAlphaType.Opaque);

        using var surface = SKSurface.Create(imageInfo)
            ?? throw new InvalidOperationException($"无法创建图片画布：{width}×{height}。");
        surface.Canvas.Scale(scale);
        DrawLayout(surface.Canvas, layout, transparentBackground ? SKColors.Transparent : White);
        using var image = surface.Snapshot();
        using var data = image.Encode(imageFormat, quality)
            ?? throw new InvalidOperationException("图片编码失败。");
        using var stream = File.Create(outputPath);
        data.SaveTo(stream);
    }

    private static void WriteTransparentPng(string outputPath, WorksheetLayout layout)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"badminton-draw-png-{Guid.NewGuid():N}.png");
        try
        {
            foreach (var scale in GetSafePngScales(layout))
            {
                WriteRaster(tempPath, layout, SKEncodedImageFormat.Png, 100, scale, transparentBackground: true);
                var length = new FileInfo(tempPath).Length;
                if (length <= MaxPngBytes || scale <= 1f)
                {
                    File.Copy(tempPath, outputPath, overwrite: true);
                    return;
                }
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static IReadOnlyList<float> GetSafePngScales(WorksheetLayout layout)
    {
        var scales = PngScaleCandidates
            .Where(scale => IsRasterSizeAllowed(layout, scale))
            .ToList();
        if (scales.Count > 0)
        {
            return scales;
        }

        var widthScale = MaxRasterDimension / Math.Max(1f, layout.Width);
        var heightScale = MaxRasterDimension / Math.Max(1f, layout.Height);
        var pixelScale = (float)Math.Sqrt(MaxRasterPixels / Math.Max(1d, (double)layout.Width * layout.Height));
        var fallbackScale = Math.Max(0.1f, Math.Min(1f, Math.Min(Math.Min(widthScale, heightScale), pixelScale)));
        return [fallbackScale];
    }

    private static bool IsRasterSizeAllowed(WorksheetLayout layout, float scale)
    {
        var width = Math.Max(1L, (long)Math.Ceiling(layout.Width * scale));
        var height = Math.Max(1L, (long)Math.Ceiling(layout.Height * scale));
        return IsRasterSizeAllowed(width, height);
    }

    private static bool IsRasterSizeAllowed(long width, long height)
    {
        return width <= MaxRasterDimension
            && height <= MaxRasterDimension
            && width * height <= MaxRasterPixels;
    }

    private static void WriteA4Pdf(string outputPath, WorksheetLayout layout, int rows, int columns)
    {
        using var stream = File.Create(outputPath);
        using var document = SKDocument.CreatePdf(stream);

        var sourceTileWidth = layout.Width / columns;
        var sourceTileHeight = layout.Height / rows;
        var pageSize = GetA4PageSize(sourceTileWidth, sourceTileHeight);
        var printableWidth = pageSize.Width - A4Margin * 2;
        var printableHeight = pageSize.Height - A4Margin * 2;
        var scale = Math.Min(
            printableWidth / (sourceTileWidth * PdfScale),
            printableHeight / (sourceTileHeight * PdfScale));
        var drawWidth = sourceTileWidth * PdfScale * scale;
        var drawHeight = sourceTileHeight * PdfScale * scale;
        var originX = A4Margin + (printableWidth - drawWidth) / 2;
        var originY = A4Margin + (printableHeight - drawHeight) / 2;

        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                using var canvas = document.BeginPage(pageSize.Width, pageSize.Height);
                canvas.Save();
                canvas.ClipRect(new SKRect(A4Margin, A4Margin, pageSize.Width - A4Margin, pageSize.Height - A4Margin));
                canvas.Translate(originX, originY);
                canvas.Scale(PdfScale * scale);
                canvas.Translate(-column * sourceTileWidth, -row * sourceTileHeight);
                DrawLayout(canvas, layout, White);
                canvas.Restore();
                document.EndPage();
            }
        }

        document.Close();
    }

    private static void WriteRoundRobinA4Pdf(string outputPath, IReadOnlyList<WorksheetLayout> pageLayouts)
    {
        WritePageLayoutsA4Pdf(outputPath, pageLayouts, RoundRobinPdfHorizontalSafetyInset);
    }

    private static void WriteScheduleGridA4Pdf(string outputPath, IReadOnlyList<WorksheetLayout> pageLayouts)
    {
        WritePageLayoutsA4Pdf(outputPath, pageLayouts, horizontalSafetyInset: 10f);
    }

    private static void WritePageLayoutsA4Pdf(
        string outputPath,
        IReadOnlyList<WorksheetLayout> pageLayouts,
        float horizontalSafetyInset)
    {
        using var stream = File.Create(outputPath);
        using var document = SKDocument.CreatePdf(stream);

        var pageSize = new PageSize(A4LandscapeWidth, A4LandscapeHeight);
        var printableWidth = pageSize.Width - (A4Margin + horizontalSafetyInset) * 2;
        var printableHeight = pageSize.Height - A4Margin * 2;

        foreach (var sourceLayout in pageLayouts)
        {
            var layout = StretchLayoutToPageHeight(sourceLayout, printableHeight / printableWidth);
            var scale = Math.Min(
                printableWidth / (layout.Width * PdfScale),
                printableHeight / (layout.Height * PdfScale));
            var drawWidth = layout.Width * PdfScale * scale;
            var drawHeight = layout.Height * PdfScale * scale;
            var originX = A4Margin + horizontalSafetyInset + (printableWidth - drawWidth) / 2;
            var originY = A4Margin + (printableHeight - drawHeight) / 2;

            using var canvas = document.BeginPage(pageSize.Width, pageSize.Height);
            canvas.Save();
            canvas.ClipRect(new SKRect(A4Margin, A4Margin, pageSize.Width - A4Margin, pageSize.Height - A4Margin));
            canvas.Translate(originX, originY);
            canvas.Scale(PdfScale * scale);
            DrawLayout(canvas, layout, White);
            canvas.Restore();
            document.EndPage();
        }

        document.Close();
    }

    private static WorksheetLayout StretchLayoutToPageHeight(WorksheetLayout layout, float targetAspectRatio)
    {
        var targetHeight = Math.Max(layout.Height, layout.Width * targetAspectRatio);
        if (targetHeight <= layout.Height + 1f)
        {
            return layout;
        }

        var sourceContentHeight = Math.Max(1f, layout.Height - CanvasMargin * 2);
        var targetContentHeight = Math.Max(1f, targetHeight - CanvasMargin * 2);
        var verticalScale = targetContentHeight / sourceContentHeight;
        var stretchedCells = layout.Cells
            .Select(cell => cell with { Bounds = StretchRectVertically(cell.Bounds, verticalScale) })
            .ToList();

        return layout with { Height = targetHeight, Cells = stretchedCells };
    }

    private static SKRect StretchRectVertically(SKRect rect, float verticalScale)
    {
        return new SKRect(
            rect.Left,
            CanvasMargin + (rect.Top - CanvasMargin) * verticalScale,
            rect.Right,
            CanvasMargin + (rect.Bottom - CanvasMargin) * verticalScale);
    }

    private static PageSize GetA4PageSize(float sourceTileWidth, float sourceTileHeight)
    {
        return sourceTileHeight > sourceTileWidth * 1.1f
            ? new PageSize(A4PortraitWidth, A4PortraitHeight)
            : new PageSize(A4LandscapeWidth, A4LandscapeHeight);
    }

    private static void DrawLayout(SKCanvas canvas, WorksheetLayout layout, SKColor background)
    {
        canvas.Clear(background);
        var orderedCells = layout.Cells
            .OrderBy(cell => cell.Bounds.Top)
            .ThenBy(cell => cell.Bounds.Left)
            .ToList();

        foreach (var cell in orderedCells)
        {
            DrawCellFill(canvas, cell);
        }

        foreach (var cell in orderedCells)
        {
            DrawText(canvas, cell);
        }

        foreach (var cell in orderedCells)
        {
            DrawBorders(canvas, cell);
        }
    }

    private static void DrawCellFill(SKCanvas canvas, VisualCell cell)
    {
        using var fillPaint = new SKPaint
        {
            Color = cell.Fill,
            IsAntialias = false,
            Style = SKPaintStyle.Fill
        };
        canvas.DrawRect(cell.Bounds, fillPaint);
    }

    private static void DrawBorders(SKCanvas canvas, VisualCell cell)
    {
        DrawBorder(canvas, cell.TopBorder, cell.Bounds.Left, cell.Bounds.Top, cell.Bounds.Right, cell.Bounds.Top);
        DrawBorder(canvas, cell.RightBorder, cell.Bounds.Right, cell.Bounds.Top, cell.Bounds.Right, cell.Bounds.Bottom);
        DrawBorder(canvas, cell.BottomBorder, cell.Bounds.Left, cell.Bounds.Bottom, cell.Bounds.Right, cell.Bounds.Bottom);
        DrawBorder(canvas, cell.LeftBorder, cell.Bounds.Left, cell.Bounds.Top, cell.Bounds.Left, cell.Bounds.Bottom);
    }

    private static void DrawBorder(SKCanvas canvas, CellBorder border, float x1, float y1, float x2, float y2)
    {
        if (border.Width <= 0)
        {
            return;
        }

        using var paint = new SKPaint
        {
            Color = border.Color,
            IsAntialias = false,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = border.Width,
            StrokeCap = SKStrokeCap.Square
        };
        canvas.DrawLine(x1, y1, x2, y2, paint);
    }

    private static void DrawText(SKCanvas canvas, VisualCell cell)
    {
        if (string.IsNullOrWhiteSpace(cell.Text))
        {
            return;
        }

        using var typeface = SKTypeface.FromFamilyName(
            cell.FontName,
            cell.IsBold ? SKFontStyle.Bold : SKFontStyle.Normal);
        using var paint = new SKPaint
        {
            Color = cell.FontColor,
            IsAntialias = true,
            Typeface = typeface ?? SKTypeface.Default,
            TextSize = cell.FontSize,
            TextAlign = ToTextAlign(cell.HorizontalAlignment)
        };
        var textBounds = cell.Bounds;
        var horizontalPadding = Math.Min(5f, Math.Max(2f, cell.Bounds.Width * 0.035f));
        var verticalPadding = Math.Min(3f, Math.Max(1.5f, cell.Bounds.Height * 0.08f));
        textBounds.Inflate(-horizontalPadding, -verticalPadding);
        var maxLineWidth = Math.Max(10, textBounds.Width * TextWidthSafetyFactor);
        var minFontSize = Math.Min(cell.FontSize, 5.8f * PointsToPixels);
        List<string> lines;
        SKFontMetrics metrics;
        float lineHeight;
        int maxLines;

        while (true)
        {
            lines = cell.WrapText
                ? WrapText(cell.Text, paint, maxLineWidth)
                : [cell.Text];
            metrics = paint.FontMetrics;
            lineHeight = Math.Max(1, metrics.Descent - metrics.Ascent + metrics.Leading);
            maxLines = Math.Max(1, (int)Math.Floor(textBounds.Height / lineHeight));
            var fitsHeight = lines.Count <= maxLines;
            var fitsWidth = lines.All(line => paint.MeasureText(line) <= maxLineWidth);
            if ((fitsHeight && fitsWidth) || paint.TextSize <= minFontSize)
            {
                break;
            }

            paint.TextSize = Math.Max(minFontSize, paint.TextSize - 0.5f);
        }

        if (lines.Count > maxLines)
        {
            lines = lines.Take(maxLines).ToList();
            lines[^1] = TrimWithEllipsis(lines[^1], paint, maxLineWidth);
        }

        lines = lines.Select(line => TrimWithEllipsis(line, paint, maxLineWidth)).ToList();

        var totalHeight = lineHeight * lines.Count;
        var firstBaseline = GetFirstBaseline(textBounds, cell.VerticalAlignment, totalHeight, metrics);
        var x = GetTextX(textBounds, paint.TextAlign);

        canvas.Save();
        canvas.ClipRect(textBounds);
        for (var i = 0; i < lines.Count; i++)
        {
            canvas.DrawText(lines[i], x, firstBaseline + i * lineHeight, paint);
        }
        canvas.Restore();
    }

    private static float GetFirstBaseline(
        SKRect bounds,
        XLAlignmentVerticalValues verticalAlignment,
        float totalHeight,
        SKFontMetrics metrics)
    {
        return verticalAlignment switch
        {
            XLAlignmentVerticalValues.Top => bounds.Top - metrics.Ascent,
            XLAlignmentVerticalValues.Bottom => bounds.Bottom - totalHeight - metrics.Ascent,
            _ => bounds.MidY - totalHeight / 2 - metrics.Ascent
        };
    }

    private static SKTextAlign ToTextAlign(XLAlignmentHorizontalValues alignment)
    {
        return alignment switch
        {
            XLAlignmentHorizontalValues.Left => SKTextAlign.Left,
            XLAlignmentHorizontalValues.Right => SKTextAlign.Right,
            _ => SKTextAlign.Center
        };
    }

    private static float GetTextX(SKRect bounds, SKTextAlign align)
    {
        return align switch
        {
            SKTextAlign.Left => bounds.Left,
            SKTextAlign.Right => bounds.Right,
            _ => bounds.MidX
        };
    }

    private static List<string> WrapText(string text, SKPaint paint, float maxWidth)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                lines.Add("");
                continue;
            }

            var current = "";
            foreach (var character in paragraph)
            {
                var candidate = current + character;
                if (current.Length == 0 || paint.MeasureText(candidate) <= maxWidth)
                {
                    current = candidate;
                    continue;
                }

                lines.Add(current);
                current = character.ToString();
            }

            if (current.Length > 0)
            {
                lines.Add(current);
            }
        }

        return lines.Count == 0 ? [""] : lines;
    }

    private static string TrimWithEllipsis(string text, SKPaint paint, float maxWidth)
    {
        const string ellipsis = "...";

        if (paint.MeasureText(text) <= maxWidth)
        {
            return text;
        }

        while (text.Length > 0 && paint.MeasureText(text + ellipsis) > maxWidth)
        {
            text = text[..^1];
        }

        return text + ellipsis;
    }

    private sealed record VisualCell(
        SKRect Bounds,
        string Text,
        SKColor Fill,
        SKColor FontColor,
        CellBorder TopBorder,
        CellBorder RightBorder,
        CellBorder BottomBorder,
        CellBorder LeftBorder,
        string FontName,
        bool IsBold,
        float FontSize,
        XLAlignmentHorizontalValues HorizontalAlignment,
        XLAlignmentVerticalValues VerticalAlignment,
        bool WrapText);

    private sealed record CellBorder(SKColor Color, float Width)
    {
        public static CellBorder None { get; } = new(SKColors.Transparent, 0);
    }

    private sealed record WorksheetLayout(float Width, float Height, IReadOnlyList<VisualCell> Cells);

    private sealed record PageSize(float Width, float Height);

    private readonly record struct RoundRobinSection(int HeaderRow, int LastColumn);

    private readonly record struct RoundRobinGroupSummary(string Name, int ParticipantCount, int PageNumber);

    private sealed class GridMetrics
    {
        private readonly int _firstRow;
        private readonly int _firstColumn;
        private readonly float[] _columnOffsets;
        private readonly float[] _rowOffsets;

        private GridMetrics(
            int firstRow,
            int firstColumn,
            IReadOnlyList<float> columnWidths,
            IReadOnlyList<float> rowHeights)
        {
            _firstRow = firstRow;
            _firstColumn = firstColumn;
            _columnOffsets = BuildOffsets(columnWidths);
            _rowOffsets = BuildOffsets(rowHeights);
            Width = _columnOffsets[^1] + CanvasMargin * 2;
            Height = _rowOffsets[^1] + CanvasMargin * 2;
        }

        public float Width { get; }

        public float Height { get; }

        public static GridMetrics Create(IXLWorksheet sheet, int firstRow, int firstColumn, int lastRow, int lastColumn)
        {
            var columnWidths = Enumerable.Range(firstColumn, lastColumn - firstColumn + 1)
                .Select(column => Math.Max(1f, (float)sheet.Column(column).Width * ExcelColumnWidthToPixels))
                .ToArray();
            var rowHeights = Enumerable.Range(firstRow, lastRow - firstRow + 1)
                .Select(row => Math.Max(1f, (float)sheet.Row(row).Height * PointsToPixels))
                .ToArray();
            return new GridMetrics(firstRow, firstColumn, columnWidths, rowHeights);
        }

        public SKRect GetRect(int firstRow, int firstColumn, int lastRow, int lastColumn)
        {
            var leftIndex = firstColumn - _firstColumn;
            var rightIndex = lastColumn - _firstColumn + 1;
            var topIndex = firstRow - _firstRow;
            var bottomIndex = lastRow - _firstRow + 1;

            return new SKRect(
                CanvasMargin + _columnOffsets[leftIndex],
                CanvasMargin + _rowOffsets[topIndex],
                CanvasMargin + _columnOffsets[rightIndex],
                CanvasMargin + _rowOffsets[bottomIndex]);
        }

        private static float[] BuildOffsets(IReadOnlyList<float> sizes)
        {
            var offsets = new float[sizes.Count + 1];
            for (var i = 0; i < sizes.Count; i++)
            {
                offsets[i + 1] = offsets[i] + sizes[i];
            }

            return offsets;
        }
    }
}
