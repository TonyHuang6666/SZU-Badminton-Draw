using System.Text.Json;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Workflows.Tournaments;

/// <param name="SourceIndex">Zero-based index in the persisted roster at expectedRevision. Sorting the UI must not renumber it.</param>
public sealed record RosterSeedEdit(int SourceIndex, bool IsSeed, int? SeedRank);

public sealed partial class TournamentWorkspaceWorkflow
{
    public WorkspaceCommandResult UpdateRosterSeeds(Guid projectId, IReadOnlyList<RosterSeedEdit> edits, long expectedRevision) =>
        Change(expectedRevision, workspace =>
        {
            var project = EditableProject(workspace, projectId);
            Require(project.Roster is not null, "roster.required", "请先导入项目名单。");
            var changes = edits.ToArray();
            var participants = project.Roster!.Participants.ToArray();
            Require(changes.Length > 0, "roster.seed-edits", "没有需要保存的种子设置。");
            Require(changes.Select(e => e.SourceIndex).Distinct().Count() == changes.Length &&
                changes.All(e => e.SourceIndex >= 0 && e.SourceIndex < participants.Length),
                "roster.seed-index", "名单行索引无效或重复，请刷新名单后重试。");
            foreach (var edit in changes)
                participants[edit.SourceIndex] = participants[edit.SourceIndex] with
                    { IsSeed = edit.IsSeed, SeedRank = edit.SeedRank };
            ValidateRosterSeeds(participants);
            var warnings = project.Roster.Warnings.Where(w => w.Code != "roster.unranked-seed")
                .Concat(participants.Where(p => p.IsSeed && p.SeedRank is null).Select(p =>
                    new ProjectRosterWarning("roster.unranked-seed", $"种子未编号：“{p.DisplayName}”标记为种子，但未填写种子序号。"))).ToArray();
            // ContentHash identifies the original imported bytes; seed edits do not change that provenance.
            var roster = project.Roster with { Participants = participants, Warnings = warnings };
            return Audit(ReplaceProject(workspace, project with { Roster = roster, Draw = null }), "RosterSeedsUpdated", projectId,
                JsonSerializer.Serialize(new { SourceFile = roster.SourceFileName, SourceFileHash = roster.ContentHash, Edits = changes }));
        });

    private static void ValidateRosterSeeds(IReadOnlyList<DrawParticipant> participants)
    {
        Require(participants.All(p => p.IsSeed || p.SeedRank is null), "roster.seed-flag", "填写种子序号的参赛方必须标记为种子。");
        var maximum = OfficialDrawRules.GetMaximumSeedCount(participants.Count);
        Require(participants.Count(p => p.IsSeed) <= maximum, "roster.seed-count", $"当前参赛数量最多设置 {maximum} 个种子。");
        Require(participants.All(p => p.SeedRank is null || p.SeedRank > 0 && p.SeedRank <= maximum),
            "roster.seed-rank", $"种子序号必须为 1 至 {maximum}。");
        var ranks = participants.Where(p => p.SeedRank.HasValue).Select(p => p.SeedRank!.Value).ToArray();
        Require(ranks.Distinct().Count() == ranks.Length, "roster.seed-duplicate", "种子序号不能重复。");
    }
}
