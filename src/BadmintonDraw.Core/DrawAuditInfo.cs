namespace BadmintonDraw.Core;

public sealed record DrawAuditInfo(
    DrawAlgorithmVersion AlgorithmVersion,
    string RandomSeed,
    DateTimeOffset GeneratedAtUtc,
    string InputHash,
    int ParticipantCount,
    int SeedCount,
    int GroupCount);
