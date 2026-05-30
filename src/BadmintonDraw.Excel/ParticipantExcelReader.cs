using BadmintonDraw.Core;
using ClosedXML.Excel;

namespace BadmintonDraw.Excel;

public sealed class ParticipantExcelReader
{
    private static readonly string[] PrimaryNameHeaders = ["姓名"];
    private static readonly string[] PartnerNameHeaders = ["搭档姓名", "搭档"];
    private static readonly string[] TeamNameHeaders = ["学院/学部", "队伍", "学院", "学部"];
    private static readonly string[] PartnerTeamNameHeaders = ["搭档学院/学部", "搭档学院", "搭档学部"];
    private static readonly string[] SeedFlagHeaders = ["是否种子"];
    private static readonly string[] SeedRankHeaders = ["种子序号"];
    private static readonly string[] NoteHeaders = ["备注"];
    private static readonly string[] SeedTrueValues = ["是", "种子", "true", "yes", "y", "1"];
    private static readonly string[] SeedFalseValues = ["否", "不是", "非种子", "false", "no", "n", "0"];
    private const string UnsupportedFileMessage = "当前仅支持 .xlsx 格式的参赛名单，请选择由模板生成的 Excel 文件。";
    private const string InvalidWorkbookMessage = "无法读取参赛名单，请确认文件未损坏、未加密且是有效的 .xlsx Excel 文件。";

    public EventKind DetectEventKind(string filePath, EventKind? preferredEventKind = null)
    {
        using var workbookHandle = OpenWorkbook(filePath);
        var worksheet = workbookHandle.Workbook.Worksheets.FirstOrDefault()
            ?? throw new ExcelImportException("参赛名单文件中没有工作表。");
        var headerRow = worksheet.FirstRowUsed()
            ?? throw new ExcelImportException("参赛名单文件为空。");
        var lastRow = worksheet.LastRowUsed()
            ?? throw new ExcelImportException("参赛名单文件为空。");
        var headerMap = BuildHeaderMap(headerRow);
        var hasPrimaryName = false;
        var hasPartnerName = false;
        var hasTeamName = false;

        for (var rowNumber = headerRow.RowNumber() + 1; rowNumber <= lastRow.RowNumber(); rowNumber++)
        {
            var row = worksheet.Row(rowNumber);
            if (IsBlankRow(row, headerMap.Values))
            {
                continue;
            }

            hasPrimaryName |= !string.IsNullOrWhiteSpace(GetCell(row, headerMap, PrimaryNameHeaders));
            hasPartnerName |= !string.IsNullOrWhiteSpace(GetCell(row, headerMap, PartnerNameHeaders));
            hasTeamName |= !string.IsNullOrWhiteSpace(GetCell(row, headerMap, TeamNameHeaders));
        }

        if (hasPartnerName)
        {
            return EventKind.Doubles;
        }

        if (hasTeamName && hasPrimaryName)
        {
            return preferredEventKind == EventKind.Team ? EventKind.Team : EventKind.Singles;
        }

        if (hasPrimaryName)
        {
            return EventKind.Singles;
        }

        return hasTeamName ? EventKind.Team : EventKind.Singles;
    }

    public bool HasPartnerData(string filePath)
    {
        return DetectEventKind(filePath) == EventKind.Doubles;
    }

    public IReadOnlyList<DrawParticipant> ReadParticipants(string filePath, EventKind eventKind)
    {
        return ReadParticipantsWithWarnings(filePath, eventKind).Participants;
    }

    public ParticipantImportResult ReadParticipantsWithWarnings(string filePath, EventKind eventKind)
    {
        using var workbookHandle = OpenWorkbook(filePath);
        var worksheet = workbookHandle.Workbook.Worksheets.FirstOrDefault()
            ?? throw new ExcelImportException("参赛名单文件中没有工作表。");
        var headerRow = worksheet.FirstRowUsed()
            ?? throw new ExcelImportException("参赛名单文件为空。");
        var lastRow = worksheet.LastRowUsed()
            ?? throw new ExcelImportException("参赛名单文件为空。");
        var headerMap = BuildHeaderMap(headerRow);
        var participantRows = new List<ParticipantRow>();

        for (var rowNumber = headerRow.RowNumber() + 1; rowNumber <= lastRow.RowNumber(); rowNumber++)
        {
            var row = worksheet.Row(rowNumber);
            if (IsBlankRow(row, headerMap.Values))
            {
                continue;
            }

            var primaryName = GetCell(row, headerMap, PrimaryNameHeaders);
            var partnerName = GetCell(row, headerMap, PartnerNameHeaders);
            var teamName = GetCell(row, headerMap, TeamNameHeaders);
            var partnerTeamName = GetCell(row, headerMap, PartnerTeamNameHeaders);
            var note = GetCell(row, headerMap, NoteHeaders);
            var seedRank = ParseSeedRank(GetCell(row, headerMap, SeedRankHeaders), rowNumber);
            var seedFlag = ParseSeedFlag(GetCell(row, headerMap, SeedFlagHeaders), rowNumber);
            if (seedRank.HasValue && seedFlag == false)
            {
                throw new ExcelImportException($"第 {rowNumber} 行填写了“种子序号”，但“是否种子”为否，请保持一致。");
            }

            var isSeed = seedRank.HasValue || seedFlag == true;
            var displayName = BuildDisplayName(eventKind, primaryName, partnerName, teamName, rowNumber);

            participantRows.Add(new ParticipantRow(
                new DrawParticipant(
                    displayName,
                    isSeed,
                    seedRank,
                    primaryName,
                    partnerName,
                    teamName,
                    note,
                    partnerTeamName),
                rowNumber));
        }

        ValidateImportedParticipantErrors(participantRows);
        return new ParticipantImportResult(
            participantRows.Select(row => row.Participant).ToList(),
            BuildImportWarnings(participantRows, eventKind));
    }

