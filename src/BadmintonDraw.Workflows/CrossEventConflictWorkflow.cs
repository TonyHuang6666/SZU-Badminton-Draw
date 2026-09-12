using System.Text.RegularExpressions;
using BadmintonDraw.Core;
using BadmintonDraw.Excel;

namespace BadmintonDraw.Workflows;

public sealed partial class CrossEventConflictWorkflow
{
    private const char ItemKeySeparator = '\u001F';

    private readonly ITournamentProgressStore _progressStore;
    private readonly CrossEventConflictDetector _detector = new();
    private readonly CrossEventConflictReportExcelWriter _writer = new();
    private readonly ScheduleWorkflow _scheduleWorkflow = new();

    public CrossEventConflictWorkflow()
        : this(new TournamentProgressStore())
    {
    }

    public CrossEventConflictWorkflow(ITournamentProgressStore progressStore)
    {
        ArgumentNullException.ThrowIfNull(progressStore);
        _progressStore = progressStore;
    }

    public CrossEventConflictReport AnalyzeProgressFiles(
        IEnumerable<string> progressFilePaths,
        int minimumRestMinutes)
    {
        var paths = NormalizeProgressPaths(progressFilePaths);
        var sources = paths.Select(ReadSource).ToList();
        return BuildBoardConflictReport(sources, minimumRestMinutes);
    }


    public CrossEventScheduleBoard LoadScheduleBoard(
        IEnumerable<string> progressFilePaths,
        int minimumRestMinutes)
    {
        var paths = NormalizeProgressPaths(progressFilePaths);
        var sources = paths.Select(ReadSource).ToList();
        return BuildScheduleBoard(sources, minimumRestMinutes, hasUnsavedChanges: false);
    }

    public CrossEventScheduleBoard RebuildScheduleBoard(
        CrossEventScheduleBoard board,
        int minimumRestMinutes,
        CrossEventSchedulingOptions? schedulingOptions = null)
    {
        return BuildScheduleBoard(
            board.Sources,
            minimumRestMinutes,
            board.HasUnsavedChanges,
            schedulingOptions ?? board.SchedulingOptions);
    }

    public CrossEventSchedulingOptions CreateSchedulingOptions(
        CrossEventScheduleBoard board,
        CrossEventSchedulingStrategy strategy)
    {
        return CreateDefaultSchedulingOptions(board, strategy);
    }

    public static ScheduleBoardView BuildScheduleBoardView(CrossEventScheduleBoard board)
    {
        var days = board.Days
            .Select(day => new ScheduleBoardDay(
                day.DayLabel,
                day.StartTime,
                day.EndTime,
                day.Courts,
                day.SlotMinutes,
                day.TimeSlots))
            .ToList();
        var items = board.Items
            .Select(item => new ScheduleBoardItem(
                item.Key,
                item.Key,
                item.Key,
                item.DayLabel,
                item.StartTime,
                item.EndTime,
                item.Court,
                item.Order,
                item.MatchLabel,
                $"{item.TimeRange} · {item.Status}",
                $"{item.SideA}  vs  {item.SideB}",
                item.IsBlockingConflict ? item.ConflictSummary : "",
                item.ConflictSummary,
                item.IsCompleted,
                item.IsBlockingConflict,
                item.EventName))
            .ToList();

        return new ScheduleBoardView(
            ScheduleBoardKind.CrossEvent,
            days,
            items);
    }


    private static IReadOnlyList<string> NormalizeProgressPaths(IEnumerable<string> progressFilePaths)
    {
        var paths = progressFilePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count < 2)
        {
            throw new DrawValidationException("请至少选择两个赛事存档用于跨项目冲突检查。");
        }

