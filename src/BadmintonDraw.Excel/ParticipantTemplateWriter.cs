using ClosedXML.Excel;

namespace BadmintonDraw.Excel;

public sealed class ParticipantTemplateWriter
{
    public void Write(string filePath)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("参赛名单");
        var headers = new[] { "姓名", "搭档", "队伍", "是否种子", "种子序号", "备注" };

        for (var i = 0; i < headers.Length; i++)
        {
            sheet.Cell(1, i + 1).Value = headers[i];
            sheet.Cell(1, i + 1).Style.Font.Bold = true;
        }

        sheet.Cell(2, 1).Value = "张三";
        sheet.Cell(2, 2).Value = "李四";
        sheet.Cell(2, 4).Value = "否";
        sheet.Cell(2, 6).Value = "双打示例";
        sheet.Cell(3, 1).Value = "王五";
        sheet.Cell(3, 4).Value = "是";
        sheet.Cell(3, 5).Value = 1;
        sheet.Cell(3, 6).Value = "单打种子示例";
        sheet.Cell(4, 3).Value = "计算机与软件学院";
        sheet.Cell(4, 4).Value = "否";
        sheet.Cell(4, 6).Value = "团体赛示例";

        var range = sheet.Range(1, 1, 4, headers.Length);
        var table = range.CreateTable();
        table.Theme = XLTableTheme.TableStyleMedium2;

        sheet.Columns(1, headers.Length).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        sheet.Columns(1, headers.Length).Style.Alignment.WrapText = true;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        range.Style.Alignment.WrapText = true;
        sheet.Row(1).Height = 24;
        sheet.Rows(2, 4).Height = 40;
        sheet.Row(1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        sheet.Column(1).Width = 10;
        sheet.Column(2).Width = 10;
        sheet.Column(3).Width = 18;
        sheet.Column(4).Width = 12;
        sheet.Column(5).Width = 12;
        sheet.Column(6).Width = 16;

        sheet.SheetView.FreezeRows(1);
        workbook.SaveAs(filePath);
    }
}
