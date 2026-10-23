using System.Globalization;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

/// <summary>Stable editor row. Drafts are independent of the immutable workspace snapshot.</summary>
public sealed class OnsiteRecordRowViewModel : ViewModelBase
{
    private readonly Action changed;
    private readonly Action<OnsiteRecordRowViewModel> resultSelected;
    private OnsiteRecordInput input = new(0, "", "", -1, "");
    private OnsiteRecordInput baseline = new(0, "", "", -1, "");
    private TournamentMatchResult? result;
    private bool available, conflict, allowCorrections;
    private string correctionReason = "", message = "";
    private string graphRevision = "", sideAIdentity = "", sideBIdentity = "";

    internal OnsiteRecordRowViewModel(WorkspaceMatchKey key, Action changed,
        Action<OnsiteRecordRowViewModel> resultSelected)
    { Key = key; this.changed = changed; this.resultSelected = resultSelected; }

    public WorkspaceMatchKey Key { get; }
    public string OrderText { get; private set; } = "";
    public string Day { get; private set; } = "";
    public string Time { get; private set; } = "";
    public string ProjectName { get; private set; } = "";
    public string Phase { get; private set; } = "";
    public string GroupName { get; private set; } = "";
    public string SideA { get; private set; } = "";
    public string SideB { get; private set; } = "";
    public string Court { get; private set; } = "";
    public string Note { get; private set; } = "";
    public string[] WinnerOptions { get; private set; } = ["请选择胜方", "A", "B"];
    public string[] KindOptions { get; } = ["正常", "弃权"];
    public int WinnerIndex { get => input.WinnerIndex; set => Edit(input with { WinnerIndex = value }, true); }
    public string Score { get => input.Score; set => Edit(input with { Score = value ?? "" }); }
    public string Duration { get => input.Duration; set => Edit(input with { Duration = value ?? "" }); }
    public string ActualDay { get => input.ActualDay; set => Edit(input with { ActualDay = value ?? "" }); }
    public int KindIndex
    {
        get => input.KindIndex;
        set => Edit(input with { KindIndex = value }, true);
    }
    public bool AllowCorrections
    {
        get => allowCorrections;
        set { if (SetProperty(ref allowCorrections, value)) { message = ""; Notify(); } }
    }
    public string CorrectionReason
    {
        get => correctionReason;
        set { if (SetProperty(ref correctionReason, value ?? "")) { message = ""; Notify(); } }
    }
    public bool HasDraft => input != baseline;
    public bool IsSaved => result is not null && !HasDraft;
    public bool HasSavedResult => result is not null;
    public bool HasRequiredChoices => WinnerIndex is 1 or 2 && KindIndex is 0 or 1;
    public bool NeedsCorrectionConsent => result is not null && HasDraft &&
        (input.WinnerIndex != baseline.WinnerIndex || input.KindIndex != baseline.KindIndex ||
         input.ActualDay.Trim() != baseline.ActualDay ||
         (!string.IsNullOrWhiteSpace(baseline.Score) && input.Score.Trim() != baseline.Score) ||
         (!string.IsNullOrWhiteSpace(baseline.Duration) && input.Duration.Trim() != baseline.Duration));
    public bool CanEdit => available && !conflict && sideAIdentity.Length > 0 && sideBIdentity.Length > 0;
    public string Status => conflict ? "对阵或赛果已更新，请放弃本行修改后重新填写。" :
        message.Length > 0 ? message : HasDraft ? result is not null ? "赛果已保存 · 修改待保存" : "待保存" : result is not null ? "已保存" :
        sideAIdentity.Length == 0 || sideBIdentity.Length == 0 ? "等待上游赛果" : "待填写";

