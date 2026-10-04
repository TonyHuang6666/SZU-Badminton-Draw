namespace BadmintonDraw.Core.Scheduling;

/// <summary>One deterministic global search over time-free graph nodes. Failure never contains a partial schedule.</summary>
public sealed class TournamentScheduler
{
    public TournamentSchedulingResult Generate(TournamentSchedulingRequest request) => Generate(request, TournamentSchedulingOptions.Default);

    internal TournamentSchedulingResult Generate(TournamentSchedulingRequest request, int capacityProofWorkUnits)
        => Generate(request, TournamentSchedulingOptions.Default with { PreflightWorkUnits = Math.Max(0, capacityProofWorkUnits) });

    public TournamentSchedulingResult Generate(TournamentSchedulingRequest request, TournamentSchedulingOptions options,
        CancellationToken cancellationToken = default)
        => Generate(request, options, cancellationToken, static (effectiveRequest, state, budget) =>
            new TournamentScheduleQualityAnalyzer().AnalyzeBounded(effectiveRequest, state, budget, true));

    // Per-call internal seam for deterministic cancellation at the quality/publication
    // boundary. Public generation always uses the real bounded analyzer above.
    internal TournamentSchedulingResult Generate(TournamentSchedulingRequest request, TournamentSchedulingOptions options,
        CancellationToken cancellationToken,
        Func<TournamentSchedulingRequest, TournamentSearchState, SchedulingWorkBudget, TournamentScheduleQuality> qualityRun)
    {
        var budget = new SchedulingWorkBudget(options, cancellationToken);
        var preflightStatus = SchedulingPreflightStatus.NotRun;
        IReadOnlyDictionary<Guid, MatchPlacement> placements = new Dictionary<Guid, MatchPlacement>();
        if (!GraphSchedulingCandidates.TryCreate(request, budget, out var built))
            return ContextFailure(request, budget);
        var context = built!;
        TournamentSchedulingResult.Failure Failure(IReadOnlyList<SchedulingViolation> issues,
            SchedulingFailureKind kind = SchedulingFailureKind.SearchIncomplete, SchedulingRunPhase phase = SchedulingRunPhase.Search,
            SchedulingCapacityEvidence? evidence = null, bool exhausted = false)
        {
            var failure = Fail(context, placements, issues, kind != SchedulingFailureKind.InvalidInput);
            return new(failure.Detail with { CapacityEvidence = evidence,
                Diagnostics = new(phase, preflightStatus, kind, budget.UsedWorkUnits, context.Request.Resources, context.Request.Policy)
                    { CachePeaks = budget.CachePeaks, RejectedBudgetPhases = RejectedPhases(budget),
                        ExhaustedPhase = exhausted && budget.HasFailedSpend(phase) && !budget.IsCanceled ? phase : null } });
        }
        var validator = TournamentPlacementValidator.FromContext(context);
        if (!validator.ValidateInput().IsValid) return Failure(context.InputViolations, SchedulingFailureKind.InvalidInput, SchedulingRunPhase.Context);
        // Resolve runtime defaults once. Balanced retains requested settings when saved:
        // expanding its defaults would turn them into explicit preferences on regeneration.
        try { context.ApplyEffectivePolicy(); }
        catch (SchedulingContextInterruptedException) { return ContextFailure(request, budget); }
        request = context.Request;
        placements = context.LockedIds.ToDictionary(id => id, id => request.BaselinePlacements![id]);
        var state = new TournamentSearchState(context, placements);
        placements = state.Placements;
        // Locked descendants can have unlocked predecessors: missing predecessors are checked
        // as they are placed and by the final gate, while all other locked conflicts fail now.
        var lockedValidation = validator.ValidateScheduleBounded(state, budget, SchedulingRunPhase.Validation, false);
        if (lockedValidation.Status == BoundedValidationStatus.Unknown)
            return Failure([], budget.IsCanceled ? SchedulingFailureKind.Canceled : SchedulingFailureKind.ValidationIncomplete,
                SchedulingRunPhase.Validation, exhausted: true);
        var lockedIssues = lockedValidation.Violations.Where(v => v.Code != SchedulingConstraintCode.MissingPlacement).ToArray();
        if (lockedIssues.Length > 0) return Failure(lockedIssues, SchedulingFailureKind.InvalidInput, SchedulingRunPhase.Validation);
        var preflight = TournamentPlayerCapacity.Check(context, budget);
        preflightStatus = preflight.Status;
        if (budget.IsCanceled) return Failure([], SchedulingFailureKind.Canceled, SchedulingRunPhase.Preflight);
        if (preflight.Status == SchedulingPreflightStatus.ProvenInfeasible)
        {
            var failure = Failure(preflight.Violations, SchedulingFailureKind.ProvenInfeasible, SchedulingRunPhase.Preflight, preflight.Evidence);
            return new TournamentSchedulingResult.Failure(failure.Detail with
            {
                Message = preflight.Violations[0].Message,
                Suggestions = preflight.Evidence!.Kind == "PlayerDailyCap"
                    ? ["增加比赛日，或由赛事组织者明确复核每日场次上限。", "仅增加场地、裁判或延长每日时段不能解决该选手的总场次容量矛盾。"]
                    : preflight.Evidence.Kind == "PlayerTime"
                        ? ["增加比赛日或延长每日可用时段。", "仅增加场地或裁判不能增加同一选手的可用比赛时间。"]
                        : ["增加比赛日或延长每日可用时段。", "增加可用场地或裁判，并检查不可用时段。"]
            });
        }
        foreach (var day in context.Days)
            if (!budget.TrySpend(SchedulingRunPhase.Search, SchedulingCapacityArithmetic.Add(
                1L + (day.UnavailableCourtWindows?.Count ?? 0), SchedulingCapacityArithmetic.DayIntegrationWork(day))))
                return Failure([], budget.IsCanceled ? SchedulingFailureKind.Canceled : SchedulingFailureKind.SearchIncomplete,
                    exhausted: true);
        var scorer = new TournamentPlacementScorer(context);
        var search = TournamentScheduleSearch.Run(validator, state, scorer, budget, options);
        placements = search.DiagnosticPlacements;
        if (budget.IsCanceled || search.CompletePlacements is null)
            return Failure(search.Violations, budget.IsCanceled ? SchedulingFailureKind.Canceled :
                    search.Phase == SchedulingRunPhase.Validation ? SchedulingFailureKind.ValidationIncomplete : SchedulingFailureKind.SearchIncomplete,
                search.Phase, exhausted: true);
        var complete = search.CompletePlacements;
        placements = TournamentScheduleBalancer.Improve(validator, state, budget, options);
        placements = TournamentScheduleSpreader.Spread(validator, placements, budget, options);
        // Optional proposals have passed whole-schedule bounded validation. Preserve the
        // complete search witness if the additional final gate cannot finish.
        var finalState = new TournamentSearchState(context, placements);
        var final = validator.ValidateScheduleBounded(finalState, budget, SchedulingRunPhase.Validation);
        if (budget.IsCanceled) return Failure([], SchedulingFailureKind.Canceled, SchedulingRunPhase.Validation);
        // Optional optimization cannot erase the complete schedule already proved above.
        if (final.Status != BoundedValidationStatus.Valid)
        {
            placements = complete;
            finalState = new(context, complete);
        }
        var revisions = request.MatchGraphs.ToDictionary(g => g.ProjectId, g => g.Revision);
        var savedPolicy = request.Policy.Strategy == ScheduleAutoSchedulingStrategy.BalancedRelaxed
            ? context.RequestedPolicy : request.Policy;
        var schedule = new TournamentSchedule(placements, request.Resources, savedPolicy, revisions, request.ScheduleRevision);
        var quality = qualityRun(request, finalState, budget);
        if (budget.IsCanceled) return Failure([], SchedulingFailureKind.Canceled, SchedulingRunPhase.Quality);
        return new TournamentSchedulingResult.Success(schedule, schedule.GraphRevisions, quality)
        { Diagnostics = new(SchedulingRunPhase.Quality, preflightStatus, null, budget.UsedWorkUnits, request.Resources, request.Policy)
            { CachePeaks = budget.CachePeaks, RejectedBudgetPhases = RejectedPhases(budget),
                HasBaselinePlacements = request.BaselinePlacements is { Count: > 0 },
                ExhaustedPhase = budget.HasFailedSpend(SchedulingRunPhase.Quality) ? SchedulingRunPhase.Quality :
                budget.HasFailedSpend(SchedulingRunPhase.Optimization) ? SchedulingRunPhase.Optimization :
                budget.HasFailedSpend(SchedulingRunPhase.Validation) ? SchedulingRunPhase.Validation : null } };
    }

