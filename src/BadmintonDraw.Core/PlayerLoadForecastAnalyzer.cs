using BadmintonDraw.Core.Scheduling;

namespace BadmintonDraw.Core;

public sealed class PlayerLoadForecastAnalyzer
{
    /// <summary>Bounded graph forecast; maximum bounds and probability completeness are reported separately.</summary>
    public IReadOnlyList<TournamentPlayerDailyLoadForecast> Analyze(
        TournamentSchedulingRequest request,
        IReadOnlyDictionary<Guid, MatchPlacement> placements,
        double winProbability = .5)
    {
        var budget = new SchedulingWorkBudget(TournamentSchedulingOptions.Default, default);
        if (!GraphSchedulingCandidates.TryCreate(request, budget, out var context)) return [];
        if (context!.InputViolations.Count > 0)
            throw new ArgumentException(
                "无法分析无效的比赛关系图或资源；请先调用 TournamentPlacementValidator.ValidateInput。",
                nameof(request));
        return TournamentPlayerLoadAnalysis.AnalyzeBounded(context, placements, budget, out _, winProbability);
    }
}
