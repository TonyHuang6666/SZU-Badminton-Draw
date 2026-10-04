namespace BadmintonDraw.Core.Scheduling;
public sealed record MatchPlacement(Guid MatchId, string DayLabel, TimeOnly StartTime, TimeOnly EndTime, string Court);
