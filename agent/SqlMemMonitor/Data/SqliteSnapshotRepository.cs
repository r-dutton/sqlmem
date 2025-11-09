using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using SqlMemDiag.Core.Reporting;
using SqlMemMonitor.Services;

namespace SqlMemMonitor.Data;

public sealed class SqliteSnapshotRepository : ISnapshotRepository
{
    private readonly string _connectionString;
    public SqliteSnapshotRepository(IOptions<SqlMemoryMonitorOptions> options)
    {
        string path = options.Value.DatabasePath;
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true,
            Cache = SqliteCacheMode.Shared
        };

        _connectionString = builder.ToString();
        EnsureDatabase();
    }

    public async Task StoreSnapshotAsync(DateTimeOffset timestamp, SqlMemoryReport report, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var rawTransaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (rawTransaction is not SqliteTransaction transaction)
        {
            throw new InvalidOperationException("Failed to begin SQLite transaction.");
        }

        long snapshotId = await InsertSnapshotAsync(connection, transaction, timestamp, report, cancellationToken).ConfigureAwait(false);
        await InsertProcessesAsync(connection, transaction, snapshotId, report, cancellationToken).ConfigureAwait(false);
        await InsertFindingsAsync(connection, transaction, snapshotId, report, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PurgeExpiredAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM snapshots WHERE captured_at < $cutoff";
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SqlSeriesPoint>> GetSqlSeriesAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - window;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.captured_at,
                   COALESCE(SUM(p.working_set_bytes), 0),
                   COALESCE(SUM(p.private_bytes), 0),
                   COALESCE(SUM(p.locked_bytes), 0),
                   COALESCE(SUM(p.large_page_bytes), 0)
            FROM snapshots s
            LEFT JOIN process_samples p ON p.snapshot_id = s.id AND p.is_sql = 1
            WHERE s.captured_at >= $cutoff
            GROUP BY s.id
            ORDER BY s.captured_at ASC;
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));

        var results = new List<SqlSeriesPoint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            DateTimeOffset timestamp = DateTimeOffset.Parse(reader.GetString(0));
            long workingSet = reader.GetInt64(1);
            long privateBytes = reader.GetInt64(2);
            long lockedBytes = reader.GetInt64(3);
            long largePageBytes = reader.GetInt64(4);
            results.Add(new SqlSeriesPoint(timestamp, workingSet, privateBytes, lockedBytes, largePageBytes));
        }

        return results;
    }

    public async Task<SnapshotOverview?> GetLatestSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        long? snapshotId = await GetLatestSnapshotIdAsync(connection, cancellationToken).ConfigureAwait(false);
        if (snapshotId is null)
        {
            return null;
        }

        await using var summaryCommand = connection.CreateCommand();
        summaryCommand.CommandText =
            "SELECT captured_at, total_physical_bytes, available_physical_bytes, kernel_nonpaged_bytes, kernel_paged_bytes, system_cache_bytes " +
            "FROM snapshots WHERE id = $id";
        summaryCommand.Parameters.AddWithValue("$id", snapshotId.Value);

        await using var reader = await summaryCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        DateTimeOffset timestamp = DateTimeOffset.Parse(reader.GetString(0));
        long totalPhysical = reader.GetInt64(1);
        long available = reader.GetInt64(2);
        long kernelNonPaged = reader.GetInt64(3);
        long kernelPaged = reader.GetInt64(4);
        long systemCache = reader.GetInt64(5);

        IReadOnlyList<ProcessSampleRecord> processes = await GetTopProcessesAsync(connection, snapshotId.Value, cancellationToken).ConfigureAwait(false);

        return new SnapshotOverview(timestamp, totalPhysical, available, kernelNonPaged, kernelPaged, systemCache, processes);
    }

    public async Task<IReadOnlyList<FindingRecord>> GetRecentFindingsAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.captured_at, f.finding_id, f.title, f.description, f.severity
            FROM findings f
            JOIN snapshots s ON s.id = f.snapshot_id
            ORDER BY s.captured_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<FindingRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            DateTimeOffset timestamp = DateTimeOffset.Parse(reader.GetString(0));
            string id = reader.GetString(1);
            string title = reader.GetString(2);
            string description = reader.GetString(3);
            double severity = reader.GetDouble(4);
            results.Add(new FindingRecord(timestamp, id, title, description, severity));
        }

        return results;
    }

    private async Task<long> InsertSnapshotAsync(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset timestamp, SqlMemoryReport report, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO snapshots (captured_at, total_physical_bytes, available_physical_bytes, kernel_nonpaged_bytes, kernel_paged_bytes, system_cache_bytes)
            VALUES ($captured_at, $total, $available, $kernel_np, $kernel_p, $cache);
            """;
        command.Parameters.AddWithValue("$captured_at", timestamp.ToString("O"));
        command.Parameters.AddWithValue("$total", ToSqliteInt(report.Summary.TotalPhysBytes));
        command.Parameters.AddWithValue("$available", ToSqliteInt(report.Summary.AvailPhysBytes));
        command.Parameters.AddWithValue("$kernel_np", ToSqliteInt(report.Summary.KernelNonPagedBytes));
        command.Parameters.AddWithValue("$kernel_p", ToSqliteInt(report.Summary.KernelPagedBytes));
        command.Parameters.AddWithValue("$cache", ToSqliteInt(report.Summary.SystemCacheBytes));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var idCommand = connection.CreateCommand();
        idCommand.Transaction = transaction;
        idCommand.CommandText = "SELECT last_insert_rowid();";
        object? result = await idCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result);
    }

    private async Task InsertProcessesAsync(SqliteConnection connection, SqliteTransaction transaction, long snapshotId, SqlMemoryReport report, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO process_samples (snapshot_id, pid, image_name, working_set_bytes, private_bytes, locked_bytes, large_page_bytes, is_sql, is_vmmem, has_lock_pages)
            VALUES ($snapshot, $pid, $name, $ws, $private, $locked, $large, $is_sql, $is_vmmem, $has_lock);
            """;

        command.Parameters.AddWithValue("$snapshot", snapshotId);
        var pidParam = command.Parameters.Add("$pid", SqliteType.Integer);
        var nameParam = command.Parameters.Add("$name", SqliteType.Text);
        var wsParam = command.Parameters.Add("$ws", SqliteType.Integer);
        var privateParam = command.Parameters.Add("$private", SqliteType.Integer);
        var lockedParam = command.Parameters.Add("$locked", SqliteType.Integer);
        var largeParam = command.Parameters.Add("$large", SqliteType.Integer);
        var isSqlParam = command.Parameters.Add("$is_sql", SqliteType.Integer);
        var isVmmemParam = command.Parameters.Add("$is_vmmem", SqliteType.Integer);
        var hasLockParam = command.Parameters.Add("$has_lock", SqliteType.Integer);

        foreach (var process in report.Processes)
        {
            pidParam.Value = (long)process.Pid;
            nameParam.Value = process.ImageName;
            wsParam.Value = ToSqliteInt(process.WorkingSetBytes);
            privateParam.Value = ToSqliteInt(process.PrivateBytes);
            lockedParam.Value = ToSqliteInt(process.LockedBytes);
            largeParam.Value = ToSqliteInt(process.LargePageBytes);
            isSqlParam.Value = process.IsSqlServer ? 1 : 0;
            isVmmemParam.Value = process.IsVmmemOrVm ? 1 : 0;
            hasLockParam.Value = process.HasLockPagesPrivilege ? 1 : 0;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task InsertFindingsAsync(SqliteConnection connection, SqliteTransaction transaction, long snapshotId, SqlMemoryReport report, CancellationToken cancellationToken)
    {
        if (report.Findings.Count == 0)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO findings (snapshot_id, finding_id, title, description, severity)
            VALUES ($snapshot, $id, $title, $description, $severity);
            """;

        command.Parameters.AddWithValue("$snapshot", snapshotId);
        var idParam = command.Parameters.Add("$id", SqliteType.Text);
        var titleParam = command.Parameters.Add("$title", SqliteType.Text);
        var descriptionParam = command.Parameters.Add("$description", SqliteType.Text);
        var severityParam = command.Parameters.Add("$severity", SqliteType.Real);

        foreach (var finding in report.Findings)
        {
            idParam.Value = finding.Id;
            titleParam.Value = finding.Title;
            descriptionParam.Value = finding.Description;
            severityParam.Value = finding.SeverityScore;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<long?> GetLatestSnapshotIdAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM snapshots ORDER BY captured_at DESC LIMIT 1";
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return null;
        }

        return Convert.ToInt64(result);
    }

    private async Task<IReadOnlyList<ProcessSampleRecord>> GetTopProcessesAsync(SqliteConnection connection, long snapshotId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT pid, image_name, working_set_bytes, private_bytes, locked_bytes, large_page_bytes, is_sql, is_vmmem
            FROM process_samples
            WHERE snapshot_id = $snapshot
            ORDER BY private_bytes DESC
            LIMIT 10;
            """;
        command.Parameters.AddWithValue("$snapshot", snapshotId);

        var processes = new List<ProcessSampleRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            uint pid = (uint)reader.GetInt64(0);
            string image = reader.GetString(1);
            long ws = reader.GetInt64(2);
            long priv = reader.GetInt64(3);
            long locked = reader.GetInt64(4);
            long large = reader.GetInt64(5);
            bool isSql = reader.GetInt64(6) == 1;
            bool isVmmem = reader.GetInt64(7) == 1;
            processes.Add(new ProcessSampleRecord(pid, image, ws, priv, locked, large, isSql, isVmmem));
        }

        return processes;
    }

    private void EnsureDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS snapshots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                captured_at TEXT NOT NULL,
                total_physical_bytes INTEGER NOT NULL,
                available_physical_bytes INTEGER NOT NULL,
                kernel_nonpaged_bytes INTEGER NOT NULL,
                kernel_paged_bytes INTEGER NOT NULL,
                system_cache_bytes INTEGER NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS process_samples (
                snapshot_id INTEGER NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
                pid INTEGER NOT NULL,
                image_name TEXT NOT NULL,
                working_set_bytes INTEGER NOT NULL,
                private_bytes INTEGER NOT NULL,
                locked_bytes INTEGER NOT NULL,
                large_page_bytes INTEGER NOT NULL,
                is_sql INTEGER NOT NULL,
                is_vmmem INTEGER NOT NULL,
                has_lock_pages INTEGER NOT NULL,
                PRIMARY KEY (snapshot_id, pid)
            );
            """;
        command.ExecuteNonQuery();

        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS findings (
                snapshot_id INTEGER NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
                finding_id TEXT NOT NULL,
                title TEXT NOT NULL,
                description TEXT NOT NULL,
                severity REAL NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        command.CommandText = "CREATE INDEX IF NOT EXISTS idx_process_samples_sql ON process_samples(is_sql, snapshot_id);";
        command.ExecuteNonQuery();
    }

    private static long ToSqliteInt(ulong value) => value >= long.MaxValue ? long.MaxValue : (long)value;
}
