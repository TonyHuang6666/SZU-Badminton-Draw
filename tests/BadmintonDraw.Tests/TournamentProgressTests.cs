using BadmintonDraw.Core;
using BadmintonDraw.Excel;
using BadmintonDraw.Workflows;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using SkiaSharp;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed partial class DrawWorkflowTests
{
    [Fact]
    public void TournamentProgressScheduleUpdateKeepsOriginalWhenCandidateValidationFails()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-progress-atomic-update-{Guid.NewGuid():N}");
        var progressPath = Path.Combine(directory, "校长杯男单.szbd");

        try
        {
            var snapshot = CreateTournamentProgressSnapshot("tournament-atomic-update");
            var store = new TournamentProgressStore();
            store.Create(progressPath, snapshot);
            using (var connection = new SqliteConnection($"Data Source={progressPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TRIGGER corrupt_candidate_after_schedule_update
                    AFTER UPDATE ON metadata
                    WHEN NEW.key = 'updated_at'
                    BEGIN
                        DELETE FROM snapshot;
                    END;
                    """;
                command.ExecuteNonQuery();
            }

            var matchToMove = snapshot.Schedule.Matches[0];
            var adjustedSchedule = snapshot.Schedule with
            {
                Matches = snapshot.Schedule.Matches
                    .Select(match => match.MatchId == matchToMove.MatchId
                        ? match with
                        {
                            DayLabel = snapshot.Schedule.Matches[^1].DayLabel,
                            StartTime = new TimeOnly(15, 0),
                            EndTime = new TimeOnly(15, 30),
                            Court = "A1"
                        }
                        : match)
                    .ToList()
            };

            _ = Assert.Throws<TournamentProgressException>(() => store.UpdateSchedule(progressPath, adjustedSchedule));
            TournamentProgressState? reopened = null;
            var readError = Record.Exception(() => reopened = store.Read(progressPath));

            Assert.Null(readError);
            Assert.NotNull(reopened);
            var reopenedMatch = reopened.Snapshot.Schedule.Matches
                .Single(match => match.MatchId == matchToMove.MatchId);
            Assert.Equal(matchToMove.DayLabel, reopenedMatch.DayLabel);
            Assert.Equal(matchToMove.StartTime, reopenedMatch.StartTime);
            Assert.Equal(matchToMove.EndTime, reopenedMatch.EndTime);
            Assert.Equal(matchToMove.Court, reopenedMatch.Court);
            Assert.False(Directory.Exists(Path.Combine(directory, "Backups")));
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void TournamentProgressWorkflowExportsNextDayPackage()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-next-package-{Guid.NewGuid():N}");

        try
        {
            var snapshot = CreateTournamentProgressSnapshot("tournament-package");
            var dayOne = snapshot.Schedule.Matches[0].DayLabel;
            var dayOneResults = BuildCompletedResultsForDay(snapshot.Schedule, dayOne);
            var state = new TournamentProgressState(
                snapshot,
                dayOneResults,
                [],
                [dayOne],
                []);

            var package = new TournamentProgressWorkflow().ExportNextDayPackage(
                state,
                directory,
                includePrintablePdf: false);
            var expectedScoreSheetCount = snapshot.Schedule.Matches.Count(match => match.DayLabel == package.DayLabel);
            var scorePdfPath = Path.Combine(package.OutputDirectory, "6月7日单场比赛计分表.pdf");

            Assert.Equal("2026-06-07", package.DayLabel);
            Assert.Equal(4, package.OutputPaths.Count);
            Assert.NotEqual(directory, package.OutputDirectory);
            Assert.True(Directory.Exists(package.OutputDirectory));
            Assert.All(package.OutputPaths, path => Assert.StartsWith(package.OutputDirectory, path, StringComparison.Ordinal));
            Assert.All(package.OutputPaths, path => Assert.True(File.Exists(path), path));
            Assert.Contains(package.OutputPaths, path => Path.GetFileName(path) == "6月7日赛程记录表.xlsx");
            Assert.Contains(package.OutputPaths, path => Path.GetFileName(path) == "6月7日赛程安排表.xlsx");
            Assert.Contains(package.OutputPaths, path => Path.GetFileName(path) == "6月7日带时间场地对阵表.xlsx");
            Assert.Contains(scorePdfPath, package.OutputPaths);

            using var recordWorkbook = new XLWorkbook(Path.Combine(package.OutputDirectory, "6月7日赛程记录表.xlsx"));
            using var scheduleWorkbook = new XLWorkbook(Path.Combine(package.OutputDirectory, "6月7日赛程安排表.xlsx"));
            Assert.Equal("对阵记录表", recordWorkbook.Worksheet("对阵记录表").Name);
            Assert.DoesNotContain(scheduleWorkbook.Worksheets, worksheet => worksheet.Name == "对阵记录表");
            Assert.Contains("2026-06-07", scheduleWorkbook.Worksheet("赛程明细").Cell(5, 2).GetString());
            AssertFileHeader(scorePdfPath, [0x25, 0x50, 0x44, 0x46]);
            AssertPdfUsesTextLayer(scorePdfPath);
            Assert.Equal(expectedScoreSheetCount, CountPdfPages(scorePdfPath));
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void TournamentProgressWorkflowExportsFirstDayPackage()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-first-package-{Guid.NewGuid():N}");

        try
        {
            var snapshot = CreateTournamentProgressSnapshot("tournament-first-package");
            var state = new TournamentProgressState(
                snapshot,
                new Dictionary<string, MatchRecordResult>(StringComparer.Ordinal),
                [],
                [],
                []);

            var package = new TournamentProgressWorkflow().ExportFirstDayPackage(
                state,
                directory,
                includePrintablePdf: false);
            var expectedScoreSheetCount = snapshot.Schedule.Matches.Count(match => match.DayLabel == package.DayLabel);
            var scorePdfPath = Path.Combine(package.OutputDirectory, "6月6日单场比赛计分表.pdf");

            Assert.Equal("2026-06-06", package.DayLabel);
            Assert.Equal(4, package.OutputPaths.Count);
            Assert.NotEqual(directory, package.OutputDirectory);
            Assert.True(Directory.Exists(package.OutputDirectory));
            Assert.All(package.OutputPaths, path => Assert.StartsWith(package.OutputDirectory, path, StringComparison.Ordinal));
            Assert.All(package.OutputPaths, path => Assert.True(File.Exists(path), path));
            Assert.Contains(package.OutputPaths, path => Path.GetFileName(path) == "6月6日赛程记录表.xlsx");
            Assert.Contains(package.OutputPaths, path => Path.GetFileName(path) == "6月6日赛程安排表.xlsx");
            Assert.Contains(package.OutputPaths, path => Path.GetFileName(path) == "6月6日带时间场地对阵表.xlsx");
            Assert.Contains(scorePdfPath, package.OutputPaths);

            using var recordWorkbook = new XLWorkbook(Path.Combine(package.OutputDirectory, "6月6日赛程记录表.xlsx"));
            using var scheduleWorkbook = new XLWorkbook(Path.Combine(package.OutputDirectory, "6月6日赛程安排表.xlsx"));
            Assert.Equal("对阵记录表", recordWorkbook.Worksheet("对阵记录表").Name);
            Assert.DoesNotContain(scheduleWorkbook.Worksheets, worksheet => worksheet.Name == "对阵记录表");
            Assert.Contains("2026-06-06", scheduleWorkbook.Worksheet("赛程明细").Cell(5, 2).GetString());
            AssertFileHeader(scorePdfPath, [0x25, 0x50, 0x44, 0x46]);
            AssertPdfUsesTextLayer(scorePdfPath);
            Assert.Equal(expectedScoreSheetCount, CountPdfPages(scorePdfPath));
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void TournamentProgressWorkflowExportsTeamScoreSheetInNextDayPackage()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-team-package-{Guid.NewGuid():N}");

        try
        {
            var participants = CreateParticipants(4);
            var result = new DrawService().Generate(
                participants,
                CreateSettings(
                    groupCount: 1,
                    mode: CompetitionMode.TeamKnockout,
                    eventKind: EventKind.Team,
                    knockoutGoal: KnockoutGoal.Champion));
            var schedule = new ScheduleService().Generate(
                result,
                new ScheduleSettings(
                    [
                        new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(15, 0), ["A1"]),
                        new ScheduleDaySettings(new DateOnly(2026, 6, 7), new TimeOnly(14, 0), new TimeOnly(16, 0), ["A1"])
                    ],
                    MatchMinutes: 30,
                    MaxMatchesPerEntrantPerDay: 2));
            Assert.True(schedule.IsComplete);
            var now = DateTimeOffset.UtcNow;
            var dayOne = schedule.Matches[0].DayLabel;
            var state = new TournamentProgressState(
                new TournamentProgressSnapshot(
                    "team-package",
                    "校长杯团体",
                    now,
                    now,
                    "/tmp/校长杯团体参赛名单.xlsx",
                    result,
                    participants,
                    [],
                    schedule),
                BuildCompletedResultsForDay(schedule, dayOne),
                [],
                [dayOne],
                []);

            var package = new TournamentProgressWorkflow().ExportNextDayPackage(
                state,
                directory,
                includePrintablePdf: false);
            var teamScorePath = Path.Combine(package.OutputDirectory, "6月7日团体赛记分表.xlsx");

            Assert.Contains(teamScorePath, package.OutputPaths);
            Assert.True(File.Exists(teamScorePath));
            using var workbook = new XLWorkbook(teamScorePath);
            var sheet = workbook.Worksheet("团体记分表");
            Assert.Contains("团体赛记分表", sheet.Cell(1, 1).GetString());
            Assert.Equal("阶段", sheet.Cell(4, 1).GetString());
            Assert.Equal("分场记录", sheet.Cell(7, 1).GetString());
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void MatchRecordImportWarningListsEveryDetectedIssue()
    {
        var importResult = new MatchRecordImportResult(
            new Dictionary<string, MatchRecordResult>(StringComparer.Ordinal),
            ["2026-06-12"],
            ExpectedMatchCount: 4,
            MissingResultRows:
            [
                "序号 10 A组小组赛1 未填写胜方，已按待赛处理",
                "序号 11 A组小组赛2 未填写胜方，已按待赛处理"
            ],
            ValidationIssues:
            [
                "序号 12 A组小组赛3 已填写胜方但比分为空",
                "序号 13 A组小组赛4 胜方不在双方名单中"
            ]);

        var message = ScheduleWorkflow.BuildMatchRecordImportWarning(importResult, "2026-06-13");

        Assert.DoesNotContain("示例", message);
        Assert.Contains("详细问题", message);
        Assert.Contains("1. 序号 10 A组小组赛1 未填写胜方，已按待赛处理", message);
        Assert.Contains("2. 序号 11 A组小组赛2 未填写胜方，已按待赛处理", message);
        Assert.Contains("1. 序号 12 A组小组赛3 已填写胜方但比分为空", message);
        Assert.Contains("2. 序号 13 A组小组赛4 胜方不在双方名单中", message);
    }

    [Fact]
    public void TournamentProgressImportConfirmationListsEveryDetectedIssue()
    {
        var selectedImportResult = new MatchRecordImportResult(
            new Dictionary<string, MatchRecordResult>(StringComparer.Ordinal)
            {
                ["A组128进64第1场"] = new MatchRecordResult(
                    "A组128进64第1场",
                    "2026-06-12",
                    "甲",
                    "乙",
                    "15-12, 15-9",
                    "21m")
            },
            ["2026-06-12"],
            ExpectedMatchCount: 4,
            MissingResultRows:
            [
                "6月12日赛程记录表.xlsx：序号 78 A组128进64第1场 未填写胜方，已按待赛处理",
                "6月12日赛程记录表.xlsx：序号 79 A组128进64第2场 未填写胜方，已按待赛处理"
            ],
            ValidationIssues:
            [
                "6月12日赛程记录表.xlsx：序号 80 A组128进64第3场 已填写胜方但用时为空",
                "6月12日赛程记录表.xlsx：序号 81 A组128进64第4场 胜方不在双方名单中"
            ]);
        var correction = new TournamentProgressCorrection(
            "A组128进64第1场",
            new MatchRecordResult("A组128进64第1场", "2026-06-12", "甲", "乙", "15-10, 15-8", "18m"),
            new MatchRecordResult("A组128进64第1场", "2026-06-12", "甲", "乙", "15-12, 15-9", "21m"));
        var preview = new TournamentProgressImportPreview(
            selectedImportResult,
            selectedImportResult,
            [correction],
            ["已导入记录表.xlsx"],
            ["旧版记录表.xlsx 未带赛事标识，已按旧版记录表处理。"],
            NewResultCount: 1,
            FilesToImport: 1);

        var message = TournamentProgressWorkflow.BuildImportConfirmation(preview, "2026-06-13");

        Assert.DoesNotContain("示例", message);
        Assert.Contains("详细问题", message);
        Assert.Contains("1. 6月12日赛程记录表.xlsx：序号 78 A组128进64第1场 未填写胜方，已按待赛处理", message);
        Assert.Contains("2. 6月12日赛程记录表.xlsx：序号 79 A组128进64第2场 未填写胜方，已按待赛处理", message);
        Assert.Contains("1. 6月12日赛程记录表.xlsx：序号 80 A组128进64第3场 已填写胜方但用时为空", message);
        Assert.Contains("2. 6月12日赛程记录表.xlsx：序号 81 A组128进64第4场 胜方不在双方名单中", message);
        Assert.Contains("1. A组128进64第1场：原结果 胜方 甲，比分 15-10, 15-8，用时 18m，比赛日 2026-06-12 → 新结果 胜方 甲，比分 15-12, 15-9，用时 21m，比赛日 2026-06-12", message);
        Assert.Contains("1. 旧版记录表.xlsx 未带赛事标识，已按旧版记录表处理。", message);
        Assert.Contains("1. 已导入记录表.xlsx", message);
    }

    [Fact]
    public void TournamentProgressStoreCreatesAndRestoresSnapshot()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-progress-create-{Guid.NewGuid():N}");
        var progressPath = Path.Combine(directory, "校长杯男单.szbd");

        try
        {
            var snapshot = CreateTournamentProgressSnapshot("tournament-create");
            var store = new TournamentProgressStore();

            var created = store.Create(progressPath, snapshot);
            var reopened = store.Read(progressPath);

            Assert.True(File.Exists(progressPath));
            Assert.Equal("tournament-create", created.Snapshot.TournamentId);
            Assert.Equal(snapshot.DrawResult.Audit.InputHash, reopened.Snapshot.DrawResult.Audit.InputHash);
            Assert.Equal(snapshot.Participants, reopened.Snapshot.Participants);
            Assert.Equal(snapshot.Schedule.Matches, reopened.Snapshot.Schedule.Matches);
            Assert.Empty(reopened.Results);
            Assert.Empty(reopened.ImportLogs);
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void TournamentProgressStoreBackfillsLegacyScheduleDependencies()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-progress-legacy-dependencies-{Guid.NewGuid():N}");
        var progressPath = Path.Combine(directory, "旧版校长杯男单.szbd");

        try
        {
            var participants = CreateParticipants(4);
            var result = new DrawService().Generate(
                participants,
                CreateSettings(
                    groupCount: 1,
                    mode: CompetitionMode.SinglesKnockout,
                    knockoutGoal: KnockoutGoal.Champion));
            var schedule = new ScheduleService().Generate(
                result,
                new ScheduleSettings(
                    [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(16, 0), ["A1", "A2", "A3"])],
                    MatchMinutes: 30,
                    MaxMatchesPerEntrantPerDay: 2));
            var now = DateTimeOffset.UtcNow;
            var snapshot = new TournamentProgressSnapshot(
                "legacy-dependencies",
                "旧版校长杯男单",
                now,
                now,
                "/tmp/旧版校长杯男单参赛名单.xlsx",
                result,
                participants,
                [],
                StripScheduleDependencies(schedule));
            var store = new TournamentProgressStore();

            store.Create(progressPath, snapshot);
            var reopened = store.Read(progressPath);
            var restoredSchedule = reopened.Snapshot.Schedule;
            var dependentMatch = restoredSchedule.Matches.Single(match => match.Dependencies.Count == 2);
            var sourceMatch = restoredSchedule.Matches.Single(match =>
                string.Equals(match.MatchId, dependentMatch.Dependencies[0].SourceMatchId, StringComparison.Ordinal));

            Assert.All(restoredSchedule.Matches, match => Assert.False(string.IsNullOrWhiteSpace(match.MatchId)));
            Assert.NotEqual(dependentMatch.MatchName, dependentMatch.MatchId);
            Assert.NotEmpty(dependentMatch.Dependencies);

            var exception = Assert.Throws<DrawValidationException>(() => ScheduleWorkflow.MoveScheduledMatch(
                restoredSchedule,
                dependentMatch.MatchName,
                sourceMatch.DayLabel,
                sourceMatch.StartTime,
                "A3"));
            Assert.Contains("赛程顺序错误", exception.Message);
            Assert.Contains("前序场次结束前开始", exception.Message);
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void TournamentProgressStoreImportsOnceAndRejectsWinnerConflict()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-progress-import-{Guid.NewGuid():N}");
        var progressPath = Path.Combine(directory, "校长杯男单.szbd");
        var recordPath = Path.Combine(directory, "第一日记录表.xlsx");
        var conflictPath = Path.Combine(directory, "第一日冲突记录表.xlsx");

        try
        {
            var snapshot = CreateTournamentProgressSnapshot("tournament-import");
            var store = new TournamentProgressStore();
            store.Create(progressPath, snapshot);
            var dayLabel = snapshot.Schedule.Matches[0].DayLabel;
            var writer = new ScheduleExcelWriter();
            writer.WriteMatchRecord(recordPath, snapshot.Schedule, dayLabel, tournamentId: snapshot.TournamentId);
            FillMatchRecordWinners(recordPath, winnerOptionColumn: 15);

            var imported = store.Import(progressPath, [recordPath]);
            var duplicatePreview = store.PreviewImport(progressPath, [recordPath]);

            Assert.NotEmpty(imported.State.Results);
            Assert.Empty(imported.State.PendingMatchNames);
            Assert.Equal(
                snapshot.Schedule.Matches.Count - imported.State.Results.Count,
                imported.State.RemainingMatchCount);
            Assert.True(imported.State.RemainingMatchCount > 0);
            Assert.Single(imported.State.ImportLogs);
            Assert.Single(duplicatePreview.DuplicateFiles);
            Assert.Equal(0, duplicatePreview.FilesToImport);
            Assert.NotNull(imported.BackupPath);
            Assert.True(File.Exists(imported.BackupPath));

            writer.WriteMatchRecord(conflictPath, snapshot.Schedule, dayLabel, tournamentId: snapshot.TournamentId);
            FillMatchRecordWinners(conflictPath, winnerOptionColumn: 16);

            var error = Assert.Throws<TournamentProgressException>(() =>
                store.PreviewImport(progressPath, [conflictPath]));
            Assert.Contains("胜负方冲突", error.Message);
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void TournamentProgressStoreRequiresConfirmationForResultCorrection()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-progress-correction-{Guid.NewGuid():N}");
        var progressPath = Path.Combine(directory, "校长杯男单.szbd");
        var firstPath = Path.Combine(directory, "第一日记录表.xlsx");
        var correctedPath = Path.Combine(directory, "第一日记录表_更正.xlsx");

        try
        {
            var snapshot = CreateTournamentProgressSnapshot("tournament-correction");
            var store = new TournamentProgressStore();
            store.Create(progressPath, snapshot);
            var dayLabel = snapshot.Schedule.Matches[0].DayLabel;
            var writer = new ScheduleExcelWriter();
            writer.WriteMatchRecord(firstPath, snapshot.Schedule, dayLabel, tournamentId: snapshot.TournamentId);
            FillMatchRecordWinners(firstPath, winnerOptionColumn: 15);
            store.Import(progressPath, [firstPath]);

            File.Copy(firstPath, correctedPath);
            using (var workbook = new XLWorkbook(correctedPath))
            {
                var sheet = workbook.Worksheet("对阵记录表");
                sheet.Cell(6, 10).Value = "25m";
                workbook.Save();
            }

            var preview = store.PreviewImport(progressPath, [correctedPath]);
            Assert.Single(preview.Corrections);
            Assert.Throws<TournamentProgressException>(() =>
                store.Import(progressPath, [correctedPath]));

            var corrected = store.Import(progressPath, [correctedPath], allowCorrections: true);
            Assert.Equal("25m", corrected.State.Results[preview.Corrections[0].MatchName].Duration);
            Assert.Equal(2, corrected.State.ImportLogs.Count);
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void TournamentProgressStoreRejectsRecordFromAnotherTournament()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-progress-identity-{Guid.NewGuid():N}");
        var progressPath = Path.Combine(directory, "校长杯男单.szbd");
        var recordPath = Path.Combine(directory, "其他赛事记录表.xlsx");

        try
        {
            var snapshot = CreateTournamentProgressSnapshot("tournament-current");
            var store = new TournamentProgressStore();
            store.Create(progressPath, snapshot);
            new ScheduleExcelWriter().WriteMatchRecord(
                recordPath,
                snapshot.Schedule,
                snapshot.Schedule.Matches[0].DayLabel,
                tournamentId: "tournament-other");
            FillMatchRecordWinners(recordPath, winnerOptionColumn: 15);

            var error = Assert.Throws<TournamentProgressException>(() =>
                store.PreviewImport(progressPath, [recordPath]));

            Assert.Contains("不属于当前赛事存档", error.Message);
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void MatchRecordReaderCarriesResultsIntoNextDayRecord()
    {
        var participants = CreateParticipants(8);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout));
        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [
                    new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(16, 0), ["A1"]),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 7), new TimeOnly(14, 0), new TimeOnly(18, 0), ["A1"])
                ],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));
        var dayOnePath = Path.Combine(Path.GetTempPath(), $"badminton-record-day1-{Guid.NewGuid():N}.xlsx");
        var dayTwoPath = Path.Combine(Path.GetTempPath(), $"badminton-record-day2-{Guid.NewGuid():N}.xlsx");

        try
        {
            Assert.True(schedule.IsComplete);
            Assert.Contains(schedule.Matches, match => match.DayLabel == "2026-06-07"
                && match.SideA.Contains("胜者", StringComparison.Ordinal));

            var writer = new ScheduleExcelWriter();
            writer.WriteMatchRecord(dayOnePath, schedule, "2026-06-06");

            using (var workbook = new XLWorkbook(dayOnePath))
            {
                var sheet = workbook.Worksheet("对阵记录表");
                var lastRow = sheet.LastRowUsed()!.RowNumber();
                for (var row = 6; row <= lastRow; row++)
                {
                    var optionA = sheet.Cell(row, 15).GetString();
                    if (!string.IsNullOrWhiteSpace(optionA))
                    {
                        sheet.Cell(row, 9).Value = "15-10, 15-12";
                        sheet.Cell(row, 10).Value = "18m";
                        sheet.Cell(row, 12).Value = optionA;
                    }
                }

                workbook.Save();
            }

            var importResult = new MatchRecordReader().Read(dayOnePath);
            Assert.Equal(4, importResult.Results.Count);
            Assert.Contains("2026-06-06", importResult.DayLabels);

            writer.WriteMatchRecord(dayTwoPath, schedule, "2026-06-07", importResult.Results);

            using var nextWorkbook = new XLWorkbook(dayTwoPath);
            var recordSheet = nextWorkbook.Worksheet("对阵记录表");
            var nextText = string.Join(
                '\n',
                recordSheet.Range(6, 6, recordSheet.LastRowUsed()!.RowNumber(), 8)
                    .Cells()
                    .Select(cell => cell.GetString()));

            Assert.DoesNotContain("8进4", nextText, StringComparison.Ordinal);
            Assert.Contains("半决赛第1场胜者", nextText, StringComparison.Ordinal);
            Assert.Contains("选手", nextText, StringComparison.Ordinal);
        }
        finally
        {
            DeleteIfExists(dayOnePath);
            DeleteIfExists(dayTwoPath);
        }
    }

    [Fact]
    public void MatchRecordReaderMergesMultipleRecordSheets()
    {
        var participants = CreateParticipants(8);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout));
        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [
                    new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(16, 0), ["A1"]),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 7), new TimeOnly(14, 0), new TimeOnly(15, 0), ["A1"]),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 8), new TimeOnly(14, 0), new TimeOnly(15, 0), ["A1"])
                ],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 1));
        var dayOnePath = Path.Combine(Path.GetTempPath(), $"badminton-record-merge-day1-{Guid.NewGuid():N}.xlsx");
        var dayTwoPath = Path.Combine(Path.GetTempPath(), $"badminton-record-merge-day2-{Guid.NewGuid():N}.xlsx");
        var dayThreePath = Path.Combine(Path.GetTempPath(), $"badminton-record-merge-day3-{Guid.NewGuid():N}.xlsx");

        try
        {
            Assert.True(schedule.IsComplete);
            var writer = new ScheduleExcelWriter();
            writer.WriteMatchRecord(dayOnePath, schedule, "2026-06-06");
            FillMatchRecordWinners(dayOnePath, winnerOptionColumn: 15);

            var reader = new MatchRecordReader();
            var dayOneResults = reader.Read(dayOnePath);
            writer.WriteMatchRecord(dayTwoPath, schedule, "2026-06-07", dayOneResults.Results);
            FillMatchRecordWinners(dayTwoPath, winnerOptionColumn: 15);

            var mergedResults = reader.ReadMany([dayOnePath, dayTwoPath]);
            Assert.Equal(6, mergedResults.Results.Count);
            Assert.Contains("2026-06-06", mergedResults.DayLabels);
            Assert.Contains("2026-06-07", mergedResults.DayLabels);

            writer.WriteMatchRecord(dayThreePath, schedule, "2026-06-08", mergedResults.Results);

            using var workbook = new XLWorkbook(dayThreePath);
            var sheet = workbook.Worksheet("对阵记录表");
            var text = string.Join(
                '\n',
                sheet.Range(6, 6, sheet.LastRowUsed()!.RowNumber(), 8)
                    .Cells()
                    .Select(cell => cell.GetString()));

            Assert.DoesNotContain("胜者", text, StringComparison.Ordinal);
            Assert.Contains("选手", text, StringComparison.Ordinal);
        }
        finally
        {
            DeleteIfExists(dayOnePath);
            DeleteIfExists(dayTwoPath);
            DeleteIfExists(dayThreePath);
        }
    }

    [Fact]
    public void MatchRecordReaderRejectsConflictingDuplicateResults()
    {
        var participants = CreateParticipants(2);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout));
        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(15, 0), ["A1"])],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));
        var firstPath = Path.Combine(Path.GetTempPath(), $"badminton-record-conflict-a-{Guid.NewGuid():N}.xlsx");
        var secondPath = Path.Combine(Path.GetTempPath(), $"badminton-record-conflict-b-{Guid.NewGuid():N}.xlsx");

        try
        {
            var writer = new ScheduleExcelWriter();
            writer.WriteMatchRecord(firstPath, schedule, "2026-06-06");
            writer.WriteMatchRecord(secondPath, schedule, "2026-06-06");
            FillMatchRecordWinners(firstPath, winnerOptionColumn: 15);
            FillMatchRecordWinners(secondPath, winnerOptionColumn: 16);

            var error = Assert.Throws<ExcelImportException>(() =>
                new MatchRecordReader().ReadMany([firstPath, secondPath]));

            Assert.Contains("胜方不一致", error.Message);
        }
        finally
        {
            DeleteIfExists(firstPath);
            DeleteIfExists(secondPath);
        }
    }

    [Fact]
    public void MatchRecordReaderReportsIncompleteRows()
    {
        var participants = CreateParticipants(2);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout));
        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(15, 0), ["A1"])],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));
        var recordPath = Path.Combine(Path.GetTempPath(), $"badminton-record-incomplete-{Guid.NewGuid():N}.xlsx");

        try
        {
            new ScheduleExcelWriter().WriteMatchRecord(recordPath, schedule, "2026-06-06");

            var importResult = new MatchRecordReader().Read(recordPath);

            Assert.False(importResult.IsComplete);
            Assert.Single(importResult.MissingResultRows);
            Assert.Contains("序号 1", importResult.MissingResultRows[0]);
            Assert.DoesNotContain("第 6 行", importResult.MissingResultRows[0]);
            Assert.Single(importResult.PendingMatchNames);
            Assert.Empty(importResult.Results);
        }
        finally
        {
            DeleteIfExists(recordPath);
        }
    }

    [Fact]
    public void MatchRecordReaderAllowsWinnerWithoutScoreForWalkover()
    {
        var participants = CreateParticipants(2);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout));
        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(15, 0), ["A1"])],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));
        var recordPath = Path.Combine(Path.GetTempPath(), $"badminton-record-walkover-{Guid.NewGuid():N}.xlsx");

        try
        {
            new ScheduleExcelWriter().WriteMatchRecord(recordPath, schedule, "2026-06-06");
            using (var workbook = new XLWorkbook(recordPath))
            {
                var sheet = workbook.Worksheet("对阵记录表");
                sheet.Cell(6, 12).Value = sheet.Cell(6, 15).GetString();
                workbook.Save();
            }

            var importResult = new MatchRecordReader().Read(recordPath);

            Assert.False(importResult.IsComplete);
            Assert.Empty(importResult.MissingResultRows);
            Assert.Empty(importResult.PendingMatchNames);
            Assert.Single(importResult.Results);
            Assert.Contains("未填写比分", importResult.ValidationIssues[0]);
            Assert.Contains("序号 1", importResult.ValidationIssues[0]);
            Assert.DoesNotContain("第 6 行", importResult.ValidationIssues[0]);
        }
        finally
        {
            DeleteIfExists(recordPath);
        }
    }

    [Fact]
    public void MatchRecordReaderReportsScoreWinnerMismatch()
    {
        var participants = CreateParticipants(2);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout));
        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(15, 0), ["A1"])],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));
        var recordPath = Path.Combine(Path.GetTempPath(), $"badminton-record-score-mismatch-{Guid.NewGuid():N}.xlsx");

        try
        {
            new ScheduleExcelWriter().WriteMatchRecord(recordPath, schedule, "2026-06-06");
            using (var workbook = new XLWorkbook(recordPath))
            {
                var sheet = workbook.Worksheet("对阵记录表");
                sheet.Cell(6, 9).Value = "15-10, 1-15, 11-15";
                sheet.Cell(6, 10).Value = "33m";
                sheet.Cell(6, 12).Value = sheet.Cell(6, 15).GetString();
                workbook.Save();
            }

            var importResult = new MatchRecordReader().Read(recordPath);

            Assert.False(importResult.IsComplete);
            Assert.Single(importResult.ValidationIssues);
            Assert.Contains("比分胜方为 B", importResult.ValidationIssues[0]);
            Assert.Contains("序号 1", importResult.ValidationIssues[0]);
            Assert.DoesNotContain("第 6 行", importResult.ValidationIssues[0]);
            Assert.Single(importResult.Results);
        }
        finally
        {
            DeleteIfExists(recordPath);
        }
    }

    [Fact]
    public void MatchRecordWriterCarriesPendingMatchesIntoNextDayRecord()
    {
        var participants = CreateParticipants(4);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout));
        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [
                    new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(15, 0), ["A1"]),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 7), new TimeOnly(14, 0), new TimeOnly(15, 0), ["A1"])
                ],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));
        var dayOnePath = Path.Combine(Path.GetTempPath(), $"badminton-record-pending-day1-{Guid.NewGuid():N}.xlsx");
        var dayTwoPath = Path.Combine(Path.GetTempPath(), $"badminton-record-pending-day2-{Guid.NewGuid():N}.xlsx");

        try
        {
            new ScheduleExcelWriter().WriteMatchRecord(dayOnePath, schedule, "2026-06-06");
            var importResult = new MatchRecordReader().Read(dayOnePath);
            Assert.NotEmpty(importResult.PendingMatchNames);

            new ScheduleExcelWriter().WriteMatchRecord(
                dayTwoPath,
                schedule,
                "2026-06-07",
                importResult.Results,
                importResult.PendingMatchNames);

            using var workbook = new XLWorkbook(dayTwoPath);
            var sheet = workbook.Worksheet("对阵记录表");
            var firstPendingMatchName = importResult.PendingMatchNames[0];
            var row = Enumerable.Range(6, sheet.LastRowUsed()!.RowNumber() - 5)
                .First(index => sheet.Cell(index, 14).GetString() == firstPendingMatchName);

            Assert.Equal("2026-06-07", sheet.Cell(row, 2).GetString());
            Assert.Equal("待安排", sheet.Cell(row, 3).GetString());
            Assert.Equal("待安排", sheet.Cell(row, 11).GetString());
            Assert.Contains("顺延补赛", sheet.Cell(row, 13).GetString());
        }
        finally
        {
            DeleteIfExists(dayOnePath);
            DeleteIfExists(dayTwoPath);
        }
    }

    [Fact]
    public void ScheduleGridA4PdfSplitsOnePagePerCompetitionDay()
    {
        var participants = CreateParticipants(8);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout));
        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [
                    new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(16, 0), ["A1"]),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 7), new TimeOnly(14, 0), new TimeOnly(16, 0), ["A1"])
                ],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));
        var workbookPath = Path.Combine(Path.GetTempPath(), $"badminton-schedule-source-{Guid.NewGuid():N}.xlsx");
        var pdfPath = Path.Combine(Path.GetTempPath(), $"badminton-schedule-a4-{Guid.NewGuid():N}.pdf");

        try
        {
            new ScheduleExcelWriter().Write(workbookPath, schedule);
            new DrawResultVisualWriter().Write(
                pdfPath,
                workbookPath,
                "时间场地网格",
                DrawResultVisualFormat.A4Pdf);

            AssertFileHeader(pdfPath, [0x25, 0x50, 0x44, 0x46]);
            AssertPdfUsesTextLayer(pdfPath);
            Assert.InRange(new FileInfo(pdfPath).Length, 1, 80L * 1024L * 1024L);
            Assert.Equal(schedule.DayCount, CountPdfPages(pdfPath));
        }
        finally
        {
            DeleteIfExists(workbookPath);
            DeleteIfExists(pdfPath);
        }
    }

    [Fact]
    public void DrawResultExcelWriterAnnotatesBracketWithScheduledTimes()
    {
        var participants = CreateParticipants(8);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout));
        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [
                    new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(16, 0), ["A1", "A2"]),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 7), new TimeOnly(14, 0), new TimeOnly(15, 0), ["A1"])
                ],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));
        var outputPath = Path.Combine(Path.GetTempPath(), $"badminton-timed-bracket-{Guid.NewGuid():N}.xlsx");

        try
        {
            new DrawResultExcelWriter().Write(outputPath, result, participants, schedule);

            using var workbook = new XLWorkbook(outputPath);
            var bracketTexts = workbook.Worksheet("对阵表")
                .CellsUsed()
                .Select(cell => cell.GetString())
                .ToList();
            var timedWinnerCell = workbook.Worksheet("对阵表")
                .CellsUsed()
                .First(cell => cell.GetString().StartsWith("胜者", StringComparison.Ordinal)
                    && cell.GetString().Contains("2026-06-06 14:30-15:00", StringComparison.Ordinal)
                    && cell.GetString().Contains("A", StringComparison.Ordinal));

            Assert.Contains(bracketTexts, text => text.Contains("2026-06-06 14:00-14:30", StringComparison.Ordinal));
            Assert.Contains(bracketTexts, text => text.Contains("A1", StringComparison.Ordinal));
            Assert.Contains(bracketTexts, text => text.Contains("2026-06-07", StringComparison.Ordinal));
            Assert.True(timedWinnerCell.WorksheetRow().Height >= 40);
        }
        finally
        {
            DeleteIfExists(outputPath);
        }
    }
}
