using BadmintonDraw.Core;
using BadmintonDraw.Excel;

namespace BadmintonDraw.Workflows;

public sealed class TournamentProgressWorkflow
{
    private readonly TournamentProgressStore _store = new();
    private readonly ScheduleWorkflow _scheduleWorkflow = new();

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

    public TournamentProgressPackageExportResult ExportNextDayPackage(
        TournamentProgressState state,
        string outputDirectory,
        bool includePrintablePdf = true,
        DrawResultVisualOptions? visualOptions = null)
    {
        var nextDayLabel = GetNextMatchRecordDayLabel(state);
        if (string.IsNullOrWhiteSpace(nextDayLabel))
        {
            throw new InvalidOperationException("当前赛事没有下一比赛日可导出。");
        }

        return ExportDayPackage(state, nextDayLabel, outputDirectory, includePrintablePdf, visualOptions);
    }

    public TournamentProgressPackageExportResult ExportFirstDayPackage(
        TournamentProgressState state,
        string outputDirectory,
        bool includePrintablePdf = true,
        DrawResultVisualOptions? visualOptions = null)
    {
        var firstDayLabel = ScheduleWorkflow.GetFirstRecordDayLabel(state.Snapshot.Schedule);
        if (string.IsNullOrWhiteSpace(firstDayLabel))
        {
            throw new InvalidOperationException("当前赛程没有可导出的比赛日。");
        }

        var initialState = state with
        {
            Results = new Dictionary<string, MatchRecordResult>(StringComparer.Ordinal),
            PendingMatchNames = [],
            ProcessedDayLabels = []
        };
        return ExportDayPackage(initialState, firstDayLabel, outputDirectory, includePrintablePdf, visualOptions);
    }

    private TournamentProgressPackageExportResult ExportDayPackage(
        TournamentProgressState state,
        string dayLabel,
        string outputDirectory,
        bool includePrintablePdf,
        DrawResultVisualOptions? visualOptions)
    {
        Directory.CreateDirectory(outputDirectory);

        var outputPaths = new List<string>();
        var schedule = state.Snapshot.Schedule;
        var completedResults = state.Results;
        var carryOverMatchNames = state.PendingMatchNames.ToHashSet(StringComparer.Ordinal);
        var workflowResult = BuildDrawWorkflowResult(state);
        var scoreSheetProjectName = BuildScoreSheetProjectName(state);

        var recordPath = Path.Combine(outputDirectory, ScheduleWorkflow.BuildDefaultMatchRecordFileName(dayLabel));
        _scheduleWorkflow.ExportMatchRecord(
            recordPath,
            schedule,
            dayLabel,
            completedResults,
            carryOverMatchNames,
            state.Snapshot.TournamentId);
        outputPaths.Add(recordPath);

        var dailySchedulePath = Path.Combine(outputDirectory, ScheduleWorkflow.BuildDefaultDailyScheduleFileName(dayLabel));
        outputPaths.AddRange(_scheduleWorkflow.ExportDailyScheduleFiles(
            dailySchedulePath,
            WorkflowExportFormat.Excel,
            schedule,
            dayLabel,
            completedResults,
            carryOverMatchNames,
            state.Snapshot.TournamentId));
        if (includePrintablePdf)
        {
            outputPaths.AddRange(_scheduleWorkflow.ExportDailyScheduleFiles(
                Path.ChangeExtension(dailySchedulePath, WorkflowExportHelpers.GetExtension(WorkflowExportFormat.A4Pdf)),
                WorkflowExportFormat.A4Pdf,
                schedule,
                dayLabel,
                completedResults,
                carryOverMatchNames,
                state.Snapshot.TournamentId));
        }

        var timedBracketPath = Path.Combine(outputDirectory, ScheduleWorkflow.BuildDefaultDailyTimedBracketFileName(dayLabel));
        outputPaths.AddRange(_scheduleWorkflow.ExportTimedBracketFilesAtPath(
            timedBracketPath,
            WorkflowExportFormat.Excel,
            workflowResult,
            schedule,
            visualOptions));
        if (includePrintablePdf)
        {
            outputPaths.AddRange(_scheduleWorkflow.ExportTimedBracketFilesAtPath(
                Path.ChangeExtension(timedBracketPath, WorkflowExportHelpers.GetExtension(WorkflowExportFormat.A4Pdf)),
                WorkflowExportFormat.A4Pdf,
                workflowResult,
                schedule,
                visualOptions));
        }

        var individualScorePath = Path.Combine(outputDirectory, ScheduleWorkflow.BuildDefaultIndividualScoreSheetFileName(dayLabel));
        _scheduleWorkflow.ExportIndividualScoreSheetPdf(
            individualScorePath,
            schedule,
            scoreSheetProjectName,
            dayLabel,
            completedResults,
            carryOverMatchNames);
        outputPaths.Add(individualScorePath);

        if (IsTeamEvent(state))
        {
            var teamScorePath = Path.Combine(outputDirectory, ScheduleWorkflow.BuildDefaultTeamScoreSheetFileName(dayLabel));
            _scheduleWorkflow.ExportTeamScoreSheets(
                teamScorePath,
                schedule,
                dayLabel,
                completedResults,
                carryOverMatchNames);
            outputPaths.Add(teamScorePath);
        }

        return new TournamentProgressPackageExportResult(
            outputDirectory,
            dayLabel,
            outputPaths);
    }

