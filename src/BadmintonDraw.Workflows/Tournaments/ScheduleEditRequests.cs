using System.Collections.ObjectModel;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Workflows.Tournaments;

/// <summary>Opaque identity of the actual schedule used by a transient editor, not its latest UI revision.</summary>
public sealed class ScheduleEditBaseline
{
    internal ScheduleEditBaseline(object owner, Guid session, Guid workspaceId, long scheduleRevision, string sourceIdentity)
    { Owner = owner; Session = session; WorkspaceId = workspaceId; ScheduleRevision = scheduleRevision; SourceIdentity = sourceIdentity; }
    internal object Owner { get; }
    internal Guid Session { get; }
    internal string SourceIdentity { get; }
    public Guid WorkspaceId { get; }
    public long ScheduleRevision { get; }
}

public sealed record MoveMatchRequest(WorkspaceMatchKey Key, string DayLabel, TimeOnly StartTime, string Court,
    ScheduleEditBaseline Baseline);

public sealed record ScheduleEditChange(WorkspaceMatchKey Key, string ProjectName, string MatchName, int Depth,
    MatchPlacement Before, MatchPlacement After);

/// <summary>Exact immutable proposal, issued only by the active workflow. A failed proposal exposes no partial map.</summary>
public sealed class ScheduleEditPreview
{
    internal ScheduleEditPreview(MoveMatchRequest root, long revision, bool cascade,
        IReadOnlyList<ScheduleEditChange> changes, IReadOnlyList<SchedulingViolation> violations,
        IReadOnlyDictionary<Guid, MatchPlacement>? placements)
    {
        Root = root; SourceRevision = revision; IsCascade = cascade;
        Changes = Array.AsReadOnly(changes.ToArray()); Violations = Array.AsReadOnly(violations.ToArray());
        Placements = placements is null ? null : new ReadOnlyDictionary<Guid, MatchPlacement>(placements.ToDictionary());
    }
    public MoveMatchRequest Root { get; }
    public long SourceRevision { get; }
    public bool IsCascade { get; }
    public IReadOnlyList<ScheduleEditChange> Changes { get; }
    public IReadOnlyList<SchedulingViolation> Violations { get; }
    public bool CanApply => Placements is not null && Violations.Count == 0;
    public bool HasChanges => Changes.Count != 0;
    internal IReadOnlyDictionary<Guid, MatchPlacement>? Placements { get; }
}
