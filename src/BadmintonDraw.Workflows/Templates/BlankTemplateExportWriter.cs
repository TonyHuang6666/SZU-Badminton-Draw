using BadmintonDraw.Excel;

namespace BadmintonDraw.Workflows.Templates;

public enum TemplateExportKind { RosterExcel, IndividualExcel, IndividualPdf, TeamExcel, TeamPdf }

public interface ITemplateExportWriter
{
    void Write(TemplateExportKind kind, string path);
}

public sealed class BlankTemplateExportWriter : ITemplateExportWriter
{
    public void Write(TemplateExportKind kind, string path)
    {
        var scoreSheets = new ScoreSheetExcelWriter();
        switch (kind)
        {
            case TemplateExportKind.RosterExcel: new ParticipantTemplateWriter().WriteBlank(path); break;
            case TemplateExportKind.IndividualExcel: scoreSheets.WriteBlankIndividualExcel(path); break;
            case TemplateExportKind.IndividualPdf: scoreSheets.WriteBlankIndividualPdf(path); break;
            case TemplateExportKind.TeamExcel: scoreSheets.WriteBlankTeamExcel(path); break;
            case TemplateExportKind.TeamPdf: scoreSheets.WriteBlankTeamPdf(path); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }
}