    public TournamentProgressPackageExportResult ExportNextDayPackage(
        string outputDirectory,
        string? sourceInputPath,
        DrawWorkflowResult workflowResult,
        SchedulePlan schedule,
        MatchRecordImportResult importResult,
        bool includePrintablePdf = true,
        DrawResultVisualOptions? visualOptions = null)
    {
        var now = DateTimeOffset.UtcNow;
        var state = new TournamentProgressState(
            new TournamentProgressSnapshot(
                "",
                WorkflowFileNames.ExtractEventName(sourceInputPath),
                now,
                now,
                sourceInputPath,
                workflowResult.Result,
                workflowResult.Participants,
                workflowResult.ImportWarnings,
                schedule),
            importResult.Results,
            importResult.PendingMatchNames,
            importResult.DayLabels,
            []);
        return ExportNextDayPackage(state, outputDirectory, includePrintablePdf, visualOptions);
    }

    public TournamentProgressPackageExportResult ExportFirstDayPackage(
        string outputDirectory,
        string? sourceInputPath,
        DrawWorkflowResult workflowResult,
        SchedulePlan schedule,
        bool includePrintablePdf = true,
        DrawResultVisualOptions? visualOptions = null)
    {
        var now = DateTimeOffset.UtcNow;
        var state = new TournamentProgressState(
            new TournamentProgressSnapshot(
                "",
                WorkflowFileNames.ExtractEventName(sourceInputPath),
                now,
                now,
                sourceInputPath,
                workflowResult.Result,
                workflowResult.Participants,
                workflowResult.ImportWarnings,
                schedule),
            new Dictionary<string, MatchRecordResult>(StringComparer.Ordinal),
            [],
            [],
            []);
        return ExportFirstDayPackage(state, outputDirectory, includePrintablePdf, visualOptions);
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

    private static bool IsTeamEvent(TournamentProgressState state)
    {
        return state.Snapshot.DrawResult.Settings.EventKind == EventKind.Team
            || state.Snapshot.DrawResult.Settings.CompetitionMode is CompetitionMode.TeamKnockout or CompetitionMode.TeamRoundRobin;
    }

    private static string BuildScoreSheetProjectName(TournamentProgressState state)
    {
        var eventKind = WorkflowLabels.GetEventKindDisplay(state.Snapshot.DrawResult.Settings.EventKind);
        var eventName = state.Snapshot.EventName?.Trim();
        if (string.IsNullOrWhiteSpace(eventName) || eventName == "深大羽协")
        {
            return eventKind;
        }

        return eventName;
    }
}

public sealed record TournamentProgressPackageExportResult(
    string OutputDirectory,
    string DayLabel,
    IReadOnlyList<string> OutputPaths);
