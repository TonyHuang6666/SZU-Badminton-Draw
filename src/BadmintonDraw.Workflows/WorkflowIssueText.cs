namespace BadmintonDraw.Workflows;

internal static class WorkflowIssueText
{
    public static string BuildDetails(params string[] sections)
    {
        var visibleSections = sections
            .Where(section => !string.IsNullOrWhiteSpace(section))
            .ToList();

        return visibleSections.Count == 0
            ? ""
            : $"\n\n详细问题：\n\n{string.Join("\n\n", visibleSections)}";
    }

    public static string BuildSection(string title, IEnumerable<string> issues)
    {
        var visibleIssues = issues
            .Where(issue => !string.IsNullOrWhiteSpace(issue))
            .ToList();

        return visibleIssues.Count == 0
            ? ""
            : $"{title}（{visibleIssues.Count}）：\n"
                + string.Join("\n", visibleIssues.Select((issue, index) => $"{index + 1}. {issue}"));
    }

    public static string ValueOrEmpty(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "未填写" : value;
    }
}
