using BadmintonDraw.Core;
using BadmintonDraw.Excel;

namespace BadmintonDraw.Workflows;

public sealed class TournamentProgressWorkflow
{
    private readonly TournamentProgressStore _store = new();

    public TournamentProgressState Create(
        string filePath,
        string? sourceInputPath,
        DrawWorkflowResult workflowResult,
        SchedulePlan schedule)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new TournamentProgressSnapshot(
            Guid.NewGuid().ToString("N"),
            WorkflowFileNames.ExtractEventName(sourceInputPath),
            now,
            now,
            sourceInputPath,
            workflowResult.Result,
            workflowResult.Participants,
            workflowResult.ImportWarnings,
            schedule);
        return _store.Create(filePath, snapshot);
    }

    public TournamentProgressState Open(string filePath)
    {
        return _store.Read(filePath);
    }

    public TournamentProgressImportPreview PreviewImport(
        string filePath,
        IEnumerable<string> recordFilePaths)
    {
        return _store.PreviewImport(filePath, recordFilePaths);
    }

    public TournamentProgressImportOutcome Import(
        string filePath,
        IEnumerable<string> recordFilePaths,
        bool allowCorrections = false)
    {
        return _store.Import(filePath, recordFilePaths, allowCorrections);
    }

    public static DrawWorkflowResult BuildDrawWorkflowResult(TournamentProgressState state)
    {
        var warnings = state.Snapshot.ImportWarnings
            .Select(warning => $"{warning.Summary}：{warning.Detail}")
            .ToList();
        return new DrawWorkflowResult(
            state.Snapshot.DrawResult,
            state.Snapshot.Participants,
            warnings,
            state.Snapshot.ImportWarnings);
    }

    public static string BuildDefaultFileName(DrawResult result, string? sourceInputPath)
    {
        var stem = string.Join("_", new[]
        {
            WorkflowFileNames.ExtractEventName(sourceInputPath),
            WorkflowFileNames.GetCompetitionModePart(result.Settings.CompetitionMode),
            WorkflowFileNames.GetEventScalePart(result.Settings.EventKind, result.Audit.ParticipantCount),
            "赛事进度"
        }
            .Select(WorkflowFileNames.Sanitize)
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        return $"{WorkflowFileNames.Limit(stem)}.szbd";
    }

    public static string? GetNextMatchRecordDayLabel(TournamentProgressState state)
    {
        return ScheduleWorkflow.GetNextMatchRecordDayLabel(
            state.Snapshot.Schedule,
            state.BuildCumulativeImportResult());
    }

    public static string BuildImportConfirmation(
        TournamentProgressImportPreview preview,
        string? nextDayLabel)
    {
        var parts = new List<string>();
        if (preview.SelectedImportResult.MissingResultRows.Count > 0)
        {
            var missingSummary = string.IsNullOrWhiteSpace(nextDayLabel)
                ? $"{preview.SelectedImportResult.MissingResultRows.Count} 场未填写胜方，需后续处理"
                : $"{preview.SelectedImportResult.MissingResultRows.Count} 场未填写胜方，将顺延到 {nextDayLabel}";
            parts.Add(missingSummary);
        }

        if (preview.SelectedImportResult.ValidationIssues.Count > 0)
        {
            parts.Add($"{preview.SelectedImportResult.ValidationIssues.Count} 处比分、用时或胜方提醒");
        }

        if (preview.Corrections.Count > 0)
        {
            parts.Add($"{preview.Corrections.Count} 场将更正比分、用时或比赛日，旧值会写入历史记录");
        }

        if (preview.CompatibilityWarnings.Count > 0)
        {
            parts.Add($"{preview.CompatibilityWarnings.Count} 张旧版记录表未带赛事标识");
        }

        if (preview.DuplicateFiles.Count > 0)
        {
            parts.Add($"{preview.DuplicateFiles.Count} 张已导入记录表将自动跳过");
        }

        var missingTitle = string.IsNullOrWhiteSpace(nextDayLabel)
            ? "未填写胜方，需后续处理"
            : $"未填写胜方，将顺延到 {nextDayLabel}";
        var detail = WorkflowIssueText.BuildDetails(
            WorkflowIssueText.BuildSection(
                missingTitle,
                preview.SelectedImportResult.MissingResultRows),
            WorkflowIssueText.BuildSection(
                "比分、用时或胜方提醒，将按已填写胜方推进",
                preview.SelectedImportResult.ValidationIssues),
            WorkflowIssueText.BuildSection(
                "更正比分、用时或比赛日，旧值会写入历史记录",
                preview.Corrections.Select(FormatCorrection)),
            WorkflowIssueText.BuildSection(
                "旧版记录表未带赛事标识",
                preview.CompatibilityWarnings),
            WorkflowIssueText.BuildSection(
                "已导入记录表，将自动跳过",
                preview.DuplicateFiles));
        return $"导入前需要裁判长确认：{string.Join("；", parts)}。{detail}\n\n是否更新赛事存档？";
    }

    private static string FormatCorrection(TournamentProgressCorrection correction)
    {
        return $"{correction.MatchName}："
            + $"原结果 {FormatResult(correction.Existing)}"
            + $" → 新结果 {FormatResult(correction.Replacement)}";
    }

    private static string FormatResult(MatchRecordResult result)
    {
        return $"胜方 {WorkflowIssueText.ValueOrEmpty(result.Winner)}，"
            + $"比分 {WorkflowIssueText.ValueOrEmpty(result.Score)}，"
            + $"用时 {WorkflowIssueText.ValueOrEmpty(result.Duration)}，"
            + $"比赛日 {WorkflowIssueText.ValueOrEmpty(result.DayLabel)}";
    }
}
