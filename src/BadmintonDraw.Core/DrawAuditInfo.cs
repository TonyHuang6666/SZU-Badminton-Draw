namespace BadmintonDraw.Core;

public sealed record DrawAuditInfo(
    DrawAlgorithmVersion AlgorithmVersion,
    string RandomSeed,
    DateTimeOffset GeneratedAt,
    string InputHash,
    int ParticipantCount,
    int SeedCount,
    int GroupCount);
