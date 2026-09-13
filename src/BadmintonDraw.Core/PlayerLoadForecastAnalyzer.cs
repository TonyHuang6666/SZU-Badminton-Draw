using BadmintonDraw.Core.Scheduling;

namespace BadmintonDraw.Core;

public sealed class PlayerLoadForecastAnalyzer
{
    /// <summary>Graph-based forecast; hard maximum remains exact when probability enumeration is unavailable.</summary>
    public IReadOnlyList<TournamentPlayerDailyLoadForecast> Analyze(
        TournamentSchedulingRequest request,
        IReadOnlyDictionary<Guid, MatchPlacement> placements,
        double winProbability = .5)
    {
        var context = new GraphSchedulingCandidates(request);
        if (context.InputViolations.Count > 0)
            throw new ArgumentException(
                "无法分析无效的比赛关系图或资源；请先调用 TournamentPlacementValidator.ValidateInput。",
                nameof(request));
        return TournamentPlayerLoadAnalysis.Analyze(context, placements, winProbability);
    }
}
