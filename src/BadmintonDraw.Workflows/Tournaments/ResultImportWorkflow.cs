using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Workflows.Tournaments;

/// <summary>Pure batch evaluation. File capture, approval binding and atomic publication belong to the facade.</summary>
public sealed partial class ResultImportWorkflow
{
    public ResultImportEvaluation Evaluate(TournamentWorkspace workspace, IReadOnlyList<WorkspaceRecordImportFile> files, ResultImportEvaluationOptions options) =>
        new Evaluation(workspace, files, options).Execute();

    private sealed partial class Evaluation(TournamentWorkspace workspace, IReadOnlyList<WorkspaceRecordImportFile> files, ResultImportEvaluationOptions options)
    {
        private readonly List<ResultImportDiagnostic> diagnostics = [];
        private readonly List<PreparedFile> prepared = [];
        private readonly List<ResultImportFileEvaluation> invalidFiles = [];
        private readonly List<ResultImportCorrection> corrections = [];
        private readonly List<Change> changes = [];
        private bool Blocked => diagnostics.Any(d => d.Severity == ResultImportDiagnosticSeverity.Blocking);
        private IEnumerable<PreparedFile> NewFiles => prepared.Where(f => f.Status == ResultImportFileStatus.New);

        internal ResultImportEvaluation Execute()
        {
            try { TournamentWorkspaceRules.Validate(workspace); }
            catch (WorkspaceValidationException exception)
            { Error("result.workspace-invalid", exception.Message); return Finish(ResultImportEvaluationStatus.Rejected); }
            if (workspace.Purpose != TournamentPurpose.FullTournament || workspace.Stage < TournamentStage.ScheduleReady || workspace.Schedule is null)
                Error("result.stage", "请先完成全部抽签确认和全局赛程编排。");
            if (options.ImportedAt == default || options.OperationId == Guid.Empty)
                Error("result.context", "导入需要明确的操作标识和记录时间。");
            if (files.Count == 0) Error("result.files-empty", "请选择至少一份有实际记录行的文件。");
            if (Blocked) return Finish(ResultImportEvaluationStatus.Rejected);
            ValidateFiles();
            if (Blocked) return Finish(ResultImportEvaluationStatus.Rejected);
            ClassifyFiles();
            if (Blocked) return Finish(ResultImportEvaluationStatus.Rejected);
            if (!NewFiles.Any()) return Finish(ResultImportEvaluationStatus.NoChanges);
            var results = workspace.Results.ToDictionary();
            EvaluateResults(results);
            if (Blocked) return Finish(ResultImportEvaluationStatus.Rejected);
            if (corrections.Count > 0 && (!options.AllowCorrections || string.IsNullOrWhiteSpace(options.CorrectionReason)))
            {
                Error("result.correction-confirmation", "赛果元数据发生变化，请明确允许更正并填写人工核对原因。");
                return Finish(ResultImportEvaluationStatus.RequiresConfirmation);
            }
            return BuildCandidate(results);
        }

        private void ClassifyFiles()
        {
            var previous = workspace.ImportLogs.ToDictionary(l => l.ContentHash, StringComparer.OrdinalIgnoreCase);
            foreach (var group in prepared.GroupBy(f => f.Source.ContentHash, StringComparer.Ordinal))
            {
                var ordered = group.OrderBy(f => f.Source.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Source.SourceFileName, StringComparer.Ordinal).ToArray();
                var first = ordered[0];
                if (ordered.Any(f => !f.Raw.Document.Rows.SequenceEqual(first.Raw.Document.Rows)))
                { Error("result.hash-payload", "相同文件哈希对应了不同的原始行，不能确定输入来源。", first.Source, ordered.Select(f => f.Source)); continue; }
                if (previous.TryGetValue(group.Key, out var log))
                {
                    first.Status = log.VoidedAt is null ? ResultImportFileStatus.PreviouslyImported : ResultImportFileStatus.PreviouslyVoided;
                    Notice("result.file-seen", log.VoidedAt is null ? "该文件已导入，本次不重复处理。" : "该文件的处理记录已作废，本次不会重新激活覆盖范围；请重新导出记录表。", first.Source);
                }
                foreach (var alias in ordered.Skip(1))
                { alias.Status = ResultImportFileStatus.DuplicateInBatch; Notice("result.file-alias", "与本批另一文件内容相同，仅保留一份导入记录。", alias.Source); }
            }
        }

