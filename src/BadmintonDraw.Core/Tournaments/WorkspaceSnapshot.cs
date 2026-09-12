using System.Collections.ObjectModel;
namespace BadmintonDraw.Core.Tournaments;
internal static class WorkspaceSnapshot
{
    internal static IReadOnlyList<T> List<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
    internal static IReadOnlyDictionary<K,V> Dictionary<K,V>(IEnumerable<KeyValuePair<K,V>> values) where K : notnull =>
        new ReadOnlyDictionary<K,V>(values.ToDictionary(pair => pair.Key, pair => pair.Value));
    internal static DrawResult Draw(DrawResult value) => value with
    {
        Groups = Groups(value.Groups), RoundOneGroups = Groups(value.RoundOneGroups), ByeGroups = Groups(value.ByeGroups)
    };
    private static IReadOnlyList<DrawGroup> Groups(IEnumerable<DrawGroup> values) =>
        List(values.Select(group => group with { Participants = List(group.Participants) }));
    internal static ScheduleDaySettings Day(ScheduleDaySettings value) => value with
    {
        Courts = List(value.Courts),
        RefereeCapacityWindows = List(value.RefereeCapacityWindows ?? []),
        UnavailableCourtWindows = List((value.UnavailableCourtWindows ?? []).Select(block => block with { Courts = List(block.Courts) }))
    };
}
