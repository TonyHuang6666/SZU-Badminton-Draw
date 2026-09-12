namespace BadmintonDraw.Core.Tournaments;

public static class WorkspaceOperationsRules
{
    /// <summary>Attach the successful redraw/replan audit and void pending coverage in the same candidate.
    /// Call before validating a candidate whose graphs or resource dates have just changed.</summary>
    public static TournamentWorkspace InvalidatePendingReceipts(TournamentWorkspace candidate,
        WorkspaceAuditEvent audit, string reason)
    {
        Require(candidate.Results.Count == 0 && candidate.ResultHistory.Count == 0 &&
            audit.Id != Guid.Empty && audit.OccurredAt != default && audit.Action is "DrawReopened" or "ScheduleGenerated" &&
            !string.IsNullOrWhiteSpace(reason), "import.void", "只有无赛果重抽或重排才能作废待处理记录。");
        var active = candidate.ImportLogs.Where(log => log.VoidedAt is null).ToArray();
        Require(active.All(log => log.Rows.All(row => !row.HadResult) && log.AddedResultCount == 0 && log.CorrectionCount == 0),
            "import.void", "已包含赛果的导入记录不能作废。");
        Require(active.All(log => log.ImportedAt <= audit.OccurredAt), "import.void-time",
            "当前时间早于待处理记录的导入时间，请核对计算机时钟后再操作。");
        return candidate with
        {
            ImportLogs = candidate.ImportLogs.Select(log => log.VoidedAt is not null ? log : log with
                { VoidedAt = audit.OccurredAt, VoidReason = reason.Trim(), VoidedByAuditEventId = audit.Id }).ToArray(),
            ProcessedDays = [],
            AuditEvents = [.. candidate.AuditEvents, audit]
        };
    }

    public static IReadOnlyList<WorkspaceProcessedDay> BuildProcessedDays(IEnumerable<WorkspaceImportLog> logs) =>
        Array.AsReadOnly(logs.Where(log => log.VoidedAt is null).SelectMany(log => log.Rows.Select(row => (row, log.ImportedAt)))
            .GroupBy(item => item.row.RecordDay).OrderBy(group => group.Key).Select(group => new WorkspaceProcessedDay(group.Key,
                group.Select(item => item.row.Key).Distinct().OrderBy(key => key.ProjectId).ThenBy(key => key.MatchId).ToArray(),
                group.Max(item => item.ImportedAt))).ToArray());

