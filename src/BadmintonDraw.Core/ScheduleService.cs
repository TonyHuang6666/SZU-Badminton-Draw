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

        return AssignTimeAndCourts(unscheduled, settings);
    }

    private static List<UnscheduledMatch> BuildKnockoutMatches(DrawResult result)
    {
        var matches = new List<UnscheduledMatch>();
        var groupQualifierEntries = new List<ScheduleBracketEntry>();
        var championshipBracketMatches = new List<ScheduleBracketMatch>();
        var groupSlotCounts = result.Groups
            .Select(group => CountMainDrawEntries(result, group))
            .ToList();
        var groupPhaseLabels = result.Groups.Count > 1
            ? BracketStageLabels.BuildQualifierMatchPhases(groupSlotCounts)
            : Array.Empty<string>();

        foreach (var group in result.Groups)
        {
            groupQualifierEntries.Add(BuildGroupKnockoutMatches(
                result,
                group,
                matches,
                groupPhaseLabels,
                result.Settings.KnockoutGoal == KnockoutGoal.Champion && result.Groups.Count == 1
                    ? championshipBracketMatches
                    : null));
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
                phaseLabels: championPhaseLabels,
                bracketMatches: championshipBracketMatches);
        }

        AddPlacementPlayoffMatches(matches, result.Settings.PlacementPlayoff, championshipBracketMatches);

        return matches;
    }

    private static ScheduleBracketEntry BuildGroupKnockoutMatches(
        DrawResult result,
        DrawGroup group,
        List<UnscheduledMatch> matches,
        IReadOnlyList<string> groupPhaseLabels,
        List<ScheduleBracketMatch>? bracketMatches)
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
            var entrantPaths = CreateEntrantPaths(first, second);
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
                entrantPaths);
            bracketEntries.Add(new ScheduleBracketEntry(
                $"{matchName}胜者",
                MinSeedRank(first, second),
                null,
                matchId,
                WithOutcome(entrantPaths, matchId, MatchOutcome.Winner)));
        }

        foreach (var participant in byeParticipants)
        {
            bracketEntries.Add(new ScheduleBracketEntry(
                participant.DisplayName,
                participant.SeedRank,
                participant,
                null,
                [CreateEntrantPath(participant)]));
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
            phaseLabels: groupPhaseLabels,
            bracketMatches: bracketMatches);
    }

    private static ScheduleBracketEntry BuildPlaceholderBracketMatches(
        List<UnscheduledMatch> matches,
        int groupNumber,
        string groupName,
        IReadOnlyList<ScheduleBracketEntry> entries,
        string finalWinnerNote,
        string phasePrefix,
        IReadOnlyList<string>? phaseLabels = null,
        List<ScheduleBracketMatch>? bracketMatches = null)
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
                var entrantPaths = MergeEntrantPaths(first.EntrantPaths, second.EntrantPaths);
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
                    entrantPaths);

                bracketMatches?.Add(new ScheduleBracketMatch(
                    matchId,
                    phase,
                    matchName,
                    entrantCount,
                    matchNumber,
                    entrantPaths));
                nextRound.Add(new ScheduleBracketEntry(
                    $"{matchName}胜者",
                    null,
                    null,
                    matchId,
                    WithOutcome(entrantPaths, matchId, MatchOutcome.Winner)));
            }

            currentRound = nextRound;
            roundIndex++;
        }

        return currentRound[0];
    }

    private static void AddPlacementPlayoffMatches(
        List<UnscheduledMatch> matches,
        PlacementPlayoff placementPlayoff,
        IReadOnlyList<ScheduleBracketMatch> championshipBracketMatches)
    {
        if (placementPlayoff == PlacementPlayoff.None)
        {
            return;
        }

        var semiFinals = championshipBracketMatches
            .Where(match => match.EntrantCount == 4)
            .OrderBy(match => match.MatchNumber)
            .ToList();
        if (semiFinals.Count >= 2)
        {
            var entrantPaths = MergeEntrantPaths(
                WithOutcome(semiFinals[0].EntrantPaths, semiFinals[0].Id, MatchOutcome.Loser),
                WithOutcome(semiFinals[1].EntrantPaths, semiFinals[1].Id, MatchOutcome.Loser));
            AddMatch(
                matches,
                groupNumber: 0,
                groupName: PlacementPlayoffLabels.GroupName,
                phase: PlacementPlayoffLabels.ThirdPlacePhase,
                matchName: PlacementPlayoffLabels.ThirdPlaceMatchName,
                sideA: PlacementPlayoffLabels.LoserOf(semiFinals[0].MatchName),
                sideB: PlacementPlayoffLabels.LoserOf(semiFinals[1].MatchName),
                note: "胜者为第3名，负者为第4名",
                sameUnit: false,
                dependencyIds: semiFinals.Select(match => match.Id).ToList(),
                entrantPaths: entrantPaths);
        }

        if (placementPlayoff != PlacementPlayoff.ThirdToEighth)
        {
            return;
        }

        var quarterFinals = championshipBracketMatches
            .Where(match => match.EntrantCount == 8)
            .OrderBy(match => match.MatchNumber)
            .ToList();
        if (quarterFinals.Count < 4)
        {
            return;
        }

        var fifthToEighthSemiFinalIds = new List<int>();
        var fifthToEighthSemiFinalPaths = new List<IReadOnlyList<EntrantPath>>();
        for (var index = 0; index < 2; index++)
        {
            var first = quarterFinals[index * 2];
            var second = quarterFinals[index * 2 + 1];
            var entrantPaths = MergeEntrantPaths(
                WithOutcome(first.EntrantPaths, first.Id, MatchOutcome.Loser),
                WithOutcome(second.EntrantPaths, second.Id, MatchOutcome.Loser));
            var matchName = PlacementPlayoffLabels.FifthToEighthSemiMatchName(index + 1);
            var matchId = AddMatch(
                matches,
                groupNumber: 0,
                groupName: PlacementPlayoffLabels.GroupName,
                phase: PlacementPlayoffLabels.FifthToEighthSemiPhase,
                matchName: matchName,
                sideA: PlacementPlayoffLabels.LoserOf(first.MatchName),
                sideB: PlacementPlayoffLabels.LoserOf(second.MatchName),
                note: "胜者进入5/6名赛，负者进入7/8名赛",
                sameUnit: false,
                dependencyIds: [first.Id, second.Id],
                entrantPaths: entrantPaths);

            fifthToEighthSemiFinalIds.Add(matchId);
            fifthToEighthSemiFinalPaths.Add(entrantPaths);
        }

        var finalDependencyIds = fifthToEighthSemiFinalIds.ToList();
        var fifthPlaceEntrantPaths = MergeEntrantPaths(
            WithOutcome(fifthToEighthSemiFinalPaths[0], fifthToEighthSemiFinalIds[0], MatchOutcome.Winner),
            WithOutcome(fifthToEighthSemiFinalPaths[1], fifthToEighthSemiFinalIds[1], MatchOutcome.Winner));
        AddMatch(
            matches,
            groupNumber: 0,
            groupName: PlacementPlayoffLabels.GroupName,
            phase: PlacementPlayoffLabels.FifthPlacePhase,
            matchName: PlacementPlayoffLabels.FifthPlaceMatchName,
            sideA: PlacementPlayoffLabels.WinnerOf(PlacementPlayoffLabels.FifthToEighthSemiMatchName(1)),
            sideB: PlacementPlayoffLabels.WinnerOf(PlacementPlayoffLabels.FifthToEighthSemiMatchName(2)),
            note: "胜者为第5名，负者为第6名",
            sameUnit: false,
            dependencyIds: finalDependencyIds,
            entrantPaths: fifthPlaceEntrantPaths);
        var seventhPlaceEntrantPaths = MergeEntrantPaths(
            WithOutcome(fifthToEighthSemiFinalPaths[0], fifthToEighthSemiFinalIds[0], MatchOutcome.Loser),
            WithOutcome(fifthToEighthSemiFinalPaths[1], fifthToEighthSemiFinalIds[1], MatchOutcome.Loser));
        AddMatch(
            matches,
            groupNumber: 0,
            groupName: PlacementPlayoffLabels.GroupName,
            phase: PlacementPlayoffLabels.SeventhPlacePhase,
            matchName: PlacementPlayoffLabels.SeventhPlaceMatchName,
            sideA: PlacementPlayoffLabels.LoserOf(PlacementPlayoffLabels.FifthToEighthSemiMatchName(1)),
            sideB: PlacementPlayoffLabels.LoserOf(PlacementPlayoffLabels.FifthToEighthSemiMatchName(2)),
            note: "胜者为第7名，负者为第8名",
            sameUnit: false,
            dependencyIds: finalDependencyIds,
            entrantPaths: seventhPlaceEntrantPaths);
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
        IReadOnlyList<EntrantPath> entrantPaths)
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
            DistinctEntrantPaths(entrantPaths)));
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
                    CreateEntrantPaths(first, second));
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

    private static SchedulePlan AssignTimeAndCourts(
        IReadOnlyList<UnscheduledMatch> matches,
        ScheduleSettings settings)
    {
        var remaining = matches.ToDictionary(match => match.Id);
        var completed = new HashSet<int>();
        var scheduled = new List<ScheduledMatch>(matches.Count);
        var order = 1;

        foreach (var day in settings.Days.OrderBy(day => day.Date))
        {
            var dailyMatches = new List<IReadOnlyList<EntrantPath>>();
            foreach (var slotStart in BuildSlotStarts(day, settings))
            {
                var slotEntrants = new List<EntrantPath>();
                var scheduledThisSlot = new List<UnscheduledMatch>();

                foreach (var court in day.Courts)
                {
                    var candidate = remaining.Values
                        .Where(match => IsEligible(match, completed, dailyMatches, slotEntrants, settings.MaxMatchesPerEntrantPerDay))
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
                    slotEntrants.AddRange(candidate.EntrantPaths);
                }

                foreach (var match in scheduledThisSlot)
                {
                    completed.Add(match.Id);
                    dailyMatches.Add(match.EntrantPaths);
                }

                if (remaining.Count == 0)
                {
                    return new SchedulePlan(scheduled, settings);
                }
            }
        }

        return new SchedulePlan(scheduled, settings, BuildUnscheduledPreviews(remaining.Values, completed, scheduled.Count));
    }

    private static IReadOnlyList<UnscheduledMatchPreview> BuildUnscheduledPreviews(
        IEnumerable<UnscheduledMatch> remaining,
        IReadOnlySet<int> completed,
        int scheduledCount)
    {
        return remaining
            .OrderBy(match => match.Id)
            .Select((match, index) => new UnscheduledMatchPreview(
                scheduledCount + index + 1,
                match.GroupNumber,
                match.GroupName,
                match.Phase,
                match.MatchName,
                match.SideA,
                match.SideB,
                match.Note,
                match.SameUnit,
                match.DependencyIds.All(completed.Contains)
                    ? "资源不足，未能安排到时间和场地"
                    : "前置比赛未能全部安排"))
            .ToList();
    }

    private static bool IsEligible(
        UnscheduledMatch match,
        IReadOnlySet<int> completed,
        IReadOnlyList<IReadOnlyList<EntrantPath>> dailyMatches,
        IReadOnlyList<EntrantPath> slotEntrants,
        int maxMatchesPerEntrantPerDay)
    {
        return match.DependencyIds.All(completed.Contains)
            && !HasCompatibleEntrantOverlap(match.EntrantPaths, slotEntrants)
            && !WouldExceedDailyLimit(match, dailyMatches, maxMatchesPerEntrantPerDay);
    }

    private static bool HasCompatibleEntrantOverlap(
        IReadOnlyList<EntrantPath> first,
        IReadOnlyList<EntrantPath> second)
    {
        return first.Any(left => second.Any(right =>
            string.Equals(left.EntrantKey, right.EntrantKey, StringComparison.Ordinal)
            && AreConditionsCompatible(left.Conditions, right.Conditions)));
    }

    private static bool WouldExceedDailyLimit(
        UnscheduledMatch candidate,
        IReadOnlyList<IReadOnlyList<EntrantPath>> dailyMatches,
        int maxMatchesPerEntrantPerDay)
    {
        var entrantKeys = candidate.EntrantPaths
            .Select(path => path.EntrantKey)
            .Distinct(StringComparer.Ordinal);
        foreach (var entrantKey in entrantKeys)
        {
            var appearances = dailyMatches
                .SelectMany(paths => paths)
                .Where(path => string.Equals(path.EntrantKey, entrantKey, StringComparison.Ordinal))
                .Concat(candidate.EntrantPaths.Where(path => string.Equals(path.EntrantKey, entrantKey, StringComparison.Ordinal)))
                .ToList();
            if (CanSelectCompatibleAppearances(
                appearances,
                maxMatchesPerEntrantPerDay + 1,
                startIndex: 0,
                conditions: new Dictionary<int, MatchOutcome>()))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CanSelectCompatibleAppearances(
        IReadOnlyList<EntrantPath> appearances,
        int targetCount,
        int startIndex,
        IReadOnlyDictionary<int, MatchOutcome> conditions)
    {
        if (targetCount <= 0)
        {
            return true;
        }

        if (appearances.Count - startIndex < targetCount)
        {
            return false;
        }

        for (var index = startIndex; index < appearances.Count; index++)
        {
            if (!TryMergeConditions(conditions, appearances[index].Conditions, out var mergedConditions))
            {
                continue;
            }

            if (CanSelectCompatibleAppearances(appearances, targetCount - 1, index + 1, mergedConditions))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryMergeConditions(
        IReadOnlyDictionary<int, MatchOutcome> existingConditions,
        IReadOnlyList<OutcomeCondition> candidateConditions,
        out IReadOnlyDictionary<int, MatchOutcome> mergedConditions)
    {
        var merged = new Dictionary<int, MatchOutcome>(existingConditions);
        foreach (var condition in candidateConditions)
        {
            if (merged.TryGetValue(condition.MatchId, out var existingOutcome)
                && existingOutcome != condition.Outcome)
            {
                mergedConditions = existingConditions;
                return false;
            }

            merged[condition.MatchId] = condition.Outcome;
        }

        mergedConditions = merged;
        return true;
    }

    private static bool AreConditionsCompatible(
        IReadOnlyList<OutcomeCondition> first,
        IReadOnlyList<OutcomeCondition> second)
    {
        return !first.Any(left => second.Any(right =>
            left.MatchId == right.MatchId && left.Outcome != right.Outcome));
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

    private static EntrantPath CreateEntrantPath(DrawParticipant participant)
    {
        return new EntrantPath(participant.NormalizedDisplayName, []);
    }

    private static IReadOnlyList<EntrantPath> CreateEntrantPaths(params DrawParticipant[] participants)
    {
        return participants.Select(CreateEntrantPath).ToList();
    }

    private static IReadOnlyList<EntrantPath> MergeEntrantPaths(
        IReadOnlyList<EntrantPath> first,
        IReadOnlyList<EntrantPath> second)
    {
        return DistinctEntrantPaths(first.Concat(second));
    }

    private static IReadOnlyList<EntrantPath> WithOutcome(
        IReadOnlyList<EntrantPath> paths,
        int matchId,
        MatchOutcome outcome)
    {
        return DistinctEntrantPaths(paths
            .Select(path => TryAddCondition(path, matchId, outcome))
            .Where(path => path is not null)
            .Select(path => path!));
    }

    private static EntrantPath? TryAddCondition(EntrantPath path, int matchId, MatchOutcome outcome)
    {
        if (path.Conditions.Any(condition => condition.MatchId == matchId && condition.Outcome != outcome))
        {
            return null;
        }

        if (path.Conditions.Any(condition => condition.MatchId == matchId && condition.Outcome == outcome))
        {
            return path;
        }

        var conditions = path.Conditions
            .Append(new OutcomeCondition(matchId, outcome))
            .OrderBy(condition => condition.MatchId)
            .ToList();
        return path with { Conditions = conditions };
    }

    private static IReadOnlyList<EntrantPath> DistinctEntrantPaths(IEnumerable<EntrantPath> paths)
    {
        return paths
            .GroupBy(BuildEntrantPathKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private static string BuildEntrantPathKey(EntrantPath path)
    {
        var conditions = string.Join(",", path.Conditions.Select(condition => $"{condition.MatchId}:{(int)condition.Outcome}"));
        return $"{path.EntrantKey}|{conditions}";
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
        IReadOnlyList<EntrantPath>? EntrantPaths = null)
    {
        public IReadOnlyList<EntrantPath> EntrantPaths { get; init; } = EntrantPaths ?? [];
    }

    private sealed record ScheduleBracketMatch(
        int Id,
        string Phase,
        string MatchName,
        int EntrantCount,
        int MatchNumber,
        IReadOnlyList<EntrantPath> EntrantPaths);

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
        IReadOnlyList<EntrantPath> EntrantPaths);

    private sealed record EntrantPath(
        string EntrantKey,
        IReadOnlyList<OutcomeCondition> Conditions);

    private readonly record struct OutcomeCondition(int MatchId, MatchOutcome Outcome);

    private enum MatchOutcome
    {
        Winner,
        Loser
    }

    private sealed record RoundRobinMatch(
        int Order,
        int Round,
        int RoundMatchNumber,
        int FirstIndex,
        int SecondIndex,
        bool SameUnit);
}
