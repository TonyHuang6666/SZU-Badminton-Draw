using BadmintonDraw.Core;

namespace BadmintonDraw.Excel;

public sealed record ParticipantImportResult(
    IReadOnlyList<DrawParticipant> Participants,
    IReadOnlyList<ParticipantImportWarning> Warnings);

public sealed record ParticipantImportWarning(
    ParticipantImportWarningKind Kind,
    string Summary,
    string Detail);

public enum ParticipantImportWarningKind
{
    DuplicatePlayerName,
    UnrankedSeed
}