    internal void Refresh(TournamentProject project, MatchNode node, ScheduledMatch placement,
        EntrantSource.Participant? a, EntrantSource.Participant? b, TournamentMatchResult? next,
        bool ownSave, bool canEdit)
    {
        var hadDraft = HasDraft;
        var identityChanged = graphRevision.Length > 0 &&
            (graphRevision != project.MatchGraph!.Revision || sideAIdentity != (a?.IdentityKey ?? "") ||
             sideBIdentity != (b?.IdentityKey ?? ""));
        if (hadDraft && !ownSave && (identityChanged || !SameVersion(result, next))) conflict = true;
        OrderText = node.Order.ToString(CultureInfo.InvariantCulture);
        Day = placement.DayLabel; Time = placement.TimeRange; Court = placement.Court;
        ProjectName = project.DisplayName; Phase = node.Phase; GroupName = node.GroupName;
        SideA = placement.SideA; SideB = placement.SideB; Note = node.Note;
        graphRevision = project.MatchGraph!.Revision;
        sideAIdentity = a?.IdentityKey ?? ""; sideBIdentity = b?.IdentityKey ?? "";
        WinnerOptions = ["请选择胜方", $"A【{SideA}】", $"B【{SideB}】"];
        result = next; available = canEdit;
        // Publication during an in-flight save is reconciled by CompleteSave with its exact submitted input.
        if (!hadDraft && !ownSave) ResetInput();
        Notify();
    }

    internal void SetAvailable(bool value) { available = value; OnPropertyChanged(nameof(CanEdit)); }
    internal void Invalidate() { conflict = true; available = false; Notify(); }
    internal void SetStatus(string value) { message = value; OnPropertyChanged(nameof(Status)); }
    internal OnsiteRecordInput Capture(bool outcomeOnly = false) => outcomeOnly ? input with { Score = "", Duration = "" } : input;
    internal void CompleteSave(OnsiteRecordInput submitted)
    {
        baseline = SavedInput();
        if (input == submitted) input = baseline;
        conflict = false; message = ""; allowCorrections = false; correctionReason = "";
        Notify();
    }
    internal void Discard()
    { conflict = false; ResetInput(); Notify(); }

    internal bool TryRequest(out WorkspaceOnsiteResultRequest? request, bool outcomeOnly = false)
    {
        request = null;
        if (conflict) { Notify(); return false; }
        if (!CanEdit) { SetStatus("当前场次暂不能填写，记录未保存。"); return false; }
        if (!HasRequiredChoices)
        { SetStatus("未保存：请选择胜方和比赛结果；比分、时长可以留空。"); return false; }
        if (!DateOnly.TryParseExact(ActualDay.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var day))
        { SetStatus("未保存：实际比赛日期请填写为 yyyy-MM-dd。"); return false; }
        if (NeedsCorrectionConsent && (!AllowCorrections || string.IsNullOrWhiteSpace(CorrectionReason)))
        { SetStatus("未保存：更正已保存记录需勾选允许更正并填写原因。"); return false; }
        request = new(Key, graphRevision, sideAIdentity, sideBIdentity, WinnerIndex == 1 ? "A" : "B",
            outcomeOnly ? "" : Score, outcomeOnly ? "" : Duration, KindIndex == 1 ? TournamentResultKind.Walkover : TournamentResultKind.Played,
            day, AllowCorrections, CorrectionReason);
        return true;
    }

    private void Edit(OnsiteRecordInput value, bool requiredChoice = false)
    {
        if (value == input) return;
        input = value; message = ""; Notify();
        if (requiredChoice && HasRequiredChoices) resultSelected(this);
    }
    private void ResetInput()
    {
        baseline = SavedInput(); input = baseline; message = "";
        allowCorrections = false; correctionReason = "";
    }
    private OnsiteRecordInput SavedInput() => result is null ? new(0, "", "", -1, Day) :
        new(result.Winner.IdentityKey == sideAIdentity ? 1 : 2, result.Score,
            result.DurationMinutes?.ToString(CultureInfo.InvariantCulture) ?? "",
            result.Kind == TournamentResultKind.Walkover ? 1 : 0,
            result.ActualPlayedDay?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "");
    private void Notify() { OnPropertyChanged(null); changed(); }
    private static bool SameVersion(TournamentMatchResult? a, TournamentMatchResult? b) =>
        a is null ? b is null : b is not null && a.RecordedAt == b.RecordedAt && a.Score == b.Score &&
        a.DurationMinutes == b.DurationMinutes && a.Kind == b.Kind && a.ActualPlayedDay == b.ActualPlayedDay &&
        a.Winner.IdentityKey == b.Winner.IdentityKey && a.Loser.IdentityKey == b.Loser.IdentityKey;
}

internal sealed record OnsiteRecordInput(int WinnerIndex, string Score, string Duration, int KindIndex, string ActualDay);
