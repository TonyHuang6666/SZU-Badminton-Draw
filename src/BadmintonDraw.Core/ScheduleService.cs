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
                forceBeforeTimingBoundary: result.Settings.KnockoutGoal == KnockoutGoal.Champion && result.Groups.Count > 1,
                isChampionshipBracket: result.Settings.KnockoutGoal == KnockoutGoal.Champion && result.Groups.Count == 1,
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
                forceBeforeTimingBoundary: false,
                isChampionshipBracket: true,
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
        bool forceBeforeTimingBoundary,
        bool isChampionshipBracket,
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
            var firstEntrantPaths = CreateEntrantPaths(first);
            var secondEntrantPaths = CreateEntrantPaths(second);
            var entrantPaths = MergeEntrantPaths(firstEntrantPaths, secondEntrantPaths);
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
                [],
                entrantPaths,
                firstEntrantPaths,
                secondEntrantPaths,
                forceBeforeTimingBoundary: forceBeforeTimingBoundary || isChampionshipBracket);
            bracketEntries.Add(new ScheduleBracketEntry(
                $"{matchName}胜者",
                MinSeedRank(first, second),
                null,
                matchId,
                matchName,
                ScheduleMatchDependencyOutcome.Winner,
                WithOutcome(entrantPaths, matchId, MatchOutcome.Winner)));
        }

        foreach (var participant in byeParticipants)
        {
            bracketEntries.Add(new ScheduleBracketEntry(
                participant.DisplayName,
                participant.SeedRank,
                participant,
                EntrantPaths: CreateEntrantPaths(participant)));
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
            forceBeforeTimingBoundary: forceBeforeTimingBoundary,
            isChampionshipBracket: isChampionshipBracket,
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
        bool forceBeforeTimingBoundary = false,
        bool isChampionshipBracket = false,
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
                var dependencies = BuildMatchDependencies(first, second);
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
                    dependencies,
                    entrantPaths,
                    first.EntrantPaths,
                    second.EntrantPaths,
                    knockoutEntrantCount: entrantCount,
                    forceBeforeTimingBoundary: forceBeforeTimingBoundary,
                    isChampionshipBracket: isChampionshipBracket,
                    isChampionshipFinal: isChampionshipBracket && isFinalInThisBracket);

                bracketMatches?.Add(new ScheduleBracketMatch(
                    matchId,
                    phase,
                    matchName,
                    entrantCount,
                    matchNumber,
                    entrantPaths));
                nextRound.Add(new ScheduleBracketEntry(
                    $"{matchName}胜者",
                    SourceMatchId: matchId,
                    SourceMatchName: matchName,
                    SourceOutcome: ScheduleMatchDependencyOutcome.Winner,
                    EntrantPaths: WithOutcome(entrantPaths, matchId, MatchOutcome.Winner)));
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
            var firstEntrantPaths = WithOutcome(semiFinals[0].EntrantPaths, semiFinals[0].Id, MatchOutcome.Loser);
            var secondEntrantPaths = WithOutcome(semiFinals[1].EntrantPaths, semiFinals[1].Id, MatchOutcome.Loser);
            var entrantPaths = MergeEntrantPaths(firstEntrantPaths, secondEntrantPaths);
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
                dependencies:
                [
                    BuildDependency(semiFinals[0], ScheduleMatchDependencyOutcome.Loser, ScheduleMatchSide.SideA),
                    BuildDependency(semiFinals[1], ScheduleMatchDependencyOutcome.Loser, ScheduleMatchSide.SideB)
                ],
                entrantPaths: entrantPaths,
                sideAEntrantPaths: firstEntrantPaths,
                sideBEntrantPaths: secondEntrantPaths,
                isPlacementPlayoff: true);
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
            var firstEntrantPaths = WithOutcome(first.EntrantPaths, first.Id, MatchOutcome.Loser);
            var secondEntrantPaths = WithOutcome(second.EntrantPaths, second.Id, MatchOutcome.Loser);
            var entrantPaths = MergeEntrantPaths(firstEntrantPaths, secondEntrantPaths);
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
                dependencies:
                [
                    BuildDependency(first, ScheduleMatchDependencyOutcome.Loser, ScheduleMatchSide.SideA),
                    BuildDependency(second, ScheduleMatchDependencyOutcome.Loser, ScheduleMatchSide.SideB)
                ],
                entrantPaths: entrantPaths,
                sideAEntrantPaths: firstEntrantPaths,
                sideBEntrantPaths: secondEntrantPaths,
                isPlacementPlayoff: true);

            fifthToEighthSemiFinalIds.Add(matchId);
            fifthToEighthSemiFinalPaths.Add(entrantPaths);
        }

        var finalDependencyIds = fifthToEighthSemiFinalIds.ToList();
        var fifthPlaceFirstEntrantPaths = WithOutcome(fifthToEighthSemiFinalPaths[0], fifthToEighthSemiFinalIds[0], MatchOutcome.Winner);
        var fifthPlaceSecondEntrantPaths = WithOutcome(fifthToEighthSemiFinalPaths[1], fifthToEighthSemiFinalIds[1], MatchOutcome.Winner);
        var fifthPlaceEntrantPaths = MergeEntrantPaths(fifthPlaceFirstEntrantPaths, fifthPlaceSecondEntrantPaths);
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
            dependencies:
            [
                BuildDependency(fifthToEighthSemiFinalIds[0], PlacementPlayoffLabels.FifthToEighthSemiMatchName(1), ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA),
                BuildDependency(fifthToEighthSemiFinalIds[1], PlacementPlayoffLabels.FifthToEighthSemiMatchName(2), ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideB)
            ],
            entrantPaths: fifthPlaceEntrantPaths,
            sideAEntrantPaths: fifthPlaceFirstEntrantPaths,
            sideBEntrantPaths: fifthPlaceSecondEntrantPaths,
            isPlacementPlayoff: true);
        var seventhPlaceFirstEntrantPaths = WithOutcome(fifthToEighthSemiFinalPaths[0], fifthToEighthSemiFinalIds[0], MatchOutcome.Loser);
        var seventhPlaceSecondEntrantPaths = WithOutcome(fifthToEighthSemiFinalPaths[1], fifthToEighthSemiFinalIds[1], MatchOutcome.Loser);
        var seventhPlaceEntrantPaths = MergeEntrantPaths(seventhPlaceFirstEntrantPaths, seventhPlaceSecondEntrantPaths);
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
            dependencies:
            [
                BuildDependency(fifthToEighthSemiFinalIds[0], PlacementPlayoffLabels.FifthToEighthSemiMatchName(1), ScheduleMatchDependencyOutcome.Loser, ScheduleMatchSide.SideA),
                BuildDependency(fifthToEighthSemiFinalIds[1], PlacementPlayoffLabels.FifthToEighthSemiMatchName(2), ScheduleMatchDependencyOutcome.Loser, ScheduleMatchSide.SideB)
            ],
            entrantPaths: seventhPlaceEntrantPaths,
            sideAEntrantPaths: seventhPlaceFirstEntrantPaths,
            sideBEntrantPaths: seventhPlaceSecondEntrantPaths,
            isPlacementPlayoff: true);
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
        IReadOnlyList<ScheduleMatchDependency> dependencies,
        IReadOnlyList<EntrantPath> entrantPaths,
        IReadOnlyList<EntrantPath>? sideAEntrantPaths = null,
        IReadOnlyList<EntrantPath>? sideBEntrantPaths = null,
        int? knockoutEntrantCount = null,
        bool forceBeforeTimingBoundary = false,
        bool isChampionshipBracket = false,
        bool isChampionshipFinal = false,
        bool isPlacementPlayoff = false)
    {
        var id = matches.Count + 1;
        var matchId = BuildMatchId(id);
        matches.Add(new UnscheduledMatch(
            id,
            matchId,
            groupNumber,
            groupName,
            phase,
            matchName,
            sideA,
            sideB,
            note,
            sameUnit,
            dependencyIds,
            dependencies,
            DistinctEntrantPaths(entrantPaths),
            DistinctEntrantPaths(sideAEntrantPaths ?? entrantPaths),
            DistinctEntrantPaths(sideBEntrantPaths ?? entrantPaths),
            knockoutEntrantCount,
            forceBeforeTimingBoundary,
            isChampionshipBracket,
            isChampionshipFinal,
            isPlacementPlayoff));
        return id;
    }

    private static IReadOnlyList<ScheduleMatchDependency> BuildMatchDependencies(
        ScheduleBracketEntry first,
        ScheduleBracketEntry second)
    {
        var dependencies = new List<ScheduleMatchDependency>();
        AddEntryDependency(dependencies, first, ScheduleMatchSide.SideA);
        AddEntryDependency(dependencies, second, ScheduleMatchSide.SideB);
        return dependencies;
    }

    private static void AddEntryDependency(
        ICollection<ScheduleMatchDependency> dependencies,
        ScheduleBracketEntry entry,
        ScheduleMatchSide side)
    {
        if (!entry.SourceMatchId.HasValue
            || string.IsNullOrWhiteSpace(entry.SourceMatchName)
            || !entry.SourceOutcome.HasValue)
        {
            return;
        }

        dependencies.Add(BuildDependency(
            entry.SourceMatchId.Value,
            entry.SourceMatchName,
            entry.SourceOutcome.Value,
            side));
    }

    private static ScheduleMatchDependency BuildDependency(
        ScheduleBracketMatch source,
        ScheduleMatchDependencyOutcome outcome,
        ScheduleMatchSide side)
    {
        return BuildDependency(source.Id, source.MatchName, outcome, side);
    }

    private static ScheduleMatchDependency BuildDependency(
        int sourceId,
        string sourceMatchName,
        ScheduleMatchDependencyOutcome outcome,
        ScheduleMatchSide side)
    {
        return new ScheduleMatchDependency(
            BuildMatchId(sourceId),
            sourceMatchName,
            outcome,
            side);
    }

    private static string BuildMatchId(int id)
    {
        return id.ToString();
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
                var firstEntrantPaths = CreateEntrantPaths(first);
                var secondEntrantPaths = CreateEntrantPaths(second);
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
                    [],
                    MergeEntrantPaths(firstEntrantPaths, secondEntrantPaths),
                    firstEntrantPaths,
                    secondEntrantPaths);
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
        var scheduledById = new Dictionary<int, ScheduledAssignment>();
        var scheduled = new List<ScheduledMatch>(matches.Count);
        var dayLoadTargets = BuildDayLoadTargets(matches, settings);
        var scheduledMinutesByDay = settings.Days.ToDictionary(day => day.DayLabel, _ => 0, StringComparer.Ordinal);
        var order = 1;

        foreach (var day in settings.Days.OrderBy(day => day.Date))
        {
            var dailyAssignments = new List<ScheduledAssignment>();
            var courtAvailableAt = day.Courts.ToDictionary(court => court, _ => day.DayStart, StringComparer.Ordinal);

            while (remaining.Count > 0)
            {
                var availableCourtTimes = courtAvailableAt.Values
                    .Where(time => (day.DayEnd - time).TotalMinutes >= settings.MinimumMatchMinutes)
                    .ToList();
                if (availableCourtTimes.Count == 0)
                {
                    break;
                }

                var currentStart = availableCourtTimes.Min();
                var currentCourts = day.Courts
                    .Where(court => courtAvailableAt[court] == currentStart)
                    .ToList();
                var idleCourts = new List<string>();

                foreach (var court in currentCourts)
                {
                    var candidate = remaining.Values
                        .Select(match => new CandidateMatch(match, ResolveTiming(match, settings)))
                        .Where(candidate => IsEligible(
                            candidate.Match,
                            candidate.Timing,
                            day,
                            currentStart,
                            court,
                            remaining.Values,
                            scheduledById,
                            dailyAssignments,
                            settings)
                            && IsWithinDayLoadTarget(
                                day,
                                candidate.Timing,
                                scheduledMinutesByDay,
                                dayLoadTargets,
                                settings.MaximumMatchMinutes,
                                settings.RefereeCount))
                        .OrderBy(candidate => GetSchedulingStageRank(candidate.Match))
                        .ThenBy(candidate => GetRestSortKey(candidate.Match, currentStart, dailyAssignments))
                        .ThenBy(candidate => candidate.Match.Id)
                        .FirstOrDefault();
                    if (candidate is null)
                    {
                        idleCourts.Add(court);
                        continue;
                    }

                    var slotEnd = currentStart.AddMinutes(candidate.Timing.MatchMinutes);
                    scheduled.Add(new ScheduledMatch(
                        order++,
                        day.DayLabel,
                        currentStart,
                        slotEnd,
                        court,
                        candidate.Match.GroupNumber,
                        candidate.Match.GroupName,
                        candidate.Match.Phase,
                        candidate.Match.MatchName,
                        candidate.Match.SideA,
                        candidate.Match.SideB,
                        candidate.Match.Note,
                        candidate.Match.SameUnit,
                        candidate.Match.MatchId,
                        candidate.Match.Dependencies,
                        ToPlayerIdentities(candidate.Match.SideAEntrantPaths),
                        ToPlayerIdentities(candidate.Match.SideBEntrantPaths)));

                    var assignment = new ScheduledAssignment(
                        candidate.Match.Id,
                        day.Date,
                        currentStart,
                        slotEnd,
                        candidate.Match.EntrantPaths,
                        candidate.Timing.Bucket);
                    scheduledById[candidate.Match.Id] = assignment;
                    dailyAssignments.Add(assignment);
                    scheduledMinutesByDay[day.DayLabel] = scheduledMinutesByDay.GetValueOrDefault(day.DayLabel) + candidate.Timing.MatchMinutes;
                    remaining.Remove(candidate.Match.Id);
                    courtAvailableAt[court] = slotEnd;
                }

                foreach (var court in idleCourts)
                {
                    courtAvailableAt[court] = FindNextWakeTime(currentStart, day, court, courtAvailableAt, dailyAssignments);
                }

                if (remaining.Count == 0)
                {
                    return BuildFinalSchedulePlan(scheduled, settings);
                }
            }
        }

        var incompletePlan = new SchedulePlan(
            scheduled,
            settings,
            BuildUnscheduledPreviews(remaining.Values, scheduledById.Keys.ToHashSet(), scheduled.Count));
        return incompletePlan with { QualityReport = BuildScheduleQualityReport(incompletePlan, spreadApplied: false) };
    }

    private static SchedulePlan BuildFinalSchedulePlan(
        IReadOnlyList<ScheduledMatch> scheduled,
        ScheduleSettings settings)
    {
        var plan = new SchedulePlan(scheduled, settings);
        if (settings.AutoSchedulingStrategy == ScheduleAutoSchedulingStrategy.Compact)
        {
            return plan with { QualityReport = BuildScheduleQualityReport(plan, spreadApplied: false) };
        }

        var finalPlan = TrySpreadMatchesWithinDays(plan, out var spreadPlan)
            ? spreadPlan
            : plan;
        return finalPlan with { QualityReport = BuildScheduleQualityReport(finalPlan, !ReferenceEquals(finalPlan, plan)) };
    }

    public static ScheduleQualityReport EvaluateScheduleQuality(
        SchedulePlan plan,
        bool spreadApplied = false)
    {
        return BuildScheduleQualityReport(plan, spreadApplied);
    }

    private static ScheduleQualityReport BuildScheduleQualityReport(
        SchedulePlan plan,
        bool spreadApplied)
    {
        var insights = new List<ScheduleQualityInsight>();
        var orderViolations = ScheduleDependencyGraph.Build(plan).FindOrderViolations().Count;
        var unscheduledCount = plan.UnscheduledMatches.Count;
        var hardViolations = orderViolations + unscheduledCount;
        insights.Add(new ScheduleQualityInsight(
            "硬约束",
            hardViolations == 0
                ? "淘汰树依赖、场地占用和裁判并发已在排程过程中作为硬约束处理。"
                : $"仍有 {orderViolations} 条淘汰树依赖顺序问题、{unscheduledCount} 场未安排，需要增加资源或人工处理。",
            hardViolations * 100_000));
        insights.Add(new ScheduleQualityInsight(
            "策略",
            $"{GetScheduleStrategyName(plan.Settings.AutoSchedulingStrategy)}；{(spreadApplied ? "已按并发波次整体插入空档。" : "保持紧凑或未能进一步分散。")}"));

        var softScore = 0;
        foreach (var day in plan.Settings.Days.OrderBy(day => day.Date))
        {
            var dayMatches = plan.Matches
                .Where(match => string.Equals(match.DayLabel, day.DayLabel, StringComparison.Ordinal))
                .ToList();
            var capacity = CalculateDayCapacityMinutes(day, plan.Settings.RefereeCount);
            var minutes = dayMatches.Sum(match => match.DurationMinutes);
            var utilization = capacity <= 0 ? 0 : minutes * 100d / capacity;
            var waveCount = dayMatches
                .GroupBy(match => match.StartTime)
                .Count();
            var gapPenalty = CalculateWaveGapPenalty(dayMatches);
            softScore += gapPenalty;
            insights.Add(new ScheduleQualityInsight(
                "每日负载",
                $"{day.DayLabel}：{dayMatches.Count} 场，约 {utilization:0.#}% 负载，{waveCount} 个开赛波次。",
                gapPenalty));
        }

        return new ScheduleQualityReport(
            GetScheduleStrategyName(plan.Settings.AutoSchedulingStrategy),
            hardViolations,
            softScore + hardViolations * 100_000,
            insights);
    }

    private static int CalculateWaveGapPenalty(IReadOnlyList<ScheduledMatch> dayMatches)
    {
        var waves = dayMatches
            .GroupBy(match => match.StartTime)
            .OrderBy(group => group.Key)
            .Select(group => new
            {
                Start = group.Key,
                End = group.Max(match => match.EndTime),
                Duration = group.Max(match => match.DurationMinutes)
            })
            .ToList();
        var penalty = 0;
        for (var index = 1; index < waves.Count; index++)
        {
            var gap = Math.Max(0, (int)(waves[index].Start - waves[index - 1].End).TotalMinutes);
            penalty += Math.Max(0, gap - waves[index - 1].Duration);
        }

        return penalty;
    }

    private static string GetScheduleStrategyName(ScheduleAutoSchedulingStrategy strategy)
    {
        return strategy switch
        {
            ScheduleAutoSchedulingStrategy.BalancedRelaxed => "均衡宽松",
            ScheduleAutoSchedulingStrategy.FinalsDayFriendly => "决赛日友好",
            _ => "紧凑完成"
        };
    }

    private static bool TrySpreadMatchesWithinDays(
        SchedulePlan plan,
        out SchedulePlan spreadPlan)
    {
        var spreadMatches = new List<ScheduledMatch>(plan.Matches.Count);
        var handledDayLabels = new HashSet<string>(StringComparer.Ordinal);
        var orderedDays = plan.Settings.Days
            .OrderBy(day => day.Date)
            .ToList();

        foreach (var day in orderedDays)
        {
            var dayMatches = plan.Matches
                .Where(match => string.Equals(match.DayLabel, day.DayLabel, StringComparison.Ordinal))
                .OrderBy(match => match.StartTime)
                .ThenBy(match => GetCourtSortIndex(day, match.Court))
                .ThenBy(match => match.Order)
                .ToList();
            handledDayLabels.Add(day.DayLabel);

            if (dayMatches.Count <= 1)
            {
                spreadMatches.AddRange(dayMatches);
                continue;
            }

            if (!TrySpreadDayMatches(dayMatches, day, plan, out var spreadDayMatches))
            {
                spreadPlan = plan;
                return false;
            }

            spreadMatches.AddRange(spreadDayMatches);
        }

        spreadMatches.AddRange(plan.Matches
            .Where(match => !handledDayLabels.Contains(match.DayLabel))
            .OrderBy(match => match.DayLabel, StringComparer.Ordinal)
            .ThenBy(match => match.StartTime)
            .ThenBy(match => match.Order));

        var dayOrder = orderedDays
            .Select((day, index) => (day.DayLabel, Index: index))
            .ToDictionary(item => item.DayLabel, item => item.Index, StringComparer.Ordinal);
        var reordered = spreadMatches
            .OrderBy(match => dayOrder.TryGetValue(match.DayLabel, out var index) ? index : int.MaxValue)
            .ThenBy(match => match.StartTime)
            .ThenBy(match =>
            {
                var day = orderedDays.FirstOrDefault(item => string.Equals(item.DayLabel, match.DayLabel, StringComparison.Ordinal));
                return day is null ? int.MaxValue : GetCourtSortIndex(day, match.Court);
            })
            .ThenBy(match => match.Order)
            .Select((match, index) => match with { Order = index + 1 })
            .ToList();

        spreadPlan = plan with { Matches = reordered };
        if (ScheduleDependencyGraph.Build(spreadPlan).FindOrderViolations().Count > 0)
        {
            spreadPlan = plan;
            return false;
        }

        return true;
    }

    private static bool TrySpreadDayMatches(
        IReadOnlyList<ScheduledMatch> matches,
        ScheduleDaySettings day,
        SchedulePlan originalPlan,
        out IReadOnlyList<ScheduledMatch> spreadMatches)
    {
        spreadMatches = matches;
        var slotStarts = BuildSpreadSlotStarts(day, originalPlan.Settings.MinimumMatchMinutes);
        if (slotStarts.Count == 0)
        {
            return false;
        }

        var sameDayMatchIds = matches
            .Where(match => !string.IsNullOrWhiteSpace(match.MatchId))
            .Select(match => match.MatchId)
            .ToHashSet(StringComparer.Ordinal);
        var waves = BuildSpreadWaves(matches, day);
        var placed = new List<ScheduledMatch>(matches.Count);
        var placedByMatchId = new Dictionary<string, ScheduledMatch>(StringComparer.Ordinal);

        for (var waveIndex = 0; waveIndex < waves.Count; waveIndex++)
        {
            var wave = waves[waveIndex];
            var idealSlotIndex = CalculateIdealSpreadSlotIndex(waveIndex, waves.Count, slotStarts.Count);
            var earliestStart = slotStarts[idealSlotIndex];
            if (placed.Count > 0)
            {
                earliestStart = LimitSpreadGapByPreviousWave(earliestStart, placed);
            }

            foreach (var match in wave)
            {
                foreach (var dependency in match.Dependencies.Where(dependency => sameDayMatchIds.Contains(dependency.SourceMatchId)))
                {
                    if (!placedByMatchId.TryGetValue(dependency.SourceMatchId, out var source))
                    {
                        spreadMatches = matches;
                        return false;
                    }

                    if (source.EndTime > earliestStart)
                    {
                        earliestStart = source.EndTime;
                    }
                }
            }

            var startIndex = FindFirstSpreadSlotIndex(slotStarts, earliestStart);
            if (!TryPlaceSpreadWave(
                wave,
                day,
                slotStarts,
                startIndex,
                originalPlan.Settings.RefereeCount,
                placed,
                out var placedWave))
            {
                spreadMatches = matches;
                return false;
            }

            foreach (var placedMatch in placedWave)
            {
                placed.Add(placedMatch);
                if (!string.IsNullOrWhiteSpace(placedMatch.MatchId))
                {
                    placedByMatchId[placedMatch.MatchId] = placedMatch;
                }
            }
        }

        spreadMatches = placed
            .OrderBy(match => match.StartTime)
            .ThenBy(match => GetCourtSortIndex(day, match.Court))
            .ThenBy(match => match.Order)
            .ToList();
        return true;
    }

    private static TimeOnly LimitSpreadGapByPreviousWave(
        TimeOnly desiredStart,
        IReadOnlyList<ScheduledMatch> placed)
    {
        var previousWaveStart = placed.Max(match => match.StartTime);
        var previousWaveMatches = placed
            .Where(match => match.StartTime == previousWaveStart)
            .ToList();
        var previousWaveEnd = previousWaveMatches.Max(match => match.EndTime);
        var previousWaveDuration = previousWaveMatches.Max(match => match.DurationMinutes);
        var latestSoftStart = previousWaveEnd.AddMinutes(previousWaveDuration);
        return desiredStart > latestSoftStart ? latestSoftStart : desiredStart;
    }

    private static IReadOnlyList<IReadOnlyList<ScheduledMatch>> BuildSpreadWaves(
        IReadOnlyList<ScheduledMatch> matches,
        ScheduleDaySettings day)
    {
        return matches
            .GroupBy(match => match.StartTime)
            .OrderBy(group => group.Key)
            .Select(group => (IReadOnlyList<ScheduledMatch>)group
                .OrderBy(match => GetCourtSortIndex(day, match.Court))
                .ThenBy(match => match.Order)
                .ToList())
            .ToList();
    }

    private static bool TryPlaceSpreadWave(
        IReadOnlyList<ScheduledMatch> wave,
        ScheduleDaySettings day,
        IReadOnlyList<TimeOnly> slotStarts,
        int startIndex,
        int? refereeCount,
        IReadOnlyList<ScheduledMatch> placed,
        out IReadOnlyList<ScheduledMatch> placedWave)
    {
        for (var slotIndex = startIndex; slotIndex < slotStarts.Count; slotIndex++)
        {
            var start = slotStarts[slotIndex];
            if (wave.Any(match => start.AddMinutes(match.DurationMinutes) > day.DayEnd))
            {
                continue;
            }

            if (WouldExceedSpreadConcurrency(day, start, wave, placed, refereeCount))
            {
                continue;
            }

            if (HasSpreadPlayerOverlap(wave, start, placed))
            {
                continue;
            }

            var proposed = new List<ScheduledMatch>(wave.Count);
            foreach (var match in wave)
            {
                var end = start.AddMinutes(match.DurationMinutes);
                var court = FindSpreadWaveCourt(match, start, end, day, placed, proposed);
                if (court is null)
                {
                    proposed.Clear();
                    break;
                }

                proposed.Add(match with
                {
                    StartTime = start,
                    EndTime = end,
                    Court = court!
                });
            }

            if (proposed.Count == wave.Count)
            {
                placedWave = proposed;
                return true;
            }
        }

        placedWave = wave;
        return false;
    }

    private static string? FindSpreadWaveCourt(
        ScheduledMatch match,
        TimeOnly start,
        TimeOnly end,
        ScheduleDaySettings day,
        IReadOnlyList<ScheduledMatch> placed,
        IReadOnlyList<ScheduledMatch> proposed)
    {
        var courtPreference = day.Courts
            .OrderBy(court => string.Equals(court, match.Court, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(court => GetCourtSortIndex(day, court));

        foreach (var court in courtPreference)
        {
            if (!ScheduleResourceCalculator.IsCourtAvailable(day, court, start, end))
            {
                continue;
            }

            if (proposed.Any(candidate => string.Equals(candidate.Court, court, StringComparison.Ordinal)))
            {
                continue;
            }

            if (placed.Any(candidate =>
                string.Equals(candidate.Court, court, StringComparison.Ordinal)
                && candidate.StartTime < end
                && start < candidate.EndTime))
            {
                continue;
            }

            return court;
        }

        return null;
    }

    private static IReadOnlyList<TimeOnly> BuildSpreadSlotStarts(
        ScheduleDaySettings day,
        int slotMinutes)
    {
        var result = new List<TimeOnly>();
        var cursor = day.DayStart;
        var step = Math.Max(1, slotMinutes);
        while (cursor < day.DayEnd)
        {
            result.Add(cursor);
            cursor = cursor.AddMinutes(step);
        }

        return result;
    }

    private static int CalculateIdealSpreadSlotIndex(int matchIndex, int matchCount, int slotCount)
    {
        if (slotCount <= 1 || matchCount <= 1)
        {
            return 0;
        }

        var ideal = (int)Math.Round(matchIndex * (slotCount - 1d) / (matchCount - 1d), MidpointRounding.AwayFromZero);
        return Math.Clamp(ideal, 0, slotCount - 1);
    }

    private static int FindFirstSpreadSlotIndex(
        IReadOnlyList<TimeOnly> slotStarts,
        TimeOnly earliestStart)
    {
        for (var index = 0; index < slotStarts.Count; index++)
        {
            if (slotStarts[index] >= earliestStart)
            {
                return index;
            }
        }

        return slotStarts.Count;
    }

    private static bool WouldExceedSpreadConcurrency(
        ScheduleDaySettings day,
        TimeOnly start,
        IReadOnlyList<ScheduledMatch> wave,
        IReadOnlyList<ScheduledMatch> placed,
        int? refereeCount)
    {
        var longestDuration = wave
            .Select(match => match.DurationMinutes)
            .DefaultIfEmpty(1)
            .Max();
        var end = start.AddMinutes(longestDuration);
        var hardLimit = ScheduleResourceCalculator.GetConcurrentMatchLimit(day, refereeCount, start, end);
        var overlappingMatches = placed.Count(match => match.StartTime < end && start < match.EndTime);
        return overlappingMatches + wave.Count > hardLimit;
    }

    private static bool HasSpreadPlayerOverlap(
        IReadOnlyList<ScheduledMatch> wave,
        TimeOnly start,
        IReadOnlyList<ScheduledMatch> placed)
    {
        foreach (var match in wave)
        {
            var end = start.AddMinutes(match.DurationMinutes);
            var playerKeys = GetScheduledPlayerKeys(match).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (playerKeys.Count == 0)
            {
                continue;
            }

            if (placed
                .Where(candidate => candidate.StartTime < end && start < candidate.EndTime)
                .Any(candidate => GetScheduledPlayerKeys(candidate).Any(playerKeys.Contains)))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetScheduledPlayerKeys(ScheduledMatch match)
    {
        return match.SideAPlayerIdentities
            .Concat(match.SideBPlayerIdentities)
            .Select(identity => identity.IdentityKey)
            .Where(key => !string.IsNullOrWhiteSpace(key));
    }

    private static int GetCourtSortIndex(ScheduleDaySettings day, string court)
    {
        for (var index = 0; index < day.Courts.Count; index++)
        {
            if (string.Equals(day.Courts[index], court, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    private static IReadOnlyDictionary<string, double> BuildDayLoadTargets(
        IReadOnlyList<UnscheduledMatch> matches,
        ScheduleSettings settings)
    {
        var orderedDays = settings.Days.OrderBy(day => day.Date).ToList();
        var capacities = orderedDays.ToDictionary(
            day => day.DayLabel,
            day => (double)CalculateDayCapacityMinutes(day, settings.RefereeCount),
            StringComparer.Ordinal);
        if (settings.AutoSchedulingStrategy == ScheduleAutoSchedulingStrategy.Compact || orderedDays.Count <= 1)
        {
            return capacities;
        }

        var totalMinutes = matches.Sum(match => ResolveTiming(match, settings).MatchMinutes);
        var totalCapacity = Math.Max(1d, capacities.Values.Sum());
        var totalUtilization = totalMinutes / totalCapacity;
        var targets = new Dictionary<string, double>(StringComparer.Ordinal);

        if (settings.AutoSchedulingStrategy == ScheduleAutoSchedulingStrategy.FinalsDayFriendly)
        {
            var lastDay = orderedDays.Last().DayLabel;
            var regularUtilization = Math.Clamp(totalUtilization * 0.95, 0.25, 0.70);
            foreach (var day in orderedDays)
            {
                targets[day.DayLabel] = string.Equals(day.DayLabel, lastDay, StringComparison.Ordinal)
                    ? capacities[day.DayLabel]
                    : capacities[day.DayLabel] * regularUtilization;
            }
        }
        else
        {
            var balancedUtilization = Math.Clamp(totalUtilization * 1.15, 0.35, 0.85);
            foreach (var day in orderedDays)
            {
                targets[day.DayLabel] = capacities[day.DayLabel] * balancedUtilization;
            }
        }

        EnsureTotalTargetCapacity(orderedDays, capacities, targets, totalMinutes);
        return targets;
    }

    private static void EnsureTotalTargetCapacity(
        IReadOnlyList<ScheduleDaySettings> orderedDays,
        IReadOnlyDictionary<string, double> capacities,
        IDictionary<string, double> targets,
        double totalMinutes)
    {
        var deficit = totalMinutes - targets.Values.Sum();
        if (deficit <= 0)
        {
            return;
        }

        foreach (var day in orderedDays.Reverse())
        {
            var current = targets[day.DayLabel];
            var capacity = capacities[day.DayLabel];
            var room = Math.Max(0, capacity - current);
            if (room <= 0)
            {
                continue;
            }

            var added = Math.Min(room, deficit);
            targets[day.DayLabel] = current + added;
            deficit -= added;
            if (deficit <= 0)
            {
                return;
            }
        }
    }

    private static bool IsWithinDayLoadTarget(
        ScheduleDaySettings day,
        ResolvedScheduleTiming timing,
        IReadOnlyDictionary<string, int> scheduledMinutesByDay,
        IReadOnlyDictionary<string, double> dayLoadTargets,
        int maximumMatchMinutes,
        int? refereeCount)
    {
        var capacity = CalculateDayCapacityMinutes(day, refereeCount);
        var target = dayLoadTargets.TryGetValue(day.DayLabel, out var value) ? value : capacity;
        if (target >= capacity)
        {
            return true;
        }

        var scheduledMinutes = scheduledMinutesByDay.TryGetValue(day.DayLabel, out var minutes) ? minutes : 0;
        return scheduledMinutes + timing.MatchMinutes <= target + maximumMatchMinutes;
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
        ResolvedScheduleTiming timing,
        ScheduleDaySettings day,
        TimeOnly start,
        string court,
        IEnumerable<UnscheduledMatch> remaining,
        IReadOnlyDictionary<int, ScheduledAssignment> scheduledById,
        IReadOnlyList<ScheduledAssignment> dailyAssignments,
        ScheduleSettings settings)
    {
        if ((day.DayEnd - start).TotalMinutes < timing.MatchMinutes)
        {
            return false;
        }

        var end = start.AddMinutes(timing.MatchMinutes);
        if (!ScheduleResourceCalculator.IsCourtAvailable(day, court, start, end))
        {
            return false;
        }

        if (WouldExceedRefereeCapacity(day, start, end, dailyAssignments, settings.RefereeCount))
        {
            return false;
        }

        if (match.IsChampionshipFinal && remaining.Any(item => item.Id != match.Id && item.IsPlacementPlayoff))
        {
            return false;
        }

        if (!match.DependencyIds.All(id => IsDependencyCompleted(id, scheduledById, day.Date, start)))
        {
            return false;
        }

        var overlappingEntrants = dailyAssignments
            .Where(assignment => assignment.StartTime < end && start < assignment.EndTime)
            .SelectMany(assignment => assignment.EntrantPaths)
            .ToList();
        if (HasCompatibleEntrantOverlap(match.EntrantPaths, overlappingEntrants))
        {
            return false;
        }

        var sameTimingBucketMatches = dailyAssignments
            .Where(assignment => assignment.TimingBucket == timing.Bucket)
            .Select(assignment => assignment.EntrantPaths)
            .ToList();
        var allTimingBucketMatches = dailyAssignments
            .Select(assignment => assignment.EntrantPaths)
            .ToList();
        return !WouldExceedDailyLimit(match, allTimingBucketMatches, timing.MaxMatchesPerEntrantPerDayAcrossDay)
               && !WouldExceedDailyLimit(match, sameTimingBucketMatches, timing.MaxMatchesPerEntrantPerDay);
    }

    private static bool IsDependencyCompleted(
        int dependencyId,
        IReadOnlyDictionary<int, ScheduledAssignment> scheduledById,
        DateOnly date,
        TimeOnly start)
    {
        return scheduledById.TryGetValue(dependencyId, out var dependency)
            && (dependency.Date < date || dependency.Date == date && dependency.EndTime <= start);
    }

    private static ResolvedScheduleTiming ResolveTiming(UnscheduledMatch match, ScheduleSettings settings)
    {
        if (settings.HasKnockoutTimingSplit && IsBeforeBoundaryTiming(match, settings))
        {
            var timing = settings.BeforeBoundaryTiming!;
            return new ResolvedScheduleTiming(
                ScheduleTimingBucket.BeforeBoundary,
                timing.MatchMinutes,
                timing.MaxMatchesPerEntrantPerDay,
                Math.Max(timing.MaxMatchesPerEntrantPerDay, settings.MaxMatchesPerEntrantPerDay));
        }

        return new ResolvedScheduleTiming(
            ScheduleTimingBucket.Default,
            settings.MatchMinutes,
            settings.MaxMatchesPerEntrantPerDay,
            settings.HasKnockoutTimingSplit
                ? Math.Max(settings.MaxMatchesPerEntrantPerDay, settings.BeforeBoundaryTiming!.MaxMatchesPerEntrantPerDay)
                : settings.MaxMatchesPerEntrantPerDay);
    }

    private static bool IsBeforeBoundaryTiming(UnscheduledMatch match, ScheduleSettings settings)
    {
        if (match.IsPlacementPlayoff)
        {
            return false;
        }

        if (match.ForceBeforeTimingBoundary)
        {
            return true;
        }

        return match.KnockoutEntrantCount.HasValue
            && match.KnockoutEntrantCount.Value > settings.KnockoutTimingBoundaryEntrants!.Value;
    }

    private static int GetSchedulingStageRank(UnscheduledMatch match)
    {
        if (match.IsPlacementPlayoff)
        {
            return GetPlacementStageRank(match.Phase);
        }

        if (match.IsChampionshipFinal)
        {
            return 10_000;
        }

        if (match.Phase.Contains("首轮", StringComparison.Ordinal))
        {
            return 0;
        }

        if (match.KnockoutEntrantCount.HasValue)
        {
            return 1_000 - match.KnockoutEntrantCount.Value;
        }

        if (TryParseKnockoutEntrantCount(match.Phase, out var entrantCount))
        {
            return 1_000 - entrantCount;
        }

        return 5_000;
    }

    private static int GetPlacementStageRank(string phase)
    {
        if (string.Equals(phase, PlacementPlayoffLabels.FifthToEighthSemiPhase, StringComparison.Ordinal))
        {
            return 995;
        }

        return 997;
    }

    private static bool TryParseKnockoutEntrantCount(string phase, out int entrantCount)
    {
        entrantCount = 0;
        var separatorIndex = phase.IndexOf('进', StringComparison.Ordinal);
        return separatorIndex > 0
            && int.TryParse(phase[..separatorIndex], out entrantCount);
    }

    private static int GetRestSortKey(
        UnscheduledMatch match,
        TimeOnly start,
        IReadOnlyList<ScheduledAssignment> dailyAssignments)
    {
        return -GetMinimumRestMinutes(match.EntrantPaths, start, dailyAssignments);
    }

    private static int GetMinimumRestMinutes(
        IReadOnlyList<EntrantPath> entrantPaths,
        TimeOnly start,
        IReadOnlyList<ScheduledAssignment> dailyAssignments)
    {
        var minimumRestMinutes = int.MaxValue;
        foreach (var entrantPath in entrantPaths)
        {
            TimeOnly? latestPreviousEnd = null;
            foreach (var assignment in dailyAssignments)
            {
                if (!assignment.EntrantPaths.Any(previousPath =>
                    string.Equals(previousPath.EntrantKey, entrantPath.EntrantKey, StringComparison.Ordinal)
                    && AreConditionsCompatible(previousPath.Conditions, entrantPath.Conditions)))
                {
                    continue;
                }

                if (!latestPreviousEnd.HasValue || assignment.EndTime > latestPreviousEnd.Value)
                {
                    latestPreviousEnd = assignment.EndTime;
                }
            }

            if (latestPreviousEnd.HasValue)
            {
                var restMinutes = (int)Math.Max(0, (start - latestPreviousEnd.Value).TotalMinutes);
                minimumRestMinutes = Math.Min(minimumRestMinutes, restMinutes);
            }
        }

        return minimumRestMinutes == int.MaxValue ? 24 * 60 : minimumRestMinutes;
    }

    private static TimeOnly FindNextWakeTime(
        TimeOnly current,
        ScheduleDaySettings day,
        string court,
        IReadOnlyDictionary<string, TimeOnly> courtAvailableAt,
        IReadOnlyList<ScheduledAssignment> dailyAssignments)
    {
        var nextCourtTime = courtAvailableAt.Values
            .Where(time => time > current)
            .DefaultIfEmpty(day.DayEnd)
            .Min();
        var nextMatchEnd = dailyAssignments
            .Select(assignment => assignment.EndTime)
            .Where(time => time > current)
            .DefaultIfEmpty(day.DayEnd)
            .Min();
        var nextUnavailableEnd = (day.UnavailableCourtWindows ?? Array.Empty<ScheduleCourtAvailabilityBlock>())
            .Where(window => window.AppliesTo(court) && window.StartTime <= current && window.EndTime > current)
            .Select(window => window.EndTime)
            .DefaultIfEmpty(day.DayEnd)
            .Min();
        var next = new[] { nextCourtTime, nextMatchEnd, nextUnavailableEnd }.Min();
        return next > current ? next : day.DayEnd;
    }

    private static int CalculateDayCapacityMinutes(ScheduleDaySettings day, int? refereeCount)
    {
        return ScheduleResourceCalculator.CalculateDayCapacityMinutes(day, refereeCount, slotMinutes: 1);
    }

    private static bool WouldExceedRefereeCapacity(
        ScheduleDaySettings day,
        TimeOnly start,
        TimeOnly end,
        IReadOnlyList<ScheduledAssignment> dailyAssignments,
        int? refereeCount)
    {
        var concurrentLimit = ScheduleResourceCalculator.GetConcurrentMatchLimit(day, refereeCount, start, end);
        var overlappingMatches = dailyAssignments.Count(assignment =>
            assignment.StartTime < end && start < assignment.EndTime);
        return overlappingMatches >= concurrentLimit;
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

    private static IReadOnlyList<EntrantPath> CreateEntrantPaths(DrawParticipant participant)
    {
        return CreatePlayerIdentities(participant)
            .Select(identity => new EntrantPath(identity.IdentityKey, identity, []))
            .ToList();
    }

    private static IReadOnlyList<EntrantPath> CreateEntrantPaths(params DrawParticipant[] participants)
    {
        return participants.SelectMany(CreateEntrantPaths).ToList();
    }

    private static IReadOnlyList<CrossEventPlayerIdentity> CreatePlayerIdentities(DrawParticipant participant)
    {
        if (!string.IsNullOrWhiteSpace(participant.TeamName)
            && string.IsNullOrWhiteSpace(participant.PrimaryName)
            && string.IsNullOrWhiteSpace(participant.PartnerName)
            && string.Equals(participant.DisplayName.Trim(), participant.TeamName!.Trim(), StringComparison.Ordinal))
        {
            return [new CrossEventPlayerIdentity(participant.TeamName!, "", IsTeam: true)];
        }

        var identities = new List<CrossEventPlayerIdentity>();
        if (!string.IsNullOrWhiteSpace(participant.PrimaryName))
        {
            identities.Add(new CrossEventPlayerIdentity(participant.PrimaryName!, participant.PrimaryStudentId ?? ""));
        }

        if (!string.IsNullOrWhiteSpace(participant.PartnerName))
        {
            identities.Add(new CrossEventPlayerIdentity(participant.PartnerName!, participant.PartnerStudentId ?? ""));
        }

        if (identities.Count == 0)
        {
            identities.Add(CrossEventPlayerIdentity.FromName(participant.DisplayName));
        }

        return identities
            .GroupBy(identity => identity.IdentityKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static IReadOnlyList<CrossEventPlayerIdentity> ToPlayerIdentities(IEnumerable<EntrantPath> paths)
    {
        return paths
            .Select(path => path.Identity)
            .GroupBy(identity => identity.IdentityKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
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

        if (settings.MaxMatchesPerEntrantPerDay <= 0)
        {
            throw new DrawValidationException("单名选手每日最多场次必须大于 0。");
        }

        if (settings.RefereeCount is <= 0)
        {
            throw new DrawValidationException("裁判人数必须是大于 0 的整数。");
        }

        if (settings.HasKnockoutTimingSplit)
        {
            if (settings.KnockoutTimingBoundaryEntrants < 2)
            {
                throw new DrawValidationException("赛程分界线至少应为 2 强。");
            }

            if (settings.BeforeBoundaryTiming!.MatchMinutes <= 0)
            {
                throw new DrawValidationException("分界线前单场比赛耗时必须大于 0 分钟。");
            }

            if (settings.BeforeBoundaryTiming.MaxMatchesPerEntrantPerDay <= 0)
            {
                throw new DrawValidationException("分界线前单名选手每日最多场次必须大于 0。");
            }
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

            if ((day.DayEnd - day.DayStart).TotalMinutes < settings.MinimumMatchMinutes)
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
        string? SourceMatchName = null,
        ScheduleMatchDependencyOutcome? SourceOutcome = null,
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
        string MatchId,
        int GroupNumber,
        string GroupName,
        string Phase,
        string MatchName,
        string SideA,
        string SideB,
        string Note,
        bool SameUnit,
        IReadOnlyList<int> DependencyIds,
        IReadOnlyList<ScheduleMatchDependency> Dependencies,
        IReadOnlyList<EntrantPath> EntrantPaths,
        IReadOnlyList<EntrantPath> SideAEntrantPaths,
        IReadOnlyList<EntrantPath> SideBEntrantPaths,
        int? KnockoutEntrantCount = null,
        bool ForceBeforeTimingBoundary = false,
        bool IsChampionshipBracket = false,
        bool IsChampionshipFinal = false,
        bool IsPlacementPlayoff = false);

    private sealed record CandidateMatch(
        UnscheduledMatch Match,
        ResolvedScheduleTiming Timing);

    private sealed record ScheduledAssignment(
        int MatchId,
        DateOnly Date,
        TimeOnly StartTime,
        TimeOnly EndTime,
        IReadOnlyList<EntrantPath> EntrantPaths,
        ScheduleTimingBucket TimingBucket);

    private readonly record struct ResolvedScheduleTiming(
        ScheduleTimingBucket Bucket,
        int MatchMinutes,
        int MaxMatchesPerEntrantPerDay,
        int MaxMatchesPerEntrantPerDayAcrossDay);

    private enum ScheduleTimingBucket
    {
        Default,
        BeforeBoundary
    }

    private sealed record EntrantPath(
        string EntrantKey,
        CrossEventPlayerIdentity Identity,
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
