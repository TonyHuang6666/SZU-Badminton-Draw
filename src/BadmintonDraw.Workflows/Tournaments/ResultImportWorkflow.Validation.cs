using System.Globalization;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Workflows.Tournaments;

public sealed partial class ResultImportWorkflow
{
    private sealed partial class Evaluation
    {
        private void ValidateFiles()
        {
            foreach (var file in files.OrderBy(f => f.Document.ContentHash, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.SourceFileName, StringComparer.Ordinal))
            {
                var hash = file.Document.ContentHash;
                var source = new ResultImportSource(hash?.ToLowerInvariant() ?? "", file.SourceFileName, file.SourcePath);
                var before = diagnostics.Count(d => d.Severity == ResultImportDiagnosticSeverity.Blocking);
                if (hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(file.SourceFileName) || string.IsNullOrWhiteSpace(file.SourcePath) || file.Document.Rows.Count == 0)
                    Error("result.file-invalid", "记录文件必须有来源、完整 SHA-256 和实际记录行。", source);
                if (file.Document.Rows.Select(r => r.Location).Distinct().Count() != file.Document.Rows.Count)
                    Error("result.row-location", "同一文件中重复出现相同工作表行位置。", source);
                var rows = new List<PreparedRow>();
                foreach (var raw in file.Document.Rows.OrderBy(r => r.Location.SheetName, StringComparer.Ordinal).ThenBy(r => r.Location.RowNumber))
                {
                    var rowSource = source with { Location = raw.Location };
                    if (string.IsNullOrWhiteSpace(raw.Location.SheetName) || raw.Location.RowNumber <= 0)
                    { Error("result.row-location", "记录行必须保留工作表名称和有效行号。", rowSource); continue; }
                    var parsed = ValidateRow(raw, rowSource);
                    if (parsed is not null) rows.Add(parsed);
                }
                if (diagnostics.Count(d => d.Severity == ResultImportDiagnosticSeverity.Blocking) != before)
                    invalidFiles.Add(new(source, ResultImportFileStatus.Invalid, file.Document.Rows.Count));
                else prepared.Add(new(file, source, rows));
            }
        }

