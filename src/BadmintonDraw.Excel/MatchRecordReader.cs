using ClosedXML.Excel;
using System.Text.RegularExpressions;

namespace BadmintonDraw.Excel;

public sealed class MatchRecordReader
{
    private const string SheetName = "对阵记录表";
    private const int FirstDataRow = 6;
    private const int DayLabelColumn = 2;
    private const int ScoreColumn = 9;
    private const int DurationColumn = 10;
    private const int WinnerColumn = 12;
    private const int MatchIdColumn = 14;
    private const int WinnerOptionAColumn = 15;
    private const int WinnerOptionBColumn = 16;
    private const int TournamentIdColumn = 17;
    private const int SideAColumn = 6;
    private const int SideBColumn = 8;

    public MatchRecordImportResult ReadMany(IEnumerable<string> filePaths)
    {
        var paths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();
        if (paths.Count == 0)
        {
            throw new ExcelImportException("请选择至少一张赛程记录表。");
        }

        var mergedResults = new Dictionary<string, MatchRecordResult>(StringComparer.Ordinal);
        var mergedDayLabels = new List<string>();
        var missingRows = new List<string>();
        var validationIssues = new List<string>();
        var pendingMatchNames = new HashSet<string>(StringComparer.Ordinal);
        var tournamentIds = new HashSet<string>(StringComparer.Ordinal);
        var expectedMatchCount = 0;
        foreach (var path in paths)
        {
            var importResult = Read(path);
            expectedMatchCount += importResult.ExpectedMatchCount;
            missingRows.AddRange(importResult.MissingResultRows.Select(row => $"{Path.GetFileName(path)}：{row}"));
            validationIssues.AddRange(importResult.ValidationIssues.Select(issue => $"{Path.GetFileName(path)}：{issue}"));
            pendingMatchNames.UnionWith(importResult.PendingMatchNames);
            tournamentIds.UnionWith(importResult.TournamentIds);
            foreach (var dayLabel in importResult.DayLabels)
            {
                if (!mergedDayLabels.Contains(dayLabel, StringComparer.Ordinal))
                {
                    mergedDayLabels.Add(dayLabel);
                }
            }

            foreach (var (matchName, result) in importResult.Results)
            {
                if (mergedResults.TryGetValue(matchName, out var existingResult))
                {
                    if (!string.Equals(existingResult.Winner, result.Winner, StringComparison.Ordinal))
                    {
                        throw new ExcelImportException(
                            $"场次“{matchName}”在多张记录表中的胜方不一致：{existingResult.Winner} / {result.Winner}。");
                    }

                    continue;
                }

                mergedResults.Add(matchName, result);
                pendingMatchNames.Remove(matchName);
            }
        }

        return new MatchRecordImportResult(
            mergedResults,
            mergedDayLabels,
            expectedMatchCount,
            missingRows,
            validationIssues,
            pendingMatchNames.ToList(),
            tournamentIds.ToList());
    }

    public MatchRecordImportResult Read(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ExcelImportException("请选择赛程记录表 Excel。");
        }

        if (!File.Exists(filePath))
        {
            throw new ExcelImportException($"找不到赛程记录表：{filePath}");
        }

