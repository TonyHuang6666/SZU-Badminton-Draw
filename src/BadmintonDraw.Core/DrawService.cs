using System.Security.Cryptography;
using System.Text;

namespace BadmintonDraw.Core;

public sealed class DrawService
{
    public DrawResult Generate(IReadOnlyList<DrawParticipant> participants, DrawSettings settings)
    {
        Validate(participants, settings);

        var rng = new StableRandom(settings.RandomSeed);
        var groups = BuildBalancedGroups(participants, settings.GroupCount, rng);
        var numberedGroups = ToDrawGroups(groups);
        var audit = BuildAudit(participants, settings);

        if (!settings.IsKnockout)
        {
            return new DrawResult(numberedGroups, Array.Empty<DrawGroup>(), Array.Empty<DrawGroup>(), settings, audit);
        }

        var knockout = SplitPerGroupPowerOfTwo(groups);

        return new DrawResult(
            ToDrawGroups(knockout.Groups),
            ToDrawGroups(knockout.RoundOneGroups),
            ToDrawGroups(knockout.ByeGroups),
            settings,
            audit);
    }

    private static List<List<DrawParticipant>> BuildBalancedGroups(
        IReadOnlyList<DrawParticipant> participants,
        int groupCount,
        StableRandom rng)
    {
        var regularParticipants = participants.Where(participant => !participant.IsSeed).ToList();
        var seededParticipants = participants
            .Where(participant => participant.IsSeed)
            .OrderBy(participant => participant.SeedRank ?? int.MaxValue)
            .ThenBy(participant => participant.NormalizedDisplayName, StringComparer.Ordinal)
            .ToList();

        rng.Shuffle(regularParticipants);

        var groups = DivideEvenly(regularParticipants, groupCount);
        groups.Sort((left, right) => left.Count.CompareTo(right.Count));
        var seedGroupOrder = BuildProtectedSeedGroupOrder(groupCount);

        for (var i = 0; i < seededParticipants.Count; i++)
        {
            groups[seedGroupOrder[i % groupCount]].Add(seededParticipants[i]);
        }

        foreach (var group in groups)
        {
            rng.Shuffle(group);
        }

        return groups;
    }

    private static IReadOnlyList<int> BuildProtectedSeedGroupOrder(int groupCount)
    {
        var order = new List<int>(groupCount);
        AddProtectedSeedPositions(order, 0, groupCount - 1);
        return order;
    }

    private static void AddProtectedSeedPositions(ICollection<int> order, int first, int last)
    {
        if (first > last)
        {
            return;
        }

        if (order.Count == 0)
        {
            AddSeedPosition(order, first);
            AddSeedPosition(order, last);
        }

        if (last - first <= 1)
        {
            return;
        }

        var middleLeft = (first + last) / 2;
        var middleRight = middleLeft + 1;
        AddSeedPosition(order, middleLeft);
        AddSeedPosition(order, middleRight);
        AddProtectedSeedPositions(order, first, middleLeft);
        AddProtectedSeedPositions(order, middleRight, last);
    }

    private static void AddSeedPosition(ICollection<int> order, int position)
    {
        if (!order.Contains(position))
        {
            order.Add(position);
        }
    }

    private static List<List<DrawParticipant>> DivideEvenly(
        IReadOnlyList<DrawParticipant> participants,
        int groupCount)
    {
        var groups = Enumerable.Range(0, groupCount)
            .Select(_ => new List<DrawParticipant>())
            .ToList();
        var index = 0;

        for (var groupIndex = 0; groupIndex < groupCount; groupIndex++)
        {
            var groupSize = participants.Count / groupCount + (groupIndex < participants.Count % groupCount ? 1 : 0);
            for (var i = 0; i < groupSize; i++)
            {
                groups[groupIndex].Add(participants[index++]);
            }
        }

        return groups;
    }

    private static KnockoutSplit SplitPerGroupPowerOfTwo(IReadOnlyList<List<DrawParticipant>> groups)
    {
        var roundOneGroups = new List<List<DrawParticipant>>();
        var byeGroups = new List<List<DrawParticipant>>();
        var finalGroups = new List<List<DrawParticipant>>();

        foreach (var group in groups)
        {
            var roundOneCount = CalculateRoundOneCount(group.Count);
            var byeCount = group.Count - roundOneCount;
            var seededParticipants = group
                .Where(participant => participant.IsSeed)
                .OrderBy(participant => participant.SeedRank ?? int.MaxValue)
                .ThenBy(participant => participant.NormalizedDisplayName, StringComparer.Ordinal)
                .ToList();
            var regularParticipants = group
                .Where(participant => !participant.IsSeed)
                .ToList();
            var protectedSeeds = seededParticipants.Take(byeCount).ToList();
            var playInSeeds = seededParticipants.Skip(byeCount).ToList();
            var regularRoundOneCount = roundOneCount - playInSeeds.Count;
            var roundOne = BuildPlayInOrder(
                playInSeeds,
                regularParticipants.Take(regularRoundOneCount).ToList(),
                roundOneCount / 2);
            var bye = ArrangeParticipantsBySeedProtection(regularParticipants
                .Skip(regularRoundOneCount)
                .Concat(protectedSeeds)
                .ToList());

            roundOneGroups.Add(roundOne);
            byeGroups.Add(bye);
            finalGroups.Add(roundOne.Concat(bye).ToList());
        }

        return new KnockoutSplit(finalGroups, roundOneGroups, byeGroups);
    }

