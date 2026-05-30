namespace BadmintonDraw.Core;

public sealed record DrawParticipant(
    string DisplayName,
    bool IsSeed = false,
    int? SeedRank = null,
    string? PrimaryName = null,
    string? PartnerName = null,
    string? TeamName = null,
    string? Note = null,
    string? PartnerTeamName = null)
{
    public string NormalizedDisplayName => DisplayName.Trim();
}
