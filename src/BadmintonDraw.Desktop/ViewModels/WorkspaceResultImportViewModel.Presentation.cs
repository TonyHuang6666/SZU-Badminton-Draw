using System.Globalization;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed partial class WorkspaceResultImportViewModel
{
    private bool acceptedEvaluation;
    public string SelectedFilesText => string.Join("\n", SelectedPaths);
    public string CountsText => Counts is not { } c ? "尚无当前检查计数。" :
        (acceptedEvaluation ? "本次实际接受：" : "预览拟变更（尚未保存）：") +
        $"新增文件 {c.NewFileCount}；重复文件 {c.DuplicateFileCount}；新增赛果 {c.AddedResultCount}；更正 {c.CorrectionCount}；待填记录行 {c.PendingRowCount}。";
    public string FileDetails => string.Join("\n\n", Files.Select(f =>
        $"{FileStatus(f.Status)} [{f.Status}]；记录行数：{f.RowCount}\n{Attribution(f.Source)}"));
    public string DiagnosticDetails => string.Join("\n\n", Diagnostics.Select(d =>
        $"{Severity(d.Severity)} [{d.Code}] {d.Message}\n{Attribution(d.Source)}" +
        string.Concat(d.RelatedSources.Select(s => "\n关联来源：\n" + Attribution(s)))));
    public string CorrectionDetails => string.Join("\n\n", Corrections.Select(c =>
    {
        var project = session.Workspace.Projects.SingleOrDefault(p => p.Id == c.Key.ProjectId);
        var node = project?.MatchGraph?.Matches.SingleOrDefault(n => n.Id == c.Key.MatchId);
        return $"{project?.DisplayName} / {node?.DisplayName}\n项目 ID：{c.Key.ProjectId}\n场次 ID：{c.Key.MatchId}\n" +
            "更正前：\n" + ResultFacts(c.Before) + "\n原记录时间：" + c.Before.RecordedAt.ToString("O", CultureInfo.InvariantCulture) +
            "\n更正后（预览；最终记录时间以实际保存为准）：\n" + ResultFacts(c.After) + "\n更正来源：\n" + Attribution(c.Source);
    }));
    private static string ResultFacts(TournamentMatchResult result) =>
        $"胜方：{Entrant(result.Winner)}\n负方：{Entrant(result.Loser)}\n类型：{(result.Kind == TournamentResultKind.Played ? "正常" : "弃权")} [{result.Kind}]；" +
        $"比分：{result.Score}；时长：{result.DurationMinutes} 分钟；实际比赛日期：{result.ActualPlayedDay?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "未知"}";
    private static string Entrant(EntrantSource.Participant participant) => $"{participant.DisplayName} / {participant.IdentityKey}\n" +
        string.Join("\n", participant.Players.Select(p => $"选手：{p.Name}；学号：{p.StudentId}；身份：{p.IdentityKey}；团体：{p.IsTeam}"));
    private static string Attribution(ResultImportSource? source) => source is null ? "全局诊断（未指定文件或行）。" :
        $"文件：{source.SourceFileName}\n完整路径：{source.SourcePath}\nSHA-256：{source.ContentHash}\n" +
        (source.Location is { } location ? $"工作表：{location.SheetName}；第 {location.RowNumber} 行" : "文件级诊断（未指定行）") +
        (source.Field is { } field ? "；字段：" + field : "");
    private static string FileStatus(ResultImportFileStatus status) => status switch
    {
        ResultImportFileStatus.New => "新文件", ResultImportFileStatus.DuplicateInBatch => "本批次重复",
        ResultImportFileStatus.PreviouslyImported => "此前已导入", ResultImportFileStatus.PreviouslyVoided => "此前记录已作废（不会重新激活）", _ => "无效文件"
    };
    private static string Severity(ResultImportDiagnosticSeverity severity) => severity switch
    { ResultImportDiagnosticSeverity.Blocking => "阻断", ResultImportDiagnosticSeverity.Warning => "警告", _ => "说明" };
    private void PresentationChanged()
    {
        foreach (var name in new[] { nameof(SelectedFilesText), nameof(CountsText), nameof(FileDetails), nameof(DiagnosticDetails), nameof(CorrectionDetails) }) OnPropertyChanged(name);
    }
}
