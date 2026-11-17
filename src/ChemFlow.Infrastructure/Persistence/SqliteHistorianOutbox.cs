using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace ChemFlow.Infrastructure.Persistence;

public sealed record SilOutboxEntry(
    long Id,
    string IdempotencyKey,
    string MeasurementsJson,
    DateTimeOffset CreatedAtUtc,
    int Attempts,
    string? LastError
);

/// <summary>
/// Persistent SQLite store-and-forward buffer for SIL Modbus telemetry when
/// IndustrialTelemetry.Engine is unreachable or returns transient failures.
/// </summary>
public sealed class SqliteHistorianOutbox
{
    private const int MaxBufferedEntries = 2000;
    private readonly SqliteDatabaseInitializer _dbInitializer;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _tableInitialized;

    public SqliteHistorianOutbox(SqliteDatabaseInitializer dbInitializer)
    {
        _dbInitializer = dbInitializer;
    }

    private async Task EnsureTableAsync(CancellationToken ct)
    {
        if (_tableInitialized)
            return;

        await _schemaLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_tableInitialized)
                return;

            await using var conn = _dbInitializer.CreateOpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS SilHistorianOutbox (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    IdempotencyKey TEXT NOT NULL UNIQUE,
                    MeasurementsJson TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    Attempts INTEGER NOT NULL DEFAULT 0,
                    LastError TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_SilHistorianOutbox_Id ON SilHistorianOutbox(Id);
                """;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _tableInitialized = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    public async Task EnqueueAsync(
        string idempotencyKey,
        string measurementsJson,
        string? lastError,
        CancellationToken ct = default)
    {
        await EnsureTableAsync(ct).ConfigureAwait(false);
        await using var conn = _dbInitializer.CreateOpenConnection();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var insertCmd = conn.CreateCommand())
        {
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT OR IGNORE INTO SilHistorianOutbox (IdempotencyKey, MeasurementsJson, CreatedAtUtc, Attempts, LastError)
                VALUES ($key, $json, $ts, 1, $err);
                """;
            insertCmd.Parameters.AddWithValue("$key", idempotencyKey);
            insertCmd.Parameters.AddWithValue("$json", measurementsJson);
            insertCmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
            insertCmd.Parameters.AddWithValue("$err", (object?)lastError ?? DBNull.Value);
            await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Enforce bounded retention on oldest entries
        await using (var trimCmd = conn.CreateCommand())
        {
            trimCmd.Transaction = tx;
            trimCmd.CommandText = """
                DELETE FROM SilHistorianOutbox
                WHERE Id NOT IN (
                    SELECT Id FROM SilHistorianOutbox ORDER BY Id DESC LIMIT $cap
                );
                """;
            trimCmd.Parameters.AddWithValue("$cap", MaxBufferedEntries);
            await trimCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SilOutboxEntry>> PeekOldestAsync(int maxCount = 25, CancellationToken ct = default)
    {
        await EnsureTableAsync(ct).ConfigureAwait(false);
        var entries = new List<SilOutboxEntry>();

        await using var conn = _dbInitializer.CreateOpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, IdempotencyKey, MeasurementsJson, CreatedAtUtc, Attempts, LastError
            FROM SilHistorianOutbox
            ORDER BY Id ASC
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$lim", Math.Clamp(maxCount, 1, 200));

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            entries.Add(new SilOutboxEntry(
                Id: reader.GetInt64(0),
                IdempotencyKey: reader.GetString(1),
                MeasurementsJson: reader.GetString(2),
                CreatedAtUtc: DateTimeOffset.TryParse(reader.GetString(3), out var dt) ? dt : DateTimeOffset.UtcNow,
                Attempts: reader.GetInt32(4),
                LastError: reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return entries;
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await EnsureTableAsync(ct).ConfigureAwait(false);
        await using var conn = _dbInitializer.CreateOpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM SilHistorianOutbox WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkFailedAsync(long id, string? error, CancellationToken ct = default)
    {
        await EnsureTableAsync(ct).ConfigureAwait(false);
        await using var conn = _dbInitializer.CreateOpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE SilHistorianOutbox
            SET Attempts = Attempts + 1, LastError = $err
            WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<long> CountPendingAsync(CancellationToken ct = default)
    {
        await EnsureTableAsync(ct).ConfigureAwait(false);
        await using var conn = _dbInitializer.CreateOpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM SilHistorianOutbox;";
        object? scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return scalar is long count ? count : Convert.ToInt64(scalar ?? 0);
    }
}
