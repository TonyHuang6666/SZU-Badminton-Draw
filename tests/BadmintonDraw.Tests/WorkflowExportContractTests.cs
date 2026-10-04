using BadmintonDraw.Excel;
using BadmintonDraw.Workflows;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class WorkflowExportContractTests
{
    [Fact]
    public void SharedExportFormatContractSupportsEveryPublicDrawFormat()
    {
        Assert.Equal(
            [WorkflowExportFormat.Excel, WorkflowExportFormat.Jpeg, WorkflowExportFormat.Png, WorkflowExportFormat.A4Pdf],
            WorkflowExportHelpers.Expand(WorkflowExportFormat.All));
        Assert.Equal(".xlsx", WorkflowExportHelpers.GetExtension(WorkflowExportFormat.Excel));
        Assert.Equal(".jpg", WorkflowExportHelpers.GetExtension(WorkflowExportFormat.Jpeg));
        Assert.Equal(".png", WorkflowExportHelpers.GetExtension(WorkflowExportFormat.Png));
        Assert.Equal(".pdf", WorkflowExportHelpers.GetExtension(WorkflowExportFormat.A4Pdf));
    }

    [Fact]
    public void FileNameSanitizerRejectsPathAndControlCharacters()
    {
        Assert.Equal("校长杯_男单_决赛", WorkflowFileNames.Sanitize(" 校长杯/男单:*?\n决赛 "));
    }

    [Fact]
    public void VisualWriterFallsBackToEmbeddedChineseFont()
    {
        var typeface = DrawResultVisualWriter.ResolveTypefaceForText(
            "Definitely Missing Font", false, "14:00-14:20\n深大羽协赛程安排表\nA组128进64第1场");

        Assert.True(DrawResultVisualWriter.HasEmbeddedExportTypeface);
        Assert.Contains("Noto", typeface.FamilyName, StringComparison.OrdinalIgnoreCase);
        Assert.True(typeface.ContainsGlyphs("深大羽协赛程安排表A组128进64第1场"));
    }
}
