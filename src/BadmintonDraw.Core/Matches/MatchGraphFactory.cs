using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Core.Matches;

public static class MatchGraphFactory
{
    /// <summary>Builds topology without resources or placement. Duration is the project's match-rule estimate.</summary>
    public static MatchGraph Create(Guid projectId, DrawResult draw, int expectedDurationMinutes = 30)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("项目标识不能为空。", nameof(projectId));
        ArgumentNullException.ThrowIfNull(draw);
        if (expectedDurationMinutes <= 0) throw new ArgumentOutOfRangeException(nameof(expectedDurationMinutes));
        var discipline = draw.Settings.EventKind switch
        {
            EventKind.Singles => EventDiscipline.MenSingles,
            EventKind.Doubles => EventDiscipline.MenDoubles,
            EventKind.Team => EventDiscipline.Team,
            _ => throw new ArgumentOutOfRangeException(nameof(draw))
        };
        var topology = MatchTopologyBuilder.Build(draw);
        var ids = topology.ToDictionary(m => m.MatchId, m => MatchId(projectId, m.MatchId), StringComparer.Ordinal);
        EntrantSource Source(MatchTopologyBuilder.UnscheduledMatch match, ScheduleMatchSide side)
        {
            var edge = match.Dependencies.SingleOrDefault(d => d.TargetSide == side);
            if (edge is not null)
                return edge.Outcome == ScheduleMatchDependencyOutcome.Winner
                    ? new EntrantSource.WinnerOf(ids[edge.SourceMatchId])
                    : new EntrantSource.LoserOf(ids[edge.SourceMatchId]);
            var participant = side == ScheduleMatchSide.SideA ? match.SideAParticipant : match.SideBParticipant;
            if (participant is null)
                throw new DrawValidationException("比赛来源缺少参赛方或明确的晋级依赖。");
            return ProjectEntrantIdentity.Create(discipline, participant);
        }

        var matches = topology.Select(m => new MatchNode(ids[m.MatchId], projectId, m.MatchId, m.Id,
            m.GroupNumber, m.Phase, m.MatchName, Source(m, ScheduleMatchSide.SideA), Source(m, ScheduleMatchSide.SideB),
            expectedDurationMinutes, m.Dependencies.Select(d => ids[d.SourceMatchId]).Distinct().ToArray())
        {
            GroupName = m.GroupName, Note = m.Note, SameUnit = m.SameUnit,
            KnockoutEntrantCount = m.KnockoutEntrantCount, ForceBeforeTimingBoundary = m.ForceBeforeTimingBoundary,
            IsChampionshipBracket = m.IsChampionshipBracket, IsChampionshipFinal = m.IsChampionshipFinal,
            IsPlacementPlayoff = m.IsPlacementPlayoff
        }).ToArray();
        // Audit timestamps describe generation, not the semantic draw version.
        var content = JsonSerializer.Serialize(new
        {
            Schema = 1, ProjectId = projectId, draw.Settings, draw.Groups, draw.RoundOneGroups, draw.ByeGroups, Matches = matches
        });
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        return new(projectId, revision, matches);
    }

    private static Guid MatchId(Guid projectId, string localKey) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{projectId:D}:{localKey}")).AsSpan(0, 16));
}
