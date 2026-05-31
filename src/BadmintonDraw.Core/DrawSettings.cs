namespace BadmintonDraw.Core;

public sealed record DrawSettings(
    CompetitionMode CompetitionMode,
    EventKind EventKind,
    int GroupCount,
    string RandomSeed,
    DrawAlgorithmVersion AlgorithmVersion = DrawAlgorithmVersion.PerGroupPowerOfTwo,
    KnockoutGoal KnockoutGoal = KnockoutGoal.OneQualifierPerGroup)
{
    public bool IsKnockout =>
        CompetitionMode is CompetitionMode.SinglesKnockout or CompetitionMode.TeamKnockout;

    public bool IsRoundRobin =>
        CompetitionMode is CompetitionMode.SinglesRoundRobin or CompetitionMode.TeamRoundRobin;
}
