using System.Text.Json;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using Microsoft.Data.Sqlite;

namespace BadmintonDraw.Persistence;

internal static class WorkspaceSerializer
{
    // Presence/count metadata detects removed rows even when the resulting domain object could otherwise be valid.
    private sealed record Header(Guid Id, string Name, TournamentKind Kind, TournamentPurpose Purpose, TournamentStage Stage,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long Revision, int ProjectCount, int ResultCount, int AuditCount, bool HasResources, bool HasSchedule);
    private sealed record ProjectHeader(Guid Id, EventDiscipline Discipline, string DisplayName, CompetitionMode CompetitionMode,
        int SortOrder, bool HasRoster, bool HasDraw, bool HasGraph);
    private sealed record ScheduleData(IReadOnlyDictionary<Guid, MatchPlacement> Placements, TournamentSchedulingPolicy Policy,
        IReadOnlyDictionary<Guid, string> GraphRevisions, long Revision);
    private static readonly JsonSerializerOptions Options = new() { RespectRequiredConstructorParameters = true };

    internal static void Write(SqliteConnection c, SqliteTransaction tx, TournamentWorkspace w)
    {
        foreach (var table in WorkspaceDatabaseSchema.Tables.Reverse()) Execute(c, tx, $"DELETE FROM {table}");
        Insert(c, tx, "workspace", ["id", "json"], w.Id, Json(new Header(w.Id, w.Name, w.Kind, w.Purpose, w.Stage, w.CreatedAt, w.UpdatedAt, w.Revision, w.Projects.Count, w.Results.Count, w.AuditEvents.Count, w.Resources is not null, w.Schedule is not null)));
        for (var i = 0; i < w.Projects.Count; i++)
        {
            var p = w.Projects[i];
            Insert(c, tx, "projects", ["id", "workspace_id", "ordinal", "json"], p.Id, w.Id, i, Json(new ProjectHeader(p.Id, p.Discipline, p.DisplayName, p.CompetitionMode, p.SortOrder, p.Roster is not null, p.Draw is not null, p.MatchGraph is not null)));
            if (p.Roster is not null) Insert(c, tx, "project_rosters", ["project_id", "json"], p.Id, Json(p.Roster));
            if (p.Draw is not null) Insert(c, tx, "project_draws", ["project_id", "json"], p.Id, Json(p.Draw));
            if (p.MatchGraph is not null) Insert(c, tx, "match_graphs", ["project_id", "json"], p.Id, Json(p.MatchGraph));
        }
        if (w.Resources is not null) Insert(c, tx, "resource_plan", ["workspace_id", "json"], w.Id, Json(w.Resources));
        if (w.Schedule is { } s) Insert(c, tx, "schedule", ["workspace_id", "json"], w.Id, Json(new ScheduleData(s.Placements, s.Policy, s.GraphRevisions, s.Revision)));
        foreach (var (key, result) in w.Results) Insert(c, tx, "match_results", ["project_id", "match_id", "json"], key.ProjectId, key.MatchId, Json(result));
        for (var i = 0; i < w.AuditEvents.Count; i++) Insert(c, tx, "audit_events", ["id", "workspace_id", "ordinal", "json"], w.AuditEvents[i].Id, w.Id, i, Json(w.AuditEvents[i]));
    }

    internal static TournamentWorkspace Read(SqliteConnection c)
    {
        var heads = Rows(c, "workspace"); Require(heads.Count == 1, "缺少或重复工作区记录。");
        var h = Parse<Header>(heads[0]); Require(heads[0]["id"] == h.Id.ToString(), "工作区身份不匹配。");
        // These contracts are introduced by the result import workflow. Never silently erase newer populated records.
        foreach (var table in new[] { "processed_days", "import_logs", "result_history" }) Require(Rows(c, table).Count == 0, "当前版本不支持非空 " + table + " 数据。");
        var rosters = Rows(c, "project_rosters"); var draws = Rows(c, "project_draws"); var graphs = Rows(c, "match_graphs");
        var projects = Rows(c, "projects", "ordinal").Select(row =>
        {
            var p = Parse<ProjectHeader>(row); Require(row["id"] == p.Id.ToString() && row["workspace_id"] == h.Id.ToString(), "项目身份不匹配。");
            return new TournamentProject(p.Id, p.Discipline, p.DisplayName, p.CompetitionMode,
                Optional<ProjectRoster>(rosters, "project_id", p.Id, p.HasRoster), Optional<ProjectDraw>(draws, "project_id", p.Id, p.HasDraw),
                Optional<MatchGraph>(graphs, "project_id", p.Id, p.HasGraph), p.SortOrder);
        }).ToArray();
        Require(projects.Length == h.ProjectCount, "项目记录缺失。");
        var resources = Optional<TournamentResourcePlan>(Rows(c, "resource_plan"), "workspace_id", h.Id, h.HasResources);
        var sd = Optional<ScheduleData>(Rows(c, "schedule"), "workspace_id", h.Id, h.HasSchedule);
        var results = Rows(c, "match_results").ToDictionary(row => new WorkspaceMatchKey(Guid.Parse(row["project_id"]), Guid.Parse(row["match_id"])), Parse<TournamentMatchResult>);
        var audit = Rows(c, "audit_events", "ordinal").Select(row => { var a = Parse<WorkspaceAuditEvent>(row); Require(row["id"] == a.Id.ToString() && row["workspace_id"] == h.Id.ToString(), "审计身份不匹配。"); return a; }).ToArray();
        Require(results.Count == h.ResultCount && audit.Length == h.AuditCount, "赛果或审计记录缺失。");
        var w = new TournamentWorkspace(h.Id, h.Name, h.Kind, h.Purpose, h.Stage, projects, resources,
            sd is null ? null : new(sd.Placements, resources ?? throw new InvalidDataException("赛程缺少资源。"), sd.Policy, sd.GraphRevisions, sd.Revision), results, audit, h.CreatedAt, h.UpdatedAt, h.Revision);
        TournamentWorkspaceRules.Validate(w); return w;
    }
    private static T? Optional<T>(List<Dictionary<string, string>> rows, string key, Guid id, bool expected) where T : class
    { var matches = rows.Where(r => r[key] == id.ToString()).ToArray(); Require(matches.Length == (expected ? 1 : 0), "聚合记录缺失或多余。"); return expected ? Parse<T>(matches[0]) : null; }
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, Options);
    private static T Parse<T>(Dictionary<string, string> row) => JsonSerializer.Deserialize<T>(row["json"], Options) ?? throw new InvalidDataException("JSON 不能为空。");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static List<Dictionary<string, string>> Rows(SqliteConnection c, string table, string? order = null)
    {
        using var cmd = c.CreateCommand(); cmd.CommandText = $"SELECT * FROM {table}" + (order is null ? "" : $" ORDER BY {order}"); using var r = cmd.ExecuteReader();
        var rows = new List<Dictionary<string, string>>(); while (r.Read()) { var row = new Dictionary<string, string>(); for (var i = 0; i < r.FieldCount; i++) row.Add(r.GetName(i), r.GetValue(i).ToString()!); rows.Add(row); }
        return rows;
    }
    private static void Insert(SqliteConnection c, SqliteTransaction tx, string table, string[] columns, params object[] values)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = $"INSERT INTO {table} ({string.Join(',', columns)}) VALUES ({string.Join(',', values.Select((_, i) => "$p" + i))})";
        for (var i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue("$p" + i, values[i] is Guid id ? id.ToString() : values[i]); cmd.ExecuteNonQuery();
    }
    private static void Execute(SqliteConnection c, SqliteTransaction tx, string sql) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
}
