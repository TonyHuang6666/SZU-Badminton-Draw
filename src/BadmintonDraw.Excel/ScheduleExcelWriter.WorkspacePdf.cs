namespace BadmintonDraw.Excel;

public sealed partial class ScheduleExcelWriter
{
    /// <summary>Renders the same typed daily overview and stage colors as the schedule workbook.</summary>
    public void WriteDailySchedulePdf(string outputPath, WorkspaceScheduleExportContext context,
        IReadOnlyList<WorkspaceRecordExportRow> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var temporaryDirectory = Directory.CreateTempSubdirectory("badminton-daily-schedule-").FullName;
        try
        {
            var workbookPath = Path.Combine(temporaryDirectory, "schedule.xlsx");
            WriteDailySchedule(workbookPath, context, rows);
            new DrawResultVisualWriter().Write(Path.GetFullPath(outputPath), workbookPath, "时间场地网格", DrawResultVisualFormat.A4Pdf);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }
}
