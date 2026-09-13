using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Excel;
using BadmintonDraw.Persistence;
using BadmintonDraw.Workflows.Tournaments;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Xunit;
using static BadmintonDraw.Tests.WorkspaceResultImportFacadeFixture;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceRecordLiteralIntegrityTests
{
    [Theory]
    [InlineData("I6", "Score")]
    [InlineData("J6", "Duration")]
    [InlineData("U6", "ResultKind")]
    [InlineData("V6", "ActualPlayedDay")]
    public void MalformedNumericPendingCellRejectsTheWholeBatchWithoutSaving(string address, string field)
        => VerifyRejected(address, field, "number", false);

    [Theory]
    [InlineData("shared-outside", false)]
    [InlineData("shared-outside", true)]
    [InlineData("number-inline", false)]
    [InlineData("number-inline", true)]
    [InlineData("inline-value", false)]
    [InlineData("inline-value", true)]
    public void InvalidPhysicalPayloadCannotHideBehindAGenuinePendingRow(string shape, bool isolatedBadRow)
        => VerifyRejected("I6", "Score", shape, isolatedBadRow);

    private static void VerifyRejected(string address, string field, string shape, bool isolatedBadRow)
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 2);
        var pending = f.Export("损坏待赛副本.xlsx", fill: false);
        var valid = f.Export("有效赛果.xlsx");
        Edit(pending, sheet => sheet.Cell(address).Value = 21);
        using (var package = SpreadsheetDocument.Open(pending, true))
        {
            var cell = package.WorkbookPart!.WorksheetParts.Single().Worksheet.Descendants<Cell>()
                .Single(c => c.CellReference?.Value == address);
            cell.DataType = null; // An omitted type is numeric, not free text.
            cell.CellValue = new CellValue("not-a-finite-number");
            if (shape == "shared-outside") { cell.DataType = CellValues.SharedString; cell.CellValue = new CellValue("999999"); }
            if (shape == "number-inline") { cell.CellValue = null; cell.InlineString = new InlineString(new Text("physical text")); }
            if (shape == "inline-value") cell.DataType = CellValues.InlineString;
            if (isolatedBadRow)
            {
                var original = (Row)cell.Parent!;
                var genuine = (Row)original.CloneNode(true); genuine.RowIndex = 7U;
                foreach (var copy in genuine.Elements<Cell>()) copy.CellReference = copy.CellReference!.Value![..^1] + "7";
                var score = genuine.Elements<Cell>().Single(c => c.CellReference?.Value == "I7");
                score.DataType = null; score.CellValue = null; score.InlineString = null;
                original.Parent!.AppendChild(genuine);
                var bad = (Cell)cell.CloneNode(true); original.RemoveAllChildren(); original.AppendChild(bad);
            }
        }
        var before = f.Session; var archiveHash = Hash(before.WorkspacePath);
        var pendingHash = Hash(pending); var validHash = Hash(valid);
        var files = Directory.GetFiles(f.DirectoryPath, "*", SearchOption.AllDirectories).Order().ToArray();
        var publications = 0; f.Workflow.SessionChanged += (_, _) => publications++;

        var preview = f.Workflow.PreviewResultImport([valid, pending]);

        Assert.Equal(ResultImportEvaluationStatus.Rejected, preview.Evaluation.Status);
        Assert.Null(preview.Evaluation.Candidate);
        Assert.Contains(preview.Evaluation.Diagnostics, d => d.Code == "result.cell-invalid" &&
            d.Severity == ResultImportDiagnosticSeverity.Blocking && d.Source?.Field == field &&
            d.Source.SourcePath == pending && d.Source.Location == new WorkspaceRecordLocation("对阵记录表", 6));
        var error = Assert.Throws<WorkspaceCommandException>(() =>
            f.Workflow.ImportResults(preview, new(true, "不能覆盖损坏输入"), f.Revision)).Error;
        Assert.False(error.Committed);
        Assert.Same(before, f.Session); Assert.Equal(0, f.Store.Mutations); Assert.Equal(0, publications);
        Assert.Equal(archiveHash, Hash(before.WorkspacePath)); Assert.Equal(pendingHash, Hash(pending)); Assert.Equal(validHash, Hash(valid));
        Assert.Equal(files, Directory.GetFiles(f.DirectoryPath, "*", SearchOption.AllDirectories).Order().ToArray());
        var saved = new TournamentWorkspaceStore().Read(before.WorkspacePath);
        Assert.Empty(saved.Results); Assert.Empty(saved.ImportLogs); Assert.Empty(saved.ProcessedDays);
        Assert.Equal(before.Workspace.Revision, saved.Revision);
    }
}