        private void EvaluateResults(Dictionary<WorkspaceMatchKey, TournamentMatchResult> results)
        {
            var rows = NewFiles.SelectMany(f => f.Rows).Where(r => r.HasWinner).GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.ToArray());
            foreach (var project in workspace.Projects.OrderBy(p => p.SortOrder).ThenBy(p => p.Id))
            foreach (var node in project.MatchGraph!.Matches.OrderBy(n => n.Order).ThenBy(n => n.Id))
            {
                var key = new WorkspaceMatchKey(project.Id, node.Id);
                if (!rows.TryGetValue(key, out var reported)) continue;
                var a = Resolve(node.SideA, project.Id, results); var b = Resolve(node.SideB, project.Id, results);
                if (a is null || b is null)
                {
                    foreach (var row in reported) Error("result.upstream-missing", "本场上游赛果缺失或被拒绝，无法确定胜负双方；请同时导入所依赖的赛果。", row.Source with { Field = "Winner" });
                    continue;
                }
                var parsed = reported.Select(row => (Row: row, Result: ParseResult(row, a, b, project.Discipline == EventDiscipline.Team))).ToArray();
                if (parsed.Any(p => p.Result is null)) continue;
                var result = parsed[0].Result!;
                if (parsed.Any(p => !TournamentResultRules.SameParticipants(result, p.Result!) || !TournamentResultRules.SameMetadata(result, p.Result!)))
                { Error("result.batch-conflict", "同一场比赛在本批中填写了不同赛果；请统一比分、时长、日期、类型和胜方后重试。", reported[0].Source, reported.Select(r => r.Source)); continue; }
                var owner = reported.OrderBy(r => r.Source.ContentHash, StringComparer.Ordinal).ThenBy(r => r.Source.Location!.SheetName, StringComparer.Ordinal).ThenBy(r => r.Source.Location!.RowNumber).First();
                if (results.TryGetValue(key, out var current))
                {
                    if (!TournamentResultRules.SameParticipants(current, result))
                    { Error("result.participants-changed", "已有赛果的胜方或负方不同，不能通过元数据更正改变对阵结果或下游已录赛果。", owner.Source); continue; }
                    if (ResultImportValueParser.Equivalent(current, result, project.Discipline == EventDiscipline.Team)) continue;
                    if (current.RecordedAt > options.ImportedAt)
                    { Error("result.correction-time", "更正时间早于已有赛果记录时间，请检查系统时间并重新预览。", owner.Source); continue; }
                    corrections.Add(new(current, result, owner.Source));
                }
                changes.Add(new(owner, result, current)); results[key] = result;
            }
        }

        private TournamentMatchResult? ParseResult(PreparedRow row, EntrantSource.Participant a, EntrantSource.Participant b, bool team)
        {
            var raw = row.Raw; var text = WorkspaceWinnerOptionText.Normalize(raw.Winner.Text); bool sideA;
            if (string.Equals(text, "A", StringComparison.OrdinalIgnoreCase) || text == WorkspaceWinnerOptionText.Normalize(WorkspaceWinnerOptionText.Format(ScheduleMatchSide.SideA, a.DisplayName))) sideA = true;
            else if (string.Equals(text, "B", StringComparison.OrdinalIgnoreCase) || text == WorkspaceWinnerOptionText.Normalize(WorkspaceWinnerOptionText.Format(ScheduleMatchSide.SideB, b.DisplayName))) sideA = false;
            else { Error("result.winner-option", "请选择 A/B 或与当前真实参赛方一致的完整下拉选项，不能只填写姓名或修改选项标题。", row.Source with { Field = "Winner" }); return null; }
            if (!ResultImportValueParser.Duration(raw.Duration, out var duration) || (row.Kind == TournamentResultKind.Played ? duration <= 0 : duration != 0))
            { Error("result.duration", "正常赛果需要正整数分钟（如 18、18m、18分钟）；明确弃权需要填写 0。", row.Source with { Field = "Duration" }); return null; }
            var score = raw.Score.Text.Trim();
            if (row.Kind == TournamentResultKind.Played)
            {
                if (!ResultImportValueParser.Score(score, team, out score, out var scoreSideA))
                { Error("result.score-format", team ? "团体赛请填写一组非平局总比分，如 3-2；退赛等其他格式暂不支持。" : "请填写一局非平局比分如 21-19，或已结束的三局两胜比分如 21-19, 19-21, 21-18；退赛等其他格式暂不支持。", row.Source with { Field = "Score" }); return null; }
                if (scoreSideA != sideA)
                { Error("result.score-winner", "比分按 A-B 方向填写，与所选胜方矛盾，请核对原始记录。", row.Source with { Field = "Score" }); return null; }
            }
            return new(row.Key, sideA ? a : b, sideA ? b : a, score, duration, options.ImportedAt) { Kind = row.Kind, ActualPlayedDay = row.ActualDay };
        }

