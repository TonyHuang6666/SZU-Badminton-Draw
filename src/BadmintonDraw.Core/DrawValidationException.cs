namespace BadmintonDraw.Core;

public sealed class DrawValidationException : Exception
{
    public DrawValidationException(string message)
        : base(message)
    {
    }
}
