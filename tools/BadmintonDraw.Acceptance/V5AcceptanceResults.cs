using System.Text.Json;
using System.Text.RegularExpressions;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Tournaments;
using ClosedXML.Excel;
using static V5AcceptanceEvidence;

internal sealed record V5ExpectedResult(WorkspaceMatchKey Key, bool ChooseA, EntrantSource.Participant Winner,
    EntrantSource.Participant Loser, string Score, int Minutes, TournamentResultKind Kind, DateOnly ActualDay);

internal static class V5AcceptanceResults
{
    internal static Dictionary<WorkspaceMatchKey, V5ExpectedResult> Fold(TournamentWorkspace source, DateOnly actualDay)
    {
        var expected = new Dictionary<WorkspaceMatchKey, V5ExpectedResult>();
        foreach (var project in source.Projects.OrderBy(p => p.SortOrder))
        {
            var remaining = project.MatchGraph!.Matches.ToDictionary(n => n.Id);
            while (remaining.Count > 0)
            {
                var ready = remaining.Values.Where(n => n.Dependencies.All(id => expected.ContainsKey(new(project.Id, id)))).OrderBy(n => n.Order).ToArray();
                Require(ready.Length > 0, "Typed graph oracle has unresolved/cyclic prerequisites.");
                foreach (var node in ready)
                {
                    EntrantSource.Participant Resolve(EntrantSource s) => s switch
                    { EntrantSource.Participant p => p, EntrantSource.WinnerOf w => expected[new(project.Id, w.MatchId)].Winner,
                        EntrantSource.LoserOf l => expected[new(project.Id, l.MatchId)].Loser, _ => throw new InvalidOperationException("Non-playable oracle source.") };
                    var a = Resolve(node.SideA); var b = Resolve(node.SideB);
                    var chooseA = Convert.ToByte(HashText($"v5-results|{project.SortOrder}|{node.OriginalMatchId}")[..2], 16) % 2 == 0;
                    var team = source.Kind == TournamentKind.Team;
                    expected.Add(new(project.Id, node.Id), new(new(project.Id, node.Id), chooseA, chooseA ? a : b, chooseA ? b : a,
                        team ? chooseA ? "3-1" : "1-3" : chooseA ? "21-15" : "15-21", 20, TournamentResultKind.Played, actualDay));
                    remaining.Remove(node.Id);
                }
            }
        }
        return expected;
    }
    internal static Dictionary<string, int> Headers(IXLWorksheet sheet)
    {
        var headers = Enumerable.Range(1, 22).ToDictionary(c => sheet.Cell(4, c).GetString(), c => c, StringComparer.Ordinal);
        foreach (var (header, column) in new[] { ("比分（A-B）", 9), ("时长（分钟）", 10), ("胜者（A/B）", 12), ("备注", 13),
                     ("MatchId", 14), ("WorkspaceId", 17), ("ProjectId", 18), ("GraphRevision", 19), ("DrawConfirmedAt", 20), ("结果类型", 21), ("实际比赛日期", 22) })
            Require(headers.TryGetValue(header, out var actual) && actual == column, "Record schema/header mismatch: " + header);
        Require(Enumerable.Range(14, 7).All(c => sheet.Column(c).IsHidden), "Record N:T provenance must be hidden.");
        return headers;
    }
    internal static IReadOnlyList<(int Row, WorkspaceMatchKey Key)> Rows(IXLWorksheet sheet)
    {
        var columns = Headers(sheet);
        return Enumerable.Range(6, Math.Max(0, (sheet.LastRowUsed()?.RowNumber() ?? 5) - 5))
            .Where(r => !sheet.Cell(r, columns["MatchId"]).IsEmpty()).Select(r =>
                (r, new WorkspaceMatchKey(Guid.Parse(sheet.Cell(r, columns["ProjectId"]).GetString()), Guid.Parse(sheet.Cell(r, columns["MatchId"]).GetString())))).ToArray();
    }
    internal static string CopyFill(string original, string destination, TournamentWorkspace source,
        IReadOnlyDictionary<WorkspaceMatchKey, V5ExpectedResult> expected, ISet<WorkspaceMatchKey>? fill = null)
    {
        var hash = Hash(original); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(original, destination, false);
        string immutable;
        using (var book = new XLWorkbook(destination))
        {
            var sheet = book.Worksheet("对阵记录表"); var rows = Rows(sheet); immutable = ProtectedCells(sheet);
            foreach (var (row, key) in rows)
            {
                Require(expected.ContainsKey(key) && Guid.Parse(sheet.Cell(row, 17).GetString()) == source.Id, "Qualified record identity is not in source graph.");
                var project = source.Projects.Single(p => p.Id == key.ProjectId);
                Require(sheet.Cell(row, 19).GetString() == project.MatchGraph!.Revision && DateTimeOffset.Parse(sheet.Cell(row, 20).GetString()) == project.Draw!.ConfirmedAt,
                    "Record graph/confirmation provenance differs.");
                if (fill is not null && !fill.Contains(key)) continue;
                var result = expected[key];
                sheet.Cell(row, 9).Value = result.Score; sheet.Cell(row, 10).Value = result.Minutes;
                sheet.Cell(row, 12).Value = result.ChooseA ? "A" : "B";
                sheet.Cell(row, 21).Value = result.Kind == TournamentResultKind.Walkover ? "弃权" : "正常";
                sheet.Cell(row, 22).Value = result.ActualDay.ToString("yyyy-MM-dd");
            }
            Require(ProtectedCells(sheet) == immutable, "Filling changed non-editable formulas/provenance/planned cells.");
            book.Save();
        }
        using (var reopened = new XLWorkbook(destination)) Require(ProtectedCells(reopened.Worksheet("对阵记录表")) == immutable, "Saving the copy changed protected source/formula/provenance cells.");
        Require(Hash(original) == hash, "Original exported record was modified."); return destination;
    }
    internal static object InspectBindings(string path, TournamentWorkspace source)
    {
        using var book = new XLWorkbook(path); var sheet = book.Worksheet("对阵记录表"); var rows = Rows(sheet);
        var byKey = rows.ToDictionary(r => r.Key, r => r.Row); var evidence = new List<object>();
        foreach (var (row, key) in rows)
        {
            var project = source.Projects.Single(p => p.Id == key.ProjectId); var node = project.MatchGraph!.Matches.Single(n => n.Id == key.MatchId);
            Require(sheet.Cell(row, 17).GetString() == source.Id.ToString("D") && sheet.Cell(row, 19).GetString() == project.MatchGraph.Revision &&
                DateTimeOffset.Parse(sheet.Cell(row, 20).GetString()) == project.Draw!.ConfirmedAt && new[] { 14, 17, 18, 19, 20 }.All(c => !sheet.Cell(row, c).HasFormula), "Record provenance is not literal/current/qualified.");
            foreach (var (input, optionColumn, displayColumn, letter) in new[] { (node.SideA, 15, 6, "A"), (node.SideB, 16, 8, "B") })
            {
                var option = sheet.Cell(row, optionColumn); var display = sheet.Cell(row, displayColumn);
                var dependency = input switch { EntrantSource.WinnerOf winner => winner.MatchId, EntrantSource.LoserOf loser => loser.MatchId, _ => Guid.Empty };
                var participant = input as EntrantSource.Participant;
                if (participant is null && source.Results.TryGetValue(new(key.ProjectId, dependency), out var result)) participant = input is EntrantSource.WinnerOf ? result.Winner : result.Loser;
                if (participant is not null)
                {
                    Require(!option.HasFormula && option.GetString() == $"{letter}【{participant.DisplayName}】", "Resolved literal option changed participant punctuation/name.");
                    var names = participant.Players.Count > 1 ? string.Join("\n", participant.Players.Select(p => p.Name)) : participant.DisplayName;
                    Require(!display.HasFormula && display.GetString() == $"{letter}【{names}】", "Visible resolved players differ from typed participants.");
                }
                else if (byKey.TryGetValue(new(key.ProjectId, dependency), out var prerequisiteRow))
                {
                    var formula = option.FormulaA1.Replace("$", "", StringComparison.Ordinal);
                    var withoutLiterals = Regex.Replace(formula, "\"(?:\"\"|[^\"])*\"", "");
                    var references = Regex.Matches(withoutLiterals, @"\b[A-Z]+[1-9][0-9]*\b").Select(m => m.Value).ToHashSet();
                    Require(option.HasFormula && references.SetEquals(new[] { "L" + prerequisiteRow, "O" + prerequisiteRow, "P" + prerequisiteRow }) &&
                        display.FormulaA1.Replace("$", "", StringComparison.Ordinal) == (optionColumn == 15 ? "O" : "P") + row, "Formula references cross-qualified/incorrect prerequisite or display cell.");
                    var selectedA = (input is EntrantSource.WinnerOf ? "O" : "P") + prerequisiteRow;
                    var selectedB = (input is EntrantSource.WinnerOf ? "P" : "O") + prerequisiteRow;
                    Require(formula.Contains("," + selectedA + ",IF(", StringComparison.Ordinal) && formula.Contains("," + selectedB + ",\"\"))", StringComparison.Ordinal), "Winner/loser formula branches are reversed or incomplete.");
                }
                else Require(!option.HasFormula && !display.HasFormula && sheet.Cell(row, 13).GetString().Contains("前置比赛未在本表", StringComparison.Ordinal), "Out-of-sheet unresolved dependency is not explicitly unavailable.");
                evidence.Add(new { key, Row = row, Side = letter, Source = input, PrerequisiteId = dependency == Guid.Empty ? (Guid?)null : dependency,
                    ResolvedIdentity = participant?.IdentityKey, OptionFormula = option.FormulaA1, DisplayFormula = display.FormulaA1,
                    LiteralOption = option.HasFormula ? null : option.GetString(), CacheState = option.HasFormula ? "not recalculated by acceptance tool" : "literal" });
            }
        }
        return new { Path = path, QualifiedRowCount = rows.Count, StaticBindings = evidence, OfficeFormulaEvaluation = "NotRun" };
    }
    private static string ProtectedCells(IXLWorksheet sheet) => JsonSerializer.Serialize(sheet.CellsUsed()
        .Where(c => c.Address.RowNumber >= 6 && c.Address.ColumnNumber is not (9 or 10 or 12 or 13 or 21 or 22))
        .Select(c => new { c.Address.RowNumber, c.Address.ColumnNumber, c.FormulaA1, Value = c.HasFormula ? null : c.Value.ToString() }));
    internal static void Verify(TournamentWorkspace source, IReadOnlyDictionary<WorkspaceMatchKey, V5ExpectedResult> expected, bool complete)
    {
        if (complete) Require(source.Stage == TournamentStage.Completed && source.Results.Count == expected.Count, "Tournament did not complete every actual graph node.");
        foreach (var pair in source.Results)
        {
            var want = expected[pair.Key]; var got = pair.Value;
            Require(got.Winner.IdentityKey == want.Winner.IdentityKey && got.Loser.IdentityKey == want.Loser.IdentityKey &&
                JsonSerializer.Serialize(got.Winner.Players) == JsonSerializer.Serialize(want.Winner.Players) &&
                JsonSerializer.Serialize(got.Loser.Players) == JsonSerializer.Serialize(want.Loser.Players), "Result winner/loser differs from independent typed fold.");
            Require(got.Score == want.Score && got.DurationMinutes == want.Minutes && got.Kind == want.Kind && got.ActualPlayedDay == want.ActualDay,
                "Result metadata differs from explicitly supplied expectation.");
        }
    }
    internal static void EmitExpected(V5AcceptanceEvidence evidence, string name, IEnumerable<V5ExpectedResult> expected) =>
        evidence.Text(name, Csv("ProjectId", "MatchId", "ChooseA", "WinnerIdentity", "LoserIdentity", "Score", "Duration", "Kind", "ActualDay") + "\n" +
            string.Join("\n", expected.OrderBy(r => r.Key.ProjectId).ThenBy(r => r.Key.MatchId).Select(r =>
                Csv(r.Key.ProjectId, r.Key.MatchId, r.ChooseA, r.Winner.IdentityKey, r.Loser.IdentityKey, r.Score, r.Minutes, r.Kind, r.ActualDay))) + "\n");
}
