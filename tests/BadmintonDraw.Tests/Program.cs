using BadmintonDraw.Core;
using BadmintonDraw.Excel;
using ClosedXML.Excel;

var tests = new (string Name, Action Run)[]
{
    ("same seed creates same result", SameSeedCreatesSameResult),
    ("groups stay balanced", GroupsStayBalanced),
    ("seeds are distributed when possible", SeedsAreDistributedWhenPossible),
    ("knockout uses per-group power of two rule", KnockoutUsesPerGroupRule),
    ("power-of-two bracket splits group header around winner cells", PowerOfTwoBracketSplitsGroupHeaderAroundWinnerCells),
    ("seed players are highlighted in exported workbook", SeedPlayersAreHighlightedInExportedWorkbook),
    ("participant template header is readable", ParticipantTemplateHeaderIsReadable),
    ("reader detects participant event kind", ReaderDetectsPartnerData)
};

foreach (var test in tests)
{
    test.Run();
    Console.WriteLine($"PASS {test.Name}");
}

static void SameSeedCreatesSameResult()
{
    var participants = CreateParticipants(12);
    var settings = CreateSettings(groupCount: 4, seed: "SZU-2026");
    var service = new DrawService();

    var first = service.Generate(participants, settings);
    var second = service.Generate(participants, settings);

    AssertEqual(Signature(first.Groups), Signature(second.Groups), "same seed should be deterministic");
}

static void GroupsStayBalanced()
{
    var participants = CreateParticipants(23);
    var result = new DrawService().Generate(participants, CreateSettings(groupCount: 4));
    var counts = result.Groups.Select(group => group.Count).ToArray();

    Assert(counts.Max() - counts.Min() <= 1, "groups should differ by at most one participant");
}

static void SeedsAreDistributedWhenPossible()
{
    var participants = CreateParticipants(12).ToList();
    participants[0] = participants[0] with { IsSeed = true, SeedRank = 1 };
    participants[1] = participants[1] with { IsSeed = true, SeedRank = 2 };
    participants[2] = participants[2] with { IsSeed = true, SeedRank = 3 };
    participants[3] = participants[3] with { IsSeed = true, SeedRank = 4 };

    var result = new DrawService().Generate(participants, CreateSettings(groupCount: 4));

    Assert(result.Groups.All(group => group.Participants.Count(participant => participant.IsSeed) == 1),
        "each group should receive one seed when seed count equals group count");
}

static void KnockoutUsesPerGroupRule()
{
    var participants = CreateParticipants(7);
    var result = new DrawService().Generate(participants, CreateSettings(
        groupCount: 2,
        mode: CompetitionMode.SinglesKnockout));

    var roundOneCounts = result.RoundOneGroups.Select(group => group.Count).Order().ToArray();
    var byeCounts = result.ByeGroups.Select(group => group.Count).Order().ToArray();

    AssertEqual("0,2", string.Join(',', roundOneCounts), "groups of 3 and 4 should have 2 and 0 first-round players");
    AssertEqual("1,4", string.Join(',', byeCounts), "groups of 3 and 4 should have 1 and 4 byes/direct entrants");
}

static void PowerOfTwoBracketSplitsGroupHeaderAroundWinnerCells()
{
    var participants = CreateParticipants(157);
    var settings = CreateSettings(groupCount: 8, mode: CompetitionMode.SinglesKnockout);
    var result = new DrawService().Generate(participants, settings);
    var outputPath = Path.Combine(Path.GetTempPath(), $"badminton-draw-bracket-{Guid.NewGuid():N}.xlsx");

    try
    {
        new DrawResultExcelWriter().Write(outputPath, result, participants);

        using var workbook = new XLWorkbook(outputPath);
        var sheet = workbook.Worksheet("对阵表");
        var mergedRanges = sheet.MergedRanges
            .Select(range => range.RangeAddress.ToStringRelative())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var row in new[] { 69, 197, 325, 453 })
        {
            Assert(mergedRanges.Contains($"A{row}:X{row}"), $"group header row {row} should be merged before the winner cell");
            Assert(mergedRanges.Contains($"Y{row}:Z{row}"), $"winner cell row {row} should stay independent");
            Assert(mergedRanges.Contains($"AA{row}:AH{row}"), $"group header row {row} should be merged after the winner cell");
            Assert(!mergedRanges.Contains($"A{row}:AH{row}"), $"group header row {row} should not cover the winner cell");
        }
    }
    finally
    {
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }
    }
}

