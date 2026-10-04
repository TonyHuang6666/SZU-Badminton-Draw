namespace BadmintonDraw.Core.Scheduling;

internal enum BoundedValidationStatus { Valid, Invalid, Unknown }
internal sealed record BoundedPlacementValidation(BoundedValidationStatus Status, IReadOnlyList<SchedulingViolation> Violations);

public sealed partial class TournamentPlacementValidator
{
    internal BoundedPlacementValidation ValidatePlacementBounded(MatchPlacement candidate, TournamentSearchState state,
        SchedulingWorkBudget budget, SchedulingRunPhase phase) => Bounded(state, budget, phase,
            run => ValidatePlacementCore(candidate, state.Placements, run, null));

    internal BoundedPlacementValidation ValidateScheduleBounded(TournamentSearchState state, SchedulingWorkBudget budget,
        SchedulingRunPhase phase, bool requireComplete = true) => Bounded(state, budget, phase,
            run => ValidateScheduleCore(state.Placements, requireComplete, run));

    private BoundedPlacementValidation Bounded(TournamentSearchState state, SchedulingWorkBudget budget,
        SchedulingRunPhase phase, Func<ValidationRun, TournamentPlacementValidation> validate)
    {
        if (!ReferenceEquals(Context, state.Context)) throw new ArgumentException("Search state belongs to another graph context.", nameof(state));
        try
        {
            var result = validate(new(state, budget, phase));
            // A canceled gate must not publish a success even on a cache hit.
            if (budget.IsCanceled) return new(BoundedValidationStatus.Unknown, []);
            return new(result.IsValid ? BoundedValidationStatus.Valid : BoundedValidationStatus.Invalid, result.Violations);
        }
        catch (ValidationInterruptedException) { return new(BoundedValidationStatus.Unknown, []); }
    }

    private sealed class ValidationRun(TournamentSearchState state, SchedulingWorkBudget budget, SchedulingRunPhase phase)
    {
        internal TournamentSearchState State { get; } = state;
        internal SchedulingWorkBudget Budget { get; } = budget;
        internal SchedulingRunPhase Phase { get; } = phase;
        internal void Charge(long units = 1)
        {
            if (!Budget.TrySpend(Phase, units)) throw new ValidationInterruptedException();
        }
    }

    private sealed class ValidationInterruptedException : Exception;
}
