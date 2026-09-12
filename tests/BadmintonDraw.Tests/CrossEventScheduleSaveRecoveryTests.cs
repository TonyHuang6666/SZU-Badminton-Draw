using BadmintonDraw.Core;
using BadmintonDraw.Excel;
using BadmintonDraw.Workflows;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed partial class DrawWorkflowTests
{
    [Fact]
    public void CrossEventScheduleSaveValidatesEveryArchiveBeforeCreatingBackups()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-cross-event-save-preflight-{Guid.NewGuid():N}");
        var firstProgressPath = Path.Combine(directory, "男单.szbd");
        var secondProgressPath = Path.Combine(directory, "混双.szbd");

        try
        {
            Directory.CreateDirectory(directory);
            CreateCrossEventSaveRecoveryArchives(firstProgressPath, secondProgressPath);
            var workflow = new CrossEventConflictWorkflow();
            var board = workflow.LoadScheduleBoard([firstProgressPath, secondProgressPath], minimumRestMinutes: 0);
            var invalidSecondSource = board.Sources[1] with
            {
                Matches = board.Sources[1].Matches
                    .Select(match => match with { MatchName = $"无效-{match.MatchName}" })
                    .ToList()
            };
            var invalidBoard = board with { Sources = [board.Sources[0], invalidSecondSource] };

            var error = Assert.Throws<TournamentProgressException>(() => workflow.SaveScheduleBoard(invalidBoard));

            Assert.Contains("场次集合与原存档不一致", error.Message);
            Assert.False(Directory.Exists(Path.Combine(directory, "Backups")));
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void CrossEventScheduleSaveRestoresEarlierArchivesWhenLaterUpdateFails()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-cross-event-save-rollback-{Guid.NewGuid():N}");
        var firstProgressPath = Path.Combine(directory, "男单.szbd");
        var secondProgressPath = Path.Combine(directory, "混双.szbd");

        try
        {
            Directory.CreateDirectory(directory);
            CreateCrossEventSaveRecoveryArchives(firstProgressPath, secondProgressPath);
            var realStore = new TournamentProgressStore();
            var originalFirstSchedule = realStore.Read(firstProgressPath).Snapshot.Schedule;
            var originalSecondSchedule = realStore.Read(secondProgressPath).Snapshot.Schedule;
            var workflow = new CrossEventConflictWorkflow(
                new FaultInjectingTournamentProgressStore(failOnUpdateNumber: 2, failRestore: false));
            var board = workflow.LoadScheduleBoard([firstProgressPath, secondProgressPath], minimumRestMinutes: 0);
            var firstKey = board.Items.Single(item => item.EventName == "男单").Key;
            var adjusted = workflow.MoveScheduleItem(
                board,
                firstKey,
                "2026-06-14",
                new TimeOnly(14, 0),
                "B1");

            var error = Assert.Throws<TournamentProgressException>(() => workflow.SaveScheduleBoard(adjusted));

            Assert.Contains("已自动恢复", error.Message);
            AssertSchedulePositionRestored(
                originalFirstSchedule,
                realStore.Read(firstProgressPath).Snapshot.Schedule);
            AssertSchedulePositionRestored(
                originalSecondSchedule,
                realStore.Read(secondProgressPath).Snapshot.Schedule);
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void CrossEventScheduleSaveReportsExactBackupWhenAutomaticRestoreFails()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-cross-event-save-manual-recovery-{Guid.NewGuid():N}");
        var firstProgressPath = Path.Combine(directory, "男单.szbd");
        var secondProgressPath = Path.Combine(directory, "混双.szbd");

        try
        {
            Directory.CreateDirectory(directory);
            CreateCrossEventSaveRecoveryArchives(firstProgressPath, secondProgressPath);
            var workflow = new CrossEventConflictWorkflow(
                new FaultInjectingTournamentProgressStore(failOnUpdateNumber: 2, failRestore: true));
            var board = workflow.LoadScheduleBoard([firstProgressPath, secondProgressPath], minimumRestMinutes: 0);
            var firstKey = board.Items.Single(item => item.EventName == "男单").Key;
            var adjusted = workflow.MoveScheduleItem(
                board,
                firstKey,
                "2026-06-14",
                new TimeOnly(14, 0),
                "B1");

            var error = Assert.Throws<TournamentProgressException>(() => workflow.SaveScheduleBoard(adjusted));
            var backupPath = Assert.Single(
                Directory.GetFiles(Path.Combine(directory, "Backups"), "男单_*.szbd"));

            Assert.Contains("未能自动恢复", error.Message);
            Assert.Contains(firstProgressPath, error.Message);
            Assert.Contains(backupPath, error.Message);
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    private static void CreateCrossEventSaveRecoveryArchives(
        string firstProgressPath,
        string secondProgressPath)
    {
        var days = new[]
        {
            new ScheduleDaySettings(
                new DateOnly(2026, 6, 13),
                new TimeOnly(14, 0),
                new TimeOnly(16, 0),
                ["B1", "C1"]),
            new ScheduleDaySettings(
                new DateOnly(2026, 6, 14),
                new TimeOnly(14, 0),
                new TimeOnly(16, 0),
                ["B1", "C1"])
        };
        var store = new TournamentProgressStore();
        store.Create(
            firstProgressPath,
            CreateManualProgressSnapshot(
                "男单",
                [new DrawParticipant("张三", PrimaryName: "张三"), new DrawParticipant("李四", PrimaryName: "李四")],
                CreateSingleMatchSchedule("男单1", "张三", "李四", new TimeOnly(14, 0), new TimeOnly(14, 30), "B1") with
                {
                    Settings = new ScheduleSettings(days, MatchMinutes: 30, MaxMatchesPerEntrantPerDay: 2)
                }));
        store.Create(
            secondProgressPath,
            CreateManualProgressSnapshot(
                "混双",
                [
                    new DrawParticipant("[王五 赵六]", PrimaryName: "王五", PartnerName: "赵六"),
                    new DrawParticipant("[孙七 周八]", PrimaryName: "孙七", PartnerName: "周八")
                ],
                CreateSingleMatchSchedule("混双1", "[王五 赵六]", "[孙七 周八]", new TimeOnly(14, 30), new TimeOnly(15, 0), "C1") with
                {
                    Settings = new ScheduleSettings(days, MatchMinutes: 30, MaxMatchesPerEntrantPerDay: 2)
                }));
    }

    private static void AssertSchedulePositionRestored(SchedulePlan expected, SchedulePlan actual)
    {
        var expectedMatch = Assert.Single(expected.Matches);
        var actualMatch = Assert.Single(actual.Matches);
        Assert.Equal(expectedMatch.DayLabel, actualMatch.DayLabel);
        Assert.Equal(expectedMatch.StartTime, actualMatch.StartTime);
        Assert.Equal(expectedMatch.EndTime, actualMatch.EndTime);
        Assert.Equal(expectedMatch.Court, actualMatch.Court);
    }

    private sealed class FaultInjectingTournamentProgressStore(
        int failOnUpdateNumber,
        bool failRestore) : ITournamentProgressStore
    {
        private readonly TournamentProgressStore _inner = new();
        private int _updateCount;

        public TournamentProgressState Read(string filePath)
        {
            return _inner.Read(filePath);
        }

        public void ValidateScheduleUpdate(string filePath, SchedulePlan schedule)
        {
            _inner.ValidateScheduleUpdate(filePath, schedule);
        }

        public TournamentProgressScheduleUpdateOutcome UpdateSchedule(string filePath, SchedulePlan schedule)
        {
            _updateCount++;
            if (_updateCount == failOnUpdateNumber)
            {
                throw new TournamentProgressException("注入的后续存档写入故障。");
            }

            return _inner.UpdateSchedule(filePath, schedule);
        }

        public TournamentProgressState RestoreBackup(string filePath, string backupPath)
        {
            if (failRestore)
            {
                throw new TournamentProgressException("注入的自动恢复故障。");
            }

            return _inner.RestoreBackup(filePath, backupPath);
        }
    }
}
