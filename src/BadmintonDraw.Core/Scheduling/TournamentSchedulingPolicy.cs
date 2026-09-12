using BadmintonDraw.Core.Tournaments;
namespace BadmintonDraw.Core.Scheduling;
public sealed record TournamentSchedulingPolicy(ScheduleAutoSchedulingStrategy Strategy,
    IReadOnlyList<TournamentDayLoadTarget> DayLoadTargets, bool SynchronizeStageWaves,
    IReadOnlyList<TournamentStageWaveTarget> StageWaveTargets, IReadOnlyList<TournamentFinalDayRule> FinalDayRules)
{
    private IReadOnlyList<TournamentDayLoadTarget> dayLoadTargets = WorkspaceSnapshot.List(DayLoadTargets);
    private IReadOnlyList<TournamentStageWaveTarget> stageWaveTargets = WorkspaceSnapshot.List(StageWaveTargets);
    private IReadOnlyList<TournamentFinalDayRule> finalDayRules = WorkspaceSnapshot.List(FinalDayRules);
    private IReadOnlyDictionary<Guid, ProjectMatchTiming> projectTimings = WorkspaceSnapshot.Dictionary(new Dictionary<Guid, ProjectMatchTiming>());
    public IReadOnlyList<TournamentDayLoadTarget> DayLoadTargets { get => dayLoadTargets; init => dayLoadTargets = WorkspaceSnapshot.List(value); }
    public IReadOnlyList<TournamentStageWaveTarget> StageWaveTargets { get => stageWaveTargets; init => stageWaveTargets = WorkspaceSnapshot.List(value); }
    public IReadOnlyList<TournamentFinalDayRule> FinalDayRules { get => finalDayRules; init => finalDayRules = WorkspaceSnapshot.List(value); }
    public IReadOnlyDictionary<Guid, ProjectMatchTiming> ProjectTimings { get => projectTimings; init => projectTimings = WorkspaceSnapshot.Dictionary(value); }
}

public sealed record TournamentDayLoadTarget(string DayLabel, double TargetUtilization, double WarningUtilization);
public sealed record TournamentStageWaveTarget(string DayLabel, double CumulativeProgress);
public enum TournamentFinalDayMatchCategory { Final = 1, Semifinal = 2, Bronze = 3, Placement5To8 = 4 }
// All preferences are soft scores; none overrides physical constraints.
public enum TournamentFinalDayPreference { Flexible = 0, AvoidFinalDay = 1, PreferFinalDay = 2, StronglyPreferFinalDay = 3 }
public sealed record TournamentFinalDayRule(Guid ProjectId, TournamentFinalDayMatchCategory Category, TournamentFinalDayPreference Preference);