    private static WorkbookHandle OpenWorkbook(string filePath)
    {
        EnsureSupportedWorkbookPath(filePath);

        try
        {
            var workbookStream = new MemoryStream(File.ReadAllBytes(filePath), writable: false);
            try
            {
                return new WorkbookHandle(new XLWorkbook(workbookStream), workbookStream);
            }
            catch
            {
                workbookStream.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (IsWorkbookOpenException(ex))
        {
            throw new ExcelImportException(InvalidWorkbookMessage);
        }
    }

    private static void EnsureSupportedWorkbookPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            throw new ExcelImportException("找不到参赛名单文件。");
        }

        if (!string.Equals(Path.GetExtension(filePath), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ExcelImportException(UnsupportedFileMessage);
        }
    }

    private static bool IsWorkbookOpenException(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or FormatException
            or InvalidOperationException
            or NotSupportedException
            || exception.GetType().Namespace?.StartsWith("ClosedXML", StringComparison.Ordinal) == true
            || exception.GetType().Namespace?.StartsWith("DocumentFormat.OpenXml", StringComparison.Ordinal) == true;
    }

    private sealed class WorkbookHandle : IDisposable
    {
        private readonly Stream _stream;

        public WorkbookHandle(XLWorkbook workbook, Stream stream)
        {
            Workbook = workbook;
            _stream = stream;
        }

        public XLWorkbook Workbook { get; }

        public void Dispose()
        {
            Workbook.Dispose();
            _stream.Dispose();
        }
    }

    private sealed record ParticipantRow(DrawParticipant Participant, int RowNumber);

    private sealed record PlayerNameEntry(string DisplayName, string NormalizedName, int RowNumber, string ColumnName);

    private static void ValidateImportedParticipantErrors(IReadOnlyList<ParticipantRow> participantRows)
    {
        ValidateSeedRanks(participantRows);
    }

    private static void ValidateSeedRanks(IReadOnlyList<ParticipantRow> participantRows)
    {
        var duplicateSeedRank = participantRows
            .Where(row => row.Participant.SeedRank.HasValue)
            .GroupBy(row => row.Participant.SeedRank!.Value)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateSeedRank is not null)
        {
            var rowNumbers = string.Join("、", duplicateSeedRank.Select(row => $"第 {row.RowNumber} 行"));
            throw new ExcelImportException($"种子序号 {duplicateSeedRank.Key} 重复：{rowNumbers}。");
        }

        var overflowSeedRank = participantRows
            .FirstOrDefault(row => row.Participant.SeedRank.HasValue
                && row.Participant.SeedRank.Value > participantRows.Count);
        if (overflowSeedRank is not null)
        {
            throw new ExcelImportException(
                $"第 {overflowSeedRank.RowNumber} 行“种子序号”为 {overflowSeedRank.Participant.SeedRank}，不能大于参赛单位总数 {participantRows.Count}。");
        }
    }

    private static IReadOnlyList<ParticipantImportWarning> BuildImportWarnings(
        IReadOnlyList<ParticipantRow> participantRows,
        EventKind eventKind)
    {
        return BuildDuplicatePlayerWarnings(participantRows, eventKind)
            .Concat(BuildUnrankedSeedWarnings(participantRows))
            .ToList();
    }

    private static IReadOnlyList<ParticipantImportWarning> BuildDuplicatePlayerWarnings(
        IReadOnlyList<ParticipantRow> participantRows,
        EventKind eventKind)
    {
        if (eventKind == EventKind.Team)
        {
            return [];
        }

        var playerNames = new List<PlayerNameEntry>();
        foreach (var row in participantRows)
        {
            AddPlayerName(playerNames, row.Participant.PrimaryName, row.RowNumber, "姓名");
            if (eventKind == EventKind.Doubles)
            {
                AddPlayerName(playerNames, row.Participant.PartnerName, row.RowNumber, "搭档");
            }
        }

        return playerNames
            .GroupBy(entry => entry.NormalizedName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group =>
            {
                var first = group.First();
                var positions = string.Join("、", group.Select(entry => $"第 {entry.RowNumber} 行“{entry.ColumnName}”"));
                return new ParticipantImportWarning(
                    ParticipantImportWarningKind.DuplicatePlayerName,
                    $"同名选手：{first.DisplayName}",
                    $"{first.DisplayName}（{positions}）");
            })
            .ToList();
    }

