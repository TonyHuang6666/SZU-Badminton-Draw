using BadmintonDraw.Core;
using BadmintonDraw.Excel;

namespace BadmintonDraw.Workflows;

public sealed class DrawWorkflow
{
    private const string BracketSheetName = "对阵表";

    private readonly DrawService _drawService = new();
    private readonly ParticipantExcelReader _reader = new();
    private readonly DrawResultExcelWriter _writer = new();
    private readonly DrawResultVisualWriter _visualWriter = new();
    private readonly ParticipantTemplateWriter _templateWriter = new();

    public ParticipantLoadResult LoadParticipants(string inputPath, EventKind preferredEventKind)
    {
        ValidateInputPath(inputPath);
        var detectedEventKind = _reader.DetectEventKind(inputPath, preferredEventKind);
        var importResult = _reader.ReadParticipantsWithWarnings(inputPath, detectedEventKind);
        return new ParticipantLoadResult(
            importResult.Participants,
            detectedEventKind,
            FormatWarningMessages(importResult.Warnings),
            importResult.Warnings);
    }

    public EventKind DetectEventKind(string inputPath, EventKind preferredEventKind)
    {
        ValidateInputPath(inputPath);
        return _reader.DetectEventKind(inputPath, preferredEventKind);
    }

    public DrawWorkflowResult Generate(DrawWorkflowRequest request)
    {
        ValidateInputPath(request.InputPath);
        var importResult = _reader.ReadParticipantsWithWarnings(request.InputPath, request.EventKind);
        var settings = new DrawSettings(
            request.CompetitionMode,
            request.EventKind,
            request.GroupCount,
            request.RandomSeed,
            KnockoutGoal: request.KnockoutGoal,
            PlacementPlayoff: request.PlacementPlayoff);
        var result = _drawService.Generate(importResult.Participants, settings);
        return new DrawWorkflowResult(
            result,
            importResult.Participants,
            FormatWarningMessages(importResult.Warnings),
            importResult.Warnings);
    }

    public void ExportExcel(string outputPath, DrawWorkflowResult workflowResult)
    {
        _writer.Write(outputPath, workflowResult.Result, workflowResult.Participants);
    }

    public IReadOnlyList<string> ExportFiles(
        string selectedPath,
        WorkflowExportFormat exportFormat,
        DrawWorkflowResult workflowResult,
        DrawResultVisualOptions? visualOptions = null)
    {
        return ExportFromWorkbook(
            selectedPath,
            exportFormat,
            BracketSheetName,
            path => _writer.Write(path, workflowResult.Result, workflowResult.Participants),
            _visualWriter,
            visualOptions);
    }

    public void WriteTemplate(string outputPath)
    {
        _templateWriter.Write(outputPath);
    }

    public static string GenerateSeed()
    {
        return $"SZUBA-{DateTime.Now:yyyyMMdd-HHmmss}-{Random.Shared.Next(1000, 9999)}";
    }

    public static string BuildDefaultDrawExcelFileName(DrawResult result, string? inputPath)
    {
        return BuildDefaultDrawFileName(result, inputPath, WorkflowExportFormat.Excel);
    }

    public static string BuildDefaultDrawFileName(
        DrawResult result,
        string? inputPath,
        WorkflowExportFormat format)
    {
        var parts = new List<string>
        {
            WorkflowFileNames.ExtractEventName(inputPath),
            WorkflowFileNames.GetCompetitionModePart(result.Settings.CompetitionMode),
            WorkflowFileNames.GetEventScalePart(result.Settings.EventKind, result.Audit.ParticipantCount),
            $"{result.Audit.GroupCount}组",
        };

        var knockoutGoalPart = WorkflowFileNames.GetKnockoutGoalPart(result.Settings);
        if (!string.IsNullOrWhiteSpace(knockoutGoalPart))
        {
            parts.Add(knockoutGoalPart);
        }

        var placementPlayoffPart = WorkflowFileNames.GetPlacementPlayoffPart(result.Settings);
        if (!string.IsNullOrWhiteSpace(placementPlayoffPart))
        {
            parts.Add(placementPlayoffPart);
        }

        parts.Add(result.Audit.GeneratedAt.LocalDateTime.ToString("yyyyMMdd_HHmm"));
        parts.Add($"seed{WorkflowFileNames.GetSeedTail(result.Audit.RandomSeed)}");

        var stem = string.Join("_", parts.Select(WorkflowFileNames.Sanitize).Where(part => !string.IsNullOrWhiteSpace(part)));
        return $"{WorkflowFileNames.Limit(stem)}{WorkflowExportHelpers.GetExtension(format)}";
    }

