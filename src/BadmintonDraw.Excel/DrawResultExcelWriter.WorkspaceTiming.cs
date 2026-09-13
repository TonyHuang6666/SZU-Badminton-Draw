using System.Globalization;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Tournaments;
using ClosedXML.Excel;

namespace BadmintonDraw.Excel;

public sealed partial class DrawResultExcelWriter
{
    /// <summary>Draw geometry with timing from the selected project's actual graph and global placements.</summary>
    public void WriteTimed(string outputPath, WorkspaceScheduleExportContext context, Guid projectId,
        DrawExportContext exportContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(exportContext);
        var workspace = context.Workspace;
        if (!context.Projects.TryGetValue(projectId, out var project) || project.Draw?.ConfirmedAt is null ||
            project.Roster is null || project.MatchGraph is null ||
            exportContext.WorkspaceId != workspace.Id || exportContext.WorkspaceName != workspace.Name ||
            exportContext.SourceRevision != workspace.Revision || exportContext.ProjectId != project.Id ||
            exportContext.ProjectName != project.DisplayName || exportContext.ConfirmedAt != project.Draw.ConfirmedAt ||
            exportContext.SourceFileName != project.Roster.SourceFileName || exportContext.SourceFileHash != project.Roster.ContentHash ||
            exportContext.RandomSeed != project.Draw.Result.Audit.RandomSeed ||
            exportContext.ParticipantHash != project.Draw.Result.Audit.InputHash ||
            exportContext.ExportAuditId == Guid.Empty || exportContext.ExportedAt == default)
            throw new WorkspaceValidationException("export.context", "时间对阵图的来源与已确认项目快照不一致。");

        var timing = new WorkspaceTiming(context, project);
        using var workbook = new XLWorkbook();
        var draw = project.Draw.Result;
        if (draw.Settings.IsKnockout) WriteUnifiedBracketSheet(workbook, draw, null, timing);
        else WriteRoundRobinSheet(workbook, draw, null, timing);
        timing.ValidateCoverage();
        WriteAuditSheet(workbook, "抽签设置与审计信息", draw, exportContext);
        WriteRosterSheet(workbook, "当前名单", project.Roster.Participants);
        WriteWorkspaceContext(workbook.Worksheet("对阵表"), exportContext, draw.Settings.IsKnockout);
        FormatWorkspaceRoster(workbook.Worksheet("当前名单"), project.Roster.Participants.Count);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        workbook.SaveAs(outputPath);
    }

    private sealed class WorkspaceTiming
    {
        private readonly WorkspaceScheduleExportContext context;
        private readonly TournamentProject project;
        private readonly Dictionary<Guid, MatchNode> nodes;
        private readonly Dictionary<string, MatchNode> knockoutNames = new(StringComparer.Ordinal);
        private readonly HashSet<(Guid MatchId, string View)> covered = [];
        private readonly HashSet<(int Row, int Column)> cells = [];

        internal WorkspaceTiming(WorkspaceScheduleExportContext context, TournamentProject project)
        {
            this.context = context; this.project = project;
            nodes = project.MatchGraph!.Matches.Where(n => n.IsPlayable).ToDictionary(n => n.Id);
            if (nodes.Count == 0) throw LayoutError("所选项目没有可标注的比赛。");

            // A structurally valid graph alone need not be the graph of this draw. Rebuild topology only,
            // never a schedule, to verify the renderer's source before associating any layout with a GUID.
            // Expected duration is a scheduling input and deliberately not part of this layout comparison.
            var expected = MatchGraphFactory.Create(project.Id, project.Draw!.Result).Matches;
            if (expected.Count != nodes.Count || expected.Any(n => !nodes.TryGetValue(n.Id, out var actual) ||
                actual.OriginalMatchId != n.OriginalMatchId || actual.Order != n.Order ||
                actual.GroupNumber != n.GroupNumber || actual.Phase != n.Phase || actual.DisplayName != n.DisplayName ||
                actual.IsPlacementPlayoff != n.IsPlacementPlayoff || actual.SameUnit != n.SameUnit ||
                !actual.Dependencies.SequenceEqual(n.Dependencies) || !SameSource(actual.SideA, n.SideA) || !SameSource(actual.SideB, n.SideB)))
                throw LayoutError("比赛图与已确认抽签的签位、参赛身份或晋级来源不一致。");

            if (project.Draw.Result.Settings.IsKnockout)
                foreach (var node in nodes.Values)
                    if (!knockoutNames.TryAdd(node.DisplayName, node))
                        throw LayoutError("淘汰赛签位对应了重复的比赛标签。");
        }

