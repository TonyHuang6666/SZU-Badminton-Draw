namespace BadmintonDraw.Core.Scheduling;

/// <summary>Duration estimates only. Dates, courts, rest and daily limits belong to the global resource plan.</summary>
public sealed record ProjectMatchTiming(int MatchMinutes, int? KnockoutTimingBoundaryEntrants = null, int? BeforeBoundaryMinutes = null);
