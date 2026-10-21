using Npgsql;

namespace NetQuota.Postgres;

public sealed partial class PostgresQuotaStorage
{
    private const string UsageColumns =
        "used, quota_limit, tier, period_start_utc, period_end_utc";

    private static async Task<(int? Used, int? Limit, string? Tier, DateTimeOffset? Start, DateTimeOffset? End, IReadOnlyList<string> Distinct)> ReadUsageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? tx,
        string userId,
        string resource,
        PeriodWindow window,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, tx, $"""
            SELECT {UsageColumns},
                   COALESCE((
                       SELECT array_agg(d.distinct_key ORDER BY d.distinct_key)
                       FROM netquota_distinct_keys d
                       WHERE d.user_id = u.user_id AND d.resource = u.resource
                         AND d.period_type = u.period_type AND d.period_key = u.period_key
                   ), ARRAY[]::text[]) AS distinct_keys
            FROM netquota_usage u
            WHERE u.user_id = @user AND u.resource = @resource
              AND u.period_type = @periodType AND u.period_key = @periodKey
            """,
            ("user", userId),
            ("resource", resource),
            ("periodType", window.PeriodType.ToString()),
            ("periodKey", window.Key));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (null, null, null, null, null, []);
        }

        var distinct = reader.IsDBNull(5)
            ? []
            : reader.GetFieldValue<string[]>(5)
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();

        return (
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            ReadDate(reader, 3),
            ReadDate(reader, 4),
            distinct);
    }

    private static async Task UpsertUsageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        string userId,
        string resource,
        PeriodWindow window,
        int used,
        int limit,
        string? tier,
        DateTimeOffset? start,
        DateTimeOffset? end,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, tx, """
            INSERT INTO netquota_usage (
                user_id, resource, period_type, period_key, used, quota_limit, tier,
                period_start_utc, period_end_utc, updated_at_utc)
            VALUES (@user, @resource, @periodType, @periodKey, @used, @limit, @tier,
                    @start, @end, @now)
            ON CONFLICT (user_id, resource, period_type, period_key) DO UPDATE SET
                used = EXCLUDED.used,
                quota_limit = EXCLUDED.quota_limit,
                tier = COALESCE(EXCLUDED.tier, netquota_usage.tier),
                period_start_utc = EXCLUDED.period_start_utc,
                period_end_utc = EXCLUDED.period_end_utc,
                updated_at_utc = EXCLUDED.updated_at_utc
            """,
            ("user", userId),
            ("resource", resource),
            ("periodType", window.PeriodType.ToString()),
            ("periodKey", window.Key),
            ("used", used),
            ("limit", limit),
            ("tier", tier),
            ("start", start),
            ("end", end),
            ("now", nowUtc));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertDistinctAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        string userId,
        string resource,
        PeriodWindow window,
        string distinctKey,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, tx, """
            INSERT INTO netquota_distinct_keys (user_id, resource, period_type, period_key, distinct_key)
            VALUES (@user, @resource, @periodType, @periodKey, @key)
            ON CONFLICT DO NOTHING
            """,
            ("user", userId),
            ("resource", resource),
            ("periodType", window.PeriodType.ToString()),
            ("periodKey", window.Key),
            ("key", distinctKey));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReplaceDistinctAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        string userId,
        string resource,
        PeriodWindow window,
        IReadOnlyList<string> distinctKeys,
        CancellationToken cancellationToken)
    {
        await using (var delete = Command(connection, tx, """
            DELETE FROM netquota_distinct_keys
            WHERE user_id = @user AND resource = @resource
              AND period_type = @periodType AND period_key = @periodKey
            """,
            ("user", userId),
            ("resource", resource),
            ("periodType", window.PeriodType.ToString()),
            ("periodKey", window.Key)))
        {
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var key in distinctKeys
                     .Where(k => !string.IsNullOrWhiteSpace(k))
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(k => k, StringComparer.Ordinal))
        {
            await InsertDistinctAsync(connection, tx, userId, resource, window, key, cancellationToken);
        }
    }
}
