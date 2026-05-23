namespace BadmintonDraw.Excel;

public sealed class ExcelImportException : Exception
{
    public ExcelImportException(string message)
        : base(message)
    {
    }
}
