using System.Data;
using Npgsql;

namespace NetQuota.Postgres;

/// <summary>
/// PostgreSQL storage for NetQuota. Every mutation runs in one transaction guarded by an advisory
/// lock on the affected usage (and idempotency record) keys, so concurrent consume / refund /
/// tier changes cannot interleave. Implements the full capability set: entitlements, promotions,
/// drain, merges and audit logs.
/// </summary>
/// <remarks>
/// Provide an <see cref="NpgsqlDataSource"/> built with pooling plus <c>Max Auto Prepare</c> and a
/// bounded <c>Command Timeout</c>; the adapter runs short, parameterised statements and benefits
/// from Npgsql's prepared-statement cache. Housekeeping is the caller's job: schedule
/// <see cref="PruneExpiredAsync"/> (it honours <see cref="PostgresQuotaOptions.UsageRetentionPastWindowEnd"/>).
/// </remarks>
public sealed partial class PostgresQuotaStorage :
    IQuotaStorage,
    IQuotaTimeSource,
    IQuotaAuditLogger,
    IPromotionStore,
    IPromotionCapabilityReporter,
    IRemainingDrainer,
    IUserMerger
{
    // Disjoint advisory-lock spaces; the salt is the second argument of hashtextextended.
    private const long UsageLockSalt = 0;
    private const long MergeRecordLockSalt = 2;
    private const long MergeUserLockSalt = 3;
    private const long CreditRecordLockSalt = 4;
    private const long RefundRecordLockSalt = 5;

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresQuotaOptions _options;
    private readonly LocalRateLimitEngine _localRateLimits = new();

    public PostgresQuotaStorage(NpgsqlDataSource dataSource, PostgresQuotaOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _options = options ?? new PostgresQuotaOptions();
    }

    public bool PromotionsSupported => true;

    /// <summary>Creates the adapter and, unless disabled, the schema.</summary>
    public static async Task<PostgresQuotaStorage> CreateAsync(
        NpgsqlDataSource dataSource,
        PostgresQuotaOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var storage = new PostgresQuotaStorage(dataSource, options);
        if (storage._options.EnsureSchema)
        {
            await PostgresQuotaSchema.EnsureCreatedAsync(dataSource, cancellationToken);
        }

        return storage;
    }

    public async Task<DateTimeOffset> GetNowUtcAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await NowAsync(connection, null, cancellationToken) ?? DateTimeOffset.UtcNow;
    }

    public async Task<QuotaUsage?> GetUsageAsync(
        string userId,
        string resource,
        PeriodWindow window,
        int defaultLimit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var (used, storedLimit, tier, start, end, distinct) =
            await ReadUsageAsync(connection, null, userId, resource, window, cancellationToken);

        if (used is null)
        {
            return new QuotaUsage(
                userId, resource, window.PeriodType, window.Key,
                Used: 0, Limit: defaultLimit, window.StartUtc, window.EndUtc, []);
        }

        var effectiveLimit = window.PeriodType == PeriodType.Forever
            ? storedLimit ?? 0
            : (storedLimit is { } limit && limit != 0 ? limit : defaultLimit);

        return new QuotaUsage(
            userId, resource, window.PeriodType, window.Key,
            used.Value, effectiveLimit, start ?? window.StartUtc, end ?? window.EndUtc,
            distinct, Tier: tier);
    }

    public async Task<StorageConsumeOutcome> TryConsumeAsync(
        StorageConsumeSpec spec,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await ConsumeCoreAsync(spec, cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Two concurrent requests with the same idempotency key: the loser replays the winner.
            if (string.IsNullOrWhiteSpace(spec.IdempotencyKey))
            {
                throw;
            }

            var record = await GetConsumptionRecordAsync(
                ScopedKey(spec.UserId, spec.IdempotencyKey), spec.NowUtc, cancellationToken);
            if (record is null)
            {
                throw;
            }

            EnsureRecordOwnership(record.UserId, record.Resource, spec.UserId, spec.Resource, record.ConsumptionId);

            return new StorageConsumeOutcome(
                record.Allowed, ConsumedNewUnit: false, WasIdempotencyHit: true,
                record.NewUsed, record.EffectiveLimit, record.DistinctKeys, record.Window.PeriodType);
        }
    }

    private async Task<StorageConsumeOutcome> ConsumeCoreAsync(
        StorageConsumeSpec spec,
        CancellationToken cancellationToken)
    {
        var amount = Math.Max(0, spec.Amount);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var scopedKey = string.IsNullOrWhiteSpace(spec.IdempotencyKey)
            ? null
            : $"consume:{ScopedKey(spec.UserId, spec.IdempotencyKey)}";

        if (scopedKey is not null)
        {
            var existing = await ReadConsumptionRecordAsync(connection, tx, scopedKey, spec.NowUtc, cancellationToken);
            if (existing is not null)
            {
                EnsureRecordOwnership(
                    existing.UserId, existing.Resource, spec.UserId, spec.Resource, scopedKey);
                await tx.CommitAsync(cancellationToken);
                return new StorageConsumeOutcome(
                    existing.Allowed, ConsumedNewUnit: false, WasIdempotencyHit: true,
                    existing.NewUsed, existing.EffectiveLimit, existing.DistinctKeys, existing.Window.PeriodType);
            }
        }

        await AcquireUsageLockAsync(connection, tx, spec.UserId, spec.Resource, spec.Window, cancellationToken);

        var (used, storedLimit, storedTier, storedStart, storedEnd, distinct) = await ReadUsageAsync(
            connection, tx, spec.UserId, spec.Resource, spec.Window, cancellationToken);
        var currentUsed = used ?? 0;
        var effectiveLimit = ResolveEffectiveLimit(spec.Window, spec.ConfiguredLimit, storedLimit, storedTier, spec.EffectiveTier);

        if (!string.IsNullOrWhiteSpace(spec.DistinctKey) &&
            distinct.Contains(spec.DistinctKey, StringComparer.Ordinal))
        {
            if (scopedKey is not null)
            {
                await InsertConsumptionRecordAsync(
                    connection, tx, scopedKey, spec, amount, currentUsed, effectiveLimit,
                    allowed: true, consumedNewUnit: false, distinct, cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            return new StorageConsumeOutcome(
                true, false, false, currentUsed, effectiveLimit, distinct, spec.Window.PeriodType);
        }

        if (effectiveLimit != -1 && currentUsed + amount > effectiveLimit)
        {
            await tx.CommitAsync(cancellationToken);
            return new StorageConsumeOutcome(
                false, false, false, currentUsed, effectiveLimit, distinct, spec.Window.PeriodType);
        }

        var newUsed = currentUsed + amount;
        var newDistinct = distinct.ToList();
        if (!string.IsNullOrWhiteSpace(spec.DistinctKey))
        {
            newDistinct.Add(spec.DistinctKey);
            newDistinct.Sort(StringComparer.Ordinal);
        }

        await UpsertUsageAsync(
            connection, tx, spec.UserId, spec.Resource, spec.Window,
            newUsed, effectiveLimit, spec.EffectiveTier ?? storedTier,
            storedStart ?? spec.Window.StartUtc, storedEnd ?? spec.Window.EndUtc,
            spec.NowUtc, cancellationToken);

        if (!string.IsNullOrWhiteSpace(spec.DistinctKey))
        {
            await InsertDistinctAsync(
                connection, tx, spec.UserId, spec.Resource, spec.Window, spec.DistinctKey, cancellationToken);
        }

        if (scopedKey is not null)
        {
            await InsertConsumptionRecordAsync(
                connection, tx, scopedKey, spec, amount, newUsed, effectiveLimit,
                allowed: true, consumedNewUnit: true, newDistinct, cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return new StorageConsumeOutcome(
            true, true, false, newUsed, effectiveLimit, newDistinct, spec.Window.PeriodType);
    }

    public async Task ApplyTierChangeAsync(
        TierChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        await AcquireUsageLockAsync(
            connection, tx, request.UserId, request.Resource, request.Window, cancellationToken);

        var now = await NowAsync(connection, tx, cancellationToken) ?? DateTimeOffset.UtcNow;
        var (used, _, _, start, end, _) = await ReadUsageAsync(
            connection, tx, request.UserId, request.Resource, request.Window, cancellationToken);

        await UpsertUsageAsync(
            connection, tx, request.UserId, request.Resource, request.Window,
            used ?? Math.Max(0, request.CurrentUsed), request.NewLimit, request.NewTier,
            start ?? request.Window.StartUtc, end ?? request.Window.EndUtc,
            now, cancellationToken);

        await using (var update = Command(connection, tx, """
            UPDATE netquota_usage SET previous_tier = @oldTier, tier_changed_at_utc = @now
            WHERE user_id = @user AND resource = @resource AND period_type = @periodType AND period_key = @periodKey
            """,
            ("oldTier", request.OldTier),
            ("now", now),
            ("user", request.UserId),
            ("resource", request.Resource),
            ("periodType", request.Window.PeriodType.ToString()),
            ("periodKey", request.Window.Key)))
        {
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        if (request.DistinctKeys is not null)
        {
            await ReplaceDistinctAsync(
                connection, tx, request.UserId, request.Resource, request.Window,
                request.DistinctKeys, cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task SetUsageAsync(
        string userId,
        string resource,
        PeriodWindow window,
        int used,
        int limit,
        IReadOnlyList<string>? distinctKeys = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        await AcquireUsageLockAsync(connection, tx, userId, resource, window, cancellationToken);

        var now = await NowAsync(connection, tx, cancellationToken) ?? DateTimeOffset.UtcNow;
        var (_, _, tier, start, end, _) = await ReadUsageAsync(
            connection, tx, userId, resource, window, cancellationToken);

        var normalizedUsed = Math.Max(0, used);
        await UpsertUsageAsync(
            connection, tx, userId, resource, window, normalizedUsed, limit, tier,
            start ?? window.StartUtc, end ?? window.EndUtc, now, cancellationToken);

        // A null list means "not supplied": keep the stored dedupe keys, matching InMemoryQuotaStorage.
        // Only an explicit list (or a reset to zero usage) replaces the set.
        if (distinctKeys is not null || normalizedUsed == 0)
        {
            await ReplaceDistinctAsync(
                connection, tx, userId, resource, window, distinctKeys ?? [], cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<StorageRefundOutcome> RefundAsync(
        RefundRequest request,
        PeriodWindow window,
        int defaultLimit,
        TimeSpan idempotencyTtl,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var amount = Math.Max(0, request.Amount);
        var scopedKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? null
            : $"refund:{ScopedKey(request.UserId, request.IdempotencyKey)}";

        try
        {
            return await RefundCoreAsync(request, window, idempotencyTtl, nowUtc, scopedKey, amount, cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && scopedKey is not null)
        {
            var record = await GetRefundRecordAsync(scopedKey, nowUtc, cancellationToken);
            if (record is null)
            {
                throw;
            }

            EnsureRecordOwnership(record.UserId, record.Resource, request.UserId, request.Resource, scopedKey);

            return new StorageRefundOutcome(record.Window.PeriodType, Amount: 0, Replayed: true);
        }
    }

    private async Task<StorageRefundOutcome> RefundCoreAsync(
        RefundRequest request,
        PeriodWindow window,
        TimeSpan idempotencyTtl,
        DateTimeOffset nowUtc,
        string? scopedKey,
        int amount,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        if (scopedKey is not null)
        {
            // Serialize duplicates on the record key before reading it, otherwise two retries both
            // see "no record" and the second insert fails instead of replaying.
            await AcquireAdvisoryLockAsync(
                connection, tx, $"refund-record:{scopedKey}", RefundRecordLockSalt, cancellationToken);

            var existing = await ReadRefundRecordAsync(connection, tx, scopedKey, nowUtc, cancellationToken);
            if (existing is not null)
            {
                EnsureRecordOwnership(
                    existing.UserId, existing.Resource, request.UserId, request.Resource, scopedKey);
                await tx.CommitAsync(cancellationToken);
                return new StorageRefundOutcome(existing.Window.PeriodType, Amount: 0, Replayed: true);
            }
        }

        await AcquireUsageLockAsync(
            connection, tx, request.UserId, request.Resource, window, cancellationToken);

        var (used, _, _, _, _, _) = await ReadUsageAsync(
            connection, tx, request.UserId, request.Resource, window, cancellationToken);

        if (used is not null)
        {
            await using var update = Command(connection, tx, """
                UPDATE netquota_usage SET used = GREATEST(0, used - @amount), updated_at_utc = @now
                WHERE user_id = @user AND resource = @resource AND period_type = @periodType AND period_key = @periodKey
                """,
                ("amount", amount),
                ("now", nowUtc),
                ("user", request.UserId),
                ("resource", request.Resource),
                ("periodType", window.PeriodType.ToString()),
                ("periodKey", window.Key));

            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(request.DistinctKeyToRemove))
        {
            await using var delete = Command(connection, tx, """
                DELETE FROM netquota_distinct_keys
                WHERE user_id = @user AND resource = @resource AND period_type = @periodType
                  AND period_key = @periodKey AND distinct_key = @key
                """,
                ("user", request.UserId),
                ("resource", request.Resource),
                ("periodType", window.PeriodType.ToString()),
                ("periodKey", window.Key),
                ("key", request.DistinctKeyToRemove));

            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        if (scopedKey is not null)
        {
            await InsertRefundRecordAsync(
                connection, tx, scopedKey, request, window, amount, nowUtc, idempotencyTtl, cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return new StorageRefundOutcome(window.PeriodType, amount, Replayed: false);
    }

    public async Task TopUpForeverCreditsAsync(
        string userId,
        string resource,
        int amount,
        string? idempotencyKey,
        TimeSpan idempotencyTtl,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var clamped = Math.Max(0, amount);
        var recordKey = string.IsNullOrWhiteSpace(idempotencyKey)
            ? null
            : $"topup:{userId}:{resource}:{idempotencyKey}";

        if (recordKey is not null)
        {
            // Lock first, then read: the duplicate must wait here, not slip past the check and
            // credit the account twice.
            await AcquireAdvisoryLockAsync(
                connection, tx, $"credit-record:{recordKey}", CreditRecordLockSalt, cancellationToken);

            if (await CreditRecordExistsAsync(connection, tx, recordKey, nowUtc, cancellationToken))
            {
                await tx.CommitAsync(cancellationToken);
                return;
            }
        }

        await AcquireUsageLockAsync(connection, tx, userId, resource, PeriodWindow.Forever, cancellationToken);

        await using (var update = Command(connection, tx, """
            INSERT INTO netquota_usage (user_id, resource, period_type, period_key, used, quota_limit, updated_at_utc)
            VALUES (@user, @resource, 'Forever', 'forever', 0, @amount, @now)
            ON CONFLICT (user_id, resource, period_type, period_key) DO UPDATE SET
                quota_limit = netquota_usage.quota_limit + @amount,
                updated_at_utc = @now
            """,
            ("user", userId),
            ("resource", resource),
            ("amount", clamped),
            ("now", nowUtc)))
        {
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        if (recordKey is not null)
        {
            await InsertCreditRecordAsync(
                connection, tx, recordKey, "topup", userId, resource, clamped, nowUtc, idempotencyTtl, cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task RefundForeverCreditsAsync(
        string userId,
        string resource,
        int amount,
        string? idempotencyKey,
        TimeSpan idempotencyTtl,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var clamped = Math.Max(0, amount);
        var recordKey = string.IsNullOrWhiteSpace(idempotencyKey)
            ? null
            : $"refund_credits:{userId}:{resource}:{idempotencyKey}";

        if (recordKey is not null)
        {
            await AcquireAdvisoryLockAsync(
                connection, tx, $"credit-record:{recordKey}", CreditRecordLockSalt, cancellationToken);

            if (await CreditRecordExistsAsync(connection, tx, recordKey, nowUtc, cancellationToken))
            {
                await tx.CommitAsync(cancellationToken);
                return;
            }
        }

        await AcquireUsageLockAsync(connection, tx, userId, resource, PeriodWindow.Forever, cancellationToken);

        await using (var update = Command(connection, tx, """
            UPDATE netquota_usage SET quota_limit = GREATEST(0, quota_limit - @amount), updated_at_utc = @now
            WHERE user_id = @user AND resource = @resource AND period_type = 'Forever' AND period_key = 'forever'
            """,
            ("user", userId),
            ("resource", resource),
            ("amount", clamped),
            ("now", nowUtc)))
        {
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        if (recordKey is not null)
        {
            await InsertCreditRecordAsync(
                connection, tx, recordKey, "refund_credits", userId, resource, clamped, nowUtc, idempotencyTtl, cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task DrainRemainingAsync(
        string userId,
        string resource,
        PeriodWindow window,
        int defaultLimit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        await AcquireUsageLockAsync(connection, tx, userId, resource, window, cancellationToken);

        var (used, _, _, _, _, _) = await ReadUsageAsync(
            connection, tx, userId, resource, window, cancellationToken);
        var currentUsed = Math.Max(0, used ?? 0);

        await using var upsert = Command(connection, tx, """
            INSERT INTO netquota_usage (user_id, resource, period_type, period_key, used, quota_limit, period_start_utc, period_end_utc, updated_at_utc)
            VALUES (@user, @resource, @periodType, @periodKey, @used, @used, @start, @end, NOW())
            ON CONFLICT (user_id, resource, period_type, period_key) DO UPDATE SET
                used = netquota_usage.used,
                quota_limit = GREATEST(0, netquota_usage.used),
                updated_at_utc = NOW()
            """,
            ("user", userId),
            ("resource", resource),
            ("periodType", window.PeriodType.ToString()),
            ("periodKey", window.Key),
            ("used", currentUsed),
            ("start", window.StartUtc),
            ("end", window.EndUtc));

        await upsert.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes recurring usage rows (and their distinct keys) whose window ended more than
    /// <see cref="PostgresQuotaOptions.UsageRetentionPastWindowEnd"/> ago, plus expired
    /// idempotency records. Forever credits and entitlements are never touched.
    /// </summary>
    public async Task<int> PruneExpiredAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var cutoff = nowUtc - _options.UsageRetentionPastWindowEnd;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        await using var command = Command(connection, null, """
            WITH keys AS (
                DELETE FROM netquota_distinct_keys d
                USING netquota_usage u
                WHERE u.period_type <> 'Forever'
                  AND u.period_end_utc IS NOT NULL
                  AND u.period_end_utc < @cutoff
                  AND d.user_id = u.user_id AND d.resource = u.resource
                  AND d.period_type = u.period_type AND d.period_key = u.period_key
                RETURNING 1
            ), usage AS (
                DELETE FROM netquota_usage
                WHERE period_type <> 'Forever' AND period_end_utc IS NOT NULL AND period_end_utc < @cutoff
                RETURNING 1
            ), consumption AS (
                DELETE FROM netquota_consumption_records WHERE expires_at_utc < @now RETURNING 1
            ), refunds AS (
                DELETE FROM netquota_refund_records WHERE expires_at_utc < @now RETURNING 1
            ), credits AS (
                DELETE FROM netquota_credit_records WHERE expires_at_utc < @now RETURNING 1
            )
            SELECT (SELECT COUNT(*) FROM usage)
                 + (SELECT COUNT(*) FROM consumption)
                 + (SELECT COUNT(*) FROM refunds)
                 + (SELECT COUNT(*) FROM credits)
            """,
            ("cutoff", cutoff),
            ("now", nowUtc));

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    public Task<bool> IsUserSealedAsync(
        string userId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        IsSealedCoreAsync(null, null, userId, nowUtc, cancellationToken);

    public Task<RateLimitStatus> CheckRateLimitAsync(
        string userId,
        string resource,
        RateLimitConfig config,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        // Per-instance windows, same trade-off as goquota's Postgres adapter (0050184 / b294cdd).
        Task.FromResult(_localRateLimits.Evaluate(userId, resource, config, nowUtc));

    private static int ResolveEffectiveLimit(
        PeriodWindow window,
        int configuredLimit,
        int? storedLimit,
        string? storedTier,
        string? effectiveTier)
    {
        if (window.PeriodType == PeriodType.Forever)
        {
            return storedLimit ?? 0;
        }

        // The current tier's unlimited quota wins over any stored cap (goquota 4782f75).
        if (configuredLimit == -1)
        {
            return -1;
        }

        if (storedLimit is { } limit &&
            limit != 0 &&
            !string.IsNullOrWhiteSpace(storedTier) &&
            !string.IsNullOrWhiteSpace(effectiveTier) &&
            string.Equals(storedTier, effectiveTier, StringComparison.OrdinalIgnoreCase))
        {
            // Preserve prorated limits written by ApplyTierChange (goquota 59c0d23 / 179602a).
            return limit;
        }

        return configuredLimit;
    }

    private static async Task<DateTimeOffset?> NowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? tx,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, tx, "SELECT NOW()");
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DateTime dt
            ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc))
            : null;
    }

    private static async Task AcquireUsageLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        string userId,
        string resource,
        PeriodWindow window,
        CancellationToken cancellationToken) =>
        await AcquireAdvisoryLockAsync(
            connection, tx, $"{userId}|{resource}|{window.PeriodType}|{window.Key}", UsageLockSalt, cancellationToken);

    private static DateTimeOffset? ReadDate(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));

    private static NpgsqlCommand Command(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] arguments)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in arguments)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
