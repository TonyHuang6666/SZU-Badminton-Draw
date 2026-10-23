using System.Collections.ObjectModel;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed record OnsiteProjectOption(Guid? Id, string Name);

public sealed partial class OnsiteRecordViewModel : ViewModelBase, IDisposable
{
    private readonly AppShellViewModel shell;
    private readonly SemaphoreSlim saveGate = new(1, 1);
    private readonly HashSet<Task> automaticSaves = [];
    private WorkspaceSession session;
    private OnsiteRecordRowViewModel? savingRow;
    private bool disposed, refreshing, isWorking, lastMessageWasSuccess;
    private string selectedDay = "", searchText = "", stateMessage = "选择胜方和比赛结果后立即保存。比分、时长可留空或稍后补充。";
    private OnsiteProjectOption? selectedProject;

    public OnsiteRecordViewModel(AppShellViewModel shell, WorkspaceSession session)
    { this.shell = shell; this.session = session; RebuildRows(); }

    public ObservableCollection<OnsiteRecordRowViewModel> Rows { get; } = [];
    public ObservableCollection<OnsiteRecordRowViewModel> VisibleRows { get; } = [];
    private IEnumerable<OnsiteRecordRowViewModel> FilteredRows => Rows.Where(row =>
        row.Day == SelectedDay &&
        (SelectedProject?.Id is not { } id || row.Key.ProjectId == id) &&
        (string.IsNullOrWhiteSpace(SearchText) ||
         $"{row.OrderText} {row.ProjectName} {row.SideA} {row.SideB} {row.Phase} {row.GroupName} {row.Court}"
             .Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase))).ToArray();
    public IReadOnlyList<string> DayOptions { get; private set; } = [];
    public IReadOnlyList<OnsiteProjectOption> ProjectOptions { get; private set; } = [];
    public bool ShowProjectFilter => session.Workspace.Projects.Count > 1;
    public string SelectedDay
    {
        get => selectedDay;
        set
        {
            if (value is null || !DayOptions.Contains(value)) return;
            if (SetProperty(ref selectedDay, value)) { RefreshVisibleRows(); OnPropertyChanged(nameof(DaySummary)); OnPropertyChanged(nameof(CanConfirmDay)); }
        }
    }
    public OnsiteProjectOption? SelectedProject
    { get => selectedProject; set { if (SetProperty(ref selectedProject, value)) RefreshVisibleRows(); } }
    public string SearchText
    { get => searchText; set { if (SetProperty(ref searchText, value ?? "")) RefreshVisibleRows(); } }
    public bool IsWorking { get => isWorking; private set => SetProperty(ref isWorking, value); }
    public string StateMessage
    {
        get => lastMessageWasSuccess && HasDrafts
            ? $"尚有 {Rows.Count(r => r.HasDraft)} 场填写待保存。完成后按 Enter 或离开该行。" : stateMessage;
        private set { lastMessageWasSuccess = false; SetProperty(ref stateMessage, value); }
    }
    public bool HasDrafts => Rows.Any(r => r.HasDraft);
    // Keep the focused editor enabled during our own queued save. Disabling a ComboBox on
    // selection drops keyboard focus; captured submissions already protect later keystrokes.
    public bool CanEdit => !disposed && !session.RequiresReload && (shell.CanMutate || IsWorking && savingRow is not null) &&
        shell.CurrentSession?.Workspace.Id == session.Workspace.Id && session.Workspace.Schedule is not null;
    public string Summary => $"已记录 {session.Workspace.Results.Count} / {Rows.Count} 场 · 待保存 {Rows.Count(r => r.HasDraft)} 场";

    public void RefreshSession(WorkspaceSession next)
    {
        if (disposed || ReferenceEquals(session, next)) return;
        if (next.Workspace.Id != session.Workspace.Id || next.WorkspacePath != session.WorkspacePath)
        { Dispose(); return; }
        // Posted older publications must never replace a newer editor snapshot.
        if (next.Workspace.Revision < session.Workspace.Revision) return;
        session = next; RebuildRows();
    }

    public void RefreshAvailability()
    {
        foreach (var row in Rows) row.SetAvailable(CanEdit && IsDayEditable(row));
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanConfirmDay));
    }

    public Task<bool> SaveRowAsync(OnsiteRecordRowViewModel row) => SaveRowCoreAsync(row, false);

    private async Task<bool> SaveRowCoreAsync(OnsiteRecordRowViewModel row, bool outcomeOnly)
    {
        await saveGate.WaitAsync();
        try
        {
            if (disposed || !Rows.Contains(row)) return false;
            if (!row.HasDraft) return true;
            if (!CanEdit)
            { row.SetStatus("待保存：请等待当前操作完成后按 Enter 重试。"); return false; }
            // Only the first outcome write omits optional drafts. A later submission must not erase
            // persisted metadata when a delayed selection event reaches the queue.
            outcomeOnly &= !row.HasSavedResult;
            if (!row.TryRequest(out var request, outcomeOnly)) { StateMessage = row.Status; return false; }
            var captured = row.Capture(outcomeOnly); var expected = session;
            savingRow = row; IsWorking = true; row.SetStatus("正在保存…");
            try
            {
                var execution = await shell.SaveOnsiteResultAsync(expected, request!);
                if (disposed) return false;
                if (execution.CompletionSession is { } completion) RefreshSession(completion);
                if (execution.Error is { } error)
                {
                    row.SetStatus(error.Committed ? "保存已写入，但读取状态失败。请重新打开赛事核对。" : $"未保存：{error.Message}");
                    StateMessage = row.Status; return false;
                }
                if (execution.Outcome is not { } outcome ||
                    outcome.Evaluation.Status is ResultImportEvaluationStatus.Rejected or ResultImportEvaluationStatus.RequiresConfirmation)
                {
                    row.SetStatus("未保存：" + (execution.Outcome?.Evaluation.Diagnostics.FirstOrDefault(
                        d => d.Severity == ResultImportDiagnosticSeverity.Blocking)?.Message ?? "请核对该行输入。"));
                    StateMessage = row.Status; return false;
                }
                row.CompleteSave(captured);
                StateMessage = $"{row.ProjectName} 第 {row.OrderText} 场已保存，后续对阵已更新。";
                lastMessageWasSuccess = true; OnPropertyChanged(nameof(StateMessage));
                return outcomeOnly || !row.HasDraft;
            }
            catch (Exception error)
            { row.SetStatus($"保存未完成：{error.Message}"); StateMessage = row.Status; return false; }
            finally { savingRow = null; IsWorking = false; RefreshAvailability(); DraftChanged(); }
        }
        finally { saveGate.Release(); }
    }

    public async Task<bool> FlushAsync()
    {
        await WaitForAutomaticSavesAsync();
        var success = true;
        foreach (var row in Rows.Where(r => r.HasDraft).ToArray())
            if (!await SaveRowAsync(row)) success = false;
        return success && !HasDrafts && !IsWorking;
    }

    public void DiscardDrafts()
    {
        if (IsWorking || disposed) return;
        foreach (var row in Rows.Where(r => r.HasDraft)) row.Discard();
        RebuildRows();
        StateMessage = "已放弃未保存的填写内容。已保存的比赛结果保持不变。";
        DraftChanged();
    }

    public void DiscardRow(OnsiteRecordRowViewModel row)
    {
        if (IsWorking || disposed || !Rows.Contains(row)) return;
        row.Discard(); RebuildRows();
        StateMessage = "已放弃该行未保存的填写内容。";
    }

    public void Dispose()
    { disposed = true; RefreshAvailability(); }

    private void RebuildRows()
    {
        refreshing = true;
        try
        {
            var workspace = session.Workspace;
            if (workspace.Schedule is null)
            {
                foreach (var row in Rows.ToArray())
                    if (row.HasDraft) row.Invalidate(); else Rows.Remove(row);
                StateMessage = "赛程已变更。未保存的填写内容保留在表中，请核对后放弃修改。";
                return;
            }
            var projects = workspace.Projects.OrderBy(p => p.SortOrder).ToArray();
            var projectOrders = projects.ToDictionary(p => p.Id, p => p.SortOrder);
            var courtOrders = workspace.Schedule.Resources.Days.SelectMany(day => day.Courts.Select((court, order) =>
                (day.DayLabel, Court: court, Order: order))).ToDictionary(item => (item.DayLabel, item.Court), item => item.Order);
            var projected = ScheduledMatchProjection.Build(projects.Select(p => p.MatchGraph!).ToArray(),
                workspace.Schedule, workspace.Results).ToDictionary(m => Guid.Parse(m.MatchId));
            var existing = Rows.ToDictionary(r => r.Key);
            var ordered = new List<OnsiteRecordRowViewModel>();
            foreach (var project in projects)
            foreach (var node in project.MatchGraph!.Matches.OrderBy(n => n.Order))
            {
                var key = new WorkspaceMatchKey(project.Id, node.Id);
                var row = existing.GetValueOrDefault(key) ?? new(key, DraftChanged, QueueAutomaticSave);
                row.Refresh(project, node, projected[node.Id], Resolve(node.SideA, project.Id),
                    Resolve(node.SideB, project.Id), workspace.Results.GetValueOrDefault(key),
                    row == savingRow, CanEdit);
                ordered.Add(row);
            }
            foreach (var removed in Rows.Except(ordered).Where(r => r.HasDraft))
            { removed.Invalidate(); ordered.Add(removed); }
            var sorted = ordered.OrderBy(r => r.Day, StringComparer.Ordinal).ThenBy(r => r.Time[..5], StringComparer.Ordinal)
                .ThenBy(r => courtOrders.GetValueOrDefault((r.Day, r.Court), int.MaxValue))
                .ThenBy(r => projectOrders.GetValueOrDefault(r.Key.ProjectId, int.MaxValue)).ThenBy(r => r.Key.ProjectId)
                .ThenBy(r => int.Parse(r.OrderText)).ThenBy(r => r.Key.MatchId).ToArray();
            foreach (var removed in Rows.Except(sorted).ToArray()) Rows.Remove(removed);
            for (var i = 0; i < sorted.Length; i++)
            {
                var old = Rows.IndexOf(sorted[i]);
                if (old < 0) Rows.Insert(i, sorted[i]); else if (old != i) Rows.Move(old, i);
            }
            var currentDay = WorkspaceDayConfirmationRules.NextUnconfirmedDay(workspace);
            DayOptions = workspace.Schedule.Resources.Days.OrderBy(d => d.Date)
                .Where(d => currentDay is null || d.Date <= currentDay.Value)
                .Select(d => d.DayLabel).ToArray();
            if (!DayOptions.Contains(selectedDay)) selectedDay = currentDay?.ToString("yyyy-MM-dd") ?? DayOptions.LastOrDefault() ?? "";
            foreach (var row in Rows) row.SetAvailable(CanEdit && IsDayEditable(row));
            var options = projects.Select(p => new OnsiteProjectOption(p.Id, p.DisplayName)).ToList();
            if (options.Count > 1) options.Insert(0, new(null, "全项目"));
            var oldProject = selectedProject?.Id;
            ProjectOptions = options;
            selectedProject = options.FirstOrDefault(p => p.Id == oldProject) ?? options.FirstOrDefault();
        }
        finally { refreshing = false; RefreshVisibleRows(); OnPropertyChanged(null); }
    }

    private void RefreshVisibleRows()
    {
        var filtered = FilteredRows.ToArray();
        foreach (var removed in VisibleRows.Except(filtered).ToArray()) VisibleRows.Remove(removed);
        for (var i = 0; i < filtered.Length; i++)
        {
            var old = VisibleRows.IndexOf(filtered[i]);
            if (old < 0) VisibleRows.Insert(i, filtered[i]); else if (old != i) VisibleRows.Move(old, i);
        }
    }

    private EntrantSource.Participant? Resolve(EntrantSource source, Guid projectId) => source switch
    {
        EntrantSource.Participant p => p,
        EntrantSource.WinnerOf winner => session.Workspace.Results.GetValueOrDefault(new(projectId, winner.MatchId))?.Winner,
        EntrantSource.LoserOf loser => session.Workspace.Results.GetValueOrDefault(new(projectId, loser.MatchId))?.Loser,
        _ => null
    };

    private void DraftChanged()
    {
        if (refreshing) return;
        OnPropertyChanged(nameof(HasDrafts)); OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(StateMessage));
        OnPropertyChanged(nameof(DaySummary)); OnPropertyChanged(nameof(CanConfirmDay));
    }

    private bool IsDayEditable(OnsiteRecordRowViewModel row) => DateOnly.TryParse(row.Day, out var day) &&
        (row.HasSavedResult || WorkspaceDayConfirmationRules.NextUnconfirmedDay(session.Workspace) == day);

    private void QueueAutomaticSave(OnsiteRecordRowViewModel row)
    {
        if (disposed || refreshing) return;
        var task = AutoSaveAsync(row);
        lock (automaticSaves) automaticSaves.Add(task);
        _ = RemoveCompletedAsync(task);
    }

    private async Task AutoSaveAsync(OnsiteRecordRowViewModel row)
    {
        if (await SaveRowCoreAsync(row, !row.HasSavedResult) && row.HasDraft)
            await SaveRowAsync(row);
    }

    private async Task RemoveCompletedAsync(Task task)
    {
        try { await task; }
        finally { lock (automaticSaves) automaticSaves.Remove(task); }
    }

    public async Task WaitForAutomaticSavesAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (automaticSaves) pending = automaticSaves.ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAll(pending);
        }
    }
}
