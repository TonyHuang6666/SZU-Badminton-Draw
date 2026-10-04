using System.Text.Json;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Scheduling;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed partial class ScheduleSetupPageViewModel
{
    private static string StrategyName(ScheduleAutoSchedulingStrategy strategy) => strategy switch
    {
        ScheduleAutoSchedulingStrategy.Compact => "尽快完成",
        ScheduleAutoSchedulingStrategy.BalancedRelaxed => "时间安排均衡",
        ScheduleAutoSchedulingStrategy.FinalsDayFriendly => "重要比赛集中在最后一天",
        _ => "自定义安排"
    };

    private static IEnumerable<string> CapturedFailureDetails(SchedulingFailure failure)
    {
        if (failure.Diagnostics is { } diagnostics)
        {
            yield return $"终止阶段：{diagnostics.Phase}；结果：{diagnostics.FailureKind}";
            yield return $"容量预检：{diagnostics.PreflightStatus}（{diagnostics.PreflightStatus switch
            {
                SchedulingPreflightStatus.NotRun => "尚未执行",
                SchedulingPreflightStatus.Unknown => "未完成，不能判断是否存在容量矛盾",
                SchedulingPreflightStatus.ProvenInfeasible => "已证明容量矛盾",
                _ => "未发现容量矛盾，不保证可排"
            }}）";
            var rejected = diagnostics.RejectedBudgetPhases
                .Concat(diagnostics.ExhaustedPhase is { } exhausted ? [exhausted] : Array.Empty<SchedulingRunPhase>()).Distinct().ToArray();
            yield return rejected.Length > 0
                ? $"预算拒绝支出：{string.Join("、", rejected)}。实际已用量可能小于额度；下一笔工作未获批准。"
                : "未记录预算拒绝支出；不能据此将终止原因归为额度耗尽。";
            if (diagnostics.FailureKind is SchedulingFailureKind.SearchIncomplete && rejected.Length == 0)
                yield return "有界搜索未找到完整方案；诊断未提供具体分支或回退限制数值。";
            yield return "已用工作单位：" + string.Join("，", diagnostics.UsedWorkUnits.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"));
            yield return $"本次设置快照：最短休息：{diagnostics.Resources.MinimumRestMinutes} 分钟；每日上限：{diagnostics.Resources.MaxPlayerMatchesPerDay} 场；策略：{StrategyName(diagnostics.Policy.Strategy)}。";
            yield return "冠亚军决赛必须在最后比赛日：" + (diagnostics.Policy.RequireChampionshipFinalsOnLastDay ? "是" : "否") + "。";
            foreach (var day in diagnostics.Resources.Days)
                yield return $"{day.DayLabel}：{day.DayStart:HH:mm:ss.fffffff}–{day.DayEnd:HH:mm:ss.fffffff}；场地：{string.Join("、", day.Courts)}";
            // These are immutable run settings, never the currently edited controls.
            // Keep every resource window and policy override available for verification.
            yield return "完整资源与策略快照：" + JsonSerializer.Serialize(new { diagnostics.Resources, diagnostics.Policy }, new JsonSerializerOptions { WriteIndented = true });
        }
        if (failure.CapacityEvidence is { } evidence)
        {
            var witness = evidence.WitnessMatchIds.ToHashSet();
            var projects = failure.UnplacedMatches.Where(m => witness.Contains(m.MatchId)).Select(m => m.ProjectName).Distinct().ToArray();
            yield return projects.Length > 0 ? "涉及项目：" + string.Join("、", projects) : "涉及项目：本次诊断未提供项目名称。";
            if (!string.IsNullOrWhiteSpace(evidence.PlayerKey)) yield return $"选手身份核对：{evidence.PlayerName}（{evidence.PlayerKey}）";
            yield return $"证据类型：{evidence.Kind}；见证场数：{evidence.WitnessMatchIds.Count}（相容路径下界，不代表精确最大值）。";
        }
    }
}
