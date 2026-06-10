using BadmintonDraw.Core;

namespace BadmintonDraw.Workflows;

public static class WorkflowLabels
{
    public static string GetEventKindDisplay(EventKind eventKind)
    {
        return eventKind switch
        {
            EventKind.Singles => "单打",
            EventKind.Team => "团体",
            _ => "双打"
        };
    }

    public static string BuildGroupName(int groupNumber)
    {
        if (groupNumber <= 0)
        {
            return "总决赛";
        }

        var columnName = "";
        var number = groupNumber;
        while (number > 0)
        {
            number--;
            columnName = (char)('A' + number % 26) + columnName;
            number /= 26;
        }

        return $"{columnName}组";
    }
}

public static class WorkflowFileNames
{
    public static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value
            .Select(ch => invalid.Contains(ch) ? '_' : ch)
            .ToArray())
            .Trim();
    }
}
