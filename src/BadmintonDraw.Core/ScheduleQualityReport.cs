namespace BadmintonDraw.Core;

public sealed record ScheduleQualityReport(
    string StrategyName,
    int HardConstraintCount,
    int SoftScore,
    IReadOnlyList<ScheduleQualityInsight> Insights)
{
    public static ScheduleQualityReport Empty(string strategyName)
    {
        return new ScheduleQualityReport(strategyName, 0, 0, Array.Empty<ScheduleQualityInsight>());
    }
}

public sealed record ScheduleQualityInsight(
    string Category,
    string Summary,
    int ScoreImpact = 0);