static void SeedPlayersAreHighlightedInExportedWorkbook()
{
    var participants = new List<DrawParticipant>
    {
        new("种子选手", IsSeed: true, SeedRank: 1),
        new("普通选手1"),
        new("普通选手2"),
        new("普通选手3")
    };
    var settings = CreateSettings(groupCount: 1, mode: CompetitionMode.SinglesKnockout);
    var result = new DrawService().Generate(participants, settings);
    var outputPath = Path.Combine(Path.GetTempPath(), $"badminton-draw-seed-style-{Guid.NewGuid():N}.xlsx");

    try
    {
        new DrawResultExcelWriter().Write(outputPath, result, participants);

        using var workbook = new XLWorkbook(outputPath);
        var bracketSeedCell = workbook.Worksheet("对阵表")
            .CellsUsed()
            .FirstOrDefault(cell => cell.GetString() == "种子选手");
        Assert(bracketSeedCell is not null, "seed player should appear in bracket sheet");
        Assert(IsSeedFont(bracketSeedCell!), "seed player should be highlighted in bracket sheet");

        var rosterSeedRow = workbook.Worksheet("原始名单").Row(2);
        Assert(rosterSeedRow.Cell(1).GetString() == "种子选手", "seed player should stay in first roster row");
        Assert(IsSeedFont(rosterSeedRow.Cell(1)), "seed row should be highlighted in roster sheet");
    }
    finally
    {
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }
    }
}

static void ParticipantTemplateHeaderIsReadable()
{
    var outputPath = Path.Combine(Path.GetTempPath(), $"badminton-template-{Guid.NewGuid():N}.xlsx");

    try
    {
        new ParticipantTemplateWriter().Write(outputPath);

        using var workbook = new XLWorkbook(outputPath);
        var sheet = workbook.Worksheet("参赛名单");
        Assert(sheet.Row(1).Height >= 24, "template header row should be tall enough for table filter buttons");
        Assert(sheet.Row(2).Height >= 40, "template example rows should be tall enough for wrapped text");
        Assert(sheet.Column(4).Width >= 12, "seed flag column should not clip its header");
        Assert(sheet.Column(5).Width >= 12, "seed rank column should not clip its header");

        foreach (var cell in sheet.Range("A1:F4").Cells())
        {
            Assert(cell.Style.Alignment.WrapText, $"template cell {cell.Address} should wrap text");
            Assert(cell.Style.Alignment.Vertical == XLAlignmentVerticalValues.Center, $"template cell {cell.Address} should be vertically centered");
        }
    }
    finally
    {
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }
    }
}

static void ReaderDetectsPartnerData()
{
    var doublesPath = Path.Combine(Path.GetTempPath(), $"badminton-doubles-detect-{Guid.NewGuid():N}.xlsx");
    var singlesPath = Path.Combine(Path.GetTempPath(), $"badminton-singles-detect-{Guid.NewGuid():N}.xlsx");
    var teamPath = Path.Combine(Path.GetTempPath(), $"badminton-team-detect-{Guid.NewGuid():N}.xlsx");

    try
    {
        WriteParticipantDetectionWorkbook(doublesPath, primaryName: "张三", partnerName: "李四", teamName: "计算机与软件学院");
        WriteParticipantDetectionWorkbook(singlesPath, primaryName: "王五", partnerName: "", teamName: "管理学院");
        WriteParticipantDetectionWorkbook(teamPath, primaryName: "", partnerName: "", teamName: "经济学院");

        var reader = new ParticipantExcelReader();
        AssertEqual(EventKind.Doubles.ToString(), reader.DetectEventKind(doublesPath).ToString(),
            "reader should classify rows with partner data as doubles");
        Assert(reader.HasPartnerData(doublesPath), "reader should detect non-empty partner cells");
        AssertEqual(EventKind.Singles.ToString(), reader.DetectEventKind(singlesPath).ToString(),
            "reader should classify rows with names and no partner as singles");
        AssertEqual(EventKind.Team.ToString(), reader.DetectEventKind(teamPath).ToString(),
            "reader should classify rows with only teams as team event");
    }
    finally
    {
        foreach (var path in new[] { doublesPath, singlesPath, teamPath })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}

static void WriteParticipantDetectionWorkbook(string outputPath, string primaryName, string partnerName, string teamName)
{
    using var workbook = new XLWorkbook();
    var sheet = workbook.AddWorksheet("参赛名单");
    sheet.Cell(1, 1).Value = "姓名";
    sheet.Cell(1, 2).Value = "搭档";
    sheet.Cell(1, 3).Value = "队伍";
    sheet.Cell(2, 1).Value = primaryName;
    sheet.Cell(2, 2).Value = partnerName;
    sheet.Cell(2, 3).Value = teamName;
    workbook.SaveAs(outputPath);
}

static IReadOnlyList<DrawParticipant> CreateParticipants(int count)
{
    return Enumerable.Range(1, count)
        .Select(index => new DrawParticipant($"选手{index:D2}"))
        .ToList();
}

static DrawSettings CreateSettings(
    int groupCount,
    string seed = "test-seed",
    CompetitionMode mode = CompetitionMode.SinglesRoundRobin)
{
    return new DrawSettings(mode, EventKind.Singles, groupCount, seed);
}

static string Signature(IReadOnlyList<DrawGroup> groups)
{
    return string.Join(';', groups.Select(group => string.Join(',', group.Participants.Select(participant => participant.DisplayName))));
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual(string expected, string actual, string message)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{message}. Expected '{expected}', actual '{actual}'.");
    }
}

static bool IsSeedFont(IXLCell cell)
{
    return cell.Style.Font.Bold
        && cell.Style.Font.FontColor.Color.ToArgb() == XLColor.FromHtml("#C00000").Color.ToArgb();
}
