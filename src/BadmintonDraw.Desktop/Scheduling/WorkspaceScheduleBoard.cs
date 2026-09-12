using System.Globalization;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.Scheduling;

public sealed record WorkspaceBoardCard(WorkspaceMatchKey Key, string ProjectName, string MatchName, string Phase, string Sides, MatchPlacement Placement, bool IsLocked)
{
    public string Title => ProjectName + " · " + MatchName;
    public string Position => $"{Placement.DayLabel} {Placement.StartTime:HH:mm:ss}–{Placement.EndTime:HH:mm:ss} · {Placement.Court}";
}
public sealed record WorkspaceBoardDay(ScheduleDaySettings Resources, IReadOnlyList<TimeOnly> TimeSlots)
{
    public string DayLabel => Resources.DayLabel;
    public IReadOnlyList<string> Courts => Resources.Courts;
}
public sealed record WorkspaceScheduleBoard(Guid WorkspaceId, long WorkspaceRevision, long ScheduleRevision,
    IReadOnlyList<WorkspaceBoardDay> Days, IReadOnlyList<WorkspaceBoardCard> Cards)
{
    public static WorkspaceScheduleBoard Build(WorkspaceSession session)
    {
        var workspace = session.Workspace; var schedule = workspace.Schedule ?? throw new WorkspaceCommandException(new("schedule.missing", "尚未生成赛程。"));
        var projects = workspace.Projects.ToDictionary(p => p.Id);
        var graphs = workspace.Projects.Select(p => p.MatchGraph!).ToArray();
        var nodes = graphs.SelectMany(g => g.Matches).ToDictionary(n => n.Id);
        var projected = ScheduledMatchProjection.Build(graphs, schedule, workspace.Results);
        var cards = projected.Select(match =>
        {
            var id = Guid.Parse(match.MatchId); var node = nodes[id]; var key = new WorkspaceMatchKey(node.ProjectId, id);
            return new WorkspaceBoardCard(key, projects[node.ProjectId].DisplayName, match.MatchName, match.Phase,
                match.SideA + "\nVS\n" + match.SideB, schedule.Placements[id], workspace.Results.ContainsKey(key));
        }).OrderBy(c => c.Placement.DayLabel, StringComparer.Ordinal).ThenBy(c => c.Placement.StartTime).ThenBy(c => c.ProjectName).ThenBy(c => c.MatchName).ToArray();
        var days = schedule.Resources.Days.OrderBy(d => d.Date).Select(day =>
        {
            // A readable quarter-hour grid also includes every real start, including sub-minute precision.
            var times = new SortedSet<TimeOnly>(cards.Where(c => c.Placement.DayLabel == day.DayLabel).Select(c => c.Placement.StartTime));
            for (var ticks = day.DayStart.Ticks; ticks < day.DayEnd.Ticks; ticks += TimeSpan.TicksPerMinute * 15) times.Add(new(ticks));
            return new WorkspaceBoardDay(day, Array.AsReadOnly(times.ToArray()));
        }).ToArray();
        return new(workspace.Id, workspace.Revision, schedule.Revision, Array.AsReadOnly(days), Array.AsReadOnly(cards));
    }
}

public static class WorkspaceBoardDrag
{
    public static string Encode(WorkspaceScheduleBoard board, WorkspaceMatchKey key) => string.Join('|', "szbd-v5", board.WorkspaceId.ToString("D"),
        board.WorkspaceRevision.ToString(CultureInfo.InvariantCulture), key.ProjectId.ToString("D"), key.MatchId.ToString("D"));
    public static bool TryParse(string? payload, WorkspaceScheduleBoard board, out WorkspaceMatchKey key)
    {
        key = default; var fields = payload?.Split('|');
        if (fields is not { Length: 5 } || fields[0] != "szbd-v5" || !Guid.TryParseExact(fields[1], "D", out var workspace) || workspace != board.WorkspaceId ||
            !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision != board.WorkspaceRevision ||
            !Guid.TryParseExact(fields[3], "D", out var project) || !Guid.TryParseExact(fields[4], "D", out var match)) return false;
        key = new(project, match);
        return board.Cards.Any(c => c.Key == new WorkspaceMatchKey(project, match) && !c.IsLocked);
    }
}

public static class WorkspaceBoardInteraction
{
    public static double AutoScrollDelta(double position, double length) => position < 48 ? -26 : position > length - 48 ? 26 : 0;
    public static double ClampZoom(double value) => double.IsFinite(value) ? Math.Clamp(value, .65, 1.6) : 1;
}

public sealed record WorkspaceBoardMoveIntent(WorkspaceMatchKey Key, string DayLabel, TimeOnly StartTime, string Court);
