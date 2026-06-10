using BadmintonDraw.Core;
using BadmintonDraw.Excel;

namespace BadmintonDraw.Workflows;

public sealed class DrawWorkflow
{
    private readonly DrawService _drawService = new();
    private readonly ParticipantExcelReader _reader = new();
    private readonly DrawResultExcelWriter _writer = new();
    private readonly ParticipantTemplateWriter _templateWriter = new();

    public ParticipantLoadResult LoadParticipants(string inputPath, EventKind preferredEventKind)
    {
        ValidateInputPath(inputPath);
        var detectedEventKind = _reader.DetectEventKind(inputPath, preferredEventKind);
        var importResult = _reader.ReadParticipantsWithWarnings(inputPath, detectedEventKind);
        return new ParticipantLoadResult(
            importResult.Participants,
            detectedEventKind,
            FormatWarningMessages(importResult.Warnings));
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
            FormatWarningMessages(importResult.Warnings));
    }

    public void ExportExcel(string outputPath, DrawWorkflowResult workflowResult)
    {
        _writer.Write(outputPath, workflowResult.Result, workflowResult.Participants);
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
        var sourceName = string.IsNullOrWhiteSpace(inputPath)
            ? "深大羽协"
            : Path.GetFileNameWithoutExtension(inputPath);
        var modeName = result.Settings.IsKnockout ? "淘汰赛" : "循环赛";
        var stem = string.Join("_", new[]
        {
            WorkflowFileNames.Sanitize(sourceName),
            modeName,
            $"{WorkflowLabels.GetEventKindDisplay(result.Settings.EventKind)}{result.Audit.ParticipantCount}人",
            $"{result.Audit.GroupCount}组",
            DateTime.Now.ToString("yyyyMMdd_HHmm")
        }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return $"{stem}.xlsx";
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
    IReadOnlyList<string> WarningMessages);

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
    IReadOnlyList<string> WarningMessages);
