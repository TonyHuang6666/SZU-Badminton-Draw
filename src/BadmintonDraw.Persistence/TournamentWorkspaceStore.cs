using System.Collections.Concurrent;
using BadmintonDraw.Core.Tournaments;
using Microsoft.Data.Sqlite;

namespace BadmintonDraw.Persistence;

public partial class TournamentWorkspaceStore(WorkspaceFileOperations? fileOperations = null) : ITournamentWorkspaceStore
{
    private readonly WorkspaceFileOperations files = fileOperations ?? new();
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

    public virtual TournamentWorkspace Read(string path)
    {
        try
        {
            using var c = Open(path, SqliteOpenMode.ReadOnly);
            using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA user_version"; var version = Convert.ToInt32(cmd.ExecuteScalar());
            if (version != WorkspaceSchemaVersion.Current)
            {
                var message = version is >= 0 and < 500
                    ? "该文件不是 v5 工作区，请使用 v4.6.0 打开：https://github.com/TonyHuang6666/SZU-Badminton-Draw/releases/tag/v4.6.0"
                    : "该文件使用更新的工作区版本，请升级应用后打开。";
                throw new WorkspaceStoreException("UnsupportedWorkspaceVersion", message);
            }
            cmd.CommandText = "PRAGMA journal_mode";
            if (!string.Equals(cmd.ExecuteScalar()?.ToString(), "delete", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("工作区必须使用 DELETE 日志模式，不能安全复制 WAL 存档。");
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            var tables = new HashSet<string>(StringComparer.Ordinal);
            using (var reader = cmd.ExecuteReader()) while (reader.Read()) tables.Add(reader.GetString(0));
            if (!tables.SetEquals(WorkspaceDatabaseSchema.Tables)) throw new InvalidDataException("工作区表结构缺失或包含未知数据表。");
            cmd.CommandText = "PRAGMA integrity_check"; if (!Equals(cmd.ExecuteScalar(), "ok")) throw new InvalidDataException("SQLite 完整性校验失败。");
            cmd.CommandText = "PRAGMA foreign_key_check"; using (var reader = cmd.ExecuteReader()) if (reader.Read()) throw new InvalidDataException("SQLite 外键校验失败。");
            return WorkspaceSerializer.Read(c);
        }
        catch (WorkspaceStoreException) { throw; }
        catch (Exception ex) { throw new WorkspaceStoreException("InvalidWorkspace", "无法读取工作区：" + ex.Message, ex); }
    }

    public TournamentWorkspace Create(string path, TournamentWorkspace workspace) => Locked(path, full =>
    {
        if (File.Exists(full)) throw new WorkspaceStoreException("DestinationExists", "目标工作区已存在。");
        return Commit(full, workspace, null, null, false).Workspace;
    });

    public WorkspaceMutationResult Mutate(string path, long expectedRevision, Func<TournamentWorkspace, TournamentWorkspace> mutation) => Locked(path, full =>
    {
        var current = Read(full);
        if (current.Revision != expectedRevision) throw new WorkspaceStoreException("RevisionConflict", "工作区已被其他操作修改，请重新打开。");
        return Commit(full, null, current, mutation, true);
    });

    public string CreateBackup(string path) => Locked(path, full => { Read(full); return Backup(full); });

    private WorkspaceMutationResult Commit(string path, TournamentWorkspace? supplied, TournamentWorkspace? current, Func<TournamentWorkspace, TournamentWorkspace>? mutation,
        bool overwrite, long? recoveryRevision = null, Action<string?>? beforePublish = null)
    {
        var candidate = Sibling(path, "candidate"); string? backup = null; var committed = false;
        try
        {
            // Copy only for normal mutation; restore builds from the validated backup aggregate.
            var copy = mutation is not null;
            if (copy) files.Copy(path, candidate);
            using (var c = Open(candidate, copy ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate))
            {
                if (!copy) WorkspaceDatabaseSchema.Initialize(c);
                using var tx = c.BeginTransaction();
                var next = mutation is null ? supplied : mutation(current!);
                if (next is null) throw new InvalidDataException("修改返回空工作区。");
                if (current is not null)
                {
                    if (next.Id != current.Id) throw new InvalidDataException("修改不能改变赛事身份。");
                    next = next with { Revision = recoveryRevision ?? checked(current.Revision + 1), CreatedAt = current.CreatedAt, UpdatedAt = DateTimeOffset.UtcNow };
                }
                WorkspaceSerializer.Write(c, tx, next); tx.Commit();
                // Validate the original candidate too: normalized rows deliberately store global resources once.
                // Reconstructing alone must not conceal inconsistent duplicated inputs in the supplied snapshot.
                TournamentWorkspaceRules.Validate(next);
            }
            Read(candidate); // Domain validation is deliberately after the candidate transaction, before publication.
            if (overwrite) backup = Backup(path);
            beforePublish?.Invoke(backup);
            files.Publish(candidate, path, overwrite); committed = true;
            return new(Read(path), backup ?? "");
        }
        catch (Exception ex)
        {
            throw new WorkspaceStoreException(committed ? "CommittedReadFailed" : "WorkspaceWriteFailed",
                committed ? "工作区已保存，但重新读取失败。请重新打开；备份可用于恢复。" : "工作区未替换，原文件保持不变：" + ex.Message,
                ex, File.Exists(candidate) ? candidate : null, backup, committed);
        }
    }
    private string Backup(string path)
    {
        var backup = Sibling(path, "backup");
        try { files.Copy(path, backup); return backup; }
        catch (Exception ex) { throw new WorkspaceStoreException("BackupFailed", "无法创建备份。", ex); }
    }
    private static string Sibling(string path, string kind) => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "." + System.IO.Path.GetFileNameWithoutExtension(path) + "." + Guid.NewGuid().ToString("N") + "." + kind + ".szbd");
    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false, ForeignKeys = true }.ToString());
        try { c.Open(); return c; } catch { c.Dispose(); throw; }
    }
    private static T Locked<T>(string path, Func<string, T> action)
    {
        var full = System.IO.Path.GetFullPath(path);
        lock (Gates.GetOrAdd(full, _ => new object()))
        {
            // Persistent sidecar avoids the unlink/recreate race between waiting processes.
            FileStream? lease = null; var started = Environment.TickCount64;
            while (lease is null)
            {
                try { lease = new FileStream(full + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException ex) { if (Environment.TickCount64 - started > 10000) throw new WorkspaceStoreException("WorkspaceBusy", "工作区正由其他进程写入。", ex); Thread.Sleep(25); }
            }
            using (lease) return action(full);
        }
    }
}
