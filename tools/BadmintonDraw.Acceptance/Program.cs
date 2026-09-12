using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using BadmintonDraw.Core;
using BadmintonDraw.Excel;
using BadmintonDraw.Workflows;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;

return new AcceptanceRunner(args).Execute();

internal sealed class AcceptanceRunner
{
    private const int RefereeCount = 12;
    private readonly string _repositoryRoot;
    private readonly string _outputRoot;
    private readonly Stopwatch _totalStopwatch = Stopwatch.StartNew();
    private readonly AcceptanceSummary _summary;

    public AcceptanceRunner(string[] args)
    {
        _repositoryRoot = FindRepositoryRoot();
        _outputRoot = ResolveOutputRoot(args, _repositoryRoot);
        Directory.CreateDirectory(_outputRoot);
        _summary = new AcceptanceSummary
        {
            StartedAt = DateTimeOffset.UtcNow,
            RepositoryRoot = _repositoryRoot,
            ArtifactRoot = _outputRoot,
            GitCommit = TryRun("git", "rev-parse HEAD", _repositoryRoot).Trim(),
            DotnetVersion = RuntimeInformation.FrameworkDescription,
            OperatingSystem = Environment.OSVersion.ToString()
        };
    }

    public int Execute()
    {
        try
        {
            Console.WriteLine($"Acceptance output: {_outputRoot}");
            var contexts = RunSingleEventSetup();
            RunCrossEventWorkflow(contexts);
            RunResultImportAndRecovery(contexts);
            ValidateGeneratedDocuments();
            _summary.Success = true;
            Console.WriteLine("v4.6.0 acceptance workflow completed successfully.");
            return 0;
        }
        catch (Exception exception)
        {
            _summary.Failures.Add(exception.ToString());
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            _totalStopwatch.Stop();
            _summary.CompletedAt = DateTimeOffset.UtcNow;
            _summary.ElapsedMilliseconds = _totalStopwatch.ElapsedMilliseconds;
            _summary.ArtifactFiles = Directory
                .EnumerateFiles(_outputRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(_outputRoot, path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
            var resultPath = Path.Combine(_outputRoot, "acceptance-results.json");
            File.WriteAllText(
                resultPath,
                JsonSerializer.Serialize(_summary, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Acceptance results: {resultPath}");
        }
    }

    private List<EventRunContext> RunSingleEventSetup()
    {
        var definitions = new[]
        {
            new EventDefinition(
                "large-doubles-knockout",
                "samples/深大羽协虚拟双打参赛名单_159人.xlsx",
                ExpectedParticipantCount: 159,
                PreferredEventKind: EventKind.Doubles,
                CompetitionMode: CompetitionMode.SinglesKnockout,
                GroupCount: 8,
                KnockoutGoal: KnockoutGoal.Champion,
                PlacementPlayoff: PlacementPlayoff.ThirdToEighth,
                Seed: "v4.6.0-large-doubles"),
            new EventDefinition(
                "small-doubles-round-robin",
                "samples/深圳大学虚拟双打参赛名单_29人.xlsx",
                ExpectedParticipantCount: 29,
                PreferredEventKind: EventKind.Doubles,
                CompetitionMode: CompetitionMode.SinglesRoundRobin,
                GroupCount: 4,
                KnockoutGoal: KnockoutGoal.Champion,
                PlacementPlayoff: PlacementPlayoff.None,
                Seed: "v4.6.0-small-doubles"),
            new EventDefinition(
                "college-team-round-robin",
                "samples/深圳大学虚拟29学院参赛名单.xlsx",
                ExpectedParticipantCount: 29,
                PreferredEventKind: EventKind.Team,
                CompetitionMode: CompetitionMode.TeamRoundRobin,
                GroupCount: 4,
                KnockoutGoal: KnockoutGoal.Champion,
                PlacementPlayoff: PlacementPlayoff.None,
                Seed: "v4.6.0-college-team")
        };

        var drawWorkflow = new DrawWorkflow();
        var scheduleWorkflow = new ScheduleWorkflow();
        var progressWorkflow = new TournamentProgressWorkflow();
        var contexts = new List<EventRunContext>();

        foreach (var definition in definitions)
        {
            var stopwatch = Stopwatch.StartNew();
            Console.WriteLine($"Preparing {definition.Slug}...");
            var samplePath = Path.Combine(_repositoryRoot, definition.SampleRelativePath);
            Require(File.Exists(samplePath), $"Sample file does not exist: {samplePath}");

            var loaded = drawWorkflow.LoadParticipants(samplePath, definition.PreferredEventKind);
            Require(loaded.DetectedEventKind == definition.PreferredEventKind,
                $"{definition.Slug}: detected {loaded.DetectedEventKind}, expected {definition.PreferredEventKind}.");
            Require(loaded.Participants.Count == definition.ExpectedParticipantCount,
                $"{definition.Slug}: loaded {loaded.Participants.Count}, expected {definition.ExpectedParticipantCount}.");
            var duplicateDisplayNames = loaded.Participants
                .GroupBy(participant => participant.DisplayName.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => $"{group.Key} x{group.Count()}")
                .ToList();
            Console.WriteLine(
                $"{definition.Slug}: participants={loaded.Participants.Count}, "
                + $"duplicate display names={duplicateDisplayNames.Count} "
                + $"({string.Join(", ", duplicateDisplayNames.Take(8))}).");

            var request = new DrawWorkflowRequest(
                samplePath,
                definition.CompetitionMode,
                loaded.DetectedEventKind,
                definition.GroupCount,
                definition.Seed,
                definition.KnockoutGoal,
                definition.PlacementPlayoff);
            var draw = drawWorkflow.GenerateFromParticipants(request, loaded.Participants, loaded.ImportWarnings);
            var repeatedDraw = drawWorkflow.GenerateFromParticipants(request, loaded.Participants, loaded.ImportWarnings);
            Require(draw.Result.Audit.InputHash == repeatedDraw.Result.Audit.InputHash,
                $"{definition.Slug}: repeated draw input hash changed.");
            Require(BuildDrawSignature(draw.Result) == BuildDrawSignature(repeatedDraw.Result),
                $"{definition.Slug}: repeated draw placement changed with the same seed.");

            var settings = CreateScheduleSettings(
                strategy: definition.CompetitionMode == CompetitionMode.SinglesKnockout
                    ? ScheduleAutoSchedulingStrategy.Compact
                    : ScheduleAutoSchedulingStrategy.BalancedRelaxed,
                includeUnavailableCourtWindow: definition.CompetitionMode != CompetitionMode.SinglesKnockout);
            var schedule = scheduleWorkflow.Generate(draw.Result, settings);
            ValidateSchedule(definition.Slug, schedule);

            var eventDirectory = Path.Combine(_outputRoot, "events", definition.Slug);
            Directory.CreateDirectory(eventDirectory);
            var archivePath = Path.Combine(eventDirectory, $"{definition.Slug}.szbd");
            var state = progressWorkflow.Create(archivePath, samplePath, draw, schedule);
            var reopened = progressWorkflow.Open(archivePath);
            Require(!string.IsNullOrWhiteSpace(reopened.Snapshot.TournamentId),
                $"{definition.Slug}: tournament ID is empty after reopen.");
            Require(reopened.Snapshot.DrawResult.Audit.InputHash == draw.Result.Audit.InputHash,
                $"{definition.Slug}: draw hash changed after archive reopen.");
            Require(BuildScheduleSignature(reopened.Snapshot.Schedule) == BuildScheduleSignature(schedule),
                $"{definition.Slug}: schedule changed after archive reopen.");
            Require(reopened.Results.Count == 0 && reopened.ImportLogs.Count == 0,
                $"{definition.Slug}: new archive unexpectedly contains imported results.");

            stopwatch.Stop();
            var constraintReport = new ScheduleConstraintAnalyzer().Analyze(schedule);
            var result = new EventAcceptanceResult
            {
                Slug = definition.Slug,
                SamplePath = definition.SampleRelativePath,
                EventKind = loaded.DetectedEventKind.ToString(),
                CompetitionMode = definition.CompetitionMode.ToString(),
                ParticipantCount = loaded.Participants.Count,
                ImportWarningCount = loaded.ImportWarnings.Count,
                ImportWarnings = loaded.ImportWarnings
                    .Select(warning => $"{warning.Summary}: {warning.Detail}")
                    .ToList(),
                GroupCount = draw.Result.Audit.GroupCount,
                DrawInputHash = draw.Result.Audit.InputHash,
                MatchCount = schedule.Matches.Count,
                DayCount = schedule.DayCount,
                ScheduleSevereCount = constraintReport.SevereCount,
                ScheduleWarningCount = constraintReport.WarningCount,
                InitialConstraintIssues = constraintReport.Issues
                    .Where(issue => issue.Severity != ScheduleConstraintSeverity.Notice)
                    .Select(FormatConstraintIssue)
                    .ToList(),
                UnavailableCourtBlockCount = settings.Days.Sum(day => day.UnavailableCourtWindows?.Count ?? 0),
                TournamentId = state.Snapshot.TournamentId,
                ArchivePath = Path.GetRelativePath(_outputRoot, archivePath),
                SetupElapsedMilliseconds = stopwatch.ElapsedMilliseconds
            };
            _summary.Events.Add(result);
            contexts.Add(new EventRunContext(definition, draw, archivePath, reopened, result));
        }

        Require(contexts.Sum(context => context.Result.UnavailableCourtBlockCount) > 0,
            "No acceptance event exercised an unavailable-court window.");

        return contexts;
    }

    private void RunCrossEventWorkflow(IReadOnlyList<EventRunContext> contexts)
    {
        var stopwatch = Stopwatch.StartNew();
        Console.WriteLine("Building and adjusting the cross-event schedule...");
        var workflow = new CrossEventConflictWorkflow();
        var archivePaths = contexts.Select(context => context.ArchivePath).ToList();
        var board = workflow.LoadScheduleBoard(archivePaths, minimumRestMinutes: 30);
        Require(board.Items.Count == contexts.Sum(context => context.State.Snapshot.Schedule.Matches.Count),
            "Cross-event board lost matches while loading archives.");

        var options = workflow.CreateSchedulingOptions(board, CrossEventSchedulingStrategy.Compact) with
        {
            RefereeCount = RefereeCount
        };
        var adjusted = workflow.AutoAdjustScheduleBoard(board, options);
        Require(adjusted.RemainingBlockingConflictItemCount == 0,
            $"Cross-event schedule still has {adjusted.RemainingBlockingConflictItemCount} blocking items. "
            + $"Placement messages: {string.Join(" | ", adjusted.Messages)}. "
            + $"Blocking issues: {string.Join(" | ", adjusted.Board.Report.Issues.Where(issue => issue.Severity != CrossEventConflictSeverity.Notice).Select(issue => issue.Detail))}.");
        Require(adjusted.Board.Report.SevereCount == 0 && adjusted.Board.Report.WarningCount == 0,
            $"Cross-event report still has severe/warning issues: {adjusted.Board.Report.SevereCount}/{adjusted.Board.Report.WarningCount}.");

        var save = workflow.SaveScheduleBoard(adjusted.Board);
        Require(save.UpdatedPaths.Count == contexts.Count, "Cross-event save did not update every archive.");
        Require(save.BackupPaths.Count == contexts.Count && save.BackupPaths.All(File.Exists),
            "Cross-event save did not retain one readable backup path per archive.");

        var progressWorkflow = new TournamentProgressWorkflow();
        foreach (var source in adjusted.Board.Sources)
        {
            var context = contexts.Single(item =>
                string.Equals(Path.GetFullPath(item.ArchivePath), Path.GetFullPath(source.SourcePath), StringComparison.OrdinalIgnoreCase));
            var reopened = progressWorkflow.Open(context.ArchivePath);
            var actualById = reopened.Snapshot.Schedule.Matches.ToDictionary(match => match.MatchId, StringComparer.Ordinal);
            foreach (var expected in source.Matches)
            {
                if (!actualById.TryGetValue(expected.MatchId, out var actual))
                {
                    throw new InvalidOperationException(
                        $"{context.Definition.Slug}: saved schedule is missing {expected.MatchId}.");
                }

                Require(actual.DayLabel == expected.DayLabel
                        && actual.StartTime == expected.StartTime
                        && actual.EndTime == expected.EndTime
                        && string.Equals(actual.Court, expected.Court, StringComparison.Ordinal),
                    $"{context.Definition.Slug}: saved placement differs for {expected.MatchId}.");
            }

            context.State = reopened;
            context.Result.CrossEventScheduleSaved = true;
            var postMergeConstraints = new ScheduleConstraintAnalyzer().Analyze(reopened.Snapshot.Schedule);
            context.Result.PostMergeScheduleSevereCount = postMergeConstraints.SevereCount;
            context.Result.PostMergeScheduleWarningCount = postMergeConstraints.WarningCount;
            context.Result.PostMergeConstraintIssues = postMergeConstraints.Issues
                .Where(issue => issue.Severity != ScheduleConstraintSeverity.Notice)
                .Select(FormatConstraintIssue)
                .ToList();
        }

        var materialsRoot = Path.Combine(_outputRoot, "merged-materials");
        var materials = workflow.ExportMergedScheduleMaterials(adjusted.Board, materialsRoot);
        Require(materials.OutputPaths.Count > 0 && materials.OutputPaths.All(File.Exists),
            "Merged materials export returned missing output files.");
        Require(materials.DayLabels.Count == adjusted.Board.Items
            .Select(item => item.DayLabel)
            .Distinct(StringComparer.Ordinal)
            .Count(), "Merged materials day count differs from the board.");

        stopwatch.Stop();
        _summary.CrossEvent = new CrossEventAcceptanceResult
        {
            SourceCount = adjusted.Board.Sources.Count,
            TotalMatchCount = adjusted.Board.Items.Count,
            MultiEventPlayerCount = adjusted.Board.MultiEventPlayerCount,
            InitialSevereCount = board.Report.SevereCount,
            InitialWarningCount = board.Report.WarningCount,
            MovedCount = adjusted.MovedCount,
            RemainingBlockingConflictItemCount = adjusted.RemainingBlockingConflictItemCount,
            FinalSevereCount = adjusted.Board.Report.SevereCount,
            FinalWarningCount = adjusted.Board.Report.WarningCount,
            BackupCount = save.BackupPaths.Count,
            MaterialFileCount = materials.OutputPaths.Count,
            MaterialDirectory = Path.GetRelativePath(_outputRoot, materials.OutputDirectory),
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
        };
    }

    private void RunResultImportAndRecovery(IReadOnlyList<EventRunContext> contexts)
    {
        var workflow = new TournamentProgressWorkflow();
        var totalResolvedReferences = 0;

        for (var index = 0; index < contexts.Count; index++)
        {
            var context = contexts[index];
            var stopwatch = Stopwatch.StartNew();
            Console.WriteLine($"Importing deterministic results for {context.Definition.Slug}...");
            var package = workflow.ExportFirstDayPackage(
                context.State,
                Path.Combine(_outputRoot, "event-materials", context.Definition.Slug, "first-day"),
                includePrintablePdf: true,
                visualOptions: GetVisualOptions(context.Definition));
            Require(package.OutputPaths.All(File.Exists), $"{context.Definition.Slug}: first-day package contains missing files.");
            var recordPath = FindRecordWorkbook(package.OutputPaths);
            var filledCount = FillDeterministicResults(recordPath, context.State);
            Require(filledCount > 0, $"{context.Definition.Slug}: no first-day results were filled.");

            if (index == 0)
            {
                ValidateBackupRestore(workflow, context, recordPath);
            }

            var preview = workflow.PreviewImport(context.ArchivePath, [recordPath]);
            Require(preview.CompatibilityWarnings.Count == 0,
                $"{context.Definition.Slug}: record import has identity compatibility warnings.");
            Require(preview.NewResultCount == filledCount && preview.FilesToImport == 1,
                $"{context.Definition.Slug}: preview result count differs from filled rows.");
            var imported = workflow.Import(context.ArchivePath, [recordPath]);
            Require(imported.BackupPath is not null && File.Exists(imported.BackupPath),
                $"{context.Definition.Slug}: import backup was not retained.");
            Require(imported.State.Results.Count >= filledCount,
                $"{context.Definition.Slug}: imported state has fewer results than the record workbook.");

            var duplicate = workflow.PreviewImport(context.ArchivePath, [recordPath]);
            Require(duplicate.DuplicateFiles.Count == 1 && duplicate.FilesToImport == 0,
                $"{context.Definition.Slug}: duplicate import was not recognized.");

            var resolvedReferenceCount = 0;
            if (imported.State.RemainingMatchCount > 0)
            {
                var nextPackage = workflow.ExportNextDayPackage(
                    imported.State,
                    Path.Combine(_outputRoot, "event-materials", context.Definition.Slug, "next-day"),
                    includePrintablePdf: true,
                    visualOptions: GetVisualOptions(context.Definition));
                Require(nextPackage.OutputPaths.All(File.Exists),
                    $"{context.Definition.Slug}: next-day package contains missing files.");
                resolvedReferenceCount = VerifyNextDayResolution(
                    FindRecordWorkbook(nextPackage.OutputPaths),
                    imported.State,
                    nextPackage.DayLabel);
                totalResolvedReferences += resolvedReferenceCount;
            }

            if (index == 0)
            {
                ValidateConflictAndCorrection(workflow, context, recordPath);
                ValidateCorruptCopyRejection(context.ArchivePath);
                imported = new TournamentProgressImportOutcome(
                    workflow.Open(context.ArchivePath),
                    imported.Preview,
                    imported.BackupPath);
            }

            context.State = imported.State;
            context.Result.FirstDayLabel = package.DayLabel;
            context.Result.FirstDayFilledResultCount = filledCount;
            context.Result.ImportedResultCount = imported.State.Results.Count;
            context.Result.DuplicateImportDetected = true;
            context.Result.NextDayResolvedReferenceCount = resolvedReferenceCount;
            context.Result.ImportLogCount = imported.State.ImportLogs.Count;
            stopwatch.Stop();
            context.Result.ImportElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
        }

        Require(totalResolvedReferences > 0,
            "No next-day outcome reference was resolved from imported results across all events.");
    }

    private void ValidateBackupRestore(
        TournamentProgressWorkflow workflow,
        EventRunContext context,
        string recordPath)
    {
        var probePath = Path.Combine(Path.GetDirectoryName(context.ArchivePath)!, "recovery-probe.szbd");
        File.Copy(context.ArchivePath, probePath, overwrite: false);
        var before = workflow.Open(probePath);
        var imported = workflow.Import(probePath, [recordPath]);
        Require(imported.BackupPath is not null && File.Exists(imported.BackupPath),
            "Recovery probe import did not create a backup.");
        Require(imported.State.Results.Count > 0, "Recovery probe did not import results.");

        var restored = new TournamentProgressStore().RestoreBackup(probePath, imported.BackupPath!);
        var reopened = workflow.Open(probePath);
        Require(restored.Snapshot.TournamentId == before.Snapshot.TournamentId,
            "Restore changed tournament identity.");
        Require(restored.Snapshot.DrawResult.Audit.InputHash == before.Snapshot.DrawResult.Audit.InputHash,
            "Restore changed draw identity.");
        Require(restored.Snapshot.Participants.SequenceEqual(before.Snapshot.Participants),
            "Restore changed participants.");
        Require(BuildScheduleSignature(restored.Snapshot.Schedule) == BuildScheduleSignature(before.Snapshot.Schedule),
            "Restore did not retain the cross-event-adjusted schedule.");
        Require(reopened.Results.Count == 0
                && reopened.ProcessedDayLabels.Count == 0
                && reopened.ImportLogs.Count == 0,
            "Restore did not roll back imported results, days, and logs.");
        context.Result.BackupRestoreVerified = true;
    }

    private void ValidateConflictAndCorrection(
        TournamentProgressWorkflow workflow,
        EventRunContext context,
        string recordPath)
    {
        var directory = Path.GetDirectoryName(recordPath)!;
        var conflictPath = Path.Combine(directory, "赛程记录表_冲突胜方.xlsx");
        File.Copy(recordPath, conflictPath, overwrite: false);
        using (var workbook = new XLWorkbook(conflictPath))
        {
            var sheet = workbook.Worksheet("对阵记录表");
            var row = FirstRecordRow(sheet);
            var currentWinner = sheet.Cell(row, 12).GetString();
            var optionA = sheet.Cell(row, 15).GetString();
            var optionB = sheet.Cell(row, 16).GetString();
            var opposite = string.Equals(currentWinner, optionA, StringComparison.Ordinal) ? optionB : optionA;
            sheet.Cell(row, 12).Value = opposite;
            sheet.Cell(row, 9).Value = string.Equals(opposite, optionA, StringComparison.Ordinal)
                ? "21-15, 21-17"
                : "15-21, 17-21";
            workbook.Save();
        }

        var conflict = RequireThrows<TournamentProgressException>(
            () => workflow.PreviewImport(context.ArchivePath, [conflictPath]),
            "Opposite winner was not rejected as a result conflict.");
        Require(conflict.Message.Contains("胜负方冲突", StringComparison.Ordinal),
            "Opposite winner failed for an unexpected reason.");
        context.Result.ConflictingWinnerBlocked = true;

        var correctionPath = Path.Combine(directory, "赛程记录表_更正用时.xlsx");
        File.Copy(recordPath, correctionPath, overwrite: false);
        using (var workbook = new XLWorkbook(correctionPath))
        {
            var sheet = workbook.Worksheet("对阵记录表");
            sheet.Cell(FirstRecordRow(sheet), 10).Value = "25m";
            workbook.Save();
        }

        var correctionPreview = workflow.PreviewImport(context.ArchivePath, [correctionPath]);
        Require(correctionPreview.Corrections.Count == 1,
            "Duration correction was not surfaced for confirmation.");
        _ = RequireThrows<TournamentProgressException>(
            () => workflow.Import(context.ArchivePath, [correctionPath]),
            "Unconfirmed correction was not blocked.");
        var corrected = workflow.Import(context.ArchivePath, [correctionPath], allowCorrections: true);
        Require(corrected.State.ImportLogs.Last().CorrectionCount == 1,
            "Confirmed correction did not update the import log.");
        Require(CountResultHistory(context.ArchivePath) >= 1,
            "Confirmed correction did not retain result history.");
        context.Result.CorrectionConfirmationVerified = true;
        context.Result.ResultHistoryCount = CountResultHistory(context.ArchivePath);
    }

    private void ValidateCorruptCopyRejection(string archivePath)
    {
        var corruptPath = Path.Combine(Path.GetDirectoryName(archivePath)!, "corrupt-copy.szbd");
        File.Copy(archivePath, corruptPath, overwrite: false);
        File.WriteAllBytes(corruptPath, Encoding.UTF8.GetBytes("not a sqlite tournament archive"));
        _ = RequireThrows<TournamentProgressException>(
            () => new TournamentProgressStore().Read(corruptPath),
            "Corrupt archive copy was accepted.");
        Require(new TournamentProgressStore().Read(archivePath).Snapshot.TournamentId.Length > 0,
            "Original archive became unreadable after corrupt-copy test.");
        _summary.CorruptCopyRejected = true;
    }

    private void ValidateGeneratedDocuments()
    {
        Console.WriteLine("Validating generated workbook and PDF structure...");
        var workbooks = Directory.EnumerateFiles(_outputRoot, "*.xlsx", SearchOption.AllDirectories).ToList();
        var pdfs = Directory.EnumerateFiles(_outputRoot, "*.pdf", SearchOption.AllDirectories).ToList();
        Require(workbooks.Count > 0, "Acceptance workflow produced no workbooks.");
        Require(pdfs.Count > 0, "Acceptance workflow produced no PDFs.");

        var formulaCount = 0;
        var recordWorkbookCount = 0;
        var recordValidationCount = 0;
        foreach (var path in workbooks)
        {
            using var workbook = new XLWorkbook(path);
            formulaCount += workbook.Worksheets
                .SelectMany(sheet => sheet.CellsUsed())
                .Count(cell => !string.IsNullOrWhiteSpace(cell.FormulaA1));
            var recordSheet = workbook.Worksheets.FirstOrDefault(sheet => sheet.Name == "对阵记录表");
            if (recordSheet is null)
            {
                continue;
            }

            recordWorkbookCount++;
            Require(Enumerable.Range(14, 4).All(column => recordSheet.Column(column).IsHidden),
                $"Record workbook has visible identity/helper columns: {path}");
            recordValidationCount += recordSheet.DataValidations.Count();
        }

        Require(formulaCount > 0, "Generated workbooks contain no formulas.");
        Require(recordWorkbookCount > 0 && recordValidationCount > 0,
            "Generated record workbooks contain no winner data validation.");

        var header = new byte[4];
        foreach (var path in pdfs)
        {
            Require(new FileInfo(path).Length > 100, $"PDF is unexpectedly small: {path}");
            using var stream = File.OpenRead(path);
            Require(stream.Read(header, 0, header.Length) == header.Length
                    && Encoding.ASCII.GetString(header) == "%PDF",
                $"File does not have a PDF header: {path}");
        }

        _summary.Documents = new DocumentAcceptanceResult
        {
            WorkbookCount = workbooks.Count,
            PdfCount = pdfs.Count,
            FormulaCount = formulaCount,
            RecordWorkbookCount = recordWorkbookCount,
            RecordDataValidationCount = recordValidationCount
        };
    }

    private static ScheduleSettings CreateScheduleSettings(
        ScheduleAutoSchedulingStrategy strategy,
        bool includeUnavailableCourtWindow)
    {
        var courts = Enumerable.Range(1, 8)
            .Select(index => $"B{index}")
            .Concat(Enumerable.Range(1, 8).Select(index => $"C{index}"))
            .ToList();
        var dates = new[]
        {
            new DateOnly(2026, 6, 16),
            new DateOnly(2026, 6, 17),
            new DateOnly(2026, 6, 18),
            new DateOnly(2026, 6, 22),
            new DateOnly(2026, 6, 23)
        };
        var days = dates
            .Select((date, index) => new ScheduleDaySettings(
                date,
                new TimeOnly(14, 0),
                new TimeOnly(20, 0),
                courts,
                UnavailableCourtWindows: includeUnavailableCourtWindow && index == 0
                    ? [new ScheduleCourtAvailabilityBlock(new TimeOnly(14, 0), new TimeOnly(15, 0), ["B1"])]
                    : null))
            .ToList();
        return new ScheduleSettings(
            days,
            MatchMinutes: 20,
            MaxMatchesPerEntrantPerDay: 4,
            RefereeCount: RefereeCount)
        {
            ConstraintProfile = ScheduleConstraintProfile.Campus,
            AutoSchedulingStrategy = strategy
        };
    }

    private static DrawResultVisualOptions GetVisualOptions(EventDefinition definition)
    {
        return definition.CompetitionMode == CompetitionMode.SinglesKnockout
            ? new DrawResultVisualOptions(PdfRows: 4, PdfColumns: 2)
            : new DrawResultVisualOptions();
    }

    private static void ValidateSchedule(string eventName, SchedulePlan schedule)
    {
        if (!schedule.IsComplete)
        {
            var reasons = string.Join(
                " | ",
                schedule.UnscheduledMatches
                    .GroupBy(match => match.Reason, StringComparer.Ordinal)
                    .Select(group => $"{group.Count()}x {group.Key}"));
            var dayLoads = string.Join(
                ", ",
                schedule.Matches
                    .GroupBy(match => match.DayLabel, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => $"{group.Key}={group.Count()}"));
            var unscheduled = string.Join(
                " | ",
                schedule.UnscheduledMatches.Select(match => $"{match.MatchName}/{match.Phase}: {match.Reason}"));
            throw new InvalidOperationException(
                $"{eventName}: schedule is incomplete ({schedule.UnscheduledMatches.Count} unscheduled). "
                + $"Reasons: {reasons}. Scheduled day loads: {dayLoads}. Matches: {unscheduled}.");
        }

        Require(schedule.TotalMatchCount == schedule.Matches.Count,
            $"{eventName}: total match count differs from scheduled count.");
        var dependencyViolations = ScheduleDependencyGraph.Build(schedule).FindOrderViolations();
        Require(dependencyViolations.Count == 0,
            $"{eventName}: schedule has {dependencyViolations.Count} dependency order violations.");
        var constraintReport = new ScheduleConstraintAnalyzer().Analyze(schedule);
        Require(constraintReport.SevereCount == 0,
            $"{eventName}: schedule has {constraintReport.SevereCount} severe constraint issues.");

        foreach (var match in schedule.Matches)
        {
            var overlapping = schedule.Matches.Count(other =>
                other.DayLabel == match.DayLabel
                && other.StartTime < match.EndTime
                && match.StartTime < other.EndTime);
            Require(overlapping <= RefereeCount,
                $"{eventName}: referee capacity exceeded at {match.DayLabel} {match.StartTime}.");
            var day = schedule.Settings.Days.Single(item => item.DayLabel == match.DayLabel);
            Require(ScheduleResourceCalculator.IsCourtAvailable(day, match.Court, match.StartTime, match.EndTime),
                $"{eventName}: {match.MatchName} uses unavailable court {match.Court}.");
        }
    }

    private static int FillDeterministicResults(string recordPath, TournamentProgressState state)
    {
        using var workbook = new XLWorkbook(recordPath);
        var sheet = workbook.Worksheet("对阵记录表");
        Require(Enumerable.Range(14, 4).All(column => sheet.Column(column).IsHidden),
            "Record workbook helper columns N:Q are not hidden.");
        var scheduleByName = state.Snapshot.Schedule.Matches.ToDictionary(match => match.MatchName, StringComparer.Ordinal);
        var results = state.Results.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var filledCount = 0;
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        for (var row = 6; row <= lastRow; row++)
        {
            var matchName = sheet.Cell(row, 14).GetString().Trim();
            if (matchName.Length == 0)
            {
                continue;
            }

            if (!scheduleByName.TryGetValue(matchName, out var match))
            {
                throw new InvalidOperationException($"Record row references unknown match {matchName}.");
            }

            var sideA = ResolveSideForResult(match.SideA, match, scheduleByName, results);
            var sideB = ResolveSideForResult(match.SideB, match, scheduleByName, results);
            var optionA = BuildWinnerOption("A", sideA);
            var optionB = BuildWinnerOption("B", sideB);
            var chooseA = SHA256.HashData(Encoding.UTF8.GetBytes($"v4.6.0|{state.Snapshot.TournamentId}|{match.MatchId}"))[0] % 2 == 0;
            var winner = chooseA ? sideA : sideB;
            var loser = chooseA ? sideB : sideA;
            var score = chooseA ? "21-15, 21-17" : "15-21, 17-21";

            sheet.Cell(row, 6).Value = BuildDisplayOption("A", sideA);
            sheet.Cell(row, 8).Value = BuildDisplayOption("B", sideB);
            sheet.Cell(row, 9).Value = score;
            sheet.Cell(row, 10).Value = "18m";
            sheet.Cell(row, 12).Value = chooseA ? optionA : optionB;
            sheet.Cell(row, 15).Value = optionA;
            sheet.Cell(row, 16).Value = optionB;
            results[matchName] = new MatchRecordResult(matchName, match.DayLabel, winner, loser, score, "18m");
            filledCount++;
        }

        workbook.Save();
        return filledCount;
    }

    private static string ResolveSideForResult(
        string originalSide,
        ScheduledMatch match,
        IReadOnlyDictionary<string, ScheduledMatch> scheduleByName,
        IReadOnlyDictionary<string, MatchRecordResult> results)
    {
        if (ScheduleMatchText.TryParseOutcomeReference(originalSide, out var sourceMatchName, out _))
        {
            Require(results.ContainsKey(sourceMatchName),
                $"{match.MatchName} depends on {sourceMatchName}, which has no earlier result in this package.");
        }

        return ScheduleMatchText.ResolveSide(originalSide, match, scheduleByName, results);
    }

    private static int VerifyNextDayResolution(
        string recordPath,
        TournamentProgressState state,
        string dayLabel)
    {
        using var workbook = new XLWorkbook(recordPath);
        var sheet = workbook.Worksheet("对阵记录表");
        var rowByMatchName = Enumerable.Range(6, Math.Max(0, (sheet.LastRowUsed()?.RowNumber() ?? 5) - 5))
            .Where(row => !string.IsNullOrWhiteSpace(sheet.Cell(row, 14).GetString()))
            .ToDictionary(row => sheet.Cell(row, 14).GetString().Trim(), row => row, StringComparer.Ordinal);
        var count = 0;
        foreach (var match in state.Snapshot.Schedule.Matches.Where(item => item.DayLabel == dayLabel))
        {
            if (!rowByMatchName.TryGetValue(match.MatchName, out var row))
            {
                continue;
            }

            count += VerifyResolvedSide(match.SideA, state.Results, sheet.Cell(row, 15).GetString());
            count += VerifyResolvedSide(match.SideB, state.Results, sheet.Cell(row, 16).GetString());
        }

        return count;
    }

    private static int VerifyResolvedSide(
        string originalSide,
        IReadOnlyDictionary<string, MatchRecordResult> results,
        string optionText)
    {
        if (!ScheduleMatchText.TryParseOutcomeReference(originalSide, out var sourceMatchName, out var outcome)
            || !results.TryGetValue(sourceMatchName, out var result))
        {
            return 0;
        }

        var expected = outcome == "胜者" ? result.Winner : result.Loser;
        Require(optionText.Contains(ScheduleMatchText.NormalizeCompetitorName(expected), StringComparison.Ordinal),
            $"Next-day workbook did not resolve {originalSide} to {expected}.");
        Require(!optionText.Contains("胜者", StringComparison.Ordinal)
                && !optionText.Contains("负者", StringComparison.Ordinal),
            $"Next-day workbook retained a resolved placeholder for {originalSide}.");
        return 1;
    }

    private static string BuildWinnerOption(string prefix, string side)
    {
        return $"{prefix}【{ScheduleMatchText.NormalizeCompetitorName(side)}】";
    }

    private static string BuildDisplayOption(string prefix, string side)
    {
        var normalized = ScheduleMatchText.NormalizeCompetitorName(side);
        var multiline = side.TrimStart().StartsWith("[", StringComparison.Ordinal)
            ? normalized.Replace(" ", Environment.NewLine, StringComparison.Ordinal)
            : normalized;
        return $"{prefix}【{multiline}】";
    }

    private static string FindRecordWorkbook(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(path => Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)))
        {
            using var workbook = new XLWorkbook(path);
            if (workbook.Worksheets.Any(sheet => sheet.Name == "对阵记录表"))
            {
                return path;
            }
        }

        throw new InvalidOperationException("Exported package does not contain a match record workbook.");
    }

    private static int FirstRecordRow(IXLWorksheet sheet)
    {
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        for (var row = 6; row <= lastRow; row++)
        {
            if (!string.IsNullOrWhiteSpace(sheet.Cell(row, 14).GetString()))
            {
                return row;
            }
        }

        throw new InvalidOperationException("Record workbook has no match rows.");
    }

    private static long CountResultHistory(string archivePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = archivePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM result_history;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string BuildDrawSignature(DrawResult result)
    {
        static string BuildGroupSignature(IEnumerable<DrawGroup> groups) => string.Join(
            "|",
            groups.SelectMany(group => group.Participants.Select(participant => $"{group.Number}:{participant.DisplayName}")));
        return string.Join(
            "\n",
            BuildGroupSignature(result.Groups),
            BuildGroupSignature(result.RoundOneGroups),
            BuildGroupSignature(result.ByeGroups));
    }

    private static string BuildScheduleSignature(SchedulePlan schedule)
    {
        return string.Join(
            "\n",
            schedule.Matches
                .OrderBy(match => match.MatchId, StringComparer.Ordinal)
                .Select(match => string.Join(
                    "|",
                    match.MatchId,
                    match.DayLabel,
                    match.StartTime.ToString("HH:mm"),
                    match.EndTime.ToString("HH:mm"),
                    match.Court)));
    }

    private static string FormatConstraintIssue(ScheduleConstraintIssue issue)
    {
        return $"{issue.Severity}/{issue.Scope} {issue.DayLabel} {issue.StartTime:HH:mm} "
            + $"{issue.MatchName}: {issue.Message}";
    }

    private static TException RequireThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "BadmintonDraw.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException("Could not locate BadmintonDraw.sln from the current directory.");
    }

    private static string ResolveOutputRoot(string[] args, string repositoryRoot)
    {
        var outputIndex = Array.FindIndex(args, argument => argument == "--output");
        if (outputIndex >= 0 && outputIndex + 1 < args.Length)
        {
            return Path.GetFullPath(args[outputIndex + 1], repositoryRoot);
        }

        return Path.Combine(
            repositoryRoot,
            "artifacts",
            "acceptance",
            "v4.6.0",
            $"run-{DateTime.Now:yyyyMMdd-HHmmss}");
    }

    private static string TryRun(string executable, string arguments, string workingDirectory)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return "unavailable";
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : "unavailable";
        }
        catch
        {
            return "unavailable";
        }
    }
}