    private static TournamentSchedulingResult.Failure ContextFailure(TournamentSchedulingRequest request, SchedulingWorkBudget budget) =>
        new(new("本次上下文构建未完成；这不表示赛事不可行。", [], [], [], [])
        {
            Diagnostics = new(SchedulingRunPhase.Context, SchedulingPreflightStatus.NotRun,
                budget.IsCanceled ? SchedulingFailureKind.Canceled : SchedulingFailureKind.SearchIncomplete,
                budget.UsedWorkUnits, request.Resources, request.Policy) { CachePeaks = budget.CachePeaks,
                    RejectedBudgetPhases = RejectedPhases(budget), ExhaustedPhase = budget.IsCanceled ? null : SchedulingRunPhase.Context }
        });

    private static SchedulingRunPhase[] RejectedPhases(SchedulingWorkBudget budget) =>
        Enum.GetValues<SchedulingRunPhase>().Where(budget.HasFailedSpend).ToArray();

    private static TournamentSchedulingResult.Failure Fail(GraphSchedulingCandidates context,
        IReadOnlyDictionary<Guid, MatchPlacement> placements, IReadOnlyList<SchedulingViolation> issues, bool includeCapacity)
    {
        var affected = issues.Select(v => v.MatchId).OfType<Guid>().ToHashSet();
        var blocked = context.Nodes.Values.Where(n => !placements.ContainsKey(n.Id) || affected.Contains(n.Id))
            .Select(n => new SchedulingBlockedMatch(n.ProjectId, context.Request.ProjectNames.GetValueOrDefault(n.ProjectId, n.ProjectId.ToString()), n.Id, n.DisplayName,
                issues.Where(v => v.MatchId == n.Id || v.MatchId is null).Select(v => v.Code)
                    .Concat(n.Dependencies.Any(id => !placements.ContainsKey(id)) ? [SchedulingConstraintCode.MissingPlacement] : Array.Empty<SchedulingConstraintCode>())
                    .DefaultIfEmpty(SchedulingConstraintCode.MissingPlacement).Distinct().ToArray())).ToArray();
        // Invalid input is reported before the allowance for resource integration is
        // reserved; formatting that failure must not perform the expensive integral.
        var capacity = includeCapacity ? context.Days.Where(d => d.DayStart < d.DayEnd).Select(d => new SchedulingDayCapacity(d.DayLabel,
            ScheduleResourceCalculator.CalculateDayCapacityMinutes(context.Request.Resources, d),
            placements.Values.Where(p => p.DayLabel == d.DayLabel).Sum(p => (int)(p.EndTime - p.StartTime).TotalMinutes))).ToArray()
            : Array.Empty<SchedulingDayCapacity>();
        return new(new("本次搜索未找到满足全部约束的完整赛程；这不表示所有编排方案都不可能。", blocked, issues, capacity,
            ["增加比赛日或延长每日可用时段。", "增加可用场地或裁判，并检查不可用时段。", "核对预计时长、锁定位置、最短休息和每日上限。"]));
    }
}
