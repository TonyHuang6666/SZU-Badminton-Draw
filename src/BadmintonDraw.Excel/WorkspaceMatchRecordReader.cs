using System.Globalization;
using System.Security.Cryptography;
using BadmintonDraw.Core.Tournaments;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace BadmintonDraw.Excel;

/// <summary>Reads raw evidence from one captured byte snapshot; never evaluates formulas or opens a source path.</summary>
public sealed class WorkspaceMatchRecordReader
{
    public WorkspaceRecordDocument ReadWorkspaceRecord(ReadOnlyMemory<byte> snapshotBytes)
    {
        var bytes = snapshotBytes.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        try
        {
            // OpenXML records whether the cache physically exists; CachedValue alone cannot distinguish a missing cache from "".
            using var packageStream = new MemoryStream(bytes, writable: false);
            using var package = SpreadsheetDocument.Open(packageStream, false);
            var workbookPart = package.WorkbookPart ?? throw new ExcelImportException("记录表缺少工作簿。");
            var recordSheet = workbookPart.Workbook.Sheets?.Elements<Sheet>().SingleOrDefault(sheet => sheet.Name?.Value == WorkspaceRecordSchema.SheetName)
                ?? throw new ExcelImportException($"记录表缺少“{WorkspaceRecordSchema.SheetName}”工作表。");
            var part = (WorksheetPart)workbookPart.GetPartById(recordSheet.Id!.Value!);
            var cells = part.Worksheet.Descendants<Cell>().Select(cell => (Cell: cell, Position: Position(cell.CellReference?.Value)))
                .Where(item => item.Position.Row >= WorkspaceRecordSchema.FirstDataRow && item.Position.Column <= WorkspaceRecordSchema.LastColumn)
                .ToDictionary(item => item.Position, item => item.Cell);

            using var valueStream = new MemoryStream(bytes, writable: false);
            using var workbook = new XLWorkbook(valueStream);
            var sheet = workbook.Worksheet(WorkspaceRecordSchema.SheetName);
            var rows = new List<WorkspaceRecordRawRow>();
            foreach (var group in cells.GroupBy(pair => pair.Key.Row).OrderBy(group => group.Key))
            {
                var row = group.Key;
                if (!group.Any(pair => pair.Value.CellFormula is not null || HasContent(sheet.Cell(row, pair.Key.Column).CachedValue))) continue;
                WorkspaceRecordCell Read(int column)
                {
                    cells.TryGetValue((row, column), out var raw);
                    var formula = raw?.CellFormula is not null;
                    if (formula && raw!.CellValue is null)
                        return new("", WorkspaceRecordCellKind.Error, true, "公式没有缓存值，请先在 Excel 或 LibreOffice 中打开并保存。");
                    return ReadValue(sheet.Cell(row, column).CachedValue, formula);
                }
                rows.Add(new(new(sheet.Name, row), Read(WorkspaceRecordSchema.WorkspaceId), Read(WorkspaceRecordSchema.ProjectId),
                    Read(WorkspaceRecordSchema.MatchId), Read(WorkspaceRecordSchema.GraphRevision), Read(WorkspaceRecordSchema.DrawConfirmedAt),
                    Read(WorkspaceRecordSchema.RecordDay), Read(WorkspaceRecordSchema.ActualPlayedDay), Read(WorkspaceRecordSchema.ResultKind),
                    Read(WorkspaceRecordSchema.Winner), Read(WorkspaceRecordSchema.Score), Read(WorkspaceRecordSchema.Duration),
                    Read(WorkspaceRecordSchema.SideA), Read(WorkspaceRecordSchema.SideB), Read(WorkspaceRecordSchema.OptionA), Read(WorkspaceRecordSchema.OptionB)));
            }
            return new(hash, rows);
        }
        catch (ExcelImportException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new ExcelImportException("无法读取赛程记录表：" + exception.Message);
        }
    }

    private static bool HasContent(XLCellValue value) => !value.IsBlank && (!value.IsText || value.GetText().Length > 0);

    private static WorkspaceRecordCell ReadValue(XLCellValue value, bool formula)
    {
        var culture = CultureInfo.InvariantCulture;
        return value.Type switch
        {
            XLDataType.Blank => new("", WorkspaceRecordCellKind.Empty, formula),
            XLDataType.Text => new(value.GetText(), WorkspaceRecordCellKind.Text, formula),
            XLDataType.Number => new(value.GetNumber().ToString("R", culture), WorkspaceRecordCellKind.Number, formula),
            XLDataType.Boolean => new(value.GetBoolean() ? "TRUE" : "FALSE", WorkspaceRecordCellKind.Boolean, formula),
            XLDataType.DateTime => new(value.GetDateTime().ToString("O", culture), WorkspaceRecordCellKind.DateTime, formula),
            XLDataType.Error => new(value.ToString(culture), WorkspaceRecordCellKind.Error, formula, "单元格错误：" + value.ToString(culture)),
            _ => new(value.ToString(culture), WorkspaceRecordCellKind.Error, formula, "不支持的单元格类型：" + value.Type)
        };
    }

    private static (int Row, int Column) Position(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) throw new ExcelImportException("记录表存在缺少位置的单元格。");
        var index = 0; var column = 0;
        while (index < reference.Length && reference[index] is >= 'A' and <= 'Z') column = checked(column * 26 + reference[index++] - 'A' + 1);
        if (column == 0 || !int.TryParse(reference.AsSpan(index), NumberStyles.None, CultureInfo.InvariantCulture, out var row) || row < 1)
            throw new ExcelImportException("记录表存在无效单元格位置：" + reference);
        return (row, column);
    }
}
