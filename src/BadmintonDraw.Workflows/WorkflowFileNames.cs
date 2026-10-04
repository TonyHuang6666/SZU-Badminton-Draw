using System.Text.RegularExpressions;

namespace BadmintonDraw.Workflows;

public static class WorkflowFileNames
{
    public static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars()
            .Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|'])
            .ToHashSet();
        var sanitized = new string(value.Trim()
            .Select(ch => char.IsControl(ch) || invalid.Contains(ch) ? '_' : ch)
            .ToArray());
        sanitized = Regex.Replace(sanitized, @"_+", "_");
        sanitized = Regex.Replace(sanitized, @"\s+", "");
        return sanitized.Trim('_', '-', ' ');
    }
}