        try
        {
            using var workbook = new XLWorkbook(filePath);
            var sheet = workbook.Worksheets.FirstOrDefault(worksheet =>
                string.Equals(worksheet.Name, SheetName, StringComparison.Ordinal));
            if (sheet is null)
            {
                throw new ExcelImportException($"赛程记录表缺少“{SheetName}”工作表。");
            }

            return ReadSheet(sheet);
        }
        catch (ExcelImportException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException)
        {
            throw new ExcelImportException($"无法读取赛程记录表：{ex.Message}");
        }
    }

    private static MatchRecordImportResult ReadSheet(IXLWorksheet sheet)
    {
        var results = new Dictionary<string, MatchRecordResult>(StringComparer.Ordinal);
        var dayLabels = new List<string>();
        var missingRows = new List<string>();
        var validationIssues = new List<string>();
        var pendingMatchNames = new List<string>();
        var tournamentIds = new HashSet<string>(StringComparer.Ordinal);
        var expectedMatchCount = 0;
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;

        for (var row = FirstDataRow; row <= lastRow; row++)
        {
            var matchName = NormalizeSpaces(sheet.Cell(row, MatchIdColumn).GetString());
            if (string.IsNullOrWhiteSpace(matchName))
            {
                continue;
            }

            expectedMatchCount++;
            var location = BuildRecordLocation(sheet, row);

            var tournamentId = NormalizeSpaces(sheet.Cell(row, TournamentIdColumn).GetString());
            if (!string.IsNullOrWhiteSpace(tournamentId))
            {
                tournamentIds.Add(tournamentId);
            }

            var dayLabel = NormalizeSpaces(sheet.Cell(row, DayLabelColumn).GetString());
            if (!string.IsNullOrWhiteSpace(dayLabel)
                && !dayLabels.Contains(dayLabel, StringComparer.Ordinal))
            {
                dayLabels.Add(dayLabel);
            }

            var score = sheet.Cell(row, ScoreColumn).GetString().Trim();
            var duration = sheet.Cell(row, DurationColumn).GetString().Trim();
            var winnerText = NormalizeSpaces(sheet.Cell(row, WinnerColumn).GetString());
            if (string.IsNullOrWhiteSpace(winnerText))
            {
                missingRows.Add($"{location} {matchName} 未填写胜方，将作为未决场次顺延");
                pendingMatchNames.Add(matchName);
                continue;
            }

            var optionA = BuildOptionFromRow(sheet, row, location, WinnerOptionAColumn, SideAColumn);
            var optionB = BuildOptionFromRow(sheet, row, location, WinnerOptionBColumn, SideBColumn);
            var winnerOption = ResolveWinnerOption(location, winnerText, optionA, optionB);
            var loserOption = string.Equals(winnerOption, optionA, StringComparison.Ordinal) ? optionB : optionA;
            if (string.IsNullOrWhiteSpace(score))
            {
                validationIssues.Add($"{location} {matchName} 未填写比分，已按胜方 {winnerOption[..1]} 推进。");
            }
            else if (TryGetScoreWinnerPrefix(score, out var scoreWinnerPrefix))
            {
                if (!string.Equals(scoreWinnerPrefix, winnerOption[..1], StringComparison.OrdinalIgnoreCase))
                {
                    validationIssues.Add($"{location} {matchName} 的比分胜方为 {scoreWinnerPrefix}，但胜方填写为 {winnerOption[..1]}。");
                }
            }
            else
            {
                validationIssues.Add($"{location} {matchName} 的比分格式无法判断胜负，已按胜方 {winnerOption[..1]} 推进。");
            }

            if (string.IsNullOrWhiteSpace(duration))
            {
                validationIssues.Add($"{location} {matchName} 未填写用时，已按胜方 {winnerOption[..1]} 推进。");
            }

            results[matchName] = new MatchRecordResult(
                matchName,
                dayLabel,
                ExtractOptionName(winnerOption),
                ExtractOptionName(loserOption),
                score,
                duration);
        }

        return new MatchRecordImportResult(
            results,
            dayLabels,
            expectedMatchCount,
            missingRows,
            validationIssues,
            pendingMatchNames,
            tournamentIds.ToList());
    }

    private static string BuildRecordLocation(IXLWorksheet sheet, int row)
    {
        var order = NormalizeSpaces(sheet.Cell(row, 1).GetString());
        return string.IsNullOrWhiteSpace(order)
            ? "序号未填写"
            : $"序号 {order}";
    }

    private static string BuildOptionFromRow(IXLWorksheet sheet, int row, string location, int optionColumn, int sideColumn)
    {
        var option = NormalizeOption(sheet.Cell(row, optionColumn).GetString());
        if (!string.IsNullOrWhiteSpace(option))
        {
            return option;
        }

        option = NormalizeOption(sheet.Cell(row, sideColumn).GetString());
        if (!string.IsNullOrWhiteSpace(option))
        {
            return option;
        }

        throw new ExcelImportException($"{location} 缺少胜方候选，请重新导出赛程记录表。");
    }

    private static string ResolveWinnerOption(string location, string winnerText, string optionA, string optionB)
    {
        var winner = NormalizeOption(winnerText);
        if (string.Equals(winner, "A", StringComparison.OrdinalIgnoreCase))
        {
            return optionA;
        }

        if (string.Equals(winner, "B", StringComparison.OrdinalIgnoreCase))
        {
            return optionB;
        }

        var winnerPlain = ExtractOptionName(winner);
        if (string.Equals(winner, optionA, StringComparison.Ordinal)
            || string.Equals(winnerPlain, ExtractOptionName(optionA), StringComparison.Ordinal))
        {
            return optionA;
        }

        if (string.Equals(winner, optionB, StringComparison.Ordinal)
            || string.Equals(winnerPlain, ExtractOptionName(optionB), StringComparison.Ordinal))
        {
            return optionB;
        }

        throw new ExcelImportException($"{location} 胜方不在本场候选列表中，请从下拉框选择。");
    }

    private static string ExtractOptionName(string option)
    {
        var normalized = NormalizeOption(option);
        var start = normalized.IndexOf('【');
        var end = normalized.LastIndexOf('】');
        if (start >= 0 && end > start)
        {
            return normalized[(start + 1)..end].Trim();
        }

        return normalized.Trim();
    }

    private static bool TryGetScoreWinnerPrefix(string score, out string winnerPrefix)
    {
        var winsA = 0;
        var winsB = 0;
        var games = Regex.Split(score.Trim(), @"[,，;；、]\s*")
            .Where(game => !string.IsNullOrWhiteSpace(game))
            .ToList();
        foreach (var game in games)
        {
            var normalized = NormalizeSpaces(game)
                .Replace('－', '-')
                .Replace('—', '-')
                .Replace('–', '-');
            var match = Regex.Match(normalized, @"^(\d+)\s*-\s*(\d+)$");
            if (!match.Success)
            {
                winnerPrefix = "";
                return false;
            }

            var left = int.Parse(match.Groups[1].Value);
            var right = int.Parse(match.Groups[2].Value);
            if (left == right)
            {
                winnerPrefix = "";
                return false;
            }

            if (left > right)
            {
                winsA++;
            }
            else
            {
                winsB++;
            }
        }

        if (winsA == winsB || Math.Max(winsA, winsB) < 2)
        {
            winnerPrefix = "";
            return false;
        }

        winnerPrefix = winsA > winsB ? "A" : "B";
        return true;
    }

    private static string NormalizeOption(string value)
    {
        return NormalizeSpaces(value.Replace('\n', ' ').Replace('\r', ' '));
    }

    private static string NormalizeSpaces(string value)
    {
        return Regex.Replace(value.Trim(), @"\s+", " ");
    }
}
