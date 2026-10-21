using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace NetQuota.Postgres;

public sealed partial class PostgresQuotaStorage
{
    private const string RecordKeyPrefixSeparator = ":";

    public async Task<ConsumptionRecord?> GetConsumptionRecordAsync(
        string scopedIdempotencyKey,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scopedIdempotencyKey))
        {
            return null;
        }

        var logicalKey = scopedIdempotencyKey.StartsWith("consume:", StringComparison.Ordinal)
            ? scopedIdempotencyKey
            : $"consume:{scopedIdempotencyKey}";

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadConsumptionRecordAsync(connection, null, logicalKey, nowUtc, cancellationToken);
    }

    public async Task<RefundRecord?> GetRefundRecordAsync(
        string scopedIdempotencyKey,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scopedIdempotencyKey))
        {
            return null;
        }

        var logicalKey = scopedIdempotencyKey.StartsWith("refund:", StringComparison.Ordinal)
            ? scopedIdempotencyKey
            : $"refund:{scopedIdempotencyKey}";

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadRefundRecordAsync(connection, null, logicalKey, nowUtc, cancellationToken);
    }

    private static async Task InsertConsumptionRecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        string idempotencyKey,
        StorageConsumeSpec spec,
        int amount,
        int newUsed,
        int effectiveLimit,
        bool allowed,
        bool consumedNewUnit,
        IReadOnlyList<string> distinctKeys,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, tx, """
            INSERT INTO netquota_consumption_records (
                idempotency_key, user_id, resource, amount, period_type, period_key,
                period_start_utc, period_end_utc, new_used, effective_limit, allowed,
                consumed_new_unit, distinct_keys, timestamp_utc, expires_at_utc)
            VALUES (@key, @user, @resource, @amount, @periodType, @periodKey,
                    @start, @end, @newUsed, @limit, @allowed, @consumed,
                    @distinctKeys, @now, @expires)
            """,
            ("key", idempotencyKey),
            ("user", spec.UserId),
            ("resource", spec.Resource),
            ("amount", amount),
            ("periodType", spec.Window.PeriodType.ToString()),
            ("periodKey", spec.Window.Key),
            ("start", spec.Window.StartUtc),
            ("end", spec.Window.EndUtc),
            ("newUsed", newUsed),
            ("limit", effectiveLimit),
            ("allowed", allowed),
            ("consumed", consumedNewUnit),
            ("distinctKeys", JsonSerializer.Serialize(distinctKeys)),
            ("now", spec.NowUtc),
            ("expires", spec.NowUtc.Add(spec.IdempotencyTtl)));

        command.Parameters["@distinctKeys"].NpgsqlDbType = NpgsqlDbType.Jsonb;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertRefundRecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        string idempotencyKey,
        RefundRequest request,
        PeriodWindow window,
        int amount,
        DateTimeOffset nowUtc,
        TimeSpan idempotencyTtl,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, tx, """
            INSERT INTO netquota_refund_records (
                idempotency_key, user_id, resource, amount, period_type, period_key,
                period_start_utc, period_end_utc, reason, timestamp_utc, expires_at_utc)
            VALUES (@key, @user, @resource, @amount, @periodType, @periodKey,
                    @start, @end, @reason, @now, @expires)
            """,
            ("key", idempotencyKey),
            ("user", request.UserId),
            ("resource", request.Resource),
            ("amount", amount),
            ("periodType", window.PeriodType.ToString()),
            ("periodKey", window.Key),
            ("start", window.StartUtc),
            ("end", window.EndUtc),
            ("reason", request.Reason),
            ("now", nowUtc),
            ("expires", nowUtc.Add(idempotencyTtl)));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ConsumptionRecord?> ReadConsumptionRecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? tx,
        string idempotencyKey,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, tx, """
            SELECT user_id, resource, amount, period_type, period_key, period_start_utc, period_end_utc,
                   new_used, effective_limit, allowed, consumed_new_unit, distinct_keys, timestamp_utc
            FROM netquota_consumption_records
            WHERE idempotency_key = @key AND expires_at_utc > @now
            """,
            ("key", idempotencyKey),
            ("now", nowUtc));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var period = ParsePeriod(reader.GetString(3), reader.GetString(4), ReadDate(reader, 5), ReadDate(reader, 6));
        var distinct = reader.IsDBNull(11)
            ? []
            : JsonSerializer.Deserialize<string[]>(reader.GetString(11)) ?? [];

        return new ConsumptionRecord(
            ConsumptionId: idempotencyKey,
            UserId: reader.GetString(0),
            Resource: reader.GetString(1),
            Amount: reader.GetInt32(2),
            Window: period,
            NewUsed: reader.GetInt32(7),
            EffectiveLimit: reader.GetInt32(8),
            DistinctKeys: distinct.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            TimestampUtc: ReadDate(reader, 12) ?? nowUtc,
            Allowed: reader.GetBoolean(9),
            ConsumedNewUnit: reader.GetBoolean(10));
    }

    private static async Task<RefundRecord?> ReadRefundRecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? tx,
        string idempotencyKey,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, tx, """
            SELECT user_id, resource, amount, period_type, period_key, period_start_utc, period_end_utc,
                   reason, timestamp_utc
            FROM netquota_refund_records
            WHERE idempotency_key = @key AND expires_at_utc > @now
            """,
            ("key", idempotencyKey),
            ("now", nowUtc));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new RefundRecord(
            RefundId: idempotencyKey,
            UserId: reader.GetString(0),
            Resource: reader.GetString(1),
            Amount: reader.GetInt32(2),
            Window: ParsePeriod(reader.GetString(3), reader.GetString(4), ReadDate(reader, 5), ReadDate(reader, 6)),
            TimestampUtc: ReadDate(reader, 8) ?? nowUtc,
            Reason: reader.IsDBNull(7) ? string.Empty : reader.GetString(7));
    }

    private static async Task<bool> CreditRecordExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        string recordKey,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, tx,
            "SELECT EXISTS(SELECT 1 FROM netquota_credit_records WHERE idempotency_key = @key AND expires_at_utc > @now)",
            ("key", recordKey),
            ("now", nowUtc));

        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task InsertCreditRecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        string recordKey,
        string kind,
        string userId,
        string resource,
        int amount,
        DateTimeOffset nowUtc,
        TimeSpan idempotencyTtl,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, tx, """
            INSERT INTO netquota_credit_records (
                idempotency_key, kind, user_id, resource, amount, timestamp_utc, expires_at_utc)
            VALUES (@key, @kind, @user, @resource, @amount, @now, @expires)
            ON CONFLICT (idempotency_key) DO NOTHING
            """,
            ("key", recordKey),
            ("kind", kind),
            ("user", userId),
            ("resource", resource),
            ("amount", amount),
            ("now", nowUtc),
            ("expires", nowUtc.Add(idempotencyTtl)));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static PeriodWindow ParsePeriod(
        string periodType,
        string periodKey,
        DateTimeOffset? start = null,
        DateTimeOffset? end = null)
    {
        var type = Enum.TryParse<PeriodType>(periodType, ignoreCase: true, out var parsed) && parsed != PeriodType.Auto
            ? parsed
            : PeriodType.Monthly;

        return new PeriodWindow(type, periodKey, start, end);
    }

    private static string ScopedKey(string userId, string idempotencyKey) =>
        idempotencyKey.StartsWith($"{userId}{RecordKeyPrefixSeparator}", StringComparison.Ordinal)
            ? idempotencyKey
            : $"{userId}{RecordKeyPrefixSeparator}{idempotencyKey}";
}
