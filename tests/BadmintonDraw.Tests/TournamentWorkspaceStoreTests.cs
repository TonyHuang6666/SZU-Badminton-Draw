using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BadmintonDraw.Tests;

public class TournamentWorkspaceStoreTests
{
    [Theory]
    [InlineData(TournamentStage.DrawsConfirmed)]
    [InlineData(TournamentStage.Completed)]
    public void AggregateRoundTrips(TournamentStage stage)
    {
        using var temp = new WorkspaceStoreTemp();
        var expected = TournamentWorkspaceRulesTests.Fixture(stage);
        expected = expected with
        {
            AuditEvents = [new(Guid.NewGuid(), "Imported", DateTimeOffset.UtcNow, expected.Projects[0].Id, Detail: "备注")],
            Projects = [expected.Projects[0] with { Roster = expected.Projects[0].Roster! with { Warnings = [new("Notice", "名单提示", 2)] } }]
        };
        if (stage == TournamentStage.DrawsConfirmed) expected = expected with { Schedule = null, Resources = null, Purpose = TournamentPurpose.PublicDrawOnly };
        var store = new TournamentWorkspaceStore();
        store.Create(temp.Path, expected);
        var actual = store.Read(temp.Path);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Stage, actual.Stage);
        Assert.Equal(expected.Projects[0].MatchGraph!.Matches[0].Id, actual.Projects[0].MatchGraph!.Matches[0].Id);
        Assert.Equal(expected.Results.Keys, actual.Results.Keys);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected.Projects), System.Text.Json.JsonSerializer.Serialize(actual.Projects));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected.AuditEvents), System.Text.Json.JsonSerializer.Serialize(actual.AuditEvents));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected.Results.Values), System.Text.Json.JsonSerializer.Serialize(actual.Results.Values));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected.Schedule), System.Text.Json.JsonSerializer.Serialize(actual.Schedule));
        Assert.Equal(expected.Schedule is null, actual.Schedule is null);
        if (actual.Schedule is not null) Assert.Equal(expected.Schedule!.Placements.Keys, actual.Schedule.Placements.Keys);
    }

    [Fact]
    public void MutationAndRestoreAdvanceRevisionAndPreserveBackup()
    {
        using var temp = new WorkspaceStoreTemp();
        var store = new TournamentWorkspaceStore();
        var initial = store.Create(temp.Path, TournamentWorkspaceRulesTests.Fixture(TournamentStage.Completed));
        var changed = store.Mutate(temp.Path, 0, w => w with { Name = "新名称", Revision = 999 });
        Assert.Equal(1, changed.Workspace.Revision);
        Assert.Equal(initial.Name, store.Read(changed.BackupPath).Name);
        var restored = store.RestoreBackup(temp.Path, changed.BackupPath);
        Assert.Equal(initial.Name, restored.Name);
        Assert.Equal(2, restored.Revision);
    }
}

internal sealed class WorkspaceStoreTemp : IDisposable
{
    public string DirectoryPath { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "workspace-tests-" + Guid.NewGuid().ToString("N"));
    public string Path => System.IO.Path.Combine(DirectoryPath, "公开抽签.szbd");
    public WorkspaceStoreTemp() => Directory.CreateDirectory(DirectoryPath);
    public void Dispose() => Directory.Delete(DirectoryPath, true);
    public static void Sql(string path, string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open(); using var command = c.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
}
