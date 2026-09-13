using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed partial class WorkspaceResultImportViewModel
{
    private async Task PickAsync()
    {
        var token = generation; var captured = session; SetWorking(true);
        try
        {
            var picked = (await pickFiles())?.ToArray();
            if (!Current(token, captured) || picked is null || picked.Length == 0) return;
            generation++; SelectedPaths = Array.AsReadOnly(picked); OnPropertyChanged(nameof(SelectedPaths));
            ClearEvidence(); StateMessage = "已选择记录表，请预览导入；尚未保存。"; OutcomeDetails = "";
        }
        catch (Exception error) { if (Current(token, captured)) ShowError(ToError(error)); }
        finally { SetWorking(false); }
    }
    private void ClearFiles()
    {
        generation++; SelectedPaths = Array.Empty<string>(); OnPropertyChanged(nameof(SelectedPaths));
        ClearEvidence(); OutcomeDetails = "";
        StateMessage = applying ? "已清空文件选择；已发起的导入不会因此回滚，请以当前工作区为准。" : "已清空选择；请重新选择并预览。";
    }
    private async Task PreviewAsync()
    {
        var token = ++generation; var captured = session; var paths = SelectedPaths.ToArray();
        ClearEvidence(); OutcomeDetails = ""; StateMessage = "正在检查记录表，尚未保存…"; SetWorking(true);
        try
        {
            var result = await shell.PreviewResultImportAsync(captured, paths);
            if (!Current(token, captured)) return;
            if (result.Succeeded && result.Value is { } value)
            {
                preview = value; Install(value.Evaluation); SourceDetails = DescribeSource(value);
                StateMessage = value.Evaluation.Status switch
                {
                    ResultImportEvaluationStatus.Ready => "预览完成，尚未保存；请核对文件和拟变更计数后明确确认。",
                    ResultImportEvaluationStatus.RequiresConfirmation => "发现元数据更正，尚未保存；请核对前后值，允许更正并填写原因后确认。",
                    ResultImportEvaluationStatus.NoChanges => "预览显示文件已处理或已作废；可明确确认检查，预计无需保存。",
                    _ => "记录表包含不能接受的内容，尚未保存；请修正后重新预览。"
                };
            }
            else if (result.Error is { } error) ShowError(error);
        }
        catch (Exception error) { if (Current(token, captured)) ShowError(ToError(error)); }
        finally { SetWorking(false); }
    }
    private async Task ImportAsync()
    {
        var consumed = preview!; var captured = session; var token = generation; var refreshes = sessionGeneration;
        var consent = new ResultCorrectionConfirmation(AllowCorrections, CorrectionReason.Trim());
        preview = null; ResetConfirmation(); applying = true; SetWorking(true);
        StateMessage = consumed.Evaluation.Status == ResultImportEvaluationStatus.NoChanges ? "正在重新核对文件与工作区，预计无需保存。" : "正在确认并保存；修改输入不会撤销已经开始的提交。"; OutcomeDetails = "";
        try
        {
            var execution = await shell.ImportResultsAsync(captured, consumed, consent);
            if (disposed || token != generation) return;
            var currentCompletion = ReferenceEquals(session, execution.CompletionSession) && ReferenceEquals(shell.CurrentSession, session);
            if (execution.Outcome is { } result)
            {
                var ownSave = !result.NoChanges && currentCompletion && sessionGeneration - refreshes <= 1 &&
                    ReferenceEquals(session.Workspace, result.Command.Workspace) && session.WorkspacePath == result.Command.WorkspacePath;
                if (!(result.NoChanges ? Current(token, captured) : ownSave)) return;
                Install(result.Evaluation, accepted: true); SourceDetails = DescribeSource(consumed);
                StateMessage = result.NoChanges ? "文件已处理或已作废，本次没有保存、备份、新审计或新增覆盖。" : "导入已保存；计数为本次实际接受结果，赛事状态以当前工作区为准。";
                OutcomeDetails = result.NoChanges ? StateMessage : $"正式文件：{result.Command.WorkspacePath}\n修订：{result.Command.Workspace.Revision}\n" +
                    $"新增文件：{result.Evaluation.ProposedCounts.NewFileCount}；新增赛果：{result.Evaluation.ProposedCounts.AddedResultCount}；更正：{result.Evaluation.ProposedCounts.CorrectionCount}；待填记录行：{result.Evaluation.ProposedCounts.PendingRowCount}" +
                    (result.Command.BackupPath is { } backup ? "\n备份：" + backup : "");
            }
            else if (execution.Error is { } error)
            {
                var ownCommittedError = error.Committed && currentCompletion && sessionGeneration - refreshes == 1 &&
                    session.WorkspacePath == captured.WorkspacePath && session.Workspace.Id == captured.Workspace.Id;
                if (error.Committed ? ownCommittedError : Current(token, captured)) ShowError(error);
            }
        }
        catch (Exception error) { if (Current(token, captured)) ShowError(ToError(error)); }
        finally { applying = false; SetWorking(false); }
    }
}
