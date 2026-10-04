using System.Text.RegularExpressions;

namespace BadmintonDraw.Core.Tournaments;

public static class WorkspaceWinnerOptionText
{
    /// <summary>Adds the side qualifier without parsing or changing the participant's full display label.</summary>
    public static string Format(ScheduleMatchSide side, string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        var prefix = side switch
        {
            ScheduleMatchSide.SideA => "A",
            ScheduleMatchSide.SideB => "B",
            _ => throw new ArgumentOutOfRangeException(nameof(side), side, "未知对阵侧别。")
        };
        return $"{prefix}【{displayName}】";
    }

    /// <summary>Trims and collapses Unicode whitespace only; identity and winner parsing belong to the evaluator.</summary>
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Regex.Replace(value.Trim(), @"\s+", " ");
    }
}