    public static void Validate(TournamentWorkspace workspace)
    {
        var logs = workspace.ImportLogs; var history = workspace.ResultHistory;
        Require(logs.Select(l => l.Id).Distinct().Count() == logs.Count &&
            logs.Select(l => l.ContentHash).Distinct(StringComparer.OrdinalIgnoreCase).Count() == logs.Count,
            "import.identity", "导入记录标识或文件哈希重复。");
        var nodes = workspace.Projects.SelectMany(p => p.MatchGraph?.Matches ?? []).Select(n => new WorkspaceMatchKey(n.ProjectId, n.Id)).ToHashSet();
        var dates = workspace.Resources?.Days.Select(d => d.Date).ToHashSet() ?? [];
        foreach (var log in logs)
        {
            Require(log.Id != Guid.Empty && log.ImportedAt != default && !string.IsNullOrWhiteSpace(log.SourceFileName) &&
                !string.IsNullOrWhiteSpace(log.SourcePath) && log.ContentHash is { Length: 64 } && log.ContentHash.All(Uri.IsHexDigit) &&
                log.Rows.Count > 0, "import.value", "导入记录的标识、来源、哈希、时间或行证据无效。");
            Require(log.Rows.Select(r => r.Location).Distinct().Count() == log.Rows.Count && log.Rows.All(r =>
                r.Key.ProjectId != Guid.Empty && r.Key.MatchId != Guid.Empty && r.RecordDay != default && LocationValid(r.Location)),
                "import.row", "导入记录行的身份、日期或位置无效或重复。");
            Require(log.Warnings.All(w => !string.IsNullOrWhiteSpace(w.Code) && !string.IsNullOrWhiteSpace(w.Message) &&
                (w.Location is null || log.Rows.Any(r => r.Location == w.Location))), "import.warning", "导入警告或行引用无效。");
            var reported = log.Rows.Where(r => r.HadResult).Select(r => r.Key).Distinct().Count();
            Require(log.AddedResultCount >= 0 && log.CorrectionCount >= 0 &&
                (long)log.AddedResultCount + log.CorrectionCount <= reported &&
                log.CorrectionCount == history.Count(h => h.ImportLogId == log.Id), "import.count", "导入新增或更正数量与行证据不符。");
            if (log.VoidedAt is { } voided)
            {
                var audit = workspace.AuditEvents.SingleOrDefault(a => a.Id == log.VoidedByAuditEventId);
                Require(voided >= log.ImportedAt && !string.IsNullOrWhiteSpace(log.VoidReason) &&
                    log.Rows.All(r => !r.HadResult) && log.AddedResultCount == 0 && log.CorrectionCount == 0 &&
                    audit is not null && audit.OccurredAt == voided && audit.Action is "DrawReopened" or "ScheduleGenerated",
                    "import.void", "只能由同次解除抽签或重排审计作废未录入赛果的待处理记录。");
            }
            else
            {
                Require(log.VoidReason is null && log.VoidedByAuditEventId is null, "import.void", "作废记录必须同时保留时间、原因和审计引用。");
                Require(workspace.Purpose == TournamentPurpose.FullTournament && workspace.Stage >= TournamentStage.ScheduleReady && workspace.Schedule is not null &&
                    log.Rows.All(r => nodes.Contains(r.Key) && dates.Contains(r.RecordDay) && (!r.HadResult || workspace.Results.ContainsKey(r.Key))),
                    "import.reference", "当前导入记录必须引用本赛事有效赛程、比赛日和赛果。");
            }
        }
        var expectedDays = BuildProcessedDays(logs).ToDictionary(d => d.Day);
        Require(workspace.ProcessedDays.Select(d => d.Day).Distinct().Count() == workspace.ProcessedDays.Count &&
            workspace.ProcessedDays.Count == expectedDays.Count && workspace.ProcessedDays.All(day =>
                expectedDays.TryGetValue(day.Day, out var expected) && day.UpdatedAt == expected.UpdatedAt &&
                day.CoveredMatches.Distinct().Count() == day.CoveredMatches.Count && day.CoveredMatches.ToHashSet().SetEquals(expected.CoveredMatches)),
            "processed.coverage", "已处理比赛日必须完整对应有效导入记录的覆盖范围，不能代表赛事完成。");
        Require(history.Select(h => h.Id).Distinct().Count() == history.Count &&
            history.Select(h => h.Sequence).Order().SequenceEqual(Enumerable.Range(1, history.Count).Select(i => (long)i)),
            "history.identity", "赛果更正历史的标识或连续顺序无效。");
        foreach (var entry in history)
        {
            var log = logs.SingleOrDefault(l => l.Id == entry.ImportLogId);
            Require(entry.Id != Guid.Empty && entry.ChangedAt != default && !string.IsNullOrWhiteSpace(entry.Reason) &&
                nodes.Contains(entry.Key) && entry.Key == entry.Before.Key && entry.Key == entry.After.Key &&
                TournamentResultRules.IsValidValue(entry.Before, dates) && TournamentResultRules.IsValidValue(entry.After, dates) &&
                TournamentResultRules.SameParticipants(entry.Before, entry.After) && !TournamentResultRules.SameMetadata(entry.Before, entry.After) &&
                entry.Before.RecordedAt <= entry.ChangedAt && entry.After.RecordedAt == entry.ChangedAt,
                "history.value", "赛果更正必须保留同一胜负方、实际前后值、日期、时间和非空原因。");
            Require(log is not null && log.VoidedAt is null && log.ImportedAt == entry.ChangedAt &&
                log.Rows.Any(r => r.HadResult && r.Key == entry.Key && r.Location == entry.Source),
                "history.source", "赛果更正必须引用同次导入中的真实赛果行。");
        }
        foreach (var chain in history.GroupBy(h => h.Key))
        {
            var ordered = chain.OrderBy(h => h.Sequence).ToArray();
            Require(ordered.Skip(1).Select((entry, index) => TournamentResultRules.SameVersion(ordered[index].After, entry.Before)).All(same => same) &&
                workspace.Results.TryGetValue(chain.Key, out var current) && TournamentResultRules.SameVersion(ordered[^1].After, current),
                "history.chain", "赛果更正历史不连续或与当前赛果不一致。");
        }
    }

    private static bool LocationValid(WorkspaceRecordLocation location) => !string.IsNullOrWhiteSpace(location.SheetName) && location.RowNumber > 0;
    private static void Require(bool condition, string code, string message)
    { if (!condition) throw new WorkspaceValidationException(code, message); }
}
