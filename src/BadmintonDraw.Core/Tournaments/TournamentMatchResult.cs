using BadmintonDraw.Core.Matches;
namespace BadmintonDraw.Core.Tournaments;
public sealed record TournamentMatchResult(WorkspaceMatchKey Key, EntrantSource.Participant Winner,
    EntrantSource.Participant Loser, string Score, int DurationMinutes, DateTimeOffset RecordedAt);
