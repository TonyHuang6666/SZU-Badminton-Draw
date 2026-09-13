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
            var physicalCells = part.Worksheet.Descendants<Cell>().ToArray();
            var cells = physicalCells.Select(cell => (Cell: cell, Position: Position(cell.CellReference?.Value)))
                .Where(item => item.Position.Row >= WorkspaceRecordSchema.FirstDataRow && item.Position.Column <= WorkspaceRecordSchema.LastColumn)
                .ToDictionary(item => item.Position, item => item.Cell);

            using var valueStream = new MemoryStream(bytes, writable: false);
            using var workbook = new XLWorkbook(valueStream);
            var sheet = workbook.Worksheet(WorkspaceRecordSchema.SheetName);
            // Array/shared anchors may be in row 5. Their members can have no <f>, no cache, or no physical <c> at all.
            var formulaRanges = physicalCells.Select(cell => cell.CellFormula)
                .Where(f => f?.Reference?.Value is not null &&
                    (f.FormulaType?.Value == CellFormulaValues.Array || f.FormulaType?.Value == CellFormulaValues.Shared))
                .Select(f => sheet.Range(f!.Reference!.Value!).RangeAddress)
                .Where(r => r.FirstAddress.ColumnNumber <= WorkspaceRecordSchema.LastColumn &&
                    r.LastAddress.RowNumber >= WorkspaceRecordSchema.FirstDataRow).ToArray();
            var rowNumbers = new SortedSet<int>(cells.Keys.Select(position => position.Row));
            foreach (var range in formulaRanges)
                for (var row = Math.Max(WorkspaceRecordSchema.FirstDataRow, range.FirstAddress.RowNumber); row <= range.LastAddress.RowNumber; row++)
                    rowNumbers.Add(row);
            bool HasFormula(int row, int column) => cells.GetValueOrDefault((row, column))?.CellFormula is not null ||
                sheet.Cell(row, column).HasFormula || formulaRanges.Any(range =>
                    row >= range.FirstAddress.RowNumber && row <= range.LastAddress.RowNumber &&
                    column >= range.FirstAddress.ColumnNumber && column <= range.LastAddress.ColumnNumber);
            var rows = new List<WorkspaceRecordRawRow>();
            foreach (var row in rowNumbers)
            {
                var evidence = Enumerable.Range(1, WorkspaceRecordSchema.LastColumn).Select(column =>
                {
                    cells.TryGetValue((row, column), out var raw);
                    var formula = HasFormula(row, column);
                    var value = sheet.Cell(row, column).CachedValue;
                    return formula ? ReadFormulaCache(raw, value) : ReadLiteral(raw, value);
                }).ToArray();
                // A malformed numeric literal can be silently loaded as blank. Keep its
                // physical evidence, including an otherwise empty/idless record row.
                if (!evidence.Any(cell => cell.HasFormula || cell.ReadError is not null ||
                    cell.Kind != WorkspaceRecordCellKind.Empty && (cell.Kind != WorkspaceRecordCellKind.Text || cell.Text.Length > 0))) continue;
                WorkspaceRecordCell Read(int column) => evidence[column - 1];
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

    private static WorkspaceRecordCell ReadLiteral(Cell? raw, XLCellValue value)
    {
        if (raw is null) return ReadValue(value, false);
        var text = raw.CellValue?.Text ?? "";
        var type = raw.DataType?.Value ?? CellValues.Number;
        WorkspaceRecordCell Invalid() => new(text, WorkspaceRecordCellKind.Error, false,
            "实际单元格内容与存储类型不一致；请检查原文件，不能按空白待赛处理。");
        if (type == CellValues.Number)
        {
            // A styled cell without <v> is genuinely blank; an explicit empty or
            // malformed <v> is not. Date-styled numeric serials remain typed dates.
            if (raw.CellValue is null) return value.IsBlank ? ReadValue(value, false) : Invalid();
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) ||
                !(value.IsNumber || value.IsDateTime || value.IsTimeSpan)) return Invalid();
        }
        else if (type == CellValues.Boolean)
            return text.Trim() switch
            {
                "1" or "true" => new("TRUE", WorkspaceRecordCellKind.Boolean),
                "0" or "false" => new("FALSE", WorkspaceRecordCellKind.Boolean),
                _ => Invalid()
            };
        else if (type == CellValues.Date)
        {
            try { System.Xml.XmlConvert.ToDateTime(text, System.Xml.XmlDateTimeSerializationMode.RoundtripKind); }
            catch (FormatException) { return Invalid(); }
            if (!value.IsDateTime) return Invalid();
        }
        else if (type == CellValues.SharedString)
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0 || !value.IsText) return Invalid();
        }
        else if (type == CellValues.Error)
            return new(text, WorkspaceRecordCellKind.Error, false, "单元格错误：" + text);
        else if (type != CellValues.String && type != CellValues.InlineString || !(value.IsText || value.IsBlank)) return Invalid();
        return ReadValue(value, false);
    }

    private static WorkspaceRecordCell ReadFormulaCache(Cell? raw, XLCellValue value)
    {
        if (raw?.CellValue is null)
            return new("", WorkspaceRecordCellKind.Error, true, "公式没有缓存值，请先在 Excel 或 LibreOffice 中打开并保存。");
        var text = raw.CellValue.Text;
        var type = raw.DataType?.Value ?? CellValues.Number;
        WorkspaceRecordCell Invalid() => new(text, WorkspaceRecordCellKind.Error, true,
            "公式缓存与单元格类型不一致，请先在 Excel 或 LibreOffice 中打开并保存。");
        // <v/> is a legitimate empty string only for a string cache, never for a numeric/boolean/date cache.
        if (type == CellValues.String)
            return text.Length == 0 ? new("", WorkspaceRecordCellKind.Text, true) : value.IsText ? ReadValue(value, true) : Invalid();
        if (type == CellValues.Boolean)
        {
            var boolean = text.Trim();
            return boolean switch
            {
                "1" or "true" => new("TRUE", WorkspaceRecordCellKind.Boolean, true),
                "0" or "false" => new("FALSE", WorkspaceRecordCellKind.Boolean, true),
                _ => Invalid()
            };
        }
        if (type == CellValues.Error)
            return new(text, WorkspaceRecordCellKind.Error, true, string.IsNullOrEmpty(text) ? "公式错误缓存为空。" : "单元格错误：" + text);
        if (type == CellValues.Number && (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)))
            return Invalid();
        if (type == CellValues.Date)
        {
            try { System.Xml.XmlConvert.ToDateTime(text, System.Xml.XmlDateTimeSerializationMode.RoundtripKind); }
            catch (FormatException) { return Invalid(); }
        }
        if (type == CellValues.SharedString && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0))
            return Invalid();
        if (type != CellValues.Number && type != CellValues.Date && type != CellValues.SharedString || value.IsBlank)
            return Invalid();
        return ReadValue(value, true);
    }

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
