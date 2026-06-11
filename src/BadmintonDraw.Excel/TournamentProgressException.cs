namespace BadmintonDraw.Excel;

public sealed class TournamentProgressException : Exception
{
    public TournamentProgressException(string message)
        : base(message)
    {
    }

    public TournamentProgressException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
