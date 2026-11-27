using System.Data;
using System.Globalization;
using JspIt.WebhookDelivery.Persistence;
using Microsoft.Data.Sqlite;

namespace JspIt.WebhookDelivery.Sqlite;

/// <summary>
/// SQLite implementation of <see cref="IDeliveryStore"/>. Suitable for single-node services and as a
/// reference for other stores. Multiple processes may share one database file on a local disk
/// (not a network share); leasing uses a single atomic <c>UPDATE ... RETURNING</c> statement.
/// </summary>
/// <remarks>
/// The schema is created or migrated on first use. Tables are prefixed <c>wdk_</c> so the store can live in an
/// existing application database. The database contains payloads (never signing secrets); protect the file
/// with filesystem permissions appropriate for the data you send.
/// </remarks>
public sealed class SqliteDeliveryStore : IDeliveryStore, IDisposable
{
    /// <summary>Schema version written by this release.</summary>
    public const int SchemaVersion = 1;

    private const string Columns =
        "id, destination_id, idempotency_key, event_type, content_type, payload, payload_sha256, status, attempt_count, " +
        "created_at, updated_at, next_attempt_at, expires_at, last_attempt_at, completed_at, last_status_code, " +
        "last_error_category, last_error, lease_owner, lease_token, lease_expires_at";

    private readonly string _connectionString;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private volatile bool _initialized;

    /// <summary>Creates a store for a SQLite connection string, e.g. <c>Data Source=webhooks.db</c>.</summary>
    public SqliteDeliveryStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (builder.DefaultTimeout < 5)
        {
            builder.DefaultTimeout = 30;
        }

