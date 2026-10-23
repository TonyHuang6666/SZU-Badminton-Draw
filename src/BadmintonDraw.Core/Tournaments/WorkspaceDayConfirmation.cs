namespace BadmintonDraw.Core.Tournaments;

public sealed record WorkspaceMatchPostponement(WorkspaceMatchKey MatchKey, string Reason, bool RefereeApproved);

public sealed record WorkspaceDayConfirmation(DateOnly Day, DateTimeOffset ConfirmedAt, long ScheduleRevision,
    int CompletedCount, IReadOnlyList<WorkspaceMatchPostponement> Postponements)
{
    private IReadOnlyList<WorkspaceMatchPostponement> postponements = WorkspaceSnapshot.List(Postponements);
    public IReadOnlyList<WorkspaceMatchPostponement> Postponements { get => postponements; init => postponements = WorkspaceSnapshot.List(value); }
}

public static class WorkspaceDayConfirmationRules
{
    public static bool IsConfirmed(TournamentWorkspace workspace, DateOnly day) => workspace.DayConfirmations.Any(c => c.Day == day);
    public static DateOnly? NextUnconfirmedDay(TournamentWorkspace workspace) => workspace.Resources?.Days.OrderBy(d => d.Date)
        .Where(d => !IsConfirmed(workspace, d.Date)).Select(d => (DateOnly?)d.Date).FirstOrDefault();

    public static void Validate(TournamentWorkspace workspace)
    {
        void Require(bool condition, string message)
        { if (!condition) throw new WorkspaceValidationException("day-confirmation.invalid", message); }
        var days = workspace.Resources?.Days.OrderBy(d => d.Date).ToArray() ?? [];
        var nodes = workspace.Projects.SelectMany(p => p.MatchGraph?.Matches ?? []).Where(n => n.IsPlayable)
            .ToDictionary(n => new WorkspaceMatchKey(n.ProjectId, n.Id));
        var dates = days.ToDictionary(d => d.DayLabel, d => d.Date);
        var confirmed = workspace.DayConfirmations.OrderBy(c => c.Day).ToArray();
        Require(confirmed.Length <= days.Length && confirmed.Select(c => c.Day).Distinct().Count() == confirmed.Length,
            "比赛日确认重复或缺少资源计划。");
        for (var i = 0; i < confirmed.Length; i++)
        {
            var c = confirmed[i];
            Require(c.Day == days[i].Date && c.ConfirmedAt != default && c.ScheduleRevision >= 0 &&
                workspace.Schedule is not null && c.ScheduleRevision <= workspace.Schedule.Revision && c.CompletedCount >= 0,
                "比赛日必须按日期连续确认。");
            Require(c.Postponements.Select(p => p.MatchKey).Distinct().Count() == c.Postponements.Count &&
                c.Postponements.All(p => p.RefereeApproved && !string.IsNullOrWhiteSpace(p.Reason)), "延期必须有原因并经裁判长同意。");
            Require(c.Postponements.All(p => nodes.ContainsKey(p.MatchKey) && workspace.Schedule is not null &&
                dates[workspace.Schedule.Placements[p.MatchKey.MatchId].DayLabel] > c.Day), "延期事实必须引用有效比赛且排在确认日之后。");
            Require(c.Postponements.All(p => !workspace.Results.TryGetValue(p.MatchKey, out var result) ||
                result.ActualPlayedDay is { } actualDay && actualDay > c.Day), "延期比赛的实际比赛日期必须晚于延期确认日。");
            Require(c.CompletedCount == nodes.Keys.Count(k => workspace.Schedule is not null &&
                workspace.Schedule.Placements[k.MatchId].DayLabel == days[i].DayLabel && workspace.Results.ContainsKey(k)),
                "已完成场次数量与确认日赛程不符。");
            Require(workspace.Schedule is not null && workspace.Projects.SelectMany(p => p.MatchGraph?.Matches ?? [])
                .Where(n => n.IsPlayable && workspace.Schedule.Placements[n.Id].DayLabel == days[i].DayLabel)
                .All(n => workspace.Results.ContainsKey(new(n.ProjectId, n.Id))), "已确认比赛日不能包含待完成比赛。");
        }
    }
}
