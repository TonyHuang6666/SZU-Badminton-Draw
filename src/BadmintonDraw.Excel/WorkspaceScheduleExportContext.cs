using System.Collections.ObjectModel;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Excel;

/// <summary>One validated global projection, shared by the material renderers before selecting rows.</summary>
public sealed class WorkspaceScheduleExportContext
{
    public WorkspaceScheduleExportContext(TournamentWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        TournamentWorkspaceRules.Validate(workspace);
        if (workspace.Schedule is null)
            throw new WorkspaceValidationException("export.schedule", "请先完成统一赛程编排，再导出赛事材料。");
        Workspace = workspace;
        Projects = new ReadOnlyDictionary<Guid, TournamentProject>(workspace.Projects.ToDictionary(p => p.Id));
        Nodes = new ReadOnlyDictionary<WorkspaceMatchKey, MatchNode>(workspace.Projects
            .SelectMany(p => p.MatchGraph!.Matches).ToDictionary(n => new WorkspaceMatchKey(n.ProjectId, n.Id)));
        var owners = Nodes.Keys.ToDictionary(k => k.MatchId);
        var projection = ScheduledMatchProjection.Build(workspace.Projects.Select(p => p.MatchGraph!).ToArray(),
            workspace.Schedule, workspace.Results);
        Matches = new ReadOnlyDictionary<WorkspaceMatchKey, ScheduledMatch>(projection
            .ToDictionary(m => owners[Guid.Parse(m.MatchId)]));
        Placements = new ReadOnlyDictionary<WorkspaceMatchKey, MatchPlacement>(Nodes.Keys
            .ToDictionary(k => k, k => workspace.Schedule.Placements[k.MatchId]));
        MatchKeys = Array.AsReadOnly(projection.Select(m => owners[Guid.Parse(m.MatchId)]).ToArray());
    }

    public TournamentWorkspace Workspace { get; }
    public IReadOnlyList<WorkspaceMatchKey> MatchKeys { get; }
    internal IReadOnlyDictionary<Guid, TournamentProject> Projects { get; }
    internal IReadOnlyDictionary<WorkspaceMatchKey, MatchNode> Nodes { get; }
    internal IReadOnlyDictionary<WorkspaceMatchKey, ScheduledMatch> Matches { get; }
    internal IReadOnlyDictionary<WorkspaceMatchKey, MatchPlacement> Placements { get; }

    // Candidate-player unions in ScheduledMatch are scheduling constraints, not resolved entrants.
    internal EntrantSource.Participant? ResolveParticipant(Guid projectId, EntrantSource source) => source switch
    {
        EntrantSource.Participant participant => participant,
        EntrantSource.WinnerOf winner when Workspace.Results.TryGetValue(new(projectId, winner.MatchId), out var result) => result.Winner,
        EntrantSource.LoserOf loser when Workspace.Results.TryGetValue(new(projectId, loser.MatchId), out var result) => result.Loser,
        _ => null
    };
}

/// <summary>Explicit coverage date, not a placement change or an inferred actual-played date.</summary>
public sealed record WorkspaceRecordExportRow(WorkspaceMatchKey Key, DateOnly RecordDay);
