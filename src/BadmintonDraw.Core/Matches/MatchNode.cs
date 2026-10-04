using BadmintonDraw.Core.Tournaments;
namespace BadmintonDraw.Core.Matches;
public sealed record MatchNode(Guid Id, Guid ProjectId, string OriginalMatchId, int Order,
    int GroupNumber, string Phase, string DisplayName, EntrantSource SideA, EntrantSource SideB,
    int ExpectedDurationMinutes, IReadOnlyList<Guid> Dependencies)
{
    private IReadOnlyList<Guid> dependencies = WorkspaceSnapshot.List(Dependencies);
    public IReadOnlyList<Guid> Dependencies { get => dependencies; init => dependencies = WorkspaceSnapshot.List(value); }
    public bool IsPlayable => SideA is not EntrantSource.Bye && SideB is not EntrantSource.Bye;
    public string GroupName { get; init; } = "";
    public string Note { get; init; } = "";
    public bool SameUnit { get; init; }
    public int? KnockoutEntrantCount { get; init; }
    public bool ForceBeforeTimingBoundary { get; init; }
    public bool IsChampionshipBracket { get; init; }
    public bool IsChampionshipFinal { get; init; }
    public bool IsPlacementPlayoff { get; init; }
}
