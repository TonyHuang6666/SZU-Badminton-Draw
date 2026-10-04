using BadmintonDraw.Core.Scheduling;

namespace BadmintonDraw.Core;

/// <summary>Compatibility-named entry point for the single v5 placement validator.</summary>
public sealed class ScheduleConstraintAnalyzer
{
    public TournamentPlacementValidation Analyze(
        TournamentSchedulingRequest request,
        IReadOnlyDictionary<Guid, MatchPlacement> placements) =>
        new TournamentPlacementValidator(request).ValidateSchedule(placements);
}

// Retained only as serialized metadata on the draw-layout DTO used by timed bracket export.
public enum ScheduleConstraintProfile
{
    Campus = 0,
    Formal = 1,
    Audit = 2
}
