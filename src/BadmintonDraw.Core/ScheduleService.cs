namespace BadmintonDraw.Core;

public sealed class ScheduleService
{
    public SchedulePlan Generate(DrawResult result, ScheduleSettings settings)
    {
        Validate(settings);

        var unscheduled = result.Settings.IsRoundRobin
            ? BuildRoundRobinMatches(result)
            : BuildKnockoutMatches(result);
        if (unscheduled.Count == 0)
        {
            throw new DrawValidationException("当前抽签结果没有可编排的比赛场次。");
        }

        return new SchedulePlan(AssignTimeAndCourts(unscheduled, settings), settings);
    }

    private static List<UnscheduledMatch> BuildKnockoutMatches(DrawResult result)
    {
        var matches = new List<UnscheduledMatch>();
        var groupQualifierEntries = new List<ScheduleBracketEntry>();
        var groupSlotCounts = result.Groups
            .Select(group => CountMainDrawEntries(result, group))
            .ToList();
        var groupPhaseLabels = result.Groups.Count > 1
            ? BracketStageLabels.BuildQualifierMatchPhases(groupSlotCounts)
            : Array.Empty<string>();

        foreach (var group in result.Groups)
        {
            groupQualifierEntries.Add(BuildGroupKnockoutMatches(result, group, matches, groupPhaseLabels));
        }

        if (result.Settings.KnockoutGoal == KnockoutGoal.Champion
            && groupQualifierEntries.Count > 1
            && IsPowerOfTwo(groupQualifierEntries.Count))
        {
            var championPhaseLabels = BracketStageLabels.BuildChampionMatchPhases(groupQualifierEntries.Count);
            BuildPlaceholderBracketMatches(
                matches,
                groupNumber: 0,
                groupName: "总决赛",
                entries: groupQualifierEntries,
                finalWinnerNote: "胜者为冠军",
                phasePrefix: "",
                phaseLabels: championPhaseLabels);
        }

        return matches;
    }

    private static ScheduleBracketEntry BuildGroupKnockoutMatches(
        DrawResult result,
        DrawGroup group,
        List<UnscheduledMatch> matches,
        IReadOnlyList<string> groupPhaseLabels)
    {
        var groupName = BuildGroupName(group.Number);
        var roundOneGroup = result.RoundOneGroups.FirstOrDefault(item => item.Number == group.Number);
        var byeGroup = result.ByeGroups.FirstOrDefault(item => item.Number == group.Number);
        var roundOneParticipants = roundOneGroup?.Participants ?? Array.Empty<DrawParticipant>();
        var byeParticipants = byeGroup?.Participants ?? group.Participants;
        var bracketEntries = new List<ScheduleBracketEntry>();

        for (var index = 0; index + 1 < roundOneParticipants.Count; index += 2)
        {
            var first = roundOneParticipants[index];
            var second = roundOneParticipants[index + 1];
            var matchNumber = index / 2 + 1;
            var matchName = $"第{group.Number}组首轮赛{matchNumber}";
            var matchId = AddMatch(
                matches,
                group.Number,
                groupName,
                "首轮赛",
                matchName,
                first.DisplayName,
                second.DisplayName,
                "胜者进入正赛",
                OfficialDrawRules.HaveSameUnit(first, second),
                [],
                [first.DisplayName, second.DisplayName]);
            bracketEntries.Add(new ScheduleBracketEntry(
                $"{matchName}胜者",
                MinSeedRank(first, second),
                null,
                matchId,
                [first.DisplayName, second.DisplayName]));
        }

        foreach (var participant in byeParticipants)
        {
            bracketEntries.Add(new ScheduleBracketEntry(
                participant.DisplayName,
                participant.SeedRank,
                participant,
                null,
                [participant.DisplayName]));
        }

        bracketEntries = ArrangeBracketEntriesBySeedProtection(bracketEntries);
        if (bracketEntries.Count <= 1)
        {
            return bracketEntries.Count == 1
                ? bracketEntries[0]
                : new ScheduleBracketEntry($"第{group.Number}组出线");
        }

        return BuildPlaceholderBracketMatches(
            matches,
            group.Number,
            groupName,
            bracketEntries,
            result.Settings.KnockoutGoal == KnockoutGoal.OneQualifierPerGroup
                ? "胜者获得本组出线名额"
                : result.Groups.Count == 1 ? "胜者为冠军" : "胜者进入总决赛",
            phasePrefix: "",
            phaseLabels: groupPhaseLabels);
    }