        _connectionString = builder.ToString();
    }

    /// <summary>Creates a store for a database file path.</summary>
    public static SqliteDeliveryStore ForFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new SqliteDeliveryStore(new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString());
    }

    /// <summary>Creates or migrates the schema. Called automatically on first use.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = await OpenRawAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", null, cancellationToken).ConfigureAwait(false);

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS wdk_schema (version INTEGER NOT NULL);", transaction, cancellationToken).ConfigureAwait(false);

            int current;
            await using (var cmd = Command(connection, "SELECT COALESCE(MAX(version), 0) FROM wdk_schema;", transaction))
            {
                current = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }

            if (current > SchemaVersion)
            {
                throw new InvalidOperationException($"Database schema version {current} is newer than this library supports ({SchemaVersion}). Upgrade JspIt.WebhookDelivery.Sqlite.");
            }

            if (current < 1)
            {
                await ExecuteAsync(connection, """
                    CREATE TABLE wdk_deliveries (
                        id                  TEXT    NOT NULL PRIMARY KEY,
                        destination_id      TEXT    NOT NULL,
                        idempotency_key     TEXT    NOT NULL,
                        event_type          TEXT    NOT NULL,
                        content_type        TEXT    NOT NULL,
                        payload             BLOB    NOT NULL,
                        payload_sha256      TEXT    NOT NULL,
                        status              INTEGER NOT NULL,
                        attempt_count       INTEGER NOT NULL DEFAULT 0,
                        created_at          INTEGER NOT NULL,
                        updated_at          INTEGER NOT NULL,
                        next_attempt_at     INTEGER NOT NULL,
                        expires_at          INTEGER NOT NULL,
                        last_attempt_at     INTEGER NULL,
                        completed_at        INTEGER NULL,
                        last_status_code    INTEGER NULL,
                        last_error_category TEXT    NULL,
                        last_error          TEXT    NULL,
                        lease_owner         TEXT    NULL,
                        lease_token         TEXT    NULL,
                        lease_expires_at    INTEGER NULL,
                        UNIQUE (destination_id, idempotency_key)
                    );
                    CREATE INDEX ix_wdk_deliveries_due ON wdk_deliveries (status, next_attempt_at);
                    CREATE INDEX ix_wdk_deliveries_lease ON wdk_deliveries (status, lease_expires_at);
                    CREATE INDEX ix_wdk_deliveries_completed ON wdk_deliveries (completed_at) WHERE completed_at IS NOT NULL;
                    CREATE TABLE wdk_attempts (
                        seq             INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        delivery_id     TEXT    NOT NULL,
                        attempt_number  INTEGER NOT NULL,
                        lease_owner     TEXT    NOT NULL,
                        started_at      INTEGER NOT NULL,
                        completed_at    INTEGER NOT NULL,
                        outcome         INTEGER NOT NULL,
                        status_code     INTEGER NULL,
                        error_category  TEXT    NULL,
                        error_message   TEXT    NULL
                    );
                    CREATE INDEX ix_wdk_attempts_delivery ON wdk_attempts (delivery_id, attempt_number);
                    INSERT INTO wdk_schema (version) VALUES (1);
                    """, transaction, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<EnqueueResult> EnqueueAsync(NewDelivery delivery, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        int inserted;
        await using (var cmd = Command(connection, """
            INSERT INTO wdk_deliveries (id, destination_id, idempotency_key, event_type, content_type, payload, payload_sha256,
                status, attempt_count, created_at, updated_at, next_attempt_at, expires_at)
            VALUES ($id, $dest, $key, $event, $ctype, $payload, $sha, 0, 0, $now, $now, $now, $expires)
            ON CONFLICT (destination_id, idempotency_key) DO NOTHING;
            """))
        {
            cmd.Parameters.AddWithValue("$id", delivery.Id);
            cmd.Parameters.AddWithValue("$dest", delivery.DestinationId);
            cmd.Parameters.AddWithValue("$key", delivery.IdempotencyKey);
            cmd.Parameters.AddWithValue("$event", delivery.EventType);
            cmd.Parameters.AddWithValue("$ctype", delivery.ContentType);
            cmd.Parameters.Add("$payload", SqliteType.Blob).Value = delivery.Payload.ToArray();
            cmd.Parameters.AddWithValue("$sha", delivery.PayloadSha256);
            cmd.Parameters.AddWithValue("$now", ToUnixMs(delivery.CreatedAt));
            cmd.Parameters.AddWithValue("$expires", ToUnixMs(delivery.ExpiresAt));
            inserted = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var record = await QuerySingleAsync(connection, "destination_id = $dest AND idempotency_key = $key",
            p =>
            {
                p.AddWithValue("$dest", delivery.DestinationId);
                p.AddWithValue("$key", delivery.IdempotencyKey);
            }, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Inserted delivery could not be read back.");

        return new EnqueueResult(record, inserted == 1);
    }

    /// <inheritdoc />
    public async Task<DeliveryRecord?> GetAsync(string deliveryId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QuerySingleAsync(connection, "id = $id", p => p.AddWithValue("$id", deliveryId), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DeliveryRecord?> FindByIdempotencyKeyAsync(string destinationId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QuerySingleAsync(connection, "destination_id = $dest AND idempotency_key = $key",
            p =>
            {
                p.AddWithValue("$dest", destinationId);
                p.AddWithValue("$key", idempotencyKey);
            }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeliveryRecord>> LeaseDueAsync(LeaseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // One statement: SQLite executes it atomically under the database write lock, so two processes
        // can never lease the same row.
        await using var cmd = Command(connection, $"""
            UPDATE wdk_deliveries
            SET last_error_category = CASE WHEN status = 1 THEN '{DeliveryErrorCategories.LeaseExpired}' ELSE last_error_category END,
                last_error          = CASE WHEN status = 1 THEN 'Previous attempt''s lease expired before it completed (process crash or stall).' ELSE last_error END,
                status = 1,
                attempt_count = attempt_count + 1,
                lease_owner = $owner,
                lease_token = lower(hex(randomblob(16))),
                lease_expires_at = $leaseExpires,
                updated_at = $now
            WHERE id IN (
                SELECT id FROM wdk_deliveries
                WHERE (status = 0 AND next_attempt_at <= $now) OR (status = 1 AND lease_expires_at <= $now)
                ORDER BY next_attempt_at, created_at
                LIMIT $limit)
            RETURNING {Columns};
            """);
        cmd.Parameters.AddWithValue("$owner", request.LeaseOwner);
        cmd.Parameters.AddWithValue("$now", ToUnixMs(request.Now));
        cmd.Parameters.AddWithValue("$leaseExpires", ToUnixMs(request.Now + request.LeaseDuration));
        cmd.Parameters.AddWithValue("$limit", request.MaxCount);

        var results = new List<DeliveryRecord>();
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(Read(reader));
            }
        }

        results.Sort((a, b) => a.NextAttemptAt.CompareTo(b.NextAttemptAt));
        return results;
    }

    /// <inheritdoc />
    public async Task<bool> CompleteAttemptAsync(AttemptCompletion completion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (completion.Outcome == AttemptOutcome.RetryScheduled && completion.NextAttemptAt is null)
        {
            throw new ArgumentException("RetryScheduled requires NextAttemptAt.", nameof(completion));
        }

        var status = completion.Outcome switch
        {
            AttemptOutcome.Succeeded => DeliveryStatus.Succeeded,
            AttemptOutcome.RetryScheduled => DeliveryStatus.Pending,
            AttemptOutcome.Failed => DeliveryStatus.Failed,
            AttemptOutcome.Cancelled => DeliveryStatus.Cancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(completion), "Unknown outcome."),
        };
        var success = completion.Outcome == AttemptOutcome.Succeeded;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var insert = Command(connection, """
            INSERT INTO wdk_attempts (delivery_id, attempt_number, lease_owner, started_at, completed_at, outcome, status_code, error_category, error_message)
            SELECT $id, $attempt, $owner, $started, $completed, $outcome, $statusCode, $category, $message
            WHERE EXISTS (SELECT 1 FROM wdk_deliveries WHERE id = $id);
            """, transaction))
        {
            insert.Parameters.AddWithValue("$id", completion.DeliveryId);
            insert.Parameters.AddWithValue("$attempt", completion.AttemptNumber);
            insert.Parameters.AddWithValue("$owner", completion.LeaseOwner);
            insert.Parameters.AddWithValue("$started", ToUnixMs(completion.StartedAt));
            insert.Parameters.AddWithValue("$completed", ToUnixMs(completion.CompletedAt));
            insert.Parameters.AddWithValue("$outcome", (int)completion.Outcome);
            insert.Parameters.AddWithValue("$statusCode", (object?)completion.StatusCode ?? DBNull.Value);
            insert.Parameters.AddWithValue("$category", (object?)completion.ErrorCategory ?? DBNull.Value);
            insert.Parameters.AddWithValue("$message", (object?)completion.ErrorMessage ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int updated;
        await using (var update = Command(connection, """
            UPDATE wdk_deliveries
            SET status = $status,
                completed_at = $completedAt,
                updated_at = $now,
                next_attempt_at = COALESCE($next, next_attempt_at),
                last_attempt_at = $started,
                last_status_code = $statusCode,
                last_error_category = $category,
                last_error = $message,
                lease_owner = NULL, lease_token = NULL, lease_expires_at = NULL
            WHERE id = $id AND status = 1 AND lease_token = $token;
            """, transaction))
        {
            update.Parameters.AddWithValue("$id", completion.DeliveryId);
            update.Parameters.AddWithValue("$token", completion.LeaseToken);
            update.Parameters.AddWithValue("$status", (int)status);
            update.Parameters.AddWithValue("$completedAt", status == DeliveryStatus.Pending ? DBNull.Value : ToUnixMs(completion.CompletedAt));
            update.Parameters.AddWithValue("$now", ToUnixMs(completion.CompletedAt));
            update.Parameters.AddWithValue("$next", completion.NextAttemptAt is { } next ? ToUnixMs(next) : DBNull.Value);
            update.Parameters.AddWithValue("$started", ToUnixMs(completion.StartedAt));
            update.Parameters.AddWithValue("$statusCode", (object?)completion.StatusCode ?? DBNull.Value);
            update.Parameters.AddWithValue("$category", success ? DBNull.Value : (object?)completion.ErrorCategory ?? DBNull.Value);
            update.Parameters.AddWithValue("$message", success ? DBNull.Value : (object?)completion.ErrorMessage ?? DBNull.Value);
            updated = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated == 1;
    }

    /// <inheritdoc />
    public async Task<CancelResult> CancelAsync(string deliveryId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var cmd = Command(connection, $"""
            UPDATE wdk_deliveries
            SET status = 4, completed_at = $now, updated_at = $now,
                lease_owner = NULL, lease_token = NULL, lease_expires_at = NULL,
                last_error_category = '{DeliveryErrorCategories.Cancelled}', last_error = 'Cancelled by the application.'
            WHERE id = $id AND status IN (0, 1);
            """))
        {
            cmd.Parameters.AddWithValue("$id", deliveryId);
            cmd.Parameters.AddWithValue("$now", ToUnixMs(now));
            if (await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
            {
                return CancelResult.Cancelled;
            }
        }

        await using var exists = Command(connection, "SELECT COUNT(1) FROM wdk_deliveries WHERE id = $id;");
        exists.Parameters.AddWithValue("$id", deliveryId);
        var count = Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return count == 0 ? CancelResult.NotFound : CancelResult.AlreadyTerminal;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeliveryAttempt>> GetAttemptsAsync(string deliveryId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = Command(connection, """
            SELECT delivery_id, attempt_number, lease_owner, started_at, completed_at, outcome, status_code, error_category, error_message
            FROM wdk_attempts WHERE delivery_id = $id ORDER BY attempt_number, seq;
            """);
        cmd.Parameters.AddWithValue("$id", deliveryId);

        var results = new List<DeliveryAttempt>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new DeliveryAttempt
            {
                DeliveryId = reader.GetString(0),
                AttemptNumber = reader.GetInt32(1),
                LeaseOwner = reader.GetString(2),
                StartedAt = FromUnixMs(reader.GetInt64(3)),
                CompletedAt = FromUnixMs(reader.GetInt64(4)),
                Outcome = (AttemptOutcome)reader.GetInt32(5),
                StatusCode = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                ErrorCategory = reader.IsDBNull(7) ? null : reader.GetString(7),
                ErrorMessage = reader.IsDBNull(8) ? null : reader.GetString(8),
            });
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<int> PurgeAsync(DateTimeOffset completedBefore, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        const string victims = "SELECT id FROM wdk_deliveries WHERE status IN (2, 3, 4) AND completed_at < $before";

        await using (var attempts = Command(connection, $"DELETE FROM wdk_attempts WHERE delivery_id IN ({victims});", transaction))
        {
            attempts.Parameters.AddWithValue("$before", ToUnixMs(completedBefore));
            await attempts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int deleted;
        await using (var deliveries = Command(connection, $"DELETE FROM wdk_deliveries WHERE id IN ({victims});", transaction))
        {
            deliveries.Parameters.AddWithValue("$before", ToUnixMs(completedBefore));
            deleted = await deliveries.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>Releases pooled connections for this database (needed before deleting the file on Windows).</summary>
    public void Dispose()
    {
        using var connection = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(connection);
        _initGate.Dispose();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await OpenRawAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenRawAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA busy_timeout = 10000; PRAGMA synchronous = NORMAL;", null, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<DeliveryRecord?> QuerySingleAsync(SqliteConnection connection, string where, Action<SqliteParameterCollection> bind, CancellationToken cancellationToken)
    {
        await using var cmd = Command(connection, $"SELECT {Columns} FROM wdk_deliveries WHERE {where} LIMIT 1;");
        bind(cmd.Parameters);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

#pragma warning disable CA2100 // SQL text is composed only from constants in this file; all values are parameters.
    private static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = transaction;
        return cmd;
    }
#pragma warning restore CA2100

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var cmd = Command(connection, sql, transaction);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DeliveryRecord Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        DestinationId = r.GetString(1),
        IdempotencyKey = r.GetString(2),
        EventType = r.GetString(3),
        ContentType = r.GetString(4),
        Payload = (byte[])r.GetValue(5),
        PayloadSha256 = r.GetString(6),
        Status = (DeliveryStatus)r.GetInt32(7),
        AttemptCount = r.GetInt32(8),
        CreatedAt = FromUnixMs(r.GetInt64(9)),
        UpdatedAt = FromUnixMs(r.GetInt64(10)),
        NextAttemptAt = FromUnixMs(r.GetInt64(11)),
        ExpiresAt = FromUnixMs(r.GetInt64(12)),
        LastAttemptAt = r.IsDBNull(13) ? null : FromUnixMs(r.GetInt64(13)),
        CompletedAt = r.IsDBNull(14) ? null : FromUnixMs(r.GetInt64(14)),
        LastStatusCode = r.IsDBNull(15) ? null : r.GetInt32(15),
        LastErrorCategory = r.IsDBNull(16) ? null : r.GetString(16),
        LastError = r.IsDBNull(17) ? null : r.GetString(17),
        LeaseOwner = r.IsDBNull(18) ? null : r.GetString(18),
        LeaseToken = r.IsDBNull(19) ? null : r.GetString(19),
        LeaseExpiresAt = r.IsDBNull(20) ? null : FromUnixMs(r.GetInt64(20)),
    };

    private static long ToUnixMs(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    private static DateTimeOffset FromUnixMs(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);
}
