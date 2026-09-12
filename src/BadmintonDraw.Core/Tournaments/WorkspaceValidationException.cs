namespace BadmintonDraw.Core.Tournaments;
public sealed class WorkspaceValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
