using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

/// <summary>Read-only evidence from one published snapshot, never an import candidate or inferred completion.</summary>
public sealed class WorkspaceOperationsHistory
{
    public IReadOnlyList<string> Results { get; }
    public IReadOnlyList<string> Coverage { get; }
    public IReadOnlyList<string> Receipts { get; }
    public IReadOnlyList<string> Corrections { get; }
    public IReadOnlyList<string> Audits { get; }

    public WorkspaceOperationsHistory(TournamentWorkspace workspace)
    {
        var captions = workspace.Projects.SelectMany(p => (p.MatchGraph?.Matches ?? []).Select(n =>
            (Key: new WorkspaceMatchKey(p.Id, n.Id), Caption: p.DisplayName + " · " + n.DisplayName))).ToDictionary(x => x.Key, x => x.Caption);
        string Caption(WorkspaceMatchKey key) => $"{captions.GetValueOrDefault(key, "历史引用")}\n项目：{key.ProjectId:D}；比赛：{key.MatchId:D}";
        string Source(WorkspaceImportLog log) => $"回执：{log.Id:D}\n文件：{log.SourceFileName}\n路径：{log.SourcePath}\nSHA-256：{log.ContentHash}";
        Results = List(workspace.Results.Values.Select(r => Caption(r.Key) + "\n" + Result(r)));
        Coverage = List(workspace.ProcessedDays.Select(day =>
            $"{day.Day:yyyy-MM-dd} · 有效导入记录覆盖（非当日完赛）\n更新时间：{day.UpdatedAt:O}\n" +
            string.Join("\n\n", day.CoveredMatches.Select(key => Caption(key) + "\n" +
                "当前实际比赛日：" + (workspace.Results.GetValueOrDefault(key)?.ActualPlayedDay?.ToString("yyyy-MM-dd") ?? "未知") + "\n" +
                string.Join("\n", workspace.ImportLogs.Where(l => l.VoidedAt is null).SelectMany(l =>
                    l.Rows.Where(r => r.Key == key && r.RecordDay == day.Day).Select(r =>
                        Source(l) + $"\n{r.Location.SheetName} · 第 {r.Location.RowNumber} 行；记录日期：{r.RecordDay:yyyy-MM-dd}")))))));
        Receipts = List(workspace.ImportLogs.Select(log => Source(log) +
            $"\n导入时间：{log.ImportedAt:O}；新增赛果：{log.AddedResultCount}；更正：{log.CorrectionCount}；记录行：{log.Rows.Count}；警告：{log.Warnings.Count}\n" +
            (log.VoidedAt is { } at ? $"已作废：{at:O}\n原因：{log.VoidReason}\n作废审计：{log.VoidedByAuditEventId:D}" : "有效回执") + "\n" +
            string.Join("\n\n", log.Rows.Select(row => Caption(row.Key) +
                $"\n{row.Location.SheetName} · 第 {row.Location.RowNumber} 行；记录日期：{row.RecordDay:yyyy-MM-dd}\n导入时填写赛果：{(row.HadResult ? "是" : "否")}（不是当前完赛状态）")) + "\n" +
            string.Join("\n", log.Warnings.Select(w => $"[{w.Code}] {w.Message}" +
                (w.Location is { } location ? $" · {location.SheetName} · 第 {location.RowNumber} 行" : " · 文件级警告")))));
        var logs = workspace.ImportLogs.ToDictionary(l => l.Id);
        Corrections = List(workspace.ResultHistory.Select(h =>
            $"更正序号：{h.Sequence}；历史 ID：{h.Id:D}\n{Caption(h.Key)}\n更正时间：{h.ChangedAt:O}\n原因：{h.Reason}\n" +
            $"来源回执：{h.ImportLogId:D}；{h.Source.SheetName} · 第 {h.Source.RowNumber} 行\n" +
            (logs.TryGetValue(h.ImportLogId, out var log) ? Source(log) : "历史来源无法在当前快照解析") +
            "\n更正前（已保存）：\n" + Result(h.Before) + "\n更正后（已保存）：\n" + Result(h.After)));
        Audits = List(workspace.AuditEvents.Select(a =>
            $"审计：{a.Id:D}\n动作：{a.Action}\n时间：{a.OccurredAt:O}\n项目：{a.ProjectId?.ToString("D") ?? "全工作区"}；比赛：{a.MatchId?.ToString("D") ?? "未指定"}\n原始详情：\n" + a.Detail));
    }

    private static IReadOnlyList<string> List(IEnumerable<string> values) => Array.AsReadOnly(values.ToArray());
    private static string Result(TournamentMatchResult result) =>
        $"种类：{result.Kind}；比分：{result.Score}；实际用时：{result.DurationMinutes} 分钟\n实际比赛日：{result.ActualPlayedDay?.ToString("yyyy-MM-dd") ?? "未知"}；记录时间：{result.RecordedAt:O}\n" +
        "胜方：" + Entrant(result.Winner) + "\n负方：" + Entrant(result.Loser);
    private static string Entrant(EntrantSource.Participant entrant) => $"{entrant.DisplayName} [{entrant.IdentityKey}]\n" +
        string.Join("\n", entrant.Players.Select(p => $"姓名：{p.Name}；学号：{p.StudentId}；团体身份：{p.IsTeam}；身份键：{p.IdentityKey}"));
}
