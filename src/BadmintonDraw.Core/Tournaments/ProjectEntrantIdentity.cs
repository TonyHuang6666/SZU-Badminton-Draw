using BadmintonDraw.Core.Matches;

namespace BadmintonDraw.Core.Tournaments;

/// <summary>Roster-to-graph identity mapping shared by domain validation and graph construction.</summary>
public static class ProjectEntrantIdentity
{
    public static EntrantSource.Participant Create(EventDiscipline discipline, DrawParticipant participant)
    {
        if (!Enum.IsDefined(discipline) || string.IsNullOrWhiteSpace(participant.DisplayName))
            throw new WorkspaceValidationException("roster.identity", "名单参赛方名称或项目无效。");
        var isTeam = discipline == EventDiscipline.Team;
        var isDoubles = discipline is EventDiscipline.MenDoubles or EventDiscipline.WomenDoubles or EventDiscipline.MixedDoubles;
        var primary = isTeam ? participant.TeamName ?? participant.DisplayName : participant.PrimaryName ?? (isDoubles ? null : participant.DisplayName);
        if (string.IsNullOrWhiteSpace(primary) || (isDoubles && string.IsNullOrWhiteSpace(participant.PartnerName)))
            throw new WorkspaceValidationException("roster.players", "名单必须保留每位选手的姓名，双打必须包含两位搭档。");
        var players = new List<CrossEventPlayerIdentity>
        {
            new(primary.Trim(), isTeam ? "" : participant.PrimaryStudentId?.Trim() ?? "", isTeam)
        };
        if (isDoubles) players.Add(new(participant.PartnerName!.Trim(), participant.PartnerStudentId?.Trim() ?? ""));
        if (players.Select(p => p.IdentityKey).Distinct(StringComparer.Ordinal).Count() != players.Count)
            throw new WorkspaceValidationException("roster.players", "同一参赛方不能包含重复选手身份。");
        return new(IdentityKey(players), participant.DisplayName.Trim(), players);
    }

    /// <summary>Order-independent membership key; student IDs take precedence over names.</summary>
    public static string IdentityKey(IEnumerable<CrossEventPlayerIdentity> players) =>
        System.Text.Json.JsonSerializer.Serialize(players.Select(p => p.IdentityKey).Order(StringComparer.Ordinal));
}
