using BadmintonDraw.Core;
using ClosedXML.Excel;

namespace BadmintonDraw.Excel;

public sealed class ParticipantExcelReader
{
    private static readonly string[] SeedTrueValues = ["是", "种子", "true", "yes", "y", "1"];

    public EventKind DetectEventKind(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new ExcelImportException("找不到参赛名单文件。");
        }

        using var workbook = new XLWorkbook(filePath);
        var worksheet = workbook.Worksheets.FirstOrDefault()
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

            hasPrimaryName |= !string.IsNullOrWhiteSpace(GetCell(row, headerMap, "姓名"));
            hasPartnerName |= !string.IsNullOrWhiteSpace(GetCell(row, headerMap, "搭档"));
            hasTeamName |= !string.IsNullOrWhiteSpace(GetCell(row, headerMap, "队伍"));
        }

        if (hasPartnerName)
        {
            return EventKind.Doubles;
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
        if (!File.Exists(filePath))
        {
            throw new ExcelImportException("找不到参赛名单文件。");
        }

        using var workbook = new XLWorkbook(filePath);
        var worksheet = workbook.Worksheets.FirstOrDefault()
            ?? throw new ExcelImportException("参赛名单文件中没有工作表。");
        var headerRow = worksheet.FirstRowUsed()
            ?? throw new ExcelImportException("参赛名单文件为空。");
        var lastRow = worksheet.LastRowUsed()
            ?? throw new ExcelImportException("参赛名单文件为空。");
        var headerMap = BuildHeaderMap(headerRow);
        var participants = new List<DrawParticipant>();

        for (var rowNumber = headerRow.RowNumber() + 1; rowNumber <= lastRow.RowNumber(); rowNumber++)
        {
            var row = worksheet.Row(rowNumber);
            if (IsBlankRow(row, headerMap.Values))
            {
                continue;
            }

            var primaryName = GetCell(row, headerMap, "姓名");
            var partnerName = GetCell(row, headerMap, "搭档");
            var teamName = GetCell(row, headerMap, "队伍");
            var note = GetCell(row, headerMap, "备注");
            var seedRank = ParseSeedRank(GetCell(row, headerMap, "种子序号"));
            var isSeed = seedRank.HasValue || ParseSeedFlag(GetCell(row, headerMap, "是否种子"));
            var displayName = BuildDisplayName(eventKind, primaryName, partnerName, teamName, rowNumber);

            participants.Add(new DrawParticipant(
                displayName,
                isSeed,
                seedRank,
                primaryName,
                partnerName,
                teamName,
                note));
        }

        return participants;
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

        if (!map.ContainsKey("姓名") && !map.ContainsKey("队伍"))
        {
            throw new ExcelImportException("参赛名单至少需要包含“姓名”或“队伍”列。");
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
        var second = RequireValue(partnerName, "搭档", rowNumber);
        return $"[{first} {second}]";
    }

    private static string BuildTeamName(string teamName, string primaryName, int rowNumber)
    {
        var value = !string.IsNullOrWhiteSpace(teamName) ? teamName : primaryName;
        return RequireValue(value, "队伍", rowNumber);
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

    private static string GetCell(IXLRow row, IReadOnlyDictionary<string, int> headerMap, string header)
    {
        return headerMap.TryGetValue(NormalizeHeader(header), out var column)
            ? row.Cell(column).GetString().Trim()
            : string.Empty;
    }

    private static string NormalizeHeader(string header)
    {
        return header.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    private static int? ParseSeedRank(string value)
    {
        return int.TryParse(value, out var rank) && rank > 0 ? rank : null;
    }

    private static bool ParseSeedFlag(string value)
    {
        return SeedTrueValues.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);
    }
}