        private ResultImportEvaluation BuildCandidate(Dictionary<WorkspaceMatchKey, TournamentMatchResult> results)
        {
            var ids = workspace.AuditEvents.Select(a => a.Id).Concat(workspace.ImportLogs.Select(l => l.Id)).Concat(workspace.ResultHistory.Select(h => h.Id)).ToHashSet();
            bool Reserve(Guid id) { if (id != Guid.Empty && ids.Add(id)) return true; Error("result.operation-collision", "操作标识与现有记录冲突，请重新预览。 "); return false; }
            Reserve(options.OperationId);
            var logs = new List<WorkspaceImportLog>(); var logIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var file in NewFiles.OrderBy(f => f.Source.ContentHash, StringComparer.Ordinal))
            {
                var id = DerivedId("log:" + file.Source.ContentHash); Reserve(id); logIds.Add(file.Source.ContentHash, id);
                var owned = changes.Where(c => c.Owner.Source.ContentHash == file.Source.ContentHash).ToArray();
                var warnings = diagnostics.Where(d => d.Severity == ResultImportDiagnosticSeverity.Warning && d.Source is { } s &&
                    s.ContentHash == file.Source.ContentHash && s.SourcePath == file.Source.SourcePath && s.SourceFileName == file.Source.SourceFileName)
                    .Select(d => new WorkspaceImportWarning(d.Code, d.Message, d.Source!.Location)).ToArray();
                logs.Add(new(id, options.ImportedAt, file.Source.SourceFileName, file.Source.SourcePath, file.Source.ContentHash,
                    file.Rows.Select(r => new WorkspaceImportedRow(r.Key, r.RecordDay, r.Raw.Location, r.HasWinner)).ToArray(), owned.Count(c => c.Before is null), owned.Count(c => c.Before is not null), warnings));
            }
            var history = workspace.ResultHistory.ToList();
            foreach (var change in changes.Where(c => c.Before is not null))
            {
                var id = DerivedId($"history:{change.Result.Key.ProjectId:D}:{change.Result.Key.MatchId:D}"); Reserve(id);
                history.Add(new(id, history.Count + 1L, change.Result.Key, change.Before!, change.Result, logIds[change.Owner.Source.ContentHash],
                    change.Owner.Raw.Location, options.ImportedAt, options.CorrectionReason!.Trim()));
            }
            if (Blocked) return Finish(ResultImportEvaluationStatus.Rejected);
            var allLogs = workspace.ImportLogs.Concat(logs).ToArray();
            var detail = JsonSerializer.Serialize(new { Hashes = logs.Select(l => l.ContentHash).ToArray(), AddedResults = changes.Count(c => c.Before is null),
                Corrections = corrections.Count, Reason = corrections.Count == 0 ? null : options.CorrectionReason!.Trim() });
            var candidate = workspace with { Results = results, ImportLogs = allLogs, ResultHistory = history,
                ProcessedDays = WorkspaceOperationsRules.BuildProcessedDays(allLogs), AuditEvents = [..workspace.AuditEvents, new(options.OperationId, "ResultsImported", options.ImportedAt, Detail: detail)] };
            try
            {
                if (candidate.Stage == TournamentStage.ScheduleReady && results.Count > 0) candidate = TournamentWorkspaceRules.Transition(candidate, TournamentStage.InProgress);
                if (candidate.Stage == TournamentStage.InProgress && candidate.Projects.SelectMany(p => p.MatchGraph!.Matches).All(n => results.ContainsKey(new(n.ProjectId, n.Id))))
                    candidate = TournamentWorkspaceRules.Transition(candidate, TournamentStage.Completed);
                TournamentWorkspaceRules.Validate(candidate);
            }
            catch (WorkspaceValidationException exception)
            { Error("result.candidate-invalid", exception.Message); return Finish(ResultImportEvaluationStatus.Rejected); }
            return Finish(ResultImportEvaluationStatus.Ready, candidate);
        }

        private Guid DerivedId(string role) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"{options.OperationId:D}:{role}")).AsSpan(0, 16));
        private static EntrantSource.Participant? Resolve(EntrantSource source, Guid project, IReadOnlyDictionary<WorkspaceMatchKey, TournamentMatchResult> results) => source switch
        {
            EntrantSource.Participant participant => participant,
            EntrantSource.WinnerOf winner when results.TryGetValue(new(project, winner.MatchId), out var result) => result.Winner,
            EntrantSource.LoserOf loser when results.TryGetValue(new(project, loser.MatchId), out var result) => result.Loser,
            _ => null
        };

        private ResultImportEvaluation Finish(ResultImportEvaluationStatus status, TournamentWorkspace? candidate = null) => new(status,
            diagnostics.OrderBy(d => d.Source?.ContentHash, StringComparer.Ordinal).ThenBy(d => d.Source?.SourcePath, StringComparer.Ordinal)
                .ThenBy(d => d.Source?.Location?.SheetName, StringComparer.Ordinal).ThenBy(d => d.Source?.Location?.RowNumber).ThenBy(d => d.Source?.Field, StringComparer.Ordinal).ThenBy(d => d.Code, StringComparer.Ordinal),
            prepared.Select(f => new ResultImportFileEvaluation(f.Source, f.Status, f.Raw.Document.Rows.Count)).Concat(invalidFiles)
                .OrderBy(f => f.Source.ContentHash, StringComparer.Ordinal).ThenBy(f => f.Source.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Source.SourceFileName, StringComparer.Ordinal),
            corrections, new(NewFiles.Count(), prepared.Count(f => f.Status != ResultImportFileStatus.New), changes.Count(c => c.Before is null), corrections.Count,
                NewFiles.SelectMany(f => f.Rows).Count(r => !r.HasWinner)), candidate);
        private void Error(string code, string message, ResultImportSource? source = null, IEnumerable<ResultImportSource>? related = null) => diagnostics.Add(new(code, ResultImportDiagnosticSeverity.Blocking, message, source, related));
        private void Warn(string code, string message, ResultImportSource source) => diagnostics.Add(new(code, ResultImportDiagnosticSeverity.Warning, message, source));
        private void Notice(string code, string message, ResultImportSource source) => diagnostics.Add(new(code, ResultImportDiagnosticSeverity.Info, message, source));
        private sealed class PreparedFile(WorkspaceRecordImportFile raw, ResultImportSource source, IReadOnlyList<PreparedRow> rows)
        { internal WorkspaceRecordImportFile Raw { get; } = raw; internal ResultImportSource Source { get; } = source; internal IReadOnlyList<PreparedRow> Rows { get; } = rows; internal ResultImportFileStatus Status { get; set; } = ResultImportFileStatus.New; }
        private sealed record PreparedRow(WorkspaceRecordRawRow Raw, ResultImportSource Source, WorkspaceMatchKey Key, DateOnly RecordDay, DateOnly? ActualDay, TournamentResultKind Kind, bool HasWinner);
        private sealed record Change(PreparedRow Owner, TournamentMatchResult Result, TournamentMatchResult? Before);
    }
}
