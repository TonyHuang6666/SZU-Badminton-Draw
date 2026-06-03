using System.Security.Cryptography;
using System.Text;

namespace BadmintonDraw.Core;

public sealed class DrawService
{
    public DrawResult Generate(IReadOnlyList<DrawParticipant> participants, DrawSettings settings)
    {
        Validate(participants, settings);
        settings = NormalizeSettings(settings);

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
        var targetSizes = Enumerable.Range(0, groupCount)
            .Select(index => participants.Count / groupCount + (index < participants.Count % groupCount ? 1 : 0))
            .ToArray();
        var groups = Enumerable.Range(0, groupCount)
            .Select(_ => new List<DrawParticipant>())
            .ToList();
        var regularParticipants = participants.Where(participant => !participant.IsSeed).ToList();
        var seededParticipants = participants
            .Where(participant => participant.IsSeed)
            .OrderBy(participant => participant.SeedRank ?? int.MaxValue)
            .ThenBy(participant => participant.NormalizedDisplayName, StringComparer.Ordinal)
            .ToList();
        var seedGroupOrder = OfficialDrawRules.GetSeedGroupOrder(groupCount);

        rng.Shuffle(regularParticipants);

        for (var i = 0; i < seededParticipants.Count; i++)
        {
            AddParticipantToBestGroup(
                seededParticipants[i],
                groups,
                targetSizes,
                rng,
                seedGroupOrder[i % seedGroupOrder.Count]);
        }

        foreach (var participant in regularParticipants)
        {
            AddParticipantToBestGroup(participant, groups, targetSizes, rng, preferredGroupIndex: null);
        }

        foreach (var group in groups)
        {
            rng.Shuffle(group);
        }

        return groups;
    }

    private static void AddParticipantToBestGroup(
        DrawParticipant participant,
        IReadOnlyList<List<DrawParticipant>> groups,
        IReadOnlyList<int> targetSizes,
        StableRandom rng,
        int? preferredGroupIndex)
    {
        var candidates = Enumerable.Range(0, groups.Count)
            .Where(index => groups[index].Count < targetSizes[index])
            .ToList();

        if (candidates.Count == 0)
        {
            candidates = Enumerable.Range(0, groups.Count).ToList();
        }

        rng.Shuffle(candidates);

        var targetGroup = candidates
            .OrderBy(index => CountUnitConflicts(participant, groups[index]))
            .ThenBy(index => preferredGroupIndex.HasValue ? GroupDistance(index, preferredGroupIndex.Value, groups.Count) : 0)
            .ThenBy(index => groups[index].Count)
            .First();

        groups[targetGroup].Add(participant);
    }

    private static int CountUnitConflicts(DrawParticipant participant, IEnumerable<DrawParticipant> group)
    {
        return group.Count(existing => OfficialDrawRules.HaveSameUnit(participant, existing));
    }

    private static int GroupDistance(int left, int right, int groupCount)
    {
        var direct = Math.Abs(left - right);
        return Math.Min(direct, groupCount - direct);
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
            var target = matches
                .Where(match => match.Count == 1 && !SharesAnyUnit(participant, match))
                .OrderByDescending(match => match[0].IsSeed)
                .FirstOrDefault()
                ?? matches.FirstOrDefault(match => match.Count == 1)
                ?? matches.FirstOrDefault(match => match.Count == 0)
                ?? matches.First(match => match.Count < 2);
            target.Add(participant);
        }

        return matches.SelectMany(match => match).ToList();
    }

    private static List<DrawParticipant> ArrangeParticipantsBySeedProtection(IReadOnlyList<DrawParticipant> participants)
    {
        var arranged = new DrawParticipant?[participants.Count];
        var protectedPositions = OfficialDrawRules.GetSeedPositionOrder(participants.Count);
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

    private static bool SharesAnyUnit(DrawParticipant participant, IEnumerable<DrawParticipant> others)
    {
        return others.Any(other => OfficialDrawRules.HaveSameUnit(participant, other));
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
        builder.AppendLine(settings.KnockoutGoal.ToString());

        foreach (var participant in participants)
        {
            builder.Append(participant.NormalizedDisplayName).Append('|')
                .Append(participant.IsSeed).Append('|')
                .Append(participant.SeedRank?.ToString() ?? string.Empty).Append('|')
                .Append(participant.PrimaryName ?? string.Empty).Append('|')
                .Append(participant.PartnerName ?? string.Empty).Append('|')
                .Append(participant.TeamName ?? string.Empty).Append('|')
                .Append(participant.PartnerTeamName ?? string.Empty).AppendLine();
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
            throw new DrawValidationException("随机数种子不能为空。");
        }

        var emptyName = participants.FirstOrDefault(participant => string.IsNullOrWhiteSpace(participant.DisplayName));
        if (emptyName is not null)
        {
            throw new DrawValidationException("参赛名单中存在空名称。");
        }

        ValidateSeedRanks(participants);
    }

    private static DrawSettings NormalizeSettings(DrawSettings settings)
    {
        if (settings.IsKnockout
            && settings.GroupCount > 1
            && !IsPowerOfTwo(settings.GroupCount)
            && settings.KnockoutGoal == KnockoutGoal.Champion)
        {
            return settings with { KnockoutGoal = KnockoutGoal.OneQualifierPerGroup };
        }

        return settings;
    }

    private static bool IsPowerOfTwo(int value)
    {
        return value > 0 && (value & (value - 1)) == 0;
    }

    private static void ValidateSeedRanks(IReadOnlyList<DrawParticipant> participants)
    {
        var seedCount = participants.Count(participant => participant.IsSeed);
        var maxSeedCount = OfficialDrawRules.GetMaximumSeedCount(participants.Count);
        if (seedCount > maxSeedCount)
        {
            throw new DrawValidationException(
                $"当前参赛数量最多设置 {maxSeedCount} 个种子，名单中设置了 {seedCount} 个。");
        }

        var invalidSeedRank = participants.FirstOrDefault(participant => participant.SeedRank.HasValue
            && participant.SeedRank.Value <= 0);
        if (invalidSeedRank is not null)
        {
            throw new DrawValidationException($"种子序号必须大于 0：{invalidSeedRank.DisplayName}");
        }

        var overflowSeedRank = participants.FirstOrDefault(participant => participant.SeedRank.HasValue
            && participant.SeedRank.Value > maxSeedCount);
        if (overflowSeedRank is not null)
        {
            throw new DrawValidationException(
                $"种子序号不能大于当前参赛数量允许的种子数量 {maxSeedCount}：{overflowSeedRank.DisplayName} 的种子序号为 {overflowSeedRank.SeedRank}");
        }

        var duplicateSeedRank = participants
            .Where(participant => participant.SeedRank.HasValue)
            .GroupBy(participant => participant.SeedRank!.Value)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateSeedRank is not null)
        {
            var duplicateNames = string.Join("、", duplicateSeedRank.Select(participant => participant.DisplayName));
            throw new DrawValidationException($"参赛名单中存在重复种子序号 {duplicateSeedRank.Key}：{duplicateNames}");
        }
    }

    private sealed record KnockoutSplit(
        List<List<DrawParticipant>> Groups,
        List<List<DrawParticipant>> RoundOneGroups,
        List<List<DrawParticipant>> ByeGroups);
}