        private PreparedRow? ValidateRow(WorkspaceRecordRawRow raw, ResultImportSource source)
        {
            var valid = true;
            bool Cell(string field, WorkspaceRecordCell cell, bool literal, params WorkspaceRecordCellKind[] allowed)
            {
                if (ResultImportValueParser.Coherent(cell) && allowed.Contains(cell.Kind) && (!literal || !cell.HasFormula)) return true;
                Error("result.cell-invalid", literal ? "身份和确认版本必须是可读取的文本常量，不能使用公式。" : "单元格内容不可读取或类型不符合此字段，请填写实际值后重试。", source with { Field = field }); valid = false; return false;
            }
            foreach (var (field, cell) in new[] { ("WorkspaceId", raw.WorkspaceId), ("ProjectId", raw.ProjectId), ("MatchId", raw.MatchId), ("GraphRevision", raw.GraphRevision), ("DrawConfirmedAt", raw.DrawConfirmedAt) })
                Cell(field, cell, true, WorkspaceRecordCellKind.Text);
            Cell("RecordDay", raw.RecordDay, false, WorkspaceRecordCellKind.Text, WorkspaceRecordCellKind.DateTime);
            Cell("ActualPlayedDay", raw.ActualPlayedDay, false, WorkspaceRecordCellKind.Empty, WorkspaceRecordCellKind.Text, WorkspaceRecordCellKind.DateTime);
            Cell("ResultKind", raw.ResultKind, false, WorkspaceRecordCellKind.Empty, WorkspaceRecordCellKind.Text);
            Cell("Winner", raw.Winner, false, WorkspaceRecordCellKind.Empty, WorkspaceRecordCellKind.Text);
            Cell("Score", raw.Score, false, ResultImportValueParser.Blank(raw.Winner)
                ? [WorkspaceRecordCellKind.Empty, WorkspaceRecordCellKind.Text, WorkspaceRecordCellKind.Number]
                : [WorkspaceRecordCellKind.Empty, WorkspaceRecordCellKind.Text]);
            Cell("Duration", raw.Duration, false, WorkspaceRecordCellKind.Empty, WorkspaceRecordCellKind.Text, WorkspaceRecordCellKind.Number);
            foreach (var (field, cell) in new[] { ("SideA", raw.SideA), ("SideB", raw.SideB), ("OptionA", raw.OptionA), ("OptionB", raw.OptionB) })
                if (!ResultImportValueParser.Coherent(cell)) Warn("result.helper-cache", "显示辅助单元格缺少可读缓存；将使用比赛图和实际赛果确定参赛方。", source with { Field = field });
            if (!valid) return null;
            bool GuidField(string field, WorkspaceRecordCell cell, out Guid value)
            {
                if (Guid.TryParse(cell.Text, out value) && value != Guid.Empty) return true;
                Error("result.identity", "缺少或无法识别记录行的赛事、项目或比赛标识，请重新导出当前记录表。", source with { Field = field }); return false;
            }
            var idsValid = GuidField("WorkspaceId", raw.WorkspaceId, out var workspaceId);
            idsValid &= GuidField("ProjectId", raw.ProjectId, out var projectId); idsValid &= GuidField("MatchId", raw.MatchId, out var matchId);
            if (!idsValid) return null;
            var project = workspace.Projects.SingleOrDefault(p => p.Id == projectId);
            if (workspaceId != workspace.Id || project?.MatchGraph?.Matches.SingleOrDefault(n => n.Id == matchId && n.IsPlayable) is null)
            { Error("result.foreign-match", "记录行不属于当前赛事和项目的有效比赛。", source); return null; }
            if (raw.GraphRevision.Text != project.MatchGraph.Revision)
            { Error("result.graph-stale", "记录表对应的比赛图已变化，请重新导出。", source with { Field = "GraphRevision" }); valid = false; }
            if (!DateTimeOffset.TryParseExact(raw.DrawConfirmedAt.Text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var epoch) || epoch != project.Draw!.ConfirmedAt)
            { Error("result.epoch-stale", "记录表的抽签确认版本已失效，请重新导出；相同随机种子重新确认也不能使用旧表。", source with { Field = "DrawConfirmedAt" }); valid = false; }
            var dates = workspace.Resources!.Days.Select(d => d.Date).ToHashSet();
            bool Day(string field, WorkspaceRecordCell cell, out DateOnly date)
            {
                if (ResultImportValueParser.Date(cell, out date) && dates.Contains(date)) return true;
                Error("result.day-invalid", "请填写已配置的比赛日期：yyyy-MM-dd 文本或不带时分秒的真实 Excel 日期；不支持数字序号或日期时间文本。", source with { Field = field }); valid = false; return false;
            }
            Day("RecordDay", raw.RecordDay, out var recordDay);
            DateOnly? actualDay = null; var hasWinner = !ResultImportValueParser.Blank(raw.Winner);
            if (!ResultImportValueParser.Blank(raw.ActualPlayedDay)) { if (Day("ActualPlayedDay", raw.ActualPlayedDay, out var actual)) actualDay = actual; }
            else if (hasWinner) { Error("result.actual-day-required", "已填写胜方的赛果必须明确实际比赛日期（弃权也需决定日期）。", source with { Field = "ActualPlayedDay" }); valid = false; }
            var kind = raw.ResultKind.Text.Trim() switch { "" or "正常" => TournamentResultKind.Played, "弃权" => TournamentResultKind.Walkover, _ => (TournamentResultKind)(-1) };
            if (!Enum.IsDefined(kind)) { Error("result.kind-invalid", "结果类型只能选择“正常”或“弃权”；空白表示正常，不会自动推定弃权。", source with { Field = "ResultKind" }); valid = false; }
            if (!hasWinner && (!ResultImportValueParser.Blank(raw.Score) || !ResultImportValueParser.Blank(raw.Duration) || actualDay is not null || kind == TournamentResultKind.Walkover))
                Warn("result.pending-details", "胜方尚未填写：此行只保留处理覆盖，已填比分、时长或日期不会形成赛果。", source);
            return valid ? new(raw, source, new(projectId, matchId), recordDay, actualDay, kind, hasWinner) : null;
        }
    }
}
