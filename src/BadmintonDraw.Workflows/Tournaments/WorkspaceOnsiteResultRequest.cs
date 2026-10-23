using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Workflows.Tournaments;

/// <summary>A completed editor row, bound to the graph and actual participants the user saw.</summary>
public sealed record WorkspaceOnsiteResultRequest(
    WorkspaceMatchKey Key,
    string GraphRevision,
    string SideAIdentityKey,
    string SideBIdentityKey,
    string Winner,
    string Score,
    string Duration,
    TournamentResultKind Kind,
    DateOnly ActualPlayedDay,
    bool AllowCorrections = false,
    string? CorrectionReason = null);
