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

        var knockout = settings.AlgorithmVersion switch
        {
            DrawAlgorithmVersion.LegacyBfszuV1 => SplitLegacyFirstRound(groups),
            DrawAlgorithmVersion.PerGroupPowerOfTwoV2 => SplitPerGroupPowerOfTwo(groups),
            _ => throw new DrawValidationException("未知的抽签算法版本。")
        };

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
        rng.Shuffle(seededParticipants);

        var groups = DivideEvenly(regularParticipants, groupCount);
        groups.Sort((left, right) => left.Count.CompareTo(right.Count));

        for (var i = 0; i < seededParticipants.Count; i++)
        {
            groups[i % groupCount].Add(seededParticipants[i]);
        }

        foreach (var group in groups)
        {
            rng.Shuffle(group);
        }

        groups.Sort((left, right) => left.Count.CompareTo(right.Count));
        return groups;
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
            var roundOne = group.Take(roundOneCount).ToList();
            var bye = group.Skip(roundOneCount).ToList();

            roundOneGroups.Add(roundOne);
            byeGroups.Add(bye);
            finalGroups.Add(roundOne.Concat(bye).ToList());
        }

        return new KnockoutSplit(finalGroups, roundOneGroups, byeGroups);
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

    private static KnockoutSplit SplitLegacyFirstRound(IReadOnlyList<List<DrawParticipant>> groups)
    {
        var oddGroup = 0;
        var oddGroupNum = 0;
        var evenGroup = 0;
        var evenGroupNum = 0;

        foreach (var group in groups)
        {
            if (group.Count % 2 != 0)
            {
                oddGroup++;
                oddGroupNum = group.Count;
            }
            else
            {
                evenGroup++;
                evenGroupNum = group.Count;
            }
        }

        var i = 0;
        while (Math.Pow(2, i) <= evenGroupNum)
        {
            i++;
        }

        var gap = evenGroupNum - (int)Math.Pow(2, i - 1);
        var roundOneNumber = gap != 0
            ? oddGroupNum < evenGroupNum
                ? gap * 2 * evenGroup + (gap - 1) * 2 * oddGroup
                : gap * 2 * evenGroup + (gap + 1) * 2 * oddGroup
            : oddGroupNum < evenGroupNum
                ? 2 * oddGroup * (oddGroupNum - (int)Math.Pow(2, i - 2))
                : oddGroup * 2;

        roundOneNumber = Math.Clamp(roundOneNumber, 0, groups.Sum(group => group.Count));
        var byeNumber = groups.Sum(group => group.Count) - roundOneNumber;
        var byeGroups = SelectEvenly(groups, byeNumber);
        var roundOneGroups = SubtractGroups(groups, byeGroups);
        var finalGroups = roundOneGroups.Zip(byeGroups, (roundOne, bye) => roundOne.Concat(bye).ToList()).ToList();

        return new KnockoutSplit(finalGroups, roundOneGroups, byeGroups);
    }

    private static List<List<DrawParticipant>> SelectEvenly(
        IReadOnlyList<List<DrawParticipant>> groups,
        int count)
    {
        var result = new List<List<DrawParticipant>>();
        var baseCount = count / groups.Count;
        var remainder = count % groups.Count;

        for (var i = 0; i < groups.Count; i++)
        {
            var take = baseCount + (i < remainder ? 1 : 0);
            result.Add(groups[i].Take(Math.Min(take, groups[i].Count)).ToList());
        }

        return result;
    }

    private static List<List<DrawParticipant>> SubtractGroups(
        IReadOnlyList<List<DrawParticipant>> groups,
        IReadOnlyList<List<DrawParticipant>> selectedGroups)
    {
        var result = new List<List<DrawParticipant>>();

        for (var i = 0; i < groups.Count; i++)
        {
            var selected = selectedGroups[i].ToHashSet();
            result.Add(groups[i].Where(participant => !selected.Contains(participant)).ToList());
        }

        return result;
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
            DateTimeOffset.UtcNow,
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
