using System.Globalization;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed partial class OnsiteRecordViewModel
{
    public string DaySummary
    {
        get
        {
            if (!DateOnly.TryParseExact(SelectedDay, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return "";
            var rows = Rows.Where(r => r.Day == SelectedDay).ToArray();
            var saved = rows.Count(r => r.HasSavedResult);
            var confirmed = session.Workspace.DayConfirmations.SingleOrDefault(c => c.Day == day);
            return confirmed is null ? $"当天：已记录 {saved} / {rows.Length} 场 · 待处理 {rows.Length - saved} 场 · 待确认" :
                $"当天已确认 · 已记录 {confirmed.CompletedCount} 场 · 经批准延期 {confirmed.Postponements.Count} 场";
        }
    }

    public bool CanConfirmDay => CanEdit && !IsWorking && DateOnly.TryParse(SelectedDay, out var day) &&
        WorkspaceDayConfirmationRules.NextUnconfirmedDay(session.Workspace) == day;

    public async Task<OnsiteDayConfirmationViewModel?> PrepareDayConfirmationAsync()
    {
        if (!CanConfirmDay) return null;
        var requestedDay = SelectedDay;
        if (!await FlushAsync())
        { StateMessage = "还有未保存的填写内容，请重试保存或放弃这些修改后再确认当天结果。"; return null; }
        if (!CanConfirmDay || SelectedDay != requestedDay) return null;
        return new(shell, session, DateOnly.ParseExact(requestedDay, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            Rows.Where(r => r.Day == requestedDay && !r.HasSavedResult).Select(r =>
                new OnsitePostponementRowViewModel(r.Key, $"{r.ProjectName} · 第 {r.OrderText} 场 · {r.Time} · {r.Court}\n{r.SideA} — {r.SideB}")).ToArray(),
            completion =>
            {
                RefreshSession(completion);
                var next = WorkspaceDayConfirmationRules.NextUnconfirmedDay(completion.Workspace);
                if (next.HasValue) SelectedDay = next.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                StateMessage = next.HasValue ? $"{requestedDay} 的比赛结果已确认，已解锁 {SelectedDay}。" : "全部比赛日结果已确认。";
                DraftChanged();
            });
    }
}
