using ClosedXML.Excel;
using SkiaSharp;

namespace BadmintonDraw.Excel;

public sealed class DrawResultVisualWriter
{
    private const string DefaultSheetName = "对阵表";
    private const long MaxPngBytes = 20L * 1024L * 1024L;
    private const float PointsToPixels = 96f / 72f;
    private const float ExcelColumnWidthToPixels = 8.3f;
    private const float CanvasMargin = 18;
    private const float PdfScale = 72f / 96f;
    private const float A4LandscapeWidth = 841.89f;
    private const float A4LandscapeHeight = 595.28f;
    private const float A4PortraitWidth = 595.28f;
    private const float A4PortraitHeight = 841.89f;
    private const float A4Margin = 2f;
    private const float DefaultFontSize = 10f * PointsToPixels;

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
        var layout = BuildLayout(sheet);
        options ??= new DrawResultVisualOptions();

        switch (format)
        {
            case DrawResultVisualFormat.Png:
                WriteTransparentPng(outputPath, layout);
                break;
            case DrawResultVisualFormat.Jpeg:
                WriteRaster(outputPath, layout, SKEncodedImageFormat.Jpeg, 95, scale: 1f, transparentBackground: false);
                break;
            case DrawResultVisualFormat.A4Pdf:
                WriteA4Pdf(outputPath, layout, options.PdfRows, options.PdfColumns);
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
            var cell = mergedRange.FirstCell();

            cells.Add(CreateVisualCell(
                cell,
                metrics.GetRect(first.RowNumber, first.ColumnNumber, last.RowNumber, last.ColumnNumber)));

            foreach (var mergedCell in mergedRange.Cells())
            {
                mergedAddresses.Add(mergedCell.Address.ToStringRelative());
            }
        }

        foreach (var cell in usedRange.CellsUsed(XLCellsUsedOptions.All))
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

    private static VisualCell CreateVisualCell(IXLCell cell, SKRect bounds)
    {
        var style = cell.Style;
        var text = cell.GetFormattedString();
        var fill = ToSkColor(style.Fill.BackgroundColor, White);
        var fontColor = ToSkColor(style.Font.FontColor, Black);
        var fontName = string.IsNullOrWhiteSpace(style.Font.FontName)
            ? "Microsoft YaHei"
            : style.Font.FontName;
        var fontSize = (float)Math.Max(6, style.Font.FontSize * PointsToPixels);

        return new VisualCell(
            bounds,
            text,
            fill,
            fontColor,
            GetBorder(style.Border.TopBorder, style.Border.TopBorderColor),
            GetBorder(style.Border.RightBorder, style.Border.RightBorderColor),
            GetBorder(style.Border.BottomBorder, style.Border.BottomBorderColor),
            GetBorder(style.Border.LeftBorder, style.Border.LeftBorderColor),
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

    private static void WriteRaster(
        string outputPath,
        WorksheetLayout layout,
        SKEncodedImageFormat imageFormat,
        int quality,
        float scale,
        bool transparentBackground)
    {
        var imageInfo = new SKImageInfo(
            Math.Max(1, (int)Math.Ceiling(layout.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(layout.Height * scale)),
            SKColorType.Bgra8888,
            transparentBackground ? SKAlphaType.Premul : SKAlphaType.Opaque);

        using var surface = SKSurface.Create(imageInfo);
        surface.Canvas.Scale(scale);
        DrawLayout(surface.Canvas, layout, transparentBackground ? SKColors.Transparent : White);
        using var image = surface.Snapshot();
        using var data = image.Encode(imageFormat, quality);
        using var stream = File.Create(outputPath);
        data.SaveTo(stream);
    }

    private static void WriteTransparentPng(string outputPath, WorksheetLayout layout)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"badminton-draw-png-{Guid.NewGuid():N}.png");
        try
        {
            foreach (var scale in new[] { 4f, 3.75f, 3.5f, 3.25f, 3f, 2.75f, 2.5f, 2.25f, 2f, 1.75f, 1.5f, 1.25f, 1f })
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
        textBounds.Inflate(-5, -3);
        var lines = cell.WrapText
            ? WrapText(cell.Text, paint, Math.Max(10, textBounds.Width))
            : [TrimWithEllipsis(cell.Text, paint, textBounds.Width)];
        var metrics = paint.FontMetrics;
        var lineHeight = Math.Max(1, metrics.Descent - metrics.Ascent + metrics.Leading);
        var maxLines = Math.Max(1, (int)Math.Floor(textBounds.Height / lineHeight));

        if (lines.Count > maxLines)
        {
            lines = lines.Take(maxLines).ToList();
            lines[^1] = TrimWithEllipsis(lines[^1], paint, textBounds.Width);
        }

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
