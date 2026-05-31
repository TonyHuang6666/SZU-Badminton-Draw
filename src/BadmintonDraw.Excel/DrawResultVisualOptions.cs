namespace BadmintonDraw.Excel;

public sealed record DrawResultVisualOptions(int PdfRows = 1, int PdfColumns = 1)
{
    public int PdfRows { get; } = Math.Max(1, PdfRows);

    public int PdfColumns { get; } = Math.Max(1, PdfColumns);
}
