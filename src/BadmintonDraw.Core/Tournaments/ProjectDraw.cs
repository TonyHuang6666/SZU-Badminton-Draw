namespace BadmintonDraw.Core.Tournaments;
public sealed record ProjectDraw(DrawResult Result, DateTimeOffset? ConfirmedAt)
{
    private DrawResult result = WorkspaceSnapshot.Draw(Result);
    public DrawResult Result { get => result; init => result = WorkspaceSnapshot.Draw(value); }
}