    private static ScheduleBracketEntry BuildPlaceholderBracketMatches(
        List<UnscheduledMatch> matches,
        int groupNumber,
        string groupName,
        IReadOnlyList<ScheduleBracketEntry> entries,
        string finalWinnerNote,
        string phasePrefix,
        IReadOnlyList<string>? phaseLabels = null)
    {
        var currentRound = entries.ToList();
        var roundIndex = 0;

        while (currentRound.Count > 1)
        {
            var entrantCount = currentRound.Count;
            var nextRound = new List<ScheduleBracketEntry>();
            var phase = phaseLabels is not null && roundIndex < phaseLabels.Count
                ? phaseLabels[roundIndex]
                : BuildKnockoutPhase(entrantCount, phasePrefix);

            for (var index = 0; index + 1 < currentRound.Count; index += 2)
            {
                var first = currentRound[index];
                var second = currentRound[index + 1];
                var matchNumber = index / 2 + 1;
                var matchName = string.IsNullOrWhiteSpace(phasePrefix)
                    ? $"{groupName}{phase}第{matchNumber}场"
                    : $"{phase}第{matchNumber}场";
                var isFinalInThisBracket = entrantCount == 2;
                var dependencyIds = new[] { first.SourceMatchId, second.SourceMatchId }
                    .Where(id => id.HasValue)
                    .Select(id => id!.Value)
                    .Distinct()
                    .ToList();
                var entrantKeys = first.EntrantKeys
                    .Concat(second.EntrantKeys)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var matchId = AddMatch(
                    matches,
                    groupNumber,
                    groupName,
                    phase,
                    matchName,
                    first.Label,
                    second.Label,
                    isFinalInThisBracket ? finalWinnerNote : "胜者晋级下一轮",
                    first.Participant is not null
                        && second.Participant is not null
                        && OfficialDrawRules.HaveSameUnit(first.Participant, second.Participant),
                    dependencyIds,
                    entrantKeys);

                nextRound.Add(new ScheduleBracketEntry($"{matchName}胜者", null, null, matchId, entrantKeys));
            }

            currentRound = nextRound;
            roundIndex++;
        }

        return currentRound[0];
    }

    private static int CountMainDrawEntries(DrawResult result, DrawGroup group)
    {
        var roundOneGroup = result.RoundOneGroups.FirstOrDefault(item => item.Number == group.Number);
        var byeGroup = result.ByeGroups.FirstOrDefault(item => item.Number == group.Number);
        var roundOneParticipants = roundOneGroup?.Participants ?? Array.Empty<DrawParticipant>();
        var byeParticipants = byeGroup?.Participants ?? group.Participants;
        return roundOneParticipants.Count / 2 + byeParticipants.Count;
    }

    private static int AddMatch(
        List<UnscheduledMatch> matches,
        int groupNumber,
        string groupName,
        string phase,
        string matchName,
        string sideA,
        string sideB,
        string note,
        bool sameUnit,
        IReadOnlyList<int> dependencyIds,
        IReadOnlyList<string> entrantKeys)
    {
        var id = matches.Count + 1;
        matches.Add(new UnscheduledMatch(
            id,
            groupNumber,
            groupName,
            phase,
            matchName,
            sideA,
            sideB,
            note,
            sameUnit,
            dependencyIds,
            entrantKeys));
        return id;
    }

    private static string BuildKnockoutPhase(int entrantCount, string phasePrefix)
    {
        var core = entrantCount switch
        {
            2 => "决赛",
            4 => "半决赛",
            _ => $"{entrantCount}进{entrantCount / 2}"
        };

        return string.IsNullOrWhiteSpace(phasePrefix) ? core : $"{phasePrefix}{core}";
    }

    private static int? MinSeedRank(DrawParticipant first, DrawParticipant second)
    {
        return new[] { first.SeedRank, second.SeedRank }
            .Where(rank => rank.HasValue)
            .Min();
    }

    private static List<ScheduleBracketEntry> ArrangeBracketEntriesBySeedProtection(IReadOnlyList<ScheduleBracketEntry> entries)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        var arranged = new ScheduleBracketEntry?[entries.Count];
        var protectedPositions = OfficialDrawRules.GetSeedPositionOrder(entries.Count);
        var seededEntries = entries
            .Where(entry => entry.ProtectedSeedRank.HasValue)
            .OrderBy(entry => entry.ProtectedSeedRank!.Value)
            .ThenBy(entry => entry.Label, StringComparer.Ordinal)
            .ToList();
        var regularEntries = new Queue<ScheduleBracketEntry>(entries.Where(entry => !entry.ProtectedSeedRank.HasValue));

