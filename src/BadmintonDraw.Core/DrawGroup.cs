namespace BadmintonDraw.Core;

public sealed record DrawGroup(int Number, IReadOnlyList<DrawParticipant> Participants)
{
    public int Count => Participants.Count;
}
