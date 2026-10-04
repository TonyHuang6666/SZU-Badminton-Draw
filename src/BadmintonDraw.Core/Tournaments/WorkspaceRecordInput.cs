namespace BadmintonDraw.Core.Tournaments;

public enum WorkspaceRecordCellKind { Empty, Text, Number, Boolean, DateTime, Error }

/// <summary>Uninterpreted cell evidence. Formula/cache errors remain visible to the evaluator.</summary>
public sealed record WorkspaceRecordCell(string Text,
    WorkspaceRecordCellKind Kind = WorkspaceRecordCellKind.Text,
    bool HasFormula = false, string? ReadError = null);

public sealed record WorkspaceRecordRawRow(
    WorkspaceRecordLocation Location,
    WorkspaceRecordCell WorkspaceId, WorkspaceRecordCell ProjectId,
    WorkspaceRecordCell MatchId, WorkspaceRecordCell GraphRevision,
    WorkspaceRecordCell DrawConfirmedAt, WorkspaceRecordCell RecordDay,
    WorkspaceRecordCell ActualPlayedDay, WorkspaceRecordCell ResultKind,
    WorkspaceRecordCell Winner, WorkspaceRecordCell Score,
    WorkspaceRecordCell Duration, WorkspaceRecordCell SideA,
    WorkspaceRecordCell SideB, WorkspaceRecordCell OptionA,
    WorkspaceRecordCell OptionB);

/// <summary>One captured workbook's ordered rows, including pending and repeated match evidence.</summary>
public sealed record WorkspaceRecordDocument(string ContentHash, IReadOnlyList<WorkspaceRecordRawRow> Rows)
{
    private IReadOnlyList<WorkspaceRecordRawRow> rows = WorkspaceSnapshot.List(Rows);
    public IReadOnlyList<WorkspaceRecordRawRow> Rows { get => rows; init => rows = WorkspaceSnapshot.List(value); }
}

/// <summary>Filesystem provenance is supplied separately from the reader's byte snapshot.</summary>
public sealed record WorkspaceRecordImportFile(string SourceFileName, string SourcePath, WorkspaceRecordDocument Document);