    private static IReadOnlyList<ParticipantImportWarning> BuildUnrankedSeedWarnings(
        IReadOnlyList<ParticipantRow> participantRows)
    {
        return participantRows
            .Where(row => row.Participant.IsSeed && !row.Participant.SeedRank.HasValue)
            .Select(row => new ParticipantImportWarning(
                ParticipantImportWarningKind.UnrankedSeed,
                $"第 {row.RowNumber} 行种子未填写序号",
                $"第 {row.RowNumber} 行“{row.Participant.DisplayName}”标记为种子，但未填写种子序号。"))
            .ToList();
    }

    private static void AddPlayerName(ICollection<PlayerNameEntry> playerNames, string? name, int rowNumber, string columnName)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var normalizedName = NormalizePlayerName(name);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return;
        }

        playerNames.Add(new PlayerNameEntry(name.Trim(), normalizedName, rowNumber, columnName));
    }

    private static Dictionary<string, int> BuildHeaderMap(IXLRow headerRow)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var cell in headerRow.CellsUsed())
        {
            var header = NormalizeHeader(cell.GetString());
            if (!string.IsNullOrWhiteSpace(header) && !map.ContainsKey(header))
            {
                map.Add(header, cell.Address.ColumnNumber);
            }
        }

        if (!HasAnyHeader(map, PrimaryNameHeaders) && !HasAnyHeader(map, TeamNameHeaders))
        {
            throw new ExcelImportException("参赛名单至少需要包含“姓名”或“学院/学部”列。");
        }

        return map;
    }

    private static string BuildDisplayName(
        EventKind eventKind,
        string primaryName,
        string partnerName,
        string teamName,
        int rowNumber)
    {
        return eventKind switch
        {
            EventKind.Doubles => BuildDoublesName(primaryName, partnerName, rowNumber),
            EventKind.Team => BuildTeamName(teamName, primaryName, rowNumber),
            _ => RequireValue(primaryName, "姓名", rowNumber)
        };
    }

    private static string BuildDoublesName(string primaryName, string partnerName, int rowNumber)
    {
        var first = RequireValue(primaryName, "姓名", rowNumber);
        var second = RequireValue(partnerName, "搭档姓名", rowNumber);
        return $"[{first} {second}]";
    }

    private static string BuildTeamName(string teamName, string primaryName, int rowNumber)
    {
        var value = !string.IsNullOrWhiteSpace(teamName) ? teamName : primaryName;
        return RequireValue(value, "学院/学部", rowNumber);
    }

    private static string RequireValue(string value, string header, int rowNumber)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ExcelImportException($"第 {rowNumber} 行缺少“{header}”。");
        }

        return value.Trim();
    }

    private static bool IsBlankRow(IXLRow row, IEnumerable<int> columns)
    {
        return columns.All(column => string.IsNullOrWhiteSpace(row.Cell(column).GetString()));
    }

    private static string GetCell(IXLRow row, IReadOnlyDictionary<string, int> headerMap, IReadOnlyList<string> headers)
    {
        foreach (var header in headers)
        {
            if (headerMap.TryGetValue(NormalizeHeader(header), out var column))
            {
                return row.Cell(column).GetString().Trim();
            }
        }

        return string.Empty;
    }

    private static bool HasAnyHeader(IReadOnlyDictionary<string, int> headerMap, IReadOnlyList<string> headers)
    {
        return headers.Any(header => headerMap.ContainsKey(NormalizeHeader(header)));
    }

    private static string NormalizeHeader(string header)
    {
        return header.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    private static int? ParseSeedRank(string value, int rowNumber)
    {
        var trimmedValue = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmedValue))
        {
            return null;
        }

        if (!int.TryParse(trimmedValue, out var rank) || rank <= 0)
        {
            throw new ExcelImportException($"第 {rowNumber} 行“种子序号”必须填写大于 0 的整数，或留空。");
        }

        return rank;
    }

    private static bool? ParseSeedFlag(string value, int rowNumber)
    {
        var trimmedValue = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmedValue))
        {
            return null;
        }

        if (SeedTrueValues.Contains(trimmedValue, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (SeedFalseValues.Contains(trimmedValue, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new ExcelImportException($"第 {rowNumber} 行“是否种子”只能填写“是”或“否”，也可以留空。");
    }

    private static string NormalizePlayerName(string name)
    {
        return string.Concat(name.Where(character => !char.IsWhiteSpace(character)));
    }
}
