namespace BadmintonDraw.Workflows;

public static class WorkflowExportHelpers
{
    public static string GetExtension(WorkflowExportFormat format) => format switch
    {
        WorkflowExportFormat.Png => ".png",
        WorkflowExportFormat.Jpeg => ".jpg",
        WorkflowExportFormat.A4Pdf => ".pdf",
        _ => ".xlsx"
    };

    public static IReadOnlyList<WorkflowExportFormat> Expand(WorkflowExportFormat format) =>
        format == WorkflowExportFormat.All
            ? [WorkflowExportFormat.Excel, WorkflowExportFormat.Jpeg, WorkflowExportFormat.Png, WorkflowExportFormat.A4Pdf]
            : [format];
}
