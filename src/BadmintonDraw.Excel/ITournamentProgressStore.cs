using BadmintonDraw.Core;

namespace BadmintonDraw.Excel;

public interface ITournamentProgressStore
{
    TournamentProgressState Read(string filePath);

    void ValidateScheduleUpdate(string filePath, SchedulePlan schedule);

    TournamentProgressScheduleUpdateOutcome UpdateSchedule(string filePath, SchedulePlan schedule);

    TournamentProgressState RestoreBackup(string filePath, string backupPath);
}
