using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Gateway;

internal interface ICheckoutIdempotencyStore
{
    Task<CheckoutIdempotencyAcquireResult> TryAcquireAsync(
        string scopedKey,
        string requestHash,
        CancellationToken cancellationToken);

    Task MarkFailedAsync(string scopedKey, CancellationToken cancellationToken);

    Task MarkCompletedAsync(
        string scopedKey,
        int statusCode,
        string responseBody,
        CancellationToken cancellationToken);
}

internal sealed record CheckoutIdempotencyAcquireResult(
    bool Acquired,
    bool PayloadMismatch,
    bool InProgress,
    int? ResponseStatusCode,
    string? ResponseBody);

internal sealed record CheckoutIdempotencyRecord(
    string RequestHash,
    bool InProgress,
    int? ResponseStatusCode,
    string? ResponseBody,
    DateTimeOffset UpdatedAt);

internal sealed class DistributedCacheCheckoutIdempotencyStore(
    IDistributedCache cache,
    IConfiguration configuration) : ICheckoutIdempotencyStore
{
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(
        Math.Clamp(configuration.GetValue("Idempotency:TtlMinutes", 1440), 1, 10080));
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async Task<CheckoutIdempotencyAcquireResult> TryAcquireAsync(
        string scopedKey,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(scopedKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var existing = await ReadAsync(scopedKey, cancellationToken);
            if (existing is not null && !string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            {
                return new CheckoutIdempotencyAcquireResult(false, true, false, null, null);
            }

            if (existing is { InProgress: true })
            {
                return new CheckoutIdempotencyAcquireResult(false, false, true, null, null);
            }

            if (existing is { ResponseBody: not null })
            {
                return new CheckoutIdempotencyAcquireResult(
                    false,
                    false,
                    false,
                    existing.ResponseStatusCode,
                    existing.ResponseBody);
            }

            var next = existing is null
                ? new CheckoutIdempotencyRecord(requestHash, true, null, null, DateTimeOffset.UtcNow)
                : existing with
                {
                    InProgress = true,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    RequestHash = requestHash
                };
            await WriteAsync(scopedKey, next, cancellationToken);
            return new CheckoutIdempotencyAcquireResult(true, false, false, null, null);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task MarkFailedAsync(string scopedKey, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(scopedKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var existing = await ReadAsync(scopedKey, cancellationToken);
            if (existing is null)
            {
                return;
            }

            await WriteAsync(
                scopedKey,
                existing with
                {
                    InProgress = false,
                    ResponseStatusCode = null,
                    ResponseBody = null,
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task MarkCompletedAsync(
        string scopedKey,
        int statusCode,
        string responseBody,
        CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(scopedKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var existing = await ReadAsync(scopedKey, cancellationToken);
            if (existing is null)
            {
                return;
            }

            await WriteAsync(
                scopedKey,
                existing with
                {
                    InProgress = false,
                    ResponseStatusCode = statusCode,
                    ResponseBody = responseBody,
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CheckoutIdempotencyRecord?> ReadAsync(string scopedKey, CancellationToken cancellationToken)
    {
        var json = await cache.GetStringAsync(CacheKey(scopedKey), cancellationToken);
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<CheckoutIdempotencyRecord>(json);
    }

    private Task WriteAsync(string scopedKey, CheckoutIdempotencyRecord record, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(record);
        return cache.SetStringAsync(
            CacheKey(scopedKey),
            json,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = _ttl },
            cancellationToken);
    }

    private static string CacheKey(string scopedKey) => $"checkout:idempotency:{scopedKey}";
}

internal sealed class PostgresCheckoutIdempotencyStore(
    NpgsqlDataSource dataSource,
    IConfiguration configuration,
    ILogger<PostgresCheckoutIdempotencyStore> logger) : ICheckoutIdempotencyStore
{
    private readonly int _ttlMinutes = Math.Clamp(configuration.GetValue("Idempotency:TtlMinutes", 1440), 1, 10080);

    public async Task<CheckoutIdempotencyAcquireResult> TryAcquireAsync(
        string scopedKey,
        string requestHash,
        CancellationToken cancellationToken)
    {
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        try
        {
            await PurgeExpiredAsync(conn, tx, scopedKey, cancellationToken);

            await InsertPlaceholderIfMissingAsync(conn, tx, scopedKey, requestHash, cancellationToken);

            var existing = await SelectForUpdateAsync(conn, tx, scopedKey, cancellationToken);
            if (existing is null)
            {
                throw new InvalidOperationException(
                    "Checkout idempotency row missing after insert; check gateway_service.checkout_idempotency schema.");
            }

            if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            {
                await tx.CommitAsync(cancellationToken);
                return new CheckoutIdempotencyAcquireResult(false, true, false, null, null);
            }

            if (existing.InProgress)
            {
                await tx.CommitAsync(cancellationToken);
                return new CheckoutIdempotencyAcquireResult(false, false, true, null, null);
            }

            if (existing.ResponseBody is not null)
            {
                await tx.CommitAsync(cancellationToken);
                return new CheckoutIdempotencyAcquireResult(
                    false,
                    false,
                    false,
                    existing.ResponseStatusCode,
                    existing.ResponseBody);
            }

            await UpdateRecordAsync(
                conn,
                tx,
                scopedKey,
                new CheckoutIdempotencyRecord(requestHash, true, null, null, DateTimeOffset.UtcNow),
                cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return new CheckoutIdempotencyAcquireResult(true, false, false, null, null);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            logger.LogError(ex, "Checkout idempotency TryAcquire failed for key {Key}", scopedKey);
            throw;
        }
    }

    public async Task MarkFailedAsync(string scopedKey, CancellationToken cancellationToken)
    {
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        try
        {
            var existing = await SelectForUpdateAsync(conn, tx, scopedKey, cancellationToken);
            if (existing is null)
            {
                await tx.CommitAsync(cancellationToken);
                return;
            }

            await UpdateRecordAsync(
                conn,
                tx,
                scopedKey,
                existing with
                {
                    InProgress = false,
                    ResponseStatusCode = null,
                    ResponseBody = null,
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            logger.LogError(ex, "Checkout idempotency MarkFailed failed for key {Key}", scopedKey);
            throw;
        }
    }

    public async Task MarkCompletedAsync(
        string scopedKey,
        int statusCode,
        string responseBody,
        CancellationToken cancellationToken)
    {
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        try
        {
            var existing = await SelectForUpdateAsync(conn, tx, scopedKey, cancellationToken);
            if (existing is null)
            {
                await tx.CommitAsync(cancellationToken);
                return;
            }

            await UpdateRecordAsync(
                conn,
                tx,
                scopedKey,
                existing with
                {
                    InProgress = false,
                    ResponseStatusCode = statusCode,
                    ResponseBody = responseBody,
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            logger.LogError(ex, "Checkout idempotency MarkCompleted failed for key {Key}", scopedKey);
            throw;
        }
    }

    private async Task PurgeExpiredAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string scopedKey,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            """
            DELETE FROM gateway_service.checkout_idempotency
            WHERE scoped_key = @scoped_key AND expires_at < NOW()
            """,
            conn,
            tx);
        cmd.Parameters.AddWithValue("scoped_key", scopedKey);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertPlaceholderIfMissingAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string scopedKey,
        string requestHash,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO gateway_service.checkout_idempotency
                (scoped_key, request_hash, in_progress, response_status_code, response_body, updated_at, expires_at)
            VALUES
                (@scoped_key, @request_hash, FALSE, NULL, NULL, NOW(), NOW() + (@ttl * INTERVAL '1 minute'))
            ON CONFLICT (scoped_key) DO NOTHING
            """,
            conn,
            tx);
        cmd.Parameters.AddWithValue("scoped_key", scopedKey);
        cmd.Parameters.AddWithValue("request_hash", requestHash);
        cmd.Parameters.Add("ttl", NpgsqlDbType.Integer).Value = _ttlMinutes;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<CheckoutIdempotencyRecord?> SelectForUpdateAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string scopedKey,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT request_hash, in_progress, response_status_code, response_body, updated_at
            FROM gateway_service.checkout_idempotency
            WHERE scoped_key = @scoped_key
            FOR UPDATE
            """,
            conn,
            tx);
        cmd.Parameters.AddWithValue("scoped_key", scopedKey);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var requestHash = reader.GetString(0);
        var inProgress = reader.GetBoolean(1);
        int? responseStatus = reader.IsDBNull(2) ? null : reader.GetInt32(2);
        string? responseBody = reader.IsDBNull(3) ? null : reader.GetString(3);
        var updatedAt = reader.GetFieldValue<DateTimeOffset>(4);
        return new CheckoutIdempotencyRecord(
            requestHash,
            inProgress,
            responseStatus,
            responseBody,
            updatedAt);
    }

    private async Task UpdateRecordAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string scopedKey,
        CheckoutIdempotencyRecord record,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE gateway_service.checkout_idempotency
            SET request_hash = @request_hash,
                in_progress = @in_progress,
                response_status_code = @response_status_code,
                response_body = @response_body,
                updated_at = NOW(),
                expires_at = NOW() + (@ttl * INTERVAL '1 minute')
            WHERE scoped_key = @scoped_key
            """,
            conn,
            tx);
        cmd.Parameters.AddWithValue("scoped_key", scopedKey);
        cmd.Parameters.AddWithValue("request_hash", record.RequestHash);
        cmd.Parameters.AddWithValue("in_progress", record.InProgress);
        cmd.Parameters.AddWithValue(
            "response_status_code",
            record.ResponseStatusCode ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("response_body", record.ResponseBody ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("ttl", _ttlMinutes);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
