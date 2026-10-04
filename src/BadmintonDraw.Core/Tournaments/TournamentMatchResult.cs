using BadmintonDraw.Core.Matches;
namespace BadmintonDraw.Core.Tournaments;
public sealed record TournamentMatchResult(WorkspaceMatchKey Key, EntrantSource.Participant Winner,
    EntrantSource.Participant Loser, string Score, int DurationMinutes, DateTimeOffset RecordedAt)
{
    public TournamentResultKind Kind { get; init; } = TournamentResultKind.Played;
    /// <summary>Null is explicitly unknown; it is never inferred from a scheduled placement.</summary>
    public DateOnly? ActualPlayedDay { get; init; }
}

public enum TournamentResultKind { Played, Walkover }
