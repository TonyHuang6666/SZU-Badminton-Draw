namespace BadmintonDraw.Core;

public sealed record DrawResult(
    IReadOnlyList<DrawGroup> Groups,
    IReadOnlyList<DrawGroup> RoundOneGroups,
    IReadOnlyList<DrawGroup> ByeGroups,
    DrawSettings Settings,
    DrawAuditInfo Audit)
{
    public bool HasKnockoutRound => RoundOneGroups.Any(group => group.Participants.Count > 0);
}
