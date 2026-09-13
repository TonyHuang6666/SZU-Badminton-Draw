using BadmintonDraw.Core;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using ClosedXML.Excel;
using static V5AcceptanceEvidence;

internal sealed record V5RosterDefinition(EventDiscipline Discipline, CompetitionMode Mode, int Count, int Groups,
    string Seed, bool EdgeNames = false, PlacementPlayoff Playoff = PlacementPlayoff.None)
{
    internal EventKind Kind => Discipline == EventDiscipline.Team ? EventKind.Team : Discipline is EventDiscipline.MenDoubles or EventDiscipline.MixedDoubles ? EventKind.Doubles : EventKind.Singles;
    internal DrawSettings Settings => new(Mode, Kind, Groups, Seed, KnockoutGoal: KnockoutGoal.Champion, PlacementPlayoff: Playoff);
}

internal static class V5AcceptanceRosterFactory
{
    internal static V5RosterDefinition[] Multiple(int singles, int pairs, int groups) =>
    [new(EventDiscipline.MenSingles, CompetitionMode.SinglesKnockout, singles, groups, "v5-scale-ms", Playoff: PlacementPlayoff.ThirdToEighth),
     new(EventDiscipline.MenDoubles, CompetitionMode.SinglesRoundRobin, pairs, 4, "v5-scale-md"),
     new(EventDiscipline.MixedDoubles, CompetitionMode.SinglesRoundRobin, pairs, 4, "v5-scale-xd")];
    internal static V5RosterDefinition Small() => new(EventDiscipline.MenSingles, CompetitionMode.SinglesKnockout, 8, 1,
        "v5-edge-single", EdgeNames: true, Playoff: PlacementPlayoff.ThirdToEighth);
    private static string Male(int i) => $"男选手{i:D3}";
    private static string Female(int i) => $"女选手{i:D3}";
    private static string MaleId(int i) => $"202600M{i:D3}";
    private static string FemaleId(int i) => $"202600F{i:D3}";
    private static readonly string[] EdgeNames = ["李  / 明【甲】胜者", "张\"三\"", "A【乙】", "B(选手)", "【胜者】王", "负者不是占位符", "林 / 刘", "陈'引号"];

    internal static string Write(V5AcceptanceEvidence evidence, V5RosterDefinition definition, int ordinal)
    {
        var headers = new[] { "姓名", "学号", "学院/学部", "搭档姓名", "搭档学号", "搭档学院/学部", "是否种子", "种子序号", "备注" };
        var rows = Enumerable.Range(1, definition.Count).Select(i =>
        {
            var college = $"学院{(i - 1) % 12 + 1}";
            var primary = definition.Discipline == EventDiscipline.MenDoubles ? 2 * i - 1 : i;
            return new[] {
                definition.Kind == EventKind.Team ? "" : definition.EdgeNames ? EdgeNames[i - 1] : Male(primary),
                definition.Kind == EventKind.Team ? "" : MaleId(primary),
                definition.Kind == EventKind.Team ? $"虚拟学院{i:D2}" : college,
                definition.Discipline == EventDiscipline.MenDoubles ? Male(2 * i) : definition.Discipline == EventDiscipline.MixedDoubles ? Female(i) : "",
                definition.Discipline == EventDiscipline.MenDoubles ? MaleId(2 * i) : definition.Discipline == EventDiscipline.MixedDoubles ? FemaleId(i) : "",
                definition.Kind == EventKind.Doubles ? college : "", i <= (definition.Count < 16 ? 2 : 4) ? "是" : "否", i <= (definition.Count < 16 ? 2 : 4) ? i.ToString() : "", "合成验收数据，非真实报名" };
        }).ToArray();
        var ids = rows.SelectMany(r => new[] { r[1], r[4] }).Where(id => id.Length > 0).ToArray();
        Require(ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() == ids.Length, "Synthetic roster repeats a player within one project.");
        var name = $"rosters/{ordinal}-{definition.Discipline}";
        evidence.Text(name + ".csv", string.Join("\n", new[] { Csv(headers) }.Concat(rows.Select(r => Csv(r)))) + "\n");
        using var book = new XLWorkbook(); var sheet = book.AddWorksheet("合成名单");
        for (var c = 0; c < headers.Length; c++) sheet.Cell(1, c + 1).Value = headers[c];
        for (var r = 0; r < rows.Length; r++) for (var c = 0; c < rows[r].Length; c++) sheet.Cell(r + 2, c + 1).Value = rows[r][c];
        sheet.Columns().AdjustToContents(); var path = evidence.PathFor(name + ".xlsx"); book.SaveAs(path); return path;
    }
    internal static void VerifyShared(TournamentWorkspace workspace, int singles, int pairs)
    {
        var entries = workspace.Projects.SelectMany(p => p.Roster!.Participants.SelectMany(e =>
            new[] { e.PrimaryStudentId, e.PartnerStudentId }.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => (p.Id, Student: id!)))).ToArray();
        var male = entries.Where(e => e.Student.StartsWith("202600M", StringComparison.Ordinal)).GroupBy(e => e.Student).ToArray();
        Require(male.Count(g => g.Select(e => e.Id).Distinct().Count() == 3) == Math.Min(singles, pairs), "Three-project shared-player identity count differs.");
        Require(male.Count(g => g.Select(e => e.Id).Distinct().Count() == 2) == Math.Min(singles, 2 * pairs) - Math.Min(singles, pairs), "Two-project shared-player identity count differs.");
    }
    internal static TournamentResourcePlan ModerateResources() => Resources(
        [new(2026, 6, 16), new(2026, 6, 17), new(2026, 6, 18), new(2026, 6, 22), new(2026, 6, 23)], 16, 12, 30, 4);
    internal static TournamentResourcePlan LargeResources() => Resources(Enumerable.Range(14, 7).Select(d => new DateOnly(2026, 9, d)).ToArray(), 16, 12, 30, 4);
    internal static TournamentResourcePlan SmallResources() => Resources([new(2026, 9, 20), new(2026, 9, 21), new(2026, 9, 22)], 2, 2, 15, 10);
    private static TournamentResourcePlan Resources(DateOnly[] dates, int courts, int referees, int rest, int cap)
    {
        var names = Enumerable.Range(1, Math.Min(8, courts)).Select(i => "B" + i).Concat(Enumerable.Range(1, Math.Max(0, courts - 8)).Select(i => "C" + i)).ToArray();
        return new(dates.Select((d, i) => new ScheduleDaySettings(d, new(14, 0), new(20, 0), names,
            UnavailableCourtWindows: i == 0 ? [new(new(14, 0), new(15, 0), ["B1"])] : null)).ToArray(), referees, rest, cap);
    }
    internal static TournamentSchedulingPolicy Policy(TournamentWorkspace workspace, bool mixed) => new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], [])
    { ProjectTimings = workspace.Projects.ToDictionary(p => p.Id, p => new ProjectMatchTiming(mixed ? p.Discipline == EventDiscipline.MenDoubles ? 25 : p.Discipline == EventDiscipline.MixedDoubles ? 30 : 20 : 20)) };
}
