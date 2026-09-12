using BadmintonDraw.Core.Tournaments;
namespace BadmintonDraw.Core.Scheduling;
public sealed record TournamentSchedulingPolicy(ScheduleAutoSchedulingStrategy Strategy,
    IReadOnlyList<CrossEventDayLoadTarget> DayLoadTargets, bool SynchronizeStageWaves,
    IReadOnlyList<CrossEventStageWaveTarget> StageWaveTargets, IReadOnlyList<CrossEventFinalDayRule> FinalDayRules)
{
    private IReadOnlyList<CrossEventDayLoadTarget> dayLoadTargets = WorkspaceSnapshot.List(DayLoadTargets);
    private IReadOnlyList<CrossEventStageWaveTarget> stageWaveTargets = WorkspaceSnapshot.List(StageWaveTargets);
    private IReadOnlyList<CrossEventFinalDayRule> finalDayRules = WorkspaceSnapshot.List(FinalDayRules);
    public IReadOnlyList<CrossEventDayLoadTarget> DayLoadTargets { get => dayLoadTargets; init => dayLoadTargets = WorkspaceSnapshot.List(value); }
    public IReadOnlyList<CrossEventStageWaveTarget> StageWaveTargets { get => stageWaveTargets; init => stageWaveTargets = WorkspaceSnapshot.List(value); }
    public IReadOnlyList<CrossEventFinalDayRule> FinalDayRules { get => finalDayRules; init => finalDayRules = WorkspaceSnapshot.List(value); }
}