    internal static IReadOnlyList<string> ExportFromWorkbook(
        string selectedPath,
        WorkflowExportFormat selectedFormat,
        string visualSheetName,
        Action<string> writeWorkbook,
        DrawResultVisualWriter visualWriter,
        DrawResultVisualOptions? visualOptions = null)
    {
        var formats = WorkflowExportHelpers.Expand(selectedFormat);
        var outputPaths = new List<string>();
        var tempExcelPath = Path.Combine(Path.GetTempPath(), $"badminton-draw-export-{Guid.NewGuid():N}.xlsx");
        string? sourceExcelPath = null;
        try
        {
            if (formats.Contains(WorkflowExportFormat.Excel))
            {
                var excelPath = WorkflowExportHelpers.BuildOutputPath(selectedPath, WorkflowExportFormat.Excel);
                writeWorkbook(excelPath);
                sourceExcelPath = excelPath;
                outputPaths.Add(excelPath);
            }

            var visualFormats = formats.Where(format => format != WorkflowExportFormat.Excel).ToList();
            if (visualFormats.Count > 0)
            {
                if (sourceExcelPath is null)
                {
                    writeWorkbook(tempExcelPath);
                    sourceExcelPath = tempExcelPath;
                }

                foreach (var format in visualFormats)
                {
                    var outputPath = WorkflowExportHelpers.BuildOutputPath(selectedPath, format);
                    visualWriter.Write(outputPath, sourceExcelPath, visualSheetName, ToVisualFormat(format), visualOptions);
                    outputPaths.Add(outputPath);
                }
            }

            return outputPaths;
        }
        finally
        {
            if (sourceExcelPath == tempExcelPath && File.Exists(tempExcelPath))
            {
                File.Delete(tempExcelPath);
            }
        }
    }

    private static DrawResultVisualFormat ToVisualFormat(WorkflowExportFormat format)
    {
        return format switch
        {
            WorkflowExportFormat.Png => DrawResultVisualFormat.Png,
            WorkflowExportFormat.Jpeg => DrawResultVisualFormat.Jpeg,
            WorkflowExportFormat.A4Pdf => DrawResultVisualFormat.A4Pdf,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Excel 导出不需要视觉格式。")
        };
    }

    private static void ValidateInputPath(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new DrawValidationException("请先选择参赛名单 Excel。");
        }
    }

    private static IReadOnlyList<string> FormatWarningMessages(IReadOnlyList<ParticipantImportWarning> warnings)
    {
        return warnings
            .Select(warning => $"{warning.Summary}：{warning.Detail}")
            .ToList();
    }
}

public sealed record ParticipantLoadResult(
    IReadOnlyList<DrawParticipant> Participants,
    EventKind DetectedEventKind,
    IReadOnlyList<string> WarningMessages,
    IReadOnlyList<ParticipantImportWarning> ImportWarnings);

public sealed record DrawWorkflowRequest(
    string InputPath,
    CompetitionMode CompetitionMode,
    EventKind EventKind,
    int GroupCount,
    string RandomSeed,
    KnockoutGoal KnockoutGoal,
    PlacementPlayoff PlacementPlayoff);

public sealed record DrawWorkflowResult(
    DrawResult Result,
    IReadOnlyList<DrawParticipant> Participants,
    IReadOnlyList<string> WarningMessages,
    IReadOnlyList<ParticipantImportWarning> ImportWarnings);

public enum WorkflowExportFormat
{
    Excel,
    Jpeg,
    Png,
    A4Pdf,
    All
}

public static class WorkflowExportHelpers
{
    public static string GetExtension(WorkflowExportFormat format)
    {
        return format switch
        {
            WorkflowExportFormat.Png => ".png",
            WorkflowExportFormat.Jpeg => ".jpg",
            WorkflowExportFormat.A4Pdf => ".pdf",
            _ => ".xlsx"
        };
    }

    public static IReadOnlyList<WorkflowExportFormat> Expand(WorkflowExportFormat format)
    {
        return format == WorkflowExportFormat.All
            ? [WorkflowExportFormat.Excel, WorkflowExportFormat.Jpeg, WorkflowExportFormat.Png, WorkflowExportFormat.A4Pdf]
            : [format];
    }

    public static string BuildOutputPath(string selectedPath, WorkflowExportFormat format)
    {
        return Path.ChangeExtension(selectedPath, GetExtension(format));
    }
}