        private static bool SameSource(EntrantSource left, EntrantSource right) => (left, right) switch
        {
            (EntrantSource.Participant a, EntrantSource.Participant b) => a.IdentityKey == b.IdentityKey &&
                a.DisplayName == b.DisplayName && a.Players.SequenceEqual(b.Players),
            (EntrantSource.WinnerOf a, EntrantSource.WinnerOf b) => a.MatchId == b.MatchId,
            (EntrantSource.LoserOf a, EntrantSource.LoserOf b) => a.MatchId == b.MatchId,
            _ => false
        };

        internal void AnnotateKnockoutSlot(IXLWorksheet sheet, int row, int column, string? canonicalName)
        {
            if (canonicalName is null || !knockoutNames.TryGetValue(canonicalName, out var node))
                throw LayoutError($"找不到淘汰赛签位对应的唯一比赛：{canonicalName}");
            Annotate(sheet, row, column, node.Id, "knockout");
        }

        internal void AnnotateKnockout(IXLWorksheet sheet, DrawResult draw, IReadOnlyList<BracketSlot> slots,
            IReadOnlyList<int> columns, int? qualifierRoundCount, bool groupedChampion)
        {
            for (var i = 0; i < slots.Count; i++)
                if (slots[i].IsPlayIn)
                    AnnotateKnockoutSlot(sheet, BracketStartRow + i * SlotRowGap, PlayInWinnerColumn, slots[i].PlayInMatchName);

            if (qualifierRoundCount is null)
            {
                var groupName = BuildScheduleGroupName(draw.Groups.Single().Number);
                for (var round = 1; round < columns.Count; round++)
                {
                    var entrants = slots.Count / (1 << (round - 1));
                    var phase = BuildScheduleKnockoutPhase(entrants, "");
                    for (var match = 0; match < entrants / 2; match++)
                        AnnotateKnockoutSlot(sheet, GetFutureRoundRow(slots.Count, round, match), columns[round],
                            $"{groupName}{phase}第{match + 1}场");
                }
                return;
            }

            var finalIndex = qualifierRoundCount.Value - 1;
            var phases = BracketStageLabels.BuildQualifierMatchPhases(draw.Groups
                .Select(g => slots.Count(s => s.GroupNumber == g.Number)).ToArray());
            foreach (var group in draw.Groups)
            {
                var firstIndex = slots.ToList().FindIndex(s => s.GroupNumber == group.Number);
                var count = slots.Count(s => s.GroupNumber == group.Number);
                if (firstIndex < 0 || !IsPowerOfTwo(count)) throw LayoutError("分组签位不可唯一映射。");
                var startRow = BracketStartRow + firstIndex * SlotRowGap;
                for (var round = 1; round <= (int)Math.Log2(count); round++)
                {
                    var survivors = count / (1 << round);
                    var phase = phases[round - 1];
                    for (var match = 0; match < survivors; match++)
                    {
                        // A shorter group reaches the shared advance column directly; its real final
                        // is not the nonexistent intermediate cell used by the legacy timing path.
                        var row = survivors == 1 ? GetGroupCenterRow(startRow, count) : GetGroupRoundRow(startRow, round, match);
                        var column = survivors == 1 ? columns[finalIndex] : columns[round];
                        AnnotateKnockoutSlot(sheet, row, column, $"{BuildScheduleGroupName(group.Number)}{phase}第{match + 1}场");
                    }
                }
            }

            if (!groupedChampion) return;
            var sourceRows = draw.Groups.Select(g => GetGroupQualifierRow(draw, slots, g.Number)).ToList();
            var championPhases = BracketStageLabels.BuildChampionMatchPhases(draw.Groups.Count);
            for (var columnIndex = finalIndex + 1; columnIndex < columns.Count; columnIndex++)
            {
                var phase = championPhases[columnIndex - finalIndex - 1];
                var targetRows = new List<int>();
                for (var match = 0; match + 1 < sourceRows.Count; match += 2)
                {
                    var row = (sourceRows[match] + sourceRows[match + 1]) / 2;
                    targetRows.Add(row);
                    AnnotateKnockoutSlot(sheet, row, columns[columnIndex], $"{BuildScheduleGroupName(0)}{phase}第{match / 2 + 1}场");
                }
                sourceRows = targetRows;
            }
        }

