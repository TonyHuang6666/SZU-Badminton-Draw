using Microsoft.Data.Sqlite;

namespace BadmintonDraw.Persistence;

internal static class WorkspaceDatabaseSchema
{
    internal static readonly string[] Tables = ["workspace", "projects", "project_rosters", "project_draws", "match_graphs", "resource_plan", "schedule", "match_results", "processed_days", "import_logs", "result_history", "audit_events"];
    internal static void Initialize(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=DELETE;
            PRAGMA foreign_keys=ON;
            PRAGMA user_version=500;
            CREATE TABLE workspace(id TEXT PRIMARY KEY NOT NULL, json TEXT NOT NULL);
            CREATE TABLE projects(id TEXT PRIMARY KEY NOT NULL, workspace_id TEXT NOT NULL REFERENCES workspace(id), ordinal INTEGER NOT NULL UNIQUE, json TEXT NOT NULL);
            CREATE TABLE project_rosters(project_id TEXT PRIMARY KEY NOT NULL REFERENCES projects(id), json TEXT NOT NULL);
            CREATE TABLE project_draws(project_id TEXT PRIMARY KEY NOT NULL REFERENCES projects(id), json TEXT NOT NULL);
            CREATE TABLE match_graphs(project_id TEXT PRIMARY KEY NOT NULL REFERENCES projects(id), json TEXT NOT NULL);
            CREATE TABLE resource_plan(workspace_id TEXT PRIMARY KEY NOT NULL REFERENCES workspace(id), json TEXT NOT NULL);
            CREATE TABLE schedule(workspace_id TEXT PRIMARY KEY NOT NULL REFERENCES workspace(id), json TEXT NOT NULL);
            CREATE TABLE match_results(project_id TEXT NOT NULL REFERENCES projects(id), match_id TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(project_id,match_id));
            CREATE TABLE processed_days(workspace_id TEXT NOT NULL REFERENCES workspace(id), day TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(workspace_id,day));
            CREATE TABLE import_logs(id TEXT PRIMARY KEY NOT NULL, workspace_id TEXT NOT NULL REFERENCES workspace(id), json TEXT NOT NULL);
            CREATE TABLE result_history(id TEXT PRIMARY KEY NOT NULL, workspace_id TEXT NOT NULL REFERENCES workspace(id), json TEXT NOT NULL);
            CREATE TABLE audit_events(id TEXT PRIMARY KEY NOT NULL, workspace_id TEXT NOT NULL REFERENCES workspace(id), ordinal INTEGER NOT NULL UNIQUE, json TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }
}
