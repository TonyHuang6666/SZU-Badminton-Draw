using ClosedXML.Excel;

namespace BadmintonDraw.Excel;

public sealed partial class ScheduleExcelWriter
{
    private const int GridEstimatedCharsPerLine = 14;
    private const double GridLineHeight = 16;
    private const double GridVerticalPadding = 10;
    private const double GridMinBodyRowHeight = 70;

    private static readonly XLColor TitleFill = XLColor.FromHtml("#1F4E78");
    private static readonly XLColor HeaderFill = XLColor.FromHtml("#305496");
    private static readonly XLColor GridFill = XLColor.FromHtml("#F8FAFC");
    private static readonly XLColor PlayInGridFill = XLColor.FromHtml("#FCE4D6");
    private static readonly XLColor Round128GridFill = XLColor.FromHtml("#EAF3FF");
    private static readonly XLColor Round64GridFill = XLColor.FromHtml("#D9EAF7");
    private static readonly XLColor Round32GridFill = XLColor.FromHtml("#E2F0D9");
    private static readonly XLColor Round16GridFill = XLColor.FromHtml("#EDE7F6");
    private static readonly XLColor Round8GridFill = XLColor.FromHtml("#DDEBF7");
    private static readonly XLColor SemiFinalGridFill = XLColor.FromHtml("#E4DFEC");
    private static readonly XLColor FinalGridFill = XLColor.FromHtml("#FFF2CC");
    private static readonly XLColor GrandFinalGridFill = XLColor.FromHtml("#F4CCCC");
    private static readonly XLColor PlacementGridFill = XLColor.FromHtml("#EADCF8");
    private static readonly XLColor[] RoundRobinGridFills =
    [
        XLColor.FromHtml("#EAF3FF"), XLColor.FromHtml("#E2F0D9"),
        XLColor.FromHtml("#FFF2CC"), XLColor.FromHtml("#FCE4D6"),
        XLColor.FromHtml("#EDE7F6"), XLColor.FromHtml("#DDEBF7")
    ];

    private static int EstimateWrappedLineCount(string text, int estimatedCharsPerLine) =>
        text.Split('\n').Sum(line => Math.Max(1,
            (int)Math.Ceiling(line.Sum(ch => ch <= 127 ? 0.55 : 1.0) / estimatedCharsPerLine)));

    private static double CalculateGridBodyRowHeight(int estimatedLineCount) =>
        Math.Max(GridMinBodyRowHeight, GridVerticalPadding + estimatedLineCount * GridLineHeight);

    private static void ApplyGridPhaseStyle(IXLCell cell, string phase)
    {
        cell.Style.Fill.BackgroundColor = GetGridPhaseFill(phase);
        cell.Style.Alignment.WrapText = true;
        if (phase.Contains("决赛", StringComparison.Ordinal)) cell.Style.Font.Bold = true;
    }

    private static XLColor GetGridPhaseFill(string phase)
    {
        if (phase.Contains("首轮", StringComparison.Ordinal)) return PlayInGridFill;
        if (phase.Contains("总决赛", StringComparison.Ordinal)) return GrandFinalGridFill;
        if (phase.Contains("名", StringComparison.Ordinal)) return PlacementGridFill;
        if (phase.Contains("半决赛", StringComparison.Ordinal)) return SemiFinalGridFill;
        if (phase.Contains("决赛", StringComparison.Ordinal)) return FinalGridFill;
        if (TryParseRoundFromPhase(phase, out var from)) return from switch
        {
            >= 128 => Round128GridFill, >= 64 => Round64GridFill, >= 32 => Round32GridFill,
            >= 16 => Round16GridFill, >= 8 => Round8GridFill, _ => GridFill
        };
        return TryParseRoundRobinRound(phase, out var round)
            ? RoundRobinGridFills[(round - 1) % RoundRobinGridFills.Length]
            : GridFill;
    }

    private static bool TryParseRoundFromPhase(string phase, out int from)
    {
        from = 0;
        var marker = phase.IndexOf('进');
        if (marker <= 0) return false;
        var start = marker - 1;
        while (start > 0 && char.IsDigit(phase[start - 1])) start--;
        return int.TryParse(phase[start..marker], out from);
    }

    private static bool TryParseRoundRobinRound(string phase, out int round)
    {
        round = 0;
        return phase.StartsWith('第') && phase.EndsWith('轮') && int.TryParse(phase[1..^1], out round);
    }

    private static void ApplySheetTitleStyle(IXLWorksheet sheet, int lastColumn)
    {
        sheet.Range(1, 1, 1, lastColumn).Style.Fill.BackgroundColor = TitleFill;
        sheet.Range(1, 1, 1, lastColumn).Style.Font.FontColor = XLColor.White;
        sheet.Range(1, 1, 1, lastColumn).Style.Font.Bold = true;
        sheet.Range(1, 1, 1, lastColumn).Style.Font.FontSize = 16;
        sheet.Range(1, 1, 2, lastColumn).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        sheet.Range(1, 1, 2, lastColumn).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        sheet.Row(1).Height = 30;
        sheet.Row(2).Height = 24;
    }

    private static void ApplyTableStyle(IXLRange range)
    {
        range.Style.Font.FontName = "Microsoft YaHei";
        range.Style.Font.FontSize = 10;
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        range.Style.Alignment.WrapText = true;
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.OutsideBorderColor = XLColor.FromHtml("#808080");
        range.Style.Border.InsideBorderColor = XLColor.FromHtml("#808080");
        range.Style.Fill.BackgroundColor = GridFill;
    }
}
