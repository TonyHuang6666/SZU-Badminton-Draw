namespace BadmintonDraw.Excel;

internal static class WorkspaceRecordSchema
{
    internal const string SheetName = "对阵记录表";
    internal const int HeaderRow = 4, ExampleRow = 5, FirstDataRow = 6;
    internal const int Order = 1, RecordDay = 2, Time = 3, Phase = 4, Group = 5,
        SideA = 6, Versus = 7, SideB = 8, Score = 9, Duration = 10, Court = 11, Winner = 12, Note = 13,
        MatchId = 14, OptionA = 15, OptionB = 16, WorkspaceId = 17, ProjectId = 18, GraphRevision = 19,
        DrawConfirmedAt = 20, ResultKind = 21, ActualPlayedDay = 22, LastColumn = ActualPlayedDay;

    internal static string Address(int row, int column) => $"${(char)('A' + column - 1)}${row}";
}
