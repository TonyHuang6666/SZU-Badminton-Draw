namespace BadmintonDraw.Core;

public static class BracketStageLabels
{
    public static IReadOnlyList<string> BuildQualifierRoundHeaders(IReadOnlyList<int> groupSlotCounts)
    {
        var headers = BuildQualifierMatchPhases(groupSlotCounts).ToList();
        headers.Add("出线");
        return headers;
    }

    public static IReadOnlyList<string> BuildQualifierMatchPhases(IReadOnlyList<int> groupSlotCounts)
    {
        var counts = groupSlotCounts
            .Where(count => count > 0)
            .Select(count => Math.Max(1, count))
            .ToList();
        var phases = new List<string>();

        while (counts.Any(count => count > 1))
        {
            var from = counts.Sum();
            counts = counts
                .Select(count => Math.Max(1, count / 2))
                .ToList();
            var to = counts.Sum();
            phases.Add($"{from}进{to}");
        }

        return phases;
    }

    public static IReadOnlyList<string> BuildChampionRoundHeaders(int qualifierCount)
    {
        var headers = new List<string>();
        var from = Math.Max(1, qualifierCount);

        while (from > 1)
        {
            var to = Math.Max(1, from / 2);
            headers.Add(to == 1 ? "冠军" : $"{from}进{to}");
            from = to;
        }

        return headers;
    }

    public static IReadOnlyList<string> BuildChampionMatchPhases(int qualifierCount)
    {
        var phases = new List<string>();
        var from = Math.Max(1, qualifierCount);

        while (from > 1)
        {
            var to = Math.Max(1, from / 2);
            phases.Add(to == 1 ? "决赛" : $"{from}进{to}");
            from = to;
        }

        return phases;
    }
}
