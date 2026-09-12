namespace BadmintonDraw.Core;

// The single time-free authority for legacy scheduling and v5 graph construction.
internal static class MatchTopologyBuilder
{
    internal static List<UnscheduledMatch> Build(DrawResult draw) => draw.Settings.IsRoundRobin
        ? BuildRoundRobinMatches(draw) : BuildKnockoutMatches(draw);

    private static List<UnscheduledMatch> BuildKnockoutMatches(DrawResult result)
    {
        // Match ids and dependencies created here are the authoritative knockout graph.
        // Scheduling, manual moves, reminders and exports must not infer progression from display text.
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
                forceBeforeTimingBoundary: forceBeforeTimingBoundary || isChampionshipBracket,
                sideAParticipant: first, sideBParticipant: second);
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
                    isChampionshipFinal: isChampionshipBracket && isFinalInThisBracket,
                    sideAParticipant: first.Participant, sideBParticipant: second.Participant);

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
        bool isPlacementPlayoff = false,
        DrawParticipant? sideAParticipant = null,
        DrawParticipant? sideBParticipant = null)
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
            isPlacementPlayoff, sideAParticipant, sideBParticipant));
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
                    secondEntrantPaths,
                    sideAParticipant: first, sideBParticipant: second);
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

    internal static IReadOnlyList<CrossEventPlayerIdentity> ToPlayerIdentities(IEnumerable<EntrantPath> paths)
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

    internal sealed record UnscheduledMatch(
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
        bool IsPlacementPlayoff = false,
        DrawParticipant? SideAParticipant = null,
        DrawParticipant? SideBParticipant = null);

    internal sealed record EntrantPath(
        string EntrantKey,
        CrossEventPlayerIdentity Identity,
        IReadOnlyList<OutcomeCondition> Conditions);

    internal readonly record struct OutcomeCondition(int MatchId, MatchOutcome Outcome);

    internal enum MatchOutcome
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
