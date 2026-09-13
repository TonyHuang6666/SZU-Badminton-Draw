using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Persistence;
using BadmintonDraw.Core.Tournaments;
using ClosedXML.Excel;
using static V5AcceptanceEvidence;

internal static class V5ArtifactValidator
{
    internal static void Snapshot(V5AcceptanceEvidence evidence, string prefix, string path)
    {
        var w = new TournamentWorkspaceStore().Read(path);
        evidence.Json(prefix + "-workspace.json", new { w.Id, w.Name, w.Kind, w.Purpose, w.Stage, w.Revision, w.Projects, w.Resources, w.Schedule,
            Results = w.Results.Values.ToArray(), w.ProcessedDays, w.ImportLogs, w.ResultHistory, w.AuditEvents, ArchiveSha256 = Hash(path) });
        var nodes = w.Projects.SelectMany(p => p.MatchGraph!.Matches.Select(n => (Project: p, Node: n))).ToArray();
        evidence.Text(prefix + "-nodes.csv", Csv("ProjectId", "ProjectOrdinal", "Discipline", "MatchId", "OriginalMatchId", "Order", "SideA", "SideB") + "\n" +
            string.Join("\n", nodes.Select(x => Csv(x.Project.Id, x.Project.SortOrder, x.Project.Discipline, x.Node.Id, x.Node.OriginalMatchId, x.Node.Order, JsonSerializer.Serialize(x.Node.SideA), JsonSerializer.Serialize(x.Node.SideB)))));
        evidence.Text(prefix + "-dependencies.csv", Csv("ProjectId", "MatchId", "PrerequisiteId") + "\n" +
            string.Join("\n", nodes.SelectMany(x => x.Node.Dependencies.Select(d => Csv(x.Project.Id, x.Node.Id, d)))));
        evidence.Text(prefix + "-results.csv", Csv("ProjectId", "MatchId", "Winner", "Loser", "Score", "Duration", "Kind", "ActualPlayedDay") + "\n" +
            string.Join("\n", w.Results.Values.Select(r => Csv(r.Key.ProjectId, r.Key.MatchId, r.Winner.IdentityKey, r.Loser.IdentityKey, r.Score, r.DurationMinutes, r.Kind, r.ActualPlayedDay))));
        evidence.Text(prefix + "-receipts.csv", Csv("ReceiptId", "Hash", "VoidedAt", "ProjectId", "MatchId", "RecordDay", "HadResult", "Sheet", "Row") + "\n" +
            string.Join("\n", w.ImportLogs.SelectMany(l => l.Rows.Select(r => Csv(l.Id, l.ContentHash, l.VoidedAt, r.Key.ProjectId, r.Key.MatchId, r.RecordDay, r.HadResult, r.Location.SheetName, r.Location.RowNumber)))));
        evidence.Text(prefix + "-history.csv", Csv("Sequence", "ProjectId", "MatchId", "Before", "After", "Reason", "ImportLogId") + "\n" +
            string.Join("\n", w.ResultHistory.Select(h => Csv(h.Sequence, h.Key.ProjectId, h.Key.MatchId, JsonSerializer.Serialize(h.Before), JsonSerializer.Serialize(h.After), h.Reason, h.ImportLogId))));
        if (w.Schedule is not { } schedule) return;
        var request = new TournamentSchedulingRequest(w.Projects.Select(p => p.MatchGraph!).ToArray(), w.Resources!, schedule.Policy)
        { ProjectNames = w.Projects.ToDictionary(p => p.Id, p => p.DisplayName), Results = w.Results, BaselinePlacements = schedule.Placements,
            LockedMatchIds = w.Results.Keys.Select(k => k.MatchId).ToArray(), ScheduleRevision = schedule.Revision };
        var validation = new TournamentPlacementValidator(request).ValidateSchedule(schedule.Placements);
        Require(validation.IsValid && validation.Violations.Count == 0, "Fresh whole-workspace placement validator found a violation.");
        Require(nodes.Select(x => x.Node.Id).ToHashSet().SetEquals(schedule.Placements.Keys), "Schedule node coverage differs.");
        long Ticks(MatchPlacement p, bool end = false) => DateOnly.ParseExact(p.DayLabel, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(end ? p.EndTime : p.StartTime).Ticks;
        foreach (var x in nodes)
        {
            var p = schedule.Placements[x.Node.Id];
            foreach (var dep in x.Node.Dependencies) Require(Ticks(p) >= Ticks(schedule.Placements[dep], true), "Independent dependency time assertion failed.");
            var day = w.Resources!.Days.Single(d => d.Date.ToString("yyyy-MM-dd") == p.DayLabel);
            Require(day.Courts.Contains(p.Court) && p.StartTime >= day.DayStart && p.EndTime <= day.DayEnd, "Independent court/day bounds failed.");
            Require(!(day.UnavailableCourtWindows ?? []).Any(b => (b.Courts.Count == 0 || b.Courts.Contains(p.Court, StringComparer.OrdinalIgnoreCase)) && b.StartTime < p.EndTime && p.StartTime < b.EndTime), "Independent unavailable-court assertion failed.");
        }
        var capacities = new List<string> { Csv("Day", "StartTicks", "EndTicks", "AvailableCourts", "RefereeLimit", "Occupancy") };
        foreach (var day in w.Resources!.Days)
        {
            var placements = schedule.Placements.Values.Where(p => p.DayLabel == day.DayLabel).ToArray();
            var boundaries = placements.SelectMany(p => new[] { p.StartTime, p.EndTime })
                .Concat((day.UnavailableCourtWindows ?? []).SelectMany(b => new[] { b.StartTime, b.EndTime }))
                .Concat((day.RefereeCapacityWindows ?? []).SelectMany(b => new[] { b.StartTime, b.EndTime }))
                .Append(day.DayStart).Append(day.DayEnd).Where(t => t >= day.DayStart && t <= day.DayEnd).Distinct().Order().ToArray();
            for (var i = 0; i + 1 < boundaries.Length; i++)
            {
                var a = boundaries[i]; var b = boundaries[i + 1];
                var available = day.Courts.Count(c => !(day.UnavailableCourtWindows ?? []).Any(block => (block.Courts.Count == 0 || block.Courts.Contains(c, StringComparer.OrdinalIgnoreCase)) && block.StartTime < b && a < block.EndTime));
                var limit = (day.RefereeCapacityWindows ?? []).Where(window => window.StartTime < b && a < window.EndTime).Select(window => window.RefereeCount).Append(w.Resources.RefereeCount ?? day.Courts.Count).Min();
                var active = placements.Where(p => p.StartTime < b && a < p.EndTime).ToArray();
                Require(active.Length <= Math.Min(available, limit) && active.Select(p => p.Court).Distinct(StringComparer.OrdinalIgnoreCase).Count() == active.Length, "Independent global concurrent court/referee occupancy failed.");
                capacities.Add(Csv(day.DayLabel, day.Date.ToDateTime(a).Ticks, day.Date.ToDateTime(b).Ticks, available, limit, active.Length));
            }
        }
        evidence.Text(prefix + "-resource-occupancy.csv", string.Join("\n", capacities) + "\n");
        var rows = nodes.OrderBy(x => x.Project.SortOrder).ThenBy(x => x.Node.OriginalMatchId, StringComparer.Ordinal).Select(x =>
        { var p = schedule.Placements[x.Node.Id]; return new { x.Project.Id, x.Project.SortOrder, x.Project.Discipline, MatchId = x.Node.Id, x.Node.OriginalMatchId, p.DayLabel, StartTicks = Ticks(p), EndTicks = Ticks(p, true), p.Court }; }).ToArray();
        evidence.Text(prefix + "-placements.csv", Csv("ProjectId", "Ordinal", "Discipline", "MatchId", "OriginalMatchId", "Day", "StartTicks", "EndTicks", "Court") + "\n" +
            string.Join("\n", rows.Select(r => Csv(r.Id, r.SortOrder, r.Discipline, r.MatchId, r.OriginalMatchId, r.DayLabel, r.StartTicks, r.EndTicks, r.Court))));
        evidence.Json(prefix + "-schedule-validation.json", new { validation, Quality = new TournamentScheduleQualityAnalyzer().Analyze(request, schedule.Placements),
            CurrentSnapshotBaseline = true, HistoricalMovementClaim = false, MatchCount = nodes.Length,
            RawGuidPlacementSha256 = HashText(JsonSerializer.Serialize(rows)), LogicalPlacementSha256 = HashText(JsonSerializer.Serialize(rows.Select(r => new { r.SortOrder, r.Discipline, r.OriginalMatchId, r.DayLabel, r.StartTicks, r.EndTicks, r.Court }))) });
    }
    internal static void ReadArtifacts(V5AcceptanceEvidence evidence)
    {
        var results = new List<object>(); var failures = new List<string>();
        foreach (var path in Directory.EnumerateFiles(evidence.Root, "*", SearchOption.AllDirectories).Where(p => Path.GetExtension(p) is ".xlsx" or ".pdf").Order(StringComparer.Ordinal))
        {
            try
            {
                if (Path.GetExtension(path) == ".xlsx")
                {
                    using var book = new XLWorkbook(path); using var zip = ZipFile.OpenRead(path);
                    XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                    var xml = zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
                        .Select(e => { using var s = e.Open(); return XDocument.Load(s); }).ToArray();
                    var formulaCount = xml.Sum(x => x.Descendants(ns + "f").Count());
                    var errors = xml.Sum(x => x.Descendants(ns + "c").Count(c => (string?)c.Attribute("t") == "e"));
                    if (book.TryGetWorksheet("对阵记录表", out var sheet)) _ = V5AcceptanceResults.Rows(sheet);
                    results.Add(new { Path = Path.GetRelativePath(evidence.Root, path), Status = "Passed", Kind = "XlsxManagedReadback", Sheets = book.Worksheets.Select(s => s.Name).ToArray(), FormulaCount = formulaCount,
                        CachedErrorCount = errors, FormulaCorrectness = "NotRun: Office recalculation and typed cache comparison required", DataValidations = xml.Sum(x => x.Descendants(ns + "dataValidation").Count()), Sha256 = Hash(path), Office = "NotRun" });
                }
                else
                {
                    var bytes = File.ReadAllBytes(path); var text = Encoding.Latin1.GetString(bytes);
                    var pages = Regex.Matches(text, @"/Type\s*/Page\b").Count;
                    Require(text.StartsWith("%PDF-", StringComparison.Ordinal) && text.Contains("%%EOF", StringComparison.Ordinal) && pages > 0, "Invalid/empty PDF structure.");
                    var embedded = Regex.Matches(text, @"/FontFile[23]?\b").Count; var unicode = Regex.Matches(text, @"/ToUnicode\b").Count;
                    Require(embedded > 0 && unicode > 0, "PDF does not expose embedded-font/ToUnicode evidence.");
                    results.Add(new { Path = Path.GetRelativePath(evidence.Root, path), Status = "Passed", Kind = "PdfManagedReadback", Pages = pages, EmbeddedFonts = embedded, ToUnicode = unicode, Sha256 = Hash(path), Visual = "NotRun", PhysicalPrinting = "NotRun" });
                }
            }
            catch (Exception e)
            {
                var rejected = false;
                if (File.Exists(path + ".expected-rejection.json"))
                {
                    using var proof = JsonDocument.Parse(File.ReadAllText(path + ".expected-rejection.json"));
                    rejected = proof.RootElement.GetProperty("Rejected").GetBoolean() && proof.RootElement.GetProperty("Sha256").GetString() == Hash(path);
                }
                if (!rejected) failures.Add(Path.GetRelativePath(evidence.Root, path) + ": " + e.Message);
                results.Add(new { Path = Path.GetRelativePath(evidence.Root, path), Status = "Failed", ExpectedMalformedInputRejection = rejected, e.Message });
            }
        }
        evidence.Json("managed-artifact-readback.json", results);
        Require(failures.Count == 0, "Managed artifact readback failed: " + string.Join("; ", failures));
    }
}
