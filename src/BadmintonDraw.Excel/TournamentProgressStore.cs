using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BadmintonDraw.Core;
using Microsoft.Data.Sqlite;

namespace BadmintonDraw.Excel;

public sealed class TournamentProgressStore
{
    public const int CurrentSchemaVersion = 1;

    private const int BackupLimit = 10;
    private readonly MatchRecordReader _matchRecordReader = new();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public TournamentProgressState Create(string filePath, TournamentProgressSnapshot snapshot)
    {
        ValidateProgressPath(filePath);
        if (!snapshot.Schedule.IsComplete)
        {
            throw new TournamentProgressException("不能为不完整赛程创建赛事存档。");
        }

        var directory = Path.GetDirectoryName(filePath) ?? ".";
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var connection = OpenConnection(tempPath, SqliteOpenMode.ReadWriteCreate))
            {
                InitializeSchema(connection);
                using var transaction = connection.BeginTransaction();
                WriteMetadata(connection, transaction, "schema_version", CurrentSchemaVersion.ToString());
                WriteMetadata(connection, transaction, "tournament_id", snapshot.TournamentId);
                WriteMetadata(connection, transaction, "event_name", snapshot.EventName);
                WriteMetadata(connection, transaction, "created_at", snapshot.CreatedAt.ToString("O"));
                WriteMetadata(connection, transaction, "updated_at", snapshot.UpdatedAt.ToString("O"));

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO snapshot (
                        id,
                        source_input_path,
                        draw_result_json,
                        participants_json,
                        import_warnings_json,
                        schedule_json)
                    VALUES (1, $sourceInputPath, $drawResult, $participants, $warnings, $schedule);
                    """;
                command.Parameters.AddWithValue("$sourceInputPath", (object?)snapshot.SourceInputPath ?? DBNull.Value);
                command.Parameters.AddWithValue("$drawResult", Serialize(snapshot.DrawResult));
                command.Parameters.AddWithValue("$participants", Serialize(snapshot.Participants));
                command.Parameters.AddWithValue("$warnings", Serialize(snapshot.ImportWarnings));
                command.Parameters.AddWithValue("$schedule", Serialize(snapshot.Schedule));
                command.ExecuteNonQuery();
                transaction.Commit();

                EnsureIntegrity(connection);
            }

            if (File.Exists(filePath))
            {
                _ = CreateBackup(filePath);
            }

            File.Move(tempPath, filePath, overwrite: true);
            return Read(filePath);
        }
        catch (TournamentProgressException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or SqliteException or JsonException)
        {
            throw new TournamentProgressException($"无法创建赛事存档：{ex.Message}", ex);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public TournamentProgressState Read(string filePath)
    {
        ValidateProgressPath(filePath);
        if (!File.Exists(filePath))
        {
            throw new TournamentProgressException($"找不到赛事存档：{filePath}");
        }

        try
        {
            using var connection = OpenConnection(filePath, SqliteOpenMode.ReadOnly);
            EnsureIntegrity(connection);
            var schemaVersion = int.Parse(ReadMetadata(connection, "schema_version"));
            if (schemaVersion > CurrentSchemaVersion)
            {
                throw new TournamentProgressException(
                    $"该赛事存档由更高版本程序创建（存档版本 {schemaVersion}，当前支持 {CurrentSchemaVersion}），请升级程序后打开。");
            }

            if (schemaVersion < CurrentSchemaVersion)
            {
                throw new TournamentProgressException(
                    $"该赛事存档版本过旧（存档版本 {schemaVersion}），当前版本暂不支持自动升级。");
            }

            using var snapshotCommand = connection.CreateCommand();
            snapshotCommand.CommandText =
                """
                SELECT source_input_path, draw_result_json, participants_json, import_warnings_json, schedule_json
                FROM snapshot
                WHERE id = 1;
                """;
            using var snapshotReader = snapshotCommand.ExecuteReader();
            if (!snapshotReader.Read())
            {
                throw new TournamentProgressException("赛事存档缺少核心快照。");
            }

            var snapshot = new TournamentProgressSnapshot(
                ReadMetadata(connection, "tournament_id"),
                ReadMetadata(connection, "event_name"),
                DateTimeOffset.Parse(ReadMetadata(connection, "created_at")),
                DateTimeOffset.Parse(ReadMetadata(connection, "updated_at")),
                snapshotReader.IsDBNull(0) ? null : snapshotReader.GetString(0),
                Deserialize<DrawResult>(snapshotReader.GetString(1)),
                Deserialize<List<DrawParticipant>>(snapshotReader.GetString(2)),
                Deserialize<List<ParticipantImportWarning>>(snapshotReader.GetString(3)),
                Deserialize<SchedulePlan>(snapshotReader.GetString(4)));
            snapshotReader.Close();

            var results = ReadResults(connection);
            var pendingMatches = ReadStringColumn(connection, "SELECT match_id FROM pending_matches ORDER BY match_id;");
            var processedDays = ReadStringColumn(connection, "SELECT day_label FROM processed_days ORDER BY day_label;");
            var logs = ReadImportLogs(connection);

            ValidateSnapshot(snapshot, results, pendingMatches);
            return new TournamentProgressState(snapshot, results, pendingMatches, processedDays, logs);
        }
        catch (TournamentProgressException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or SqliteException or JsonException or FormatException)
        {
            throw new TournamentProgressException($"无法打开赛事存档：{ex.Message}", ex);
        }
    }

    public TournamentProgressImportPreview PreviewImport(string filePath, IEnumerable<string> recordFilePaths)
    {
        return EvaluateImport(filePath, recordFilePaths).Preview;
    }

    public TournamentProgressImportOutcome Import(
        string filePath,
        IEnumerable<string> recordFilePaths,
        bool allowCorrections = false)
    {
        var evaluation = EvaluateImport(filePath, recordFilePaths);
        if (evaluation.Preview.Corrections.Count > 0 && !allowCorrections)
        {
            throw new TournamentProgressException(
                $"有 {evaluation.Preview.Corrections.Count} 场比赛的比分、用时或比赛日与存档不同，需要裁判长确认后才能更正。");
        }

        if (evaluation.Files.Count == 0)
        {
            return new TournamentProgressImportOutcome(evaluation.State, evaluation.Preview, null);
        }

        var backupPath = CreateBackup(filePath);
        try
        {
            using var connection = OpenConnection(filePath, SqliteOpenMode.ReadWrite);
            using var transaction = connection.BeginTransaction();
            var currentResults = evaluation.State.Results.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var importedAt = DateTimeOffset.UtcNow;

            foreach (var file in evaluation.Files)
            {
                var fileCorrectionCount = 0;
                foreach (var result in file.ImportResult.Results.Values)
                {
                    if (currentResults.TryGetValue(result.MatchName, out var existing))
                    {
                        if (existing == result)
                        {
                            continue;
                        }

                        fileCorrectionCount++;
                        InsertResultHistory(connection, transaction, result.MatchName, existing, result, importedAt, file.Hash);
                        UpdateResult(connection, transaction, result, file, importedAt);
                    }
                    else
                    {
                        InsertResult(connection, transaction, result, file, importedAt);
                    }

                    currentResults[result.MatchName] = result;
                }

                InsertImportLog(connection, transaction, file, importedAt, fileCorrectionCount);
            }

            ReplaceStringTable(
                connection,
                transaction,
                "pending_matches",
                "match_id",
                evaluation.Preview.ProjectedCumulativeResult.PendingMatchNames);
            ReplaceStringTable(
                connection,
                transaction,
                "processed_days",
                "day_label",
                evaluation.Preview.ProjectedCumulativeResult.DayLabels);
            WriteMetadata(connection, transaction, "updated_at", importedAt.ToString("O"));
            transaction.Commit();

            EnsureIntegrity(connection);
            var updatedState = Read(filePath);
            return new TournamentProgressImportOutcome(updatedState, evaluation.Preview, backupPath);
        }
        catch (TournamentProgressException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            throw new TournamentProgressException(
                $"更新赛事存档失败，原存档已保留备份：{backupPath}。错误：{ex.Message}",
                ex);
        }
    }

    private ImportEvaluation EvaluateImport(string filePath, IEnumerable<string> recordFilePaths)
    {
        var paths = recordFilePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0)
        {
            throw new TournamentProgressException("请选择至少一张赛程记录表。");
        }

        var state = Read(filePath);
        var knownHashes = state.ImportLogs
            .Select(log => log.SourceHash)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicateFiles = new List<string>();
        var compatibilityWarnings = new List<string>();
        var files = new List<RecordFileImport>();
        var scheduleMatchNames = state.Snapshot.Schedule.Matches
            .Select(match => match.MatchName)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                throw new TournamentProgressException($"找不到赛程记录表：{path}");
            }

            var hash = ComputeFileHash(path);
            if (knownHashes.Contains(hash) || !selectedHashes.Add(hash))
            {
                duplicateFiles.Add(Path.GetFileName(path));
                continue;
            }

            var importResult = _matchRecordReader.Read(path);
            ValidateTournamentIdentity(state.Snapshot.TournamentId, path, importResult, compatibilityWarnings);
            ValidateImportedMatches(path, importResult, scheduleMatchNames);
            files.Add(new RecordFileImport(path, hash, importResult));
        }

        var candidateResults = state.Results.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var pendingMatches = state.PendingMatchNames.ToHashSet(StringComparer.Ordinal);
        var processedDays = state.ProcessedDayLabels.ToHashSet(StringComparer.Ordinal);
        var selectedResults = new Dictionary<string, MatchRecordResult>(StringComparer.Ordinal);
        var selectedDays = new List<string>();
        var missingRows = new List<string>();
        var validationIssues = new List<string>();
        var selectedPending = new HashSet<string>(StringComparer.Ordinal);
        var tournamentIds = new HashSet<string>(StringComparer.Ordinal);
        var corrections = new Dictionary<string, TournamentProgressCorrection>(StringComparer.Ordinal);
        var expectedMatchCount = 0;
        var newResultCount = 0;

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file.Path);
            var importResult = file.ImportResult;
            expectedMatchCount += importResult.ExpectedMatchCount;
            missingRows.AddRange(importResult.MissingResultRows.Select(row => $"{fileName}：{row}"));
            validationIssues.AddRange(importResult.ValidationIssues.Select(issue => $"{fileName}：{issue}"));
            selectedPending.UnionWith(importResult.PendingMatchNames);
            tournamentIds.UnionWith(importResult.TournamentIds);
            foreach (var dayLabel in importResult.DayLabels)
            {
                if (!selectedDays.Contains(dayLabel, StringComparer.Ordinal))
                {
                    selectedDays.Add(dayLabel);
                }

                processedDays.Add(dayLabel);
            }

            foreach (var result in importResult.Results.Values)
            {
                if (selectedResults.TryGetValue(result.MatchName, out var selectedExisting)
                    && selectedExisting != result)
                {
                    throw new TournamentProgressException(
                        $"场次“{result.MatchName}”在本次选择的多张记录表中内容不一致。");
                }

                selectedResults[result.MatchName] = result;
                if (candidateResults.TryGetValue(result.MatchName, out var existing))
                {
                    if (!string.Equals(existing.Winner, result.Winner, StringComparison.Ordinal)
                        || !string.Equals(existing.Loser, result.Loser, StringComparison.Ordinal))
                    {
                        throw new TournamentProgressException(
                            $"场次“{result.MatchName}”与赛事存档中的胜负方冲突："
                            + $"{existing.Winner} / {existing.Loser} 与 {result.Winner} / {result.Loser}。");
                    }

                    if (existing != result)
                    {
                        corrections[result.MatchName] = new TournamentProgressCorrection(result.MatchName, existing, result);
                        candidateResults[result.MatchName] = result;
                    }

                    continue;
                }

                candidateResults[result.MatchName] = result;
                newResultCount++;
            }

            pendingMatches.UnionWith(importResult.PendingMatchNames);
            pendingMatches.ExceptWith(candidateResults.Keys);
        }

        var selectedImportResult = new MatchRecordImportResult(
            selectedResults,
            selectedDays,
            expectedMatchCount,
            missingRows,
            validationIssues,
            selectedPending.ToList(),
            tournamentIds.ToList());
        var projectedCumulativeResult = new MatchRecordImportResult(
            candidateResults,
            processedDays.OrderBy(day => day, StringComparer.Ordinal).ToList(),
            candidateResults.Count + pendingMatches.Count,
            pendingMatches.Select(matchName => $"{matchName} 尚未填写胜方").ToList(),
            [],
            pendingMatches.OrderBy(matchName => matchName, StringComparer.Ordinal).ToList(),
            [state.Snapshot.TournamentId]);
        var preview = new TournamentProgressImportPreview(
            selectedImportResult,
            projectedCumulativeResult,
            corrections.Values.ToList(),
            duplicateFiles,
            compatibilityWarnings,
            newResultCount,
            files.Count);
        return new ImportEvaluation(state, files, preview);
    }

    private static void ValidateTournamentIdentity(
        string tournamentId,
        string path,
        MatchRecordImportResult importResult,
        ICollection<string> compatibilityWarnings)
    {
        if (importResult.TournamentIds.Count == 0)
        {
            compatibilityWarnings.Add(
                $"{Path.GetFileName(path)} 未包含赛事标识，已按场次标识兼容校验；建议使用当前存档重新导出记录表。");
            return;
        }

        if (importResult.TournamentIds.Count != 1
            || !string.Equals(importResult.TournamentIds[0], tournamentId, StringComparison.Ordinal))
        {
            throw new TournamentProgressException(
                $"{Path.GetFileName(path)} 不属于当前赛事存档，请选择由本存档导出的记录表。");
        }
    }

    private static void ValidateImportedMatches(
        string path,
        MatchRecordImportResult importResult,
        IReadOnlySet<string> scheduleMatchNames)
    {
        var unknownMatches = importResult.Results.Keys
            .Concat(importResult.PendingMatchNames)
            .Where(matchName => !scheduleMatchNames.Contains(matchName))
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToList();
        if (unknownMatches.Count > 0)
        {
            throw new TournamentProgressException(
                $"{Path.GetFileName(path)} 包含当前赛事不存在的场次：{string.Join("、", unknownMatches)}。");
        }
    }

    private static void ValidateSnapshot(
        TournamentProgressSnapshot snapshot,
        IReadOnlyDictionary<string, MatchRecordResult> results,
        IReadOnlyList<string> pendingMatches)
    {
        if (string.IsNullOrWhiteSpace(snapshot.TournamentId)
            || string.IsNullOrWhiteSpace(snapshot.EventName))
        {
            throw new TournamentProgressException("赛事存档缺少赛事标识或赛事名称。");
        }

        var matchNames = snapshot.Schedule.Matches
            .Select(match => match.MatchName)
            .ToHashSet(StringComparer.Ordinal);
        var unknownMatches = results.Keys
            .Concat(pendingMatches)
            .Where(matchName => !matchNames.Contains(matchName))
            .Take(5)
            .ToList();
        if (unknownMatches.Count > 0)
        {
            throw new TournamentProgressException(
                $"赛事存档包含不属于当前赛程的场次：{string.Join("、", unknownMatches)}。");
        }
    }

    private static void InitializeSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = DELETE;
            PRAGMA foreign_keys = ON;
            PRAGMA user_version = 1;

            CREATE TABLE metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE snapshot (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                source_input_path TEXT NULL,
                draw_result_json TEXT NOT NULL,
                participants_json TEXT NOT NULL,
                import_warnings_json TEXT NOT NULL,
                schedule_json TEXT NOT NULL
            );

            CREATE TABLE match_results (
                match_id TEXT PRIMARY KEY,
                day_label TEXT NOT NULL,
                winner TEXT NOT NULL,
                loser TEXT NOT NULL,
                score TEXT NOT NULL,
                duration TEXT NOT NULL,
                source_file_name TEXT NOT NULL,
                source_path TEXT NOT NULL,
                source_hash TEXT NOT NULL,
                imported_at TEXT NOT NULL
            );

            CREATE TABLE pending_matches (
                match_id TEXT PRIMARY KEY
            );

            CREATE TABLE processed_days (
                day_label TEXT PRIMARY KEY
            );

            CREATE TABLE import_logs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                imported_at TEXT NOT NULL,
                source_file_name TEXT NOT NULL,
                source_path TEXT NOT NULL,
                source_hash TEXT NOT NULL UNIQUE,
                expected_match_count INTEGER NOT NULL,
                imported_result_count INTEGER NOT NULL,
                warning_count INTEGER NOT NULL,
                correction_count INTEGER NOT NULL
            );

            CREATE TABLE result_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                match_id TEXT NOT NULL,
                previous_result_json TEXT NOT NULL,
                replacement_result_json TEXT NOT NULL,
                changed_at TEXT NOT NULL,
                source_hash TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection OpenConnection(string filePath, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void EnsureIntegrity(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = command.ExecuteScalar()?.ToString();
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new TournamentProgressException($"赛事存档完整性检查失败：{result ?? "未知错误"}");
        }
    }

    private static void WriteMetadata(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO metadata (key, value)
            VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static string ReadMetadata(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar()?.ToString()
            ?? throw new TournamentProgressException($"赛事存档缺少元数据：{key}");
    }

    private static IReadOnlyDictionary<string, MatchRecordResult> ReadResults(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT match_id, day_label, winner, loser, score, duration
            FROM match_results
            ORDER BY match_id;
            """;
        using var reader = command.ExecuteReader();
        var results = new Dictionary<string, MatchRecordResult>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var result = new MatchRecordResult(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5));
            results.Add(result.MatchName, result);
        }

        return results;
    }

    private static IReadOnlyList<string> ReadStringColumn(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static IReadOnlyList<TournamentProgressImportLog> ReadImportLogs(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, imported_at, source_file_name, source_path, source_hash,
                   expected_match_count, imported_result_count, warning_count, correction_count
            FROM import_logs
            ORDER BY id;
            """;
        using var reader = command.ExecuteReader();
        var logs = new List<TournamentProgressImportLog>();
        while (reader.Read())
        {
            logs.Add(new TournamentProgressImportLog(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8)));
        }

        return logs;
    }

    private static void InsertResult(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MatchRecordResult result,
        RecordFileImport file,
        DateTimeOffset importedAt)
    {
        using var command = BuildResultCommand(connection, transaction, result, file, importedAt);
        command.CommandText =
            """
            INSERT INTO match_results (
                match_id, day_label, winner, loser, score, duration,
                source_file_name, source_path, source_hash, imported_at)
            VALUES (
                $matchId, $dayLabel, $winner, $loser, $score, $duration,
                $sourceFileName, $sourcePath, $sourceHash, $importedAt);
            """;
        command.ExecuteNonQuery();
    }

    private static void UpdateResult(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MatchRecordResult result,
        RecordFileImport file,
        DateTimeOffset importedAt)
    {
        using var command = BuildResultCommand(connection, transaction, result, file, importedAt);
        command.CommandText =
            """
            UPDATE match_results
            SET day_label = $dayLabel,
                winner = $winner,
                loser = $loser,
                score = $score,
                duration = $duration,
                source_file_name = $sourceFileName,
                source_path = $sourcePath,
                source_hash = $sourceHash,
                imported_at = $importedAt
            WHERE match_id = $matchId;
            """;
        command.ExecuteNonQuery();
    }

    private static SqliteCommand BuildResultCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MatchRecordResult result,
        RecordFileImport file,
        DateTimeOffset importedAt)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$matchId", result.MatchName);
        command.Parameters.AddWithValue("$dayLabel", result.DayLabel);
        command.Parameters.AddWithValue("$winner", result.Winner);
        command.Parameters.AddWithValue("$loser", result.Loser);
        command.Parameters.AddWithValue("$score", result.Score);
        command.Parameters.AddWithValue("$duration", result.Duration);
        command.Parameters.AddWithValue("$sourceFileName", Path.GetFileName(file.Path));
        command.Parameters.AddWithValue("$sourcePath", file.Path);
        command.Parameters.AddWithValue("$sourceHash", file.Hash);
        command.Parameters.AddWithValue("$importedAt", importedAt.ToString("O"));
        return command;
    }

    private static void InsertResultHistory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string matchName,
        MatchRecordResult previous,
        MatchRecordResult replacement,
        DateTimeOffset changedAt,
        string sourceHash)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO result_history (
                match_id, previous_result_json, replacement_result_json, changed_at, source_hash)
            VALUES ($matchId, $previous, $replacement, $changedAt, $sourceHash);
            """;
        command.Parameters.AddWithValue("$matchId", matchName);
        command.Parameters.AddWithValue("$previous", Serialize(previous));
        command.Parameters.AddWithValue("$replacement", Serialize(replacement));
        command.Parameters.AddWithValue("$changedAt", changedAt.ToString("O"));
        command.Parameters.AddWithValue("$sourceHash", sourceHash);
        command.ExecuteNonQuery();
    }

    private static void InsertImportLog(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecordFileImport file,
        DateTimeOffset importedAt,
        int correctionCount)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO import_logs (
                imported_at, source_file_name, source_path, source_hash,
                expected_match_count, imported_result_count, warning_count, correction_count)
            VALUES (
                $importedAt, $sourceFileName, $sourcePath, $sourceHash,
                $expectedMatchCount, $importedResultCount, $warningCount, $correctionCount);
            """;
        command.Parameters.AddWithValue("$importedAt", importedAt.ToString("O"));
        command.Parameters.AddWithValue("$sourceFileName", Path.GetFileName(file.Path));
        command.Parameters.AddWithValue("$sourcePath", file.Path);
        command.Parameters.AddWithValue("$sourceHash", file.Hash);
        command.Parameters.AddWithValue("$expectedMatchCount", file.ImportResult.ExpectedMatchCount);
        command.Parameters.AddWithValue("$importedResultCount", file.ImportResult.Results.Count);
        command.Parameters.AddWithValue(
            "$warningCount",
            file.ImportResult.MissingResultRows.Count + file.ImportResult.ValidationIssues.Count);
        command.Parameters.AddWithValue("$correctionCount", correctionCount);
        command.ExecuteNonQuery();
    }

    private static void ReplaceStringTable(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName,
        IEnumerable<string> values)
    {
        using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = $"DELETE FROM {tableName};";
            deleteCommand.ExecuteNonQuery();
        }

        foreach (var value in values.Distinct(StringComparer.Ordinal))
        {
            using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = $"INSERT INTO {tableName} ({columnName}) VALUES ($value);";
            insertCommand.Parameters.AddWithValue("$value", value);
            insertCommand.ExecuteNonQuery();
        }
    }

    private static string CreateBackup(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return "";
        }

        var backupDirectory = Path.Combine(Path.GetDirectoryName(filePath) ?? ".", "Backups");
        Directory.CreateDirectory(backupDirectory);
        var stem = Path.GetFileNameWithoutExtension(filePath);
        var backupPath = Path.Combine(
            backupDirectory,
            $"{stem}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.szbd");
        File.Copy(filePath, backupPath, overwrite: false);

        foreach (var oldBackup in Directory
                     .GetFiles(backupDirectory, $"{stem}_*.szbd")
                     .OrderByDescending(path => path, StringComparer.Ordinal)
                     .Skip(BackupLimit))
        {
            File.Delete(oldBackup);
        }

        return backupPath;
    }

    private static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static T Deserialize<T>(string value)
    {
        return JsonSerializer.Deserialize<T>(value, JsonOptions)
            ?? throw new TournamentProgressException($"赛事存档中的 {typeof(T).Name} 数据为空。");
    }

    private static void ValidateProgressPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new TournamentProgressException("请选择赛事存档文件。");
        }
    }

    private sealed record RecordFileImport(
        string Path,
        string Hash,
        MatchRecordImportResult ImportResult);

    private sealed record ImportEvaluation(
        TournamentProgressState State,
        IReadOnlyList<RecordFileImport> Files,
        TournamentProgressImportPreview Preview);
}
