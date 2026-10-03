namespace BadmintonDraw.Core.Scheduling;

public sealed class VenueCourtGroup(string name, IReadOnlyList<string> courts)
{
    public string Name { get; } = name;
    public IReadOnlyList<string> Courts { get; } = Array.AsReadOnly(courts.ToArray());
}

public sealed class VenuePreset(string id, string campus, string name, string shortName, IReadOnlyList<VenueCourtGroup> groups)
{
    public string Id { get; } = id;
    public string Campus { get; } = campus;
    public string Name { get; } = name;
    public string ShortName { get; } = shortName;
    public IReadOnlyList<VenueCourtGroup> Groups { get; } = Array.AsReadOnly(groups.ToArray());
    public string DisplayName => $"{Campus}·{Name}";
    public string QualifiedCourtName(string label) => $"{ShortName} · {label}";
}

public static class BuiltInVenueCatalog
{
    public static IReadOnlyList<VenuePreset> Venues { get; } = Array.AsReadOnly(new VenuePreset[]
    {
        new("szu-yuehai-east", "粤海校区", "运动广场东馆羽毛球场", "粤海东馆",
            new[] { "A", "B", "C", "D" }.Select(group => new VenueCourtGroup(group,
                Enumerable.Range(1, 8).Select(number => $"{group}{number}").ToArray())).ToArray()),
        new("szu-lihu-zhikuai", "丽湖校区", "至快体育馆", "丽湖至快",
            [new("场地", Enumerable.Range(1, 12).Select(number => $"{number}号场").ToArray())]),
        new("szu-lihu-zhichang", "丽湖校区", "至畅体育馆", "丽湖至畅",
            [new("场地", Enumerable.Range(1, 10).Select(number => $"{number}号场").ToArray())])
    });

    public static VenuePreset? FindForCourts(IReadOnlyList<string> courts)
    {
        if (courts.Count == 0) return null;
        return Venues.FirstOrDefault(venue =>
        {
            var names = venue.Groups.SelectMany(group => group.Courts)
                .Select(venue.QualifiedCourtName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return courts.All(names.Contains);
        });
    }
}