internal sealed record EventDefinition(
    string Slug,
    string SampleRelativePath,
    int ExpectedParticipantCount,
    EventKind PreferredEventKind,
    CompetitionMode CompetitionMode,
    int GroupCount,
    KnockoutGoal KnockoutGoal,
    PlacementPlayoff PlacementPlayoff,
    string Seed);

internal sealed class EventRunContext(
    EventDefinition definition,
    DrawWorkflowResult draw,
    string archivePath,
    TournamentProgressState state,
    EventAcceptanceResult result)
{
    public EventDefinition Definition { get; } = definition;
    public DrawWorkflowResult Draw { get; } = draw;
    public string ArchivePath { get; } = archivePath;
    public TournamentProgressState State { get; set; } = state;
    public EventAcceptanceResult Result { get; } = result;
}

internal sealed class AcceptanceSummary
{
    public bool Success { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public string RepositoryRoot { get; set; } = "";
    public string ArtifactRoot { get; set; } = "";
    public string GitCommit { get; set; } = "";
    public string DotnetVersion { get; set; } = "";
    public string OperatingSystem { get; set; } = "";
    public bool CorruptCopyRejected { get; set; }
    public List<EventAcceptanceResult> Events { get; } = [];
    public CrossEventAcceptanceResult? CrossEvent { get; set; }
    public DocumentAcceptanceResult? Documents { get; set; }
    public List<string> ArtifactFiles { get; set; } = [];
    public List<string> Failures { get; } = [];
}

internal sealed class EventAcceptanceResult
{
    public string Slug { get; set; } = "";
    public string SamplePath { get; set; } = "";
    public string EventKind { get; set; } = "";
    public string CompetitionMode { get; set; } = "";
    public int ParticipantCount { get; set; }
    public int ImportWarningCount { get; set; }
    public List<string> ImportWarnings { get; set; } = [];
    public int GroupCount { get; set; }
    public string DrawInputHash { get; set; } = "";
    public int MatchCount { get; set; }
    public int DayCount { get; set; }
    public int ScheduleSevereCount { get; set; }
    public int ScheduleWarningCount { get; set; }
    public List<string> InitialConstraintIssues { get; set; } = [];
    public int UnavailableCourtBlockCount { get; set; }
    public int PostMergeScheduleSevereCount { get; set; }
    public int PostMergeScheduleWarningCount { get; set; }
    public List<string> PostMergeConstraintIssues { get; set; } = [];
    public string TournamentId { get; set; } = "";
    public string ArchivePath { get; set; } = "";
    public bool CrossEventScheduleSaved { get; set; }
    public string FirstDayLabel { get; set; } = "";
    public int FirstDayFilledResultCount { get; set; }
    public int ImportedResultCount { get; set; }
    public bool DuplicateImportDetected { get; set; }
    public bool ConflictingWinnerBlocked { get; set; }
    public bool CorrectionConfirmationVerified { get; set; }
    public long ResultHistoryCount { get; set; }
    public bool BackupRestoreVerified { get; set; }
    public int NextDayResolvedReferenceCount { get; set; }
    public int ImportLogCount { get; set; }
    public long SetupElapsedMilliseconds { get; set; }
    public long ImportElapsedMilliseconds { get; set; }
}

internal sealed class CrossEventAcceptanceResult
{
    public int SourceCount { get; set; }
    public int TotalMatchCount { get; set; }
    public int MultiEventPlayerCount { get; set; }
    public int InitialSevereCount { get; set; }
    public int InitialWarningCount { get; set; }
    public int MovedCount { get; set; }
    public int RemainingBlockingConflictItemCount { get; set; }
    public int FinalSevereCount { get; set; }
    public int FinalWarningCount { get; set; }
    public int BackupCount { get; set; }
    public int MaterialFileCount { get; set; }
    public string MaterialDirectory { get; set; } = "";
    public long ElapsedMilliseconds { get; set; }
}

internal sealed class DocumentAcceptanceResult
{
    public int WorkbookCount { get; set; }
    public int PdfCount { get; set; }
    public int FormulaCount { get; set; }
    public int RecordWorkbookCount { get; set; }
    public int RecordDataValidationCount { get; set; }
}
