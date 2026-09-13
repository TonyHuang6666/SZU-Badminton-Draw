using ClosedXML.Excel;
using SkiaSharp;

namespace BadmintonDraw.Excel;

public sealed partial class DrawResultVisualWriter
{
    /// <summary>Checks the actual cell region/font without the legacy shrink/ellipsis fallback.</summary>
    internal static bool FitsAtDeclaredFontSize(IXLRange range, float leftPageInset, float rightPageInset)
    {
        var address = range.RangeAddress;
        var first = address.FirstAddress; var last = address.LastAddress;
        var used = range.Worksheet.RangeUsed(XLCellsUsedOptions.All)!.RangeAddress;
        var metrics = GridMetrics.Create(range.Worksheet, used.FirstAddress.RowNumber, used.FirstAddress.ColumnNumber,
            used.LastAddress.RowNumber, used.LastAddress.ColumnNumber);
        var originalBounds = metrics.GetRect(first.RowNumber, first.ColumnNumber, last.RowNumber, last.ColumnNumber);
        var source = BuildLayout(range.Worksheet);
        var index = source.Cells.Select((cell, index) => (cell, index)).Single(p => p.cell.Bounds == originalBounds).index;
        // The score PDF stretches the real full-sheet region to A4. Testing the untransformed
        // cell would incorrectly reject captions that fit its actual final printable region.
        var layout = StretchLayoutToPageAspect(source,
            (A4LandscapeHeight - A4Margin * 2) / (A4LandscapeWidth - leftPageInset - rightPageInset));
        var cell = layout.Cells[index];
        var bounds = GetTextBounds(cell);
        var width = Math.Max(10, bounds.Width * TextWidthSafetyFactor);
        using var paint = new SKPaint
        {
            Typeface = ResolveTypefaceForText(cell.FontName, cell.IsBold, cell.Text), TextSize = cell.FontSize, IsAntialias = true
        };
        var lines = cell.WrapText ? WrapText(cell.Text, paint, width) : [cell.Text];
        var font = paint.FontMetrics;
        var lineHeight = Math.Max(1, font.Descent - font.Ascent + font.Leading);
        return lines.Count * lineHeight <= bounds.Height && lines.All(line => paint.MeasureText(line) <= width);
    }

    private static SKRect GetTextBounds(VisualCell cell)
    {
        var bounds = cell.Bounds;
        var horizontalPadding = Math.Min(5f, Math.Max(2f, cell.Bounds.Width * 0.035f));
        var verticalPadding = Math.Min(3f, Math.Max(1.5f, cell.Bounds.Height * 0.08f));
        bounds.Inflate(-horizontalPadding, -verticalPadding);
        return bounds;
    }
}
