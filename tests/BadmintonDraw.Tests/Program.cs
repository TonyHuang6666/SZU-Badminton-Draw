using BadmintonDraw.Core;

var tests = new (string Name, Action Run)[]
{
    ("same seed creates same result", SameSeedCreatesSameResult),
    ("groups stay balanced", GroupsStayBalanced),
    ("seeds are distributed when possible", SeedsAreDistributedWhenPossible),
    ("knockout uses per-group power of two rule", KnockoutUsesPerGroupRule)
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
