namespace BadmintonDraw.Persistence;

public sealed class WorkspaceStoreException : Exception
{
    public string Code { get; }
    public string? CandidatePath { get; }
    public string? BackupPath { get; }
    public bool Committed { get; }
    public WorkspaceStoreException(string code, string message, Exception? inner = null, string? candidatePath = null, string? backupPath = null, bool committed = false) : base(message, inner)
    { Code = code; CandidatePath = candidatePath; BackupPath = backupPath; Committed = committed; }
}
