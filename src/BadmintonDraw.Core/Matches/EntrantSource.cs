using BadmintonDraw.Core.Tournaments;
using System.Text.Json.Serialization;
namespace BadmintonDraw.Core.Matches;
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$source")]
[JsonDerivedType(typeof(EntrantSource.Participant), "participant")]
[JsonDerivedType(typeof(EntrantSource.WinnerOf), "winner")]
[JsonDerivedType(typeof(EntrantSource.LoserOf), "loser")]
[JsonDerivedType(typeof(EntrantSource.Bye), "bye")]
public abstract record EntrantSource
{
    private EntrantSource() { }
    public sealed record Participant(string IdentityKey, string DisplayName,
        IReadOnlyList<CrossEventPlayerIdentity> Players) : EntrantSource
    {
        private IReadOnlyList<CrossEventPlayerIdentity> players = WorkspaceSnapshot.List(Players);
        public IReadOnlyList<CrossEventPlayerIdentity> Players { get => players; init => players = WorkspaceSnapshot.List(value); }
    }
    public sealed record WinnerOf(Guid MatchId) : EntrantSource;
    public sealed record LoserOf(Guid MatchId) : EntrantSource;
    public sealed record Bye : EntrantSource;
}
