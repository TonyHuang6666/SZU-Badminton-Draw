using BadmintonDraw.Core.Matches;

namespace BadmintonDraw.Core.Tournaments;

/// <summary>Shared structural result validity. Record import owns supported score parsing, not this gate.</summary>
public static class TournamentResultRules
{
    public static bool IsValidValue(TournamentMatchResult result, IEnumerable<DateOnly> resourceDates) =>
        ParticipantValid(result.Winner) && ParticipantValid(result.Loser) &&
        result.Score is not null && result.RecordedAt != default && Enum.IsDefined(result.Kind) &&
        (result.Kind == TournamentResultKind.Walkover ? result.DurationMinutes == 0 :
            result.DurationMinutes > 0 && !string.IsNullOrWhiteSpace(result.Score)) &&
        (result.ActualPlayedDay is null || resourceDates.Contains(result.ActualPlayedDay.Value));

    public static bool SameParticipants(TournamentMatchResult a, TournamentMatchResult b) =>
        SameEntrant(a.Winner, b.Winner) && SameEntrant(a.Loser, b.Loser);

    public static bool SameMetadata(TournamentMatchResult a, TournamentMatchResult b) =>
        a.Kind == b.Kind && a.Score == b.Score && a.DurationMinutes == b.DurationMinutes && a.ActualPlayedDay == b.ActualPlayedDay;

    internal static bool SameVersion(TournamentMatchResult a, TournamentMatchResult b) =>
        a.Key == b.Key && SameParticipants(a, b) && SameMetadata(a, b) && a.RecordedAt == b.RecordedAt;

    private static bool SameEntrant(EntrantSource.Participant a, EntrantSource.Participant b) =>
        a.IdentityKey == b.IdentityKey && a.Players.Select(p => p.IdentityKey).Order(StringComparer.Ordinal)
            .SequenceEqual(b.Players.Select(p => p.IdentityKey).Order(StringComparer.Ordinal));

    private static bool ParticipantValid(EntrantSource.Participant? participant) =>
        participant is not null && !string.IsNullOrWhiteSpace(participant.IdentityKey) && !string.IsNullOrWhiteSpace(participant.DisplayName) &&
        participant.Players.Count > 0 && participant.Players.All(player => !string.IsNullOrWhiteSpace(player.Name)) &&
        participant.Players.Select(player => player.IdentityKey).Distinct(StringComparer.Ordinal).Count() == participant.Players.Count;
}