    private static List<DrawParticipant> BuildPlayInOrder(
        IReadOnlyList<DrawParticipant> playInSeeds,
        IReadOnlyList<DrawParticipant> regularParticipants,
        int matchCount)
    {
        if (matchCount == 0)
        {
            return [];
        }

        var matches = Enumerable.Range(0, matchCount)
            .Select(_ => new List<DrawParticipant>(capacity: 2))
            .ToList();

        for (var i = 0; i < playInSeeds.Count; i++)
        {
            matches[i % matchCount].Add(playInSeeds[i]);
        }

        foreach (var participant in regularParticipants)
        {
            var seededMatch = matches.FirstOrDefault(match => match.Count == 1 && match[0].IsSeed);
            var target = seededMatch
                ?? matches.FirstOrDefault(match => match.Count == 0)
                ?? matches.First(match => match.Count < 2);
            target.Add(participant);
        }

        return matches.SelectMany(match => match).ToList();
    }

    private static List<DrawParticipant> ArrangeParticipantsBySeedProtection(IReadOnlyList<DrawParticipant> participants)
    {
        var arranged = new DrawParticipant?[participants.Count];
        var protectedPositions = BuildProtectedSeedGroupOrder(participants.Count);
        var seededParticipants = participants
            .Where(participant => participant.IsSeed)
            .OrderBy(participant => participant.SeedRank ?? int.MaxValue)
            .ThenBy(participant => participant.NormalizedDisplayName, StringComparer.Ordinal)
            .ToList();
        var regularParticipants = new Queue<DrawParticipant>(participants.Where(participant => !participant.IsSeed));

        for (var i = 0; i < seededParticipants.Count; i++)
        {
            arranged[protectedPositions[i % protectedPositions.Count]] = seededParticipants[i];
        }

        for (var i = 0; i < arranged.Length; i++)
        {
            arranged[i] ??= regularParticipants.Dequeue();
        }

        return arranged.Cast<DrawParticipant>().ToList();
    }

    private static int CalculateRoundOneCount(int groupSize)
    {
        if (groupSize <= 1)
        {
            return 0;
        }

        var power = 1;
        while (power * 2 <= groupSize)
        {
            power *= 2;
        }

        return 2 * (groupSize - power);
    }

    private static IReadOnlyList<DrawGroup> ToDrawGroups(IReadOnlyList<List<DrawParticipant>> groups)
    {
        return groups
            .Select((group, index) => new DrawGroup(index + 1, group))
            .ToList();
    }

    private static DrawAuditInfo BuildAudit(IReadOnlyList<DrawParticipant> participants, DrawSettings settings)
    {
        return new DrawAuditInfo(
            settings.AlgorithmVersion,
            settings.RandomSeed.Trim(),
            DateTimeOffset.Now,
            ComputeInputHash(participants, settings),
            participants.Count,
            participants.Count(participant => participant.IsSeed),
            settings.GroupCount);
    }

    private static string ComputeInputHash(IReadOnlyList<DrawParticipant> participants, DrawSettings settings)
    {
        var builder = new StringBuilder();
        builder.AppendLine(settings.CompetitionMode.ToString());
        builder.AppendLine(settings.EventKind.ToString());
        builder.AppendLine(settings.GroupCount.ToString());
        builder.AppendLine(settings.AlgorithmVersion.ToString());

        foreach (var participant in participants)
        {
            builder.Append(participant.NormalizedDisplayName).Append('|')
                .Append(participant.IsSeed).Append('|')
                .Append(participant.SeedRank?.ToString() ?? string.Empty).Append('|')
                .Append(participant.PrimaryName ?? string.Empty).Append('|')
                .Append(participant.PartnerName ?? string.Empty).Append('|')
                .Append(participant.TeamName ?? string.Empty).AppendLine();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void Validate(IReadOnlyList<DrawParticipant> participants, DrawSettings settings)
    {
        if (participants.Count == 0)
        {
            throw new DrawValidationException("参赛名单不能为空。");
        }

        if (settings.GroupCount <= 0)
        {
            throw new DrawValidationException("小组数必须大于 0。");
        }

        if (settings.GroupCount > participants.Count)
        {
            throw new DrawValidationException("小组数不能大于参赛人数或队伍数。");
        }

        if (string.IsNullOrWhiteSpace(settings.RandomSeed))
        {
            throw new DrawValidationException("随机种子不能为空。");
        }

        var emptyName = participants.FirstOrDefault(participant => string.IsNullOrWhiteSpace(participant.DisplayName));
        if (emptyName is not null)
        {
            throw new DrawValidationException("参赛名单中存在空名称。");
        }

        var duplicate = participants
            .GroupBy(participant => participant.NormalizedDisplayName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw new DrawValidationException($"参赛名单中存在重复名称：{duplicate.Key}");
        }
    }

    private sealed record KnockoutSplit(
        List<List<DrawParticipant>> Groups,
        List<List<DrawParticipant>> RoundOneGroups,
        List<List<DrawParticipant>> ByeGroups);
}