        internal IReadOnlyList<RoundRobinMatch> RoundRobinSchedule(DrawGroup group)
        {
            var indices = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < group.Participants.Count; i++)
                if (!indices.TryAdd(ProjectEntrantIdentity.Create(project.Discipline, group.Participants[i]).IdentityKey, i))
                    throw LayoutError("循环赛矩阵中有重复参赛身份。");
            var pairs = new HashSet<string>(StringComparer.Ordinal);
            var schedule = new List<RoundRobinMatch>();
            foreach (var node in nodes.Values.Where(n => n.GroupNumber == group.Number).OrderBy(n => n.Order))
            {
                if (node.SideA is not EntrantSource.Participant a || node.SideB is not EntrantSource.Participant b ||
                    !indices.TryGetValue(a.IdentityKey, out var first) || !indices.TryGetValue(b.IdentityKey, out var second) ||
                    first == second || !pairs.Add(BuildPairKey(first, second)))
                    throw LayoutError("循环赛比赛不能唯一映射到真实参赛方矩阵。");
                schedule.Add(new(node.Order, 0, 0, first, second, node.SameUnit, node.Id, node.Phase));
            }
            if (schedule.Count != group.Participants.Count * (group.Participants.Count - 1) / 2)
                throw LayoutError("循环赛矩阵没有覆盖每一对真实参赛方。");
            return schedule;
        }

        internal void AnnotateRoundRobinSlot(IXLWorksheet sheet, int row, int column, RoundRobinMatch match, string view)
        {
            if (match.MatchId is not { } id) throw LayoutError("循环赛标注缺少比赛标识。");
            Annotate(sheet, row, column, id, view);
        }

        private void Annotate(IXLWorksheet sheet, int row, int column, Guid matchId, string view)
        {
            var cell = sheet.Cell(row, column);
            var target = cell.MergedRange()?.FirstCell() ?? cell;
            if (!nodes.ContainsKey(matchId) || !covered.Add((matchId, view)) ||
                !cells.Add((target.Address.RowNumber, target.Address.ColumnNumber)) || target.IsEmpty())
                throw LayoutError("比赛标注缺失、重复或覆盖了其他签位。");
            var placement = context.Placements[new(project.Id, matchId)];
            AppendScheduleAnnotation(sheet, row, column,
                $"{placement.DayLabel} {TimeText(placement.StartTime)}-{TimeText(placement.EndTime)}\n{placement.Court}");
        }

        internal void ValidateCoverage()
        {
            var views = project.Draw!.Result.Settings.IsKnockout ? new[] { "knockout" } : new[] { "matrix", "list" };
            if (covered.Count != nodes.Count * views.Length || nodes.Keys.Any(id => views.Any(view => !covered.Contains((id, view)))))
                throw LayoutError("时间对阵图没有完整覆盖所选项目的全部比赛。");
        }

        private static string TimeText(TimeOnly value) => value.Ticks % TimeSpan.TicksPerMinute == 0
            ? value.ToString("HH:mm", CultureInfo.InvariantCulture)
            : value.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
        private static WorkspaceValidationException LayoutError(string message) => new("export.layout", message);
    }
}
