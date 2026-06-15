namespace BadmintonDraw.Core;

public sealed record ScheduledMatch(
    int Order,
    string DayLabel,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string Court,
    int GroupNumber,
    string GroupName,
    string Phase,
    string MatchName,
    string SideA,
    string SideB,
    string Note = "",
    bool SameUnit = false,
    string MatchId = "",
    IReadOnlyList<ScheduleMatchDependency>? Dependencies = null)
{
    public string MatchId { get; init; } = string.IsNullOrWhiteSpace(MatchId) ? MatchName : MatchId;

    public IReadOnlyList<ScheduleMatchDependency> Dependencies { get; init; } = Dependencies ?? Array.Empty<ScheduleMatchDependency>();

    public string TimeRange => $"{StartTime:HH:mm}-{EndTime:HH:mm}";

    public int DurationMinutes => Math.Max(1, (int)(EndTime - StartTime).TotalMinutes);

    public bool Equals(ScheduledMatch? other)
    {
        if (ReferenceEquals(null, other))
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Order == other.Order
               && DayLabel == other.DayLabel
               && StartTime == other.StartTime
               && EndTime == other.EndTime
               && Court == other.Court
               && GroupNumber == other.GroupNumber
               && GroupName == other.GroupName
               && Phase == other.Phase
               && MatchName == other.MatchName
               && SideA == other.SideA
               && SideB == other.SideB
               && Note == other.Note
               && SameUnit == other.SameUnit
               && MatchId == other.MatchId
               && Dependencies.SequenceEqual(other.Dependencies);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Order);
        hash.Add(DayLabel);
        hash.Add(StartTime);
        hash.Add(EndTime);
        hash.Add(Court);
        hash.Add(GroupNumber);
        hash.Add(GroupName);
        hash.Add(Phase);
        hash.Add(MatchName);
        hash.Add(SideA);
        hash.Add(SideB);
        hash.Add(Note);
        hash.Add(SameUnit);
        hash.Add(MatchId);
        foreach (var dependency in Dependencies)
        {
            hash.Add(dependency);
        }

        return hash.ToHashCode();
    }
}
