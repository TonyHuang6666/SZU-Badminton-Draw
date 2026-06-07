namespace BadmintonDraw.Core;

public sealed record UnscheduledMatchPreview(
    int Order,
    int GroupNumber,
    string GroupName,
    string Phase,
    string MatchName,
    string SideA,
    string SideB,
    string Note,
    bool SameUnit,
    string Reason);