        for (var i = 0; i < seededEntries.Count; i++)
        {
            arranged[protectedPositions[i % protectedPositions.Count]] = seededEntries[i];
        }

        for (var i = 0; i < arranged.Length; i++)
        {
            arranged[i] ??= regularEntries.Dequeue();
        }

        return arranged.Cast<ScheduleBracketEntry>().ToList();
    }

    private static List<UnscheduledMatch> BuildRoundRobinMatches(DrawResult result)
    {
        var matches = new List<UnscheduledMatch>();

        foreach (var group in result.Groups)
        {
            var roundRobinMatches = BuildRoundRobinSchedule(group.Participants, result.Settings.CompetitionMode);
            foreach (var match in roundRobinMatches)
            {
                var first = group.Participants[match.FirstIndex];
                var second = group.Participants[match.SecondIndex];
                AddMatch(
                    matches,
                    group.Number,
                    BuildGroupName(group.Number),
                    $"第{match.Round}轮",
                    $"第{group.Number}组第{match.Order}场",
                    first.DisplayName,
                    second.DisplayName,
                    match.SameUnit ? "同单位优先" : "",
                    match.SameUnit,
                    [],
                    [first.DisplayName, second.DisplayName]);
            }
        }

        return matches;
    }

    private static IReadOnlyList<RoundRobinMatch> BuildRoundRobinSchedule(
        IReadOnlyList<DrawParticipant> participants,
        CompetitionMode competitionMode)
    {
        if (participants.Count <= 1)
        {
            return [];
        }

        var slots = participants
            .Select((_, index) => (int?)index)
            .ToList();
        if (slots.Count % 2 == 1)
        {
            slots.Add(null);
        }

        var matches = new List<RoundRobinMatch>();
        var roundCount = slots.Count - 1;
        var matchesPerRound = slots.Count / 2;

        for (var round = 1; round <= roundCount; round++)
        {
            for (var matchIndex = 0; matchIndex < matchesPerRound; matchIndex++)
            {
                var first = slots[matchIndex];
                var second = slots[slots.Count - 1 - matchIndex];
                if (first.HasValue && second.HasValue)
                {
                    var sameUnit = competitionMode != CompetitionMode.TeamRoundRobin
                        && OfficialDrawRules.HaveSameUnit(participants[first.Value], participants[second.Value]);
                    matches.Add(new RoundRobinMatch(0, round, matchIndex + 1, first.Value, second.Value, sameUnit));
                }
            }

            RotateRoundRobinSlots(slots);
        }

        return matches
            .OrderBy(match => match.SameUnit ? 0 : 1)
            .ThenBy(match => match.Round)
            .ThenBy(match => match.RoundMatchNumber)
            .Select((match, index) => match with { Order = index + 1 })
            .ToList();
    }

    private static void RotateRoundRobinSlots(IList<int?> slots)
    {
        if (slots.Count <= 2)
        {
            return;
        }

        var last = slots[^1];
        for (var index = slots.Count - 1; index > 1; index--)
        {
            slots[index] = slots[index - 1];
        }

        slots[1] = last;
    }

    private static IReadOnlyList<ScheduledMatch> AssignTimeAndCourts(
        IReadOnlyList<UnscheduledMatch> matches,
        ScheduleSettings settings)
    {
        var remaining = matches.ToDictionary(match => match.Id);
        var completed = new HashSet<int>();
        var scheduled = new List<ScheduledMatch>(matches.Count);
        var order = 1;

        foreach (var day in settings.Days.OrderBy(day => day.Date))
        {
            var dailyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var slotStart in BuildSlotStarts(day, settings))
            {
                var slotUsedEntrants = new HashSet<string>(StringComparer.Ordinal);
                var scheduledThisSlot = new List<UnscheduledMatch>();

                foreach (var court in day.Courts)
                {
                    var candidate = remaining.Values
                        .Where(match => IsEligible(match, completed, dailyCounts, slotUsedEntrants, settings.MaxMatchesPerEntrantPerDay))
                        .OrderBy(match => match.Id)
                        .FirstOrDefault();
                    if (candidate is null)
                    {
                        continue;
                    }

                    var slotEnd = slotStart.AddMinutes(settings.MatchMinutes);
                    scheduled.Add(new ScheduledMatch(
                        order++,
                        day.DayLabel,
                        slotStart,
                        slotEnd,
                        court,
                        candidate.GroupNumber,
                        candidate.GroupName,
                        candidate.Phase,
                        candidate.MatchName,
                        candidate.SideA,
                        candidate.SideB,
                        candidate.Note,
                        candidate.SameUnit));

                    scheduledThisSlot.Add(candidate);
                    remaining.Remove(candidate.Id);
                    foreach (var entrant in candidate.EntrantKeys)
                    {
                        slotUsedEntrants.Add(entrant);
                    }
                }

                foreach (var match in scheduledThisSlot)
                {
                    completed.Add(match.Id);
                    foreach (var entrant in match.EntrantKeys)
                    {
                        dailyCounts[entrant] = dailyCounts.GetValueOrDefault(entrant) + 1;
                    }
                }

                if (remaining.Count == 0)
                {
                    return scheduled;
                }
            }
        }

        throw new DrawValidationException(
            $"当前赛程资源不足，仍有 {remaining.Count} 场无法安排。请增加比赛日、场地、时间段，或提高单名选手每日最多场次。");
    }

    private static bool IsEligible(
        UnscheduledMatch match,
        IReadOnlySet<int> completed,
        IReadOnlyDictionary<string, int> dailyCounts,
        IReadOnlySet<string> slotUsedEntrants,
        int maxMatchesPerEntrantPerDay)
    {
        return match.DependencyIds.All(completed.Contains)
            && match.EntrantKeys.All(entrant => !slotUsedEntrants.Contains(entrant))
            && match.EntrantKeys.All(entrant => dailyCounts.GetValueOrDefault(entrant) < maxMatchesPerEntrantPerDay);
    }

    private static IEnumerable<TimeOnly> BuildSlotStarts(ScheduleDaySettings day, ScheduleSettings settings)
    {
        var current = day.DayStart;
        while (current.AddMinutes(settings.MatchMinutes) <= day.DayEnd)
        {
            yield return current;
            current = current.AddMinutes(settings.SlotMinutes);
        }
    }

    private static void Validate(ScheduleSettings settings)
    {
        if (settings.Days.Count == 0)
        {
            throw new DrawValidationException("请至少添加一个赛程日。");
        }

        if (settings.MatchMinutes <= 0)
        {
            throw new DrawValidationException("单场比赛耗时必须大于 0 分钟。");
        }

        if (settings.BreakMinutes < 0)
        {
            throw new DrawValidationException("场次间隔不能小于 0 分钟。");
        }

        if (settings.MaxMatchesPerEntrantPerDay <= 0)
        {
            throw new DrawValidationException("单名选手每日最多场次必须大于 0。");
        }

        foreach (var day in settings.Days)
        {
            if (day.Courts.Count == 0 || day.Courts.Any(string.IsNullOrWhiteSpace))
            {
                throw new DrawValidationException($"{day.DayLabel} 请至少设置一片可用场地。");
            }

            if (day.DayEnd <= day.DayStart)
            {
                throw new DrawValidationException($"{day.DayLabel} 的结束时间必须晚于开始时间。");
            }

            if (!BuildSlotStarts(day, settings).Any())
            {
                throw new DrawValidationException($"{day.DayLabel} 的时间段无法容纳一场比赛。");
            }
        }
    }

    private static string BuildGroupName(int groupNumber)
    {
        return groupNumber == 0 ? "总决赛" : $"{ToGroupLetter(groupNumber)}组";
    }

    private static string ToGroupLetter(int groupNumber)
    {
        if (groupNumber <= 0)
        {
            return groupNumber.ToString();
        }

        var value = groupNumber;
        var chars = new Stack<char>();
        while (value > 0)
        {
            value--;
            chars.Push((char)('A' + value % 26));
            value /= 26;
        }

        return new string(chars.ToArray());
    }

    private static bool IsPowerOfTwo(int value)
    {
        return value > 0 && (value & (value - 1)) == 0;
    }

    private sealed record ScheduleBracketEntry(
        string Label,
        int? ProtectedSeedRank = null,
        DrawParticipant? Participant = null,
        int? SourceMatchId = null,
        IReadOnlyList<string>? EntrantKeys = null)
    {
        public IReadOnlyList<string> EntrantKeys { get; init; } = EntrantKeys ?? [];
    }

    private sealed record UnscheduledMatch(
        int Id,
        int GroupNumber,
        string GroupName,
        string Phase,
        string MatchName,
        string SideA,
        string SideB,
        string Note,
        bool SameUnit,
        IReadOnlyList<int> DependencyIds,
        IReadOnlyList<string> EntrantKeys);

    private sealed record RoundRobinMatch(
        int Order,
        int Round,
        int RoundMatchNumber,
        int FirstIndex,
        int SecondIndex,
        bool SameUnit);
}