        return paths;
    }

    private CrossEventScheduleSource ReadSource(string path)
    {
        var state = _progressStore.Read(path);
        var snapshot = state.Snapshot;
        var eventKind = snapshot.DrawResult.Settings.EventKind;
        var playerLookup = BuildPlayerLookup(snapshot.Participants, eventKind);
        var unresolvedSideCount = 0;
        var matches = snapshot.Schedule.Matches
            .Select(match =>
            {
                var sideA = ResolveSide(match.SideA, state.Results, out var sideAResolved);
                var sideB = ResolveSide(match.SideB, state.Results, out var sideBResolved);
                var sideAPlayerIdentities = ResolvePlayerIdentities(sideA, eventKind, playerLookup);
                var sideBPlayerIdentities = ResolvePlayerIdentities(sideB, eventKind, playerLookup);
                var sideAPlayers = sideAPlayerIdentities.Select(identity => identity.Name).ToList();
                var sideBPlayers = sideBPlayerIdentities.Select(identity => identity.Name).ToList();

                if (!sideAResolved && sideAPlayerIdentities.Count == 0)
                {
                    unresolvedSideCount++;
                }

                if (!sideBResolved && sideBPlayerIdentities.Count == 0)
                {
                    unresolvedSideCount++;
                }

                return new CrossEventScheduledMatch(
                    match.Order,
                    match.DayLabel,
                    match.StartTime,
                    match.EndTime,
                    match.Court,
                    match.GroupName,
                    match.Phase,
                    match.MatchName,
                    sideA ?? match.SideA,
                    sideB ?? match.SideB,
                    sideAPlayers,
                    sideBPlayers,
                    match.GroupNumber,
                    match.Note,
                    match.SameUnit,
                    state.Results.ContainsKey(match.MatchName),
                    match.MatchId,
                    match.Dependencies,
                    sideAPlayerIdentities,
                    sideBPlayerIdentities);
            })
            .ToList();

        return new CrossEventScheduleSource(
            path,
            string.IsNullOrWhiteSpace(snapshot.EventName) ? Path.GetFileNameWithoutExtension(path) : snapshot.EventName,
            path,
            eventKind,
            matches,
            unresolvedSideCount,
            snapshot.Schedule.Settings);
    }

    private CrossEventScheduleBoard BuildScheduleBoard(
        IReadOnlyList<CrossEventScheduleSource> sources,
        int minimumRestMinutes,
        bool hasUnsavedChanges,
        CrossEventSchedulingOptions? schedulingOptions = null)
    {
        var report = BuildBoardConflictReport(sources, minimumRestMinutes);
        var conflicts = BuildBoardConflicts(report);
        var days = BuildBoardDays(sources);
        var items = sources
            .SelectMany(source => source.Matches.Select(match =>
            {
                var key = BuildItemKey(source.SourceId, match.MatchName);
                var hasConflict = conflicts.TryGetValue(key, out var conflict);
                return new CrossEventScheduleBoardItem(
                    key,
                    source.SourceId,
                    source.EventName,
                    source.SourcePath,
                    source.EventKind,
                    match.Order,
                    match.DayLabel,
                    match.StartTime,
                    match.EndTime,
                    match.Court,
                    match.GroupName,
                    match.Phase,
                    match.MatchName,
                    match.SideA,
                    match.SideB,
                    match.Note,
                    match.DurationMinutes,
                    match.IsCompleted,
                    hasConflict ? conflict!.Severity : null,
                    hasConflict ? string.Join("；", conflict!.Messages.Distinct(StringComparer.Ordinal)) : "",
                    match.MatchId,
                    match.Dependencies,
                    match.SideAPlayerIdentities,
                    match.SideBPlayerIdentities);
            }))
            .OrderBy(item => item.DayLabel, StringComparer.Ordinal)
            .ThenBy(item => item.StartTime)
            .ThenBy(item => item.Court, StringComparer.Ordinal)
            .ThenBy(item => item.EventName, StringComparer.Ordinal)
            .ThenBy(item => item.Order)
            .ToList();
        var playerDetails = BuildPlayerDetails(sources, items, report);
        var board = new CrossEventScheduleBoard(
            sources,
            days,
            items,
            playerDetails,
            report,
            minimumRestMinutes,
            hasUnsavedChanges,
            schedulingOptions);
        return board with
        {
            QualityReport = BuildCrossEventQualityReport(
                board,
                movedCount: 0,
                messages: Array.Empty<string>())
        };
    }


    private static IReadOnlyList<CrossEventScheduleBoardDay> BuildBoardDays(IReadOnlyList<CrossEventScheduleSource> sources)
    {
        var dayBuilders = new Dictionary<string, BoardDayBuilder>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            foreach (var day in source.ScheduleSettings?.Days ?? [])
            {
                var builder = GetDayBuilder(dayBuilders, day.DayLabel);
                builder.StartTime = MinTime(builder.StartTime, day.DayStart);
                builder.EndTime = MaxTime(builder.EndTime, day.DayEnd);
                foreach (var court in day.Courts)
                {
                    builder.Courts.Add(court);
                }

                builder.RefereeCapacityWindows.AddRange(day.RefereeCapacityWindows ?? []);
                builder.UnavailableCourtWindows.AddRange(day.UnavailableCourtWindows ?? []);
            }

            foreach (var match in source.Matches)
            {
                var builder = GetDayBuilder(dayBuilders, match.DayLabel);
                builder.StartTime = MinTime(builder.StartTime, match.StartTime);
                builder.EndTime = MaxTime(builder.EndTime, match.EndTime);
                builder.Courts.Add(match.Court);
                builder.Durations.Add(match.DurationMinutes);
            }
        }

        return dayBuilders
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                var start = pair.Value.StartTime ?? new TimeOnly(8, 0);
                var end = pair.Value.EndTime ?? new TimeOnly(22, 0);
                var slotMinutes = NormalizeSlotMinutes(pair.Value.Durations);
                return new CrossEventScheduleBoardDay(
                    pair.Key,
                    start,
                    end,
                    pair.Value.Courts.OrderBy(court => court, StringComparer.OrdinalIgnoreCase).ToList(),
                    slotMinutes,
                    BuildTimeSlots(start, end, slotMinutes),
                    pair.Value.RefereeCapacityWindows
                        .Distinct()
                        .OrderBy(window => window.StartTime)
                        .ThenBy(window => window.EndTime)
                        .ThenBy(window => window.RefereeCount)
                        .ToList(),
                    pair.Value.UnavailableCourtWindows
                        .Distinct()
                        .OrderBy(window => window.StartTime)
                        .ThenBy(window => window.EndTime)
                        .ToList());
            })
            .ToList();
    }


    private static ScheduleSettings BuildScheduleSettings(
        ScheduleSettings? currentSettings,
        IReadOnlyList<ScheduledMatch> matches)
    {
        var dayBuilders = new Dictionary<string, BoardDayBuilder>(StringComparer.Ordinal);
        foreach (var day in currentSettings?.Days ?? [])
        {
            var builder = GetDayBuilder(dayBuilders, day.DayLabel);
            builder.StartTime = MinTime(builder.StartTime, day.DayStart);
            builder.EndTime = MaxTime(builder.EndTime, day.DayEnd);
            foreach (var court in day.Courts)
            {
                builder.Courts.Add(court);
            }

            builder.RefereeCapacityWindows.AddRange(day.RefereeCapacityWindows ?? []);
            builder.UnavailableCourtWindows.AddRange(day.UnavailableCourtWindows ?? []);
        }

        foreach (var match in matches)
        {
            var builder = GetDayBuilder(dayBuilders, match.DayLabel);
            builder.StartTime = MinTime(builder.StartTime, match.StartTime);
            builder.EndTime = MaxTime(builder.EndTime, match.EndTime);
            builder.Courts.Add(match.Court);
        }

        var days = dayBuilders
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ScheduleDaySettings(
                DateOnly.Parse(pair.Key),
                pair.Value.StartTime ?? new TimeOnly(8, 0),
                pair.Value.EndTime ?? new TimeOnly(22, 0),
                pair.Value.Courts.OrderBy(court => court, StringComparer.OrdinalIgnoreCase).ToList(),
                pair.Value.RefereeCapacityWindows
                    .Distinct()
                    .OrderBy(window => window.StartTime)
                    .ThenBy(window => window.EndTime)
                    .ThenBy(window => window.RefereeCount)
                    .ToList(),
                pair.Value.UnavailableCourtWindows
                    .Distinct()
                    .OrderBy(window => window.StartTime)
                    .ThenBy(window => window.EndTime)
                    .ToList()))
            .ToList();
        var matchMinutes = currentSettings?.MatchMinutes
            ?? matches.Select(match => (int)(match.EndTime - match.StartTime).TotalMinutes).DefaultIfEmpty(20).Min();
        return new ScheduleSettings(
            days,
            matchMinutes,
            currentSettings?.MaxMatchesPerEntrantPerDay ?? 2,
            currentSettings?.KnockoutTimingBoundaryEntrants,
            currentSettings?.BeforeBoundaryTiming);
    }

    private static TimeOnly? MinTime(TimeOnly? current, TimeOnly candidate)
    {
        return current.HasValue && current.Value <= candidate ? current.Value : candidate;
    }

    private static TimeOnly? MaxTime(TimeOnly? current, TimeOnly candidate)
    {
        return current.HasValue && current.Value >= candidate ? current.Value : candidate;
    }

    private static int NormalizeSlotMinutes(IReadOnlyCollection<int> durations)
    {
        if (durations.Count == 0)
        {
            return 20;
        }

        return Math.Clamp(durations.Aggregate(GreatestCommonDivisor), 5, 30);
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        left = Math.Abs(left);
        right = Math.Abs(right);
        while (right != 0)
        {
            var remainder = left % right;
            left = right;
            right = remainder;
        }

        return Math.Max(1, left);
    }

    private static IReadOnlyList<TimeOnly> BuildTimeSlots(TimeOnly startTime, TimeOnly endTime, int slotMinutes)
    {
        var slots = new List<TimeOnly>();
        for (var cursor = startTime; cursor < endTime; cursor = cursor.AddMinutes(slotMinutes))
        {
            slots.Add(cursor);
        }

        return slots;
    }

    private static string BuildItemKey(string sourceId, string matchName)
    {
        return $"{sourceId}{ItemKeySeparator}{matchName}";
    }

    private static string BuildSourceMatchIdKey(string sourceId, string matchId)
    {
        return $"{sourceId}{ItemKeySeparator}{matchId}";
    }

    private static string BuildMergedMatchId(string sourceId, string matchId)
    {
        return BuildSourceMatchIdKey(sourceId, matchId);
    }

    private static int SeverityOrder(CrossEventConflictSeverity severity)
    {
        return severity switch
        {
            CrossEventConflictSeverity.Severe => 0,
            CrossEventConflictSeverity.Warning => 1,
            _ => 2
        };
    }

    private static Dictionary<string, IReadOnlyList<CrossEventPlayerIdentity>> BuildPlayerLookup(
        IEnumerable<DrawParticipant> participants,
        EventKind eventKind)
    {
        var lookup = new Dictionary<string, IReadOnlyList<CrossEventPlayerIdentity>>(StringComparer.OrdinalIgnoreCase);
        foreach (var participant in participants)
        {
            var players = GetParticipantPlayerIdentities(participant, eventKind);
            AddLookup(lookup, participant.DisplayName, players);
            AddLookup(lookup, CleanCompetitorText(participant.DisplayName), players);
        }

        return lookup;
    }

    private static IReadOnlyList<CrossEventPlayerIdentity> GetParticipantPlayerIdentities(
        DrawParticipant participant,
        EventKind eventKind)
    {
        if (eventKind == EventKind.Doubles)
        {
            var players = new[]
                {
                    BuildPlayerIdentity(participant.PrimaryName, participant.PrimaryStudentId),
                    BuildPlayerIdentity(participant.PartnerName, participant.PartnerStudentId)
                }
                .Where(identity => identity is not null)
                .Select(identity => identity!)
                .GroupBy(identity => identity.IdentityKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            return players.Count > 0
                ? players
                : SplitCompetitorIdentities(participant.DisplayName, eventKind);
        }

        if (eventKind == EventKind.Singles)
        {
            var name = !string.IsNullOrWhiteSpace(participant.PrimaryName)
                ? participant.PrimaryName!
                : participant.DisplayName;
            return [new CrossEventPlayerIdentity(name.Trim(), participant.PrimaryStudentId ?? "")];
        }

        var teamName = !string.IsNullOrWhiteSpace(participant.TeamName)
            ? participant.TeamName!
            : participant.DisplayName;
        return [new CrossEventPlayerIdentity(teamName.Trim(), "", IsTeam: true)];
    }

    private static void AddLookup(
        IDictionary<string, IReadOnlyList<CrossEventPlayerIdentity>> lookup,
        string value,
        IReadOnlyList<CrossEventPlayerIdentity> players)
    {
        var key = NormalizeCompetitorKey(value);
        if (!string.IsNullOrWhiteSpace(key) && !lookup.ContainsKey(key))
        {
            lookup.Add(key, players);
        }
    }

    private static IReadOnlyList<CrossEventPlayerIdentity> ResolvePlayerIdentities(
        string? side,
        EventKind eventKind,
        IReadOnlyDictionary<string, IReadOnlyList<CrossEventPlayerIdentity>> playerLookup)
    {
        if (string.IsNullOrWhiteSpace(side))
        {
            return [];
        }

        var key = NormalizeCompetitorKey(side);
        if (playerLookup.TryGetValue(key, out var players))
        {
            return players;
        }

        return SplitCompetitorIdentities(side, eventKind);
    }

    private static string? ResolveSide(
        string side,
        IReadOnlyDictionary<string, MatchRecordResult> results,
        out bool isKnown)
    {
        if (side.EndsWith("胜者", StringComparison.Ordinal))
        {
            var sourceMatchName = side[..^"胜者".Length];
            if (results.TryGetValue(sourceMatchName, out var result))
            {
                isKnown = true;
                return result.Winner;
            }

            isKnown = false;
            return null;
        }

        if (side.EndsWith("负者", StringComparison.Ordinal))
        {
            var sourceMatchName = side[..^"负者".Length];
            if (results.TryGetValue(sourceMatchName, out var result))
            {
                isKnown = true;
                return result.Loser;
            }

            isKnown = false;
            return null;
        }

        isKnown = true;
        return side;
    }

    private static CrossEventPlayerIdentity? BuildPlayerIdentity(string? name, string? studentId)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new CrossEventPlayerIdentity(name.Trim(), studentId ?? "");
    }

    private static IReadOnlyList<CrossEventPlayerIdentity> SplitCompetitorIdentities(string value, EventKind eventKind)
    {
        var cleaned = CleanCompetitorText(value);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return [];
        }

        if (eventKind == EventKind.Team)
        {
            return [CrossEventPlayerIdentity.FromName(cleaned, isTeam: true)];
        }

        if (eventKind != EventKind.Doubles)
        {
            return [CrossEventPlayerIdentity.FromName(cleaned)];
        }

        var parts = Regex.Split(cleaned, @"[\s,，、/／&]+")
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return parts.Count > 0
            ? parts.Select(part => CrossEventPlayerIdentity.FromName(part)).ToList()
            : [CrossEventPlayerIdentity.FromName(cleaned)];
    }

    private static string NormalizeCompetitorKey(string value)
    {
        return string.Concat(CleanCompetitorText(value).Where(character => !char.IsWhiteSpace(character)));
    }

    private static string NormalizePlayerName(string value)
    {
        return string.Concat(value.Trim().Where(character => !char.IsWhiteSpace(character)));
    }

    private static string BuildPlayerIssueKey(string normalizedPlayerName, string itemKey)
    {
        return $"{normalizedPlayerName}{ItemKeySeparator}{itemKey}";
    }

    private static string CleanCompetitorText(string value)
    {
        var text = Regex.Replace(value.Trim(), @"\s+", " ");
        var optionMatch = Regex.Match(text, @"^[ABab]\s*[【\[](?<name>.+)[】\]]$");
        if (optionMatch.Success)
        {
            text = optionMatch.Groups["name"].Value.Trim();
        }

        if (text.Length >= 2
            && ((text[0] == '[' && text[^1] == ']')
                || (text[0] == '【' && text[^1] == '】')))
        {
            text = text[1..^1].Trim();
        }

        return text;
    }

    private sealed class BoardDayBuilder
    {
        public TimeOnly? StartTime { get; set; }

        public TimeOnly? EndTime { get; set; }

        public HashSet<string> Courts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<int> Durations { get; } = [];

        public List<ScheduleRefereeCapacityWindow> RefereeCapacityWindows { get; } = [];

        public List<ScheduleCourtAvailabilityBlock> UnavailableCourtWindows { get; } = [];
    }

    private sealed class BoardConflictAccumulator(CrossEventConflictSeverity severity)
    {
        public CrossEventConflictSeverity Severity { get; set; } = severity;

        public List<string> Messages { get; } = [];
    }

    private sealed record GlobalSchedulingContext(
        CrossEventSchedulingOptions Options,
        string FinalDayLabel,
        IReadOnlyDictionary<string, int> DayIndex,
        IReadOnlyDictionary<string, int> DayCapacityMinutes,
        IReadOnlyDictionary<string, CrossEventDayLoadTarget> DayLoadTargets,
        IReadOnlyList<CrossEventStageWaveTarget> StageWaveTargets,
        IReadOnlyDictionary<FinalDayRuleKey, CrossEventFinalDayPolicy> FinalDayRules,
        int OriginalPositionWeight,
        int CrossDayMoveWeight,
        double TargetLoadWeight,
        double WarningLoadWeight,
        long EarlyStageWavePenalty,
        long LateStageWavePenalty);

    private sealed record FinalDayRuleKey(
        string EventName,
        CrossEventFinalDayMatchCategory Category);

    private sealed class GlobalScheduleEntry(
        CrossEventScheduleSource source,
        CrossEventScheduledMatch match,
        string key,
        IReadOnlyList<string> playerKeys)
    {
        public CrossEventScheduleSource Source { get; } = source;

        public CrossEventScheduledMatch Match { get; } = match;

        public string Key { get; } = key;

        public IReadOnlyList<string> PlayerKeys { get; } = playerKeys;

        public List<string> DependencyKeys { get; } = [];

        public List<string> DependentKeys { get; } = [];
    }

    private sealed record GlobalSchedulePlacement(
        string DayLabel,
        TimeOnly StartTime,
        TimeOnly EndTime,
        string Court);

    private sealed record GlobalCascadeEntry(
        int Depth,
        GlobalScheduleEntry Entry);

    private sealed record PlayerAppearanceBuilder(
        string PlayerName,
        string NormalizedPlayerName,
        CrossEventPlayerScheduleAppearance Appearance);

    private sealed record CrossEventLoadForecastContribution(
        CrossEventScheduleSource Source,
        PlayerDailyLoadForecast Forecast,
        CrossEventPlayerAppearance Anchor);
}

public sealed record CrossEventConflictExportResult(
    string OutputPath,
    CrossEventConflictReport Report);

public sealed record CrossEventMergedMaterialsExportResult(
    string OutputDirectory,
    IReadOnlyList<string> OutputPaths,
    SchedulePlan Schedule,
    IReadOnlyList<string> DayLabels);
