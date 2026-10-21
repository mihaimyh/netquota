using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace NetQuota.Postgres;

public sealed partial class PostgresQuotaStorage
{
    private sealed record MergeRecordPayload(
        Dictionary<string, int> TransferredUsage,
        Dictionary<string, int> TransferredForever);

    public async Task<MergeUserResult> MergeUserAsync(
        StorageMergeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(request.SourceUserId, request.TargetUserId, StringComparison.Ordinal))
        {
            throw new SameUserMergeException(request.SourceUserId);
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // Same-key merges serialize on the record key; a replay is then a plain read.
        await AcquireAdvisoryLockAsync(
            connection, tx, $"merge-record:{request.IdempotencyKey}", MergeRecordLockSalt, cancellationToken);

        var existing = await ReadMergeRecordAsync(connection, tx, request.IdempotencyKey, cancellationToken);
        if (existing is { } record)
        {
            // The key is global, so a replay must prove it names the same pair; otherwise it is a
            // different merge reported as already done.
            if (!string.Equals(record.Source, request.SourceUserId, StringComparison.Ordinal) ||
                !string.Equals(record.Target, request.TargetUserId, StringComparison.Ordinal))
            {
                throw new QuotaException(
                    $"Merge idempotency key '{request.IdempotencyKey}' was already used for a different user pair.");
            }

            await tx.CommitAsync(cancellationToken);
            return new MergeUserResult(
                request.SourceUserId,
                request.TargetUserId,
                AlreadyMerged: true,
                record.Payload.TransferredUsage,
                record.Payload.TransferredForever);
        }

        // Deterministic lock order across the two identities prevents merge/merge deadlocks.
        foreach (var userId in new[] { request.SourceUserId, request.TargetUserId }
                     .OrderBy(u => u, StringComparer.Ordinal))
        {
            await AcquireAdvisoryLockAsync(
                connection, tx, $"merge-user:{userId}", MergeUserLockSalt, cancellationToken);
        }

        if (await IsSealedCoreAsync(connection, tx, request.SourceUserId, request.NowUtc, cancellationToken))
        {
            throw new UserSealedException(request.SourceUserId);
        }

        if (await IsSealedCoreAsync(connection, tx, request.TargetUserId, request.NowUtc, cancellationToken))
        {
            throw new UserSealedException(request.TargetUserId);
        }

        // Lock every usage key involved, sorted, so concurrent consumes cannot slip between read and write.
        var usageKeys = request.Items
            .SelectMany(item => new[]
            {
                $"{request.SourceUserId}|{item.Resource}|{item.SourceWindow.PeriodType}|{item.SourceWindow.Key}",
                $"{request.TargetUserId}|{item.Resource}|{item.TargetWindow.PeriodType}|{item.TargetWindow.Key}"
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal);

        foreach (var key in usageKeys)
        {
            await AcquireAdvisoryLockAsync(connection, tx, key, UsageLockSalt, cancellationToken);
        }

        // Only a merge that actually seals the source clears the overlay, so only that merge can
        // report a dropped promotion — and only an overlay that is still in effect counts.
        var droppedPromotion = request.SealSource
            ? await ReadPromotionAsync(connection, tx, request.SourceUserId, request.NowUtc, cancellationToken)
            : null;

        var transferredUsage = new Dictionary<string, int>(StringComparer.Ordinal);
        var transferredForever = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var item in request.Items)
        {
            var source = await ReadUsageAsync(
                connection, tx, request.SourceUserId, item.Resource, item.SourceWindow, cancellationToken);
            if (source.Used is null)
            {
                continue;
            }

            var target = await ReadUsageAsync(
                connection, tx, request.TargetUserId, item.Resource, item.TargetWindow, cancellationToken);

            if (item.PeriodType == PeriodType.Forever)
            {
                var sourceUsed = source.Used.Value;
                var sourceLimit = source.Limit ?? 0;
                var remaining = Math.Max(0, sourceLimit - sourceUsed);
                if (remaining == 0)
                {
                    continue;
                }

                var targetUsed = target.Used ?? 0;
                var targetLimit = target.Limit ?? 0;

                // Forever credits are additive: a missing or zero target limit means "no credits yet".
                await UpsertUsageAsync(
                    connection, tx, request.TargetUserId, item.Resource, item.TargetWindow,
                    targetUsed, targetLimit + remaining, target.Tier,
                    item.TargetWindow.StartUtc, item.TargetWindow.EndUtc, request.NowUtc, cancellationToken);

                await UpsertUsageAsync(
                    connection, tx, request.SourceUserId, item.Resource, item.SourceWindow,
                    sourceUsed, sourceUsed, source.Tier,
                    item.SourceWindow.StartUtc, item.SourceWindow.EndUtc, request.NowUtc, cancellationToken);

                transferredForever[item.Resource] = remaining;
                continue;
            }

            var mergedDistinct = target.Distinct
                .Concat(source.Distinct)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();

            var targetUsedBefore = target.Used ?? 0;

            // One distinct key means one charged unit only while every consume on both sides
            // charged exactly one unit. Amounts above one make the key count smaller than the real
            // usage, and replacing Used with it would erase usage (ISM-01).
            var countBased = targetUsedBefore == target.Distinct.Count &&
                             source.Used.Value == source.Distinct.Count;
            var newTargetUsed = countBased
                ? Math.Max(targetUsedBefore, mergedDistinct.Length)
                : targetUsedBefore + source.Used.Value;

            // A stored limit of 0 means "unset", so it must not pin the target to zero.
            var mergedLimit = target.Limit is > 0
                ? target.Limit.Value
                : source.Limit is > 0
                    ? source.Limit.Value
                    : 0;

            await UpsertUsageAsync(
                connection, tx, request.TargetUserId, item.Resource, item.TargetWindow,
                newTargetUsed, mergedLimit, target.Tier,
                item.TargetWindow.StartUtc, item.TargetWindow.EndUtc, request.NowUtc, cancellationToken);

            await ReplaceDistinctAsync(
                connection, tx, request.TargetUserId, item.Resource, item.TargetWindow,
                mergedDistinct, cancellationToken);

            if (request.SealSource)
            {
                await UpsertUsageAsync(
                    connection, tx, request.SourceUserId, item.Resource, item.SourceWindow,
                    0, source.Limit ?? 0, source.Tier,
                    item.SourceWindow.StartUtc, item.SourceWindow.EndUtc, request.NowUtc, cancellationToken);
                await ReplaceDistinctAsync(
                    connection, tx, request.SourceUserId, item.Resource, item.SourceWindow, [], cancellationToken);
            }

            var key = $"{item.Resource}:{item.PeriodType}";
            transferredUsage[key] = transferredUsage.GetValueOrDefault(key) + Math.Max(0, newTargetUsed - targetUsedBefore);
        }

        if (request.SealSource)
        {
            var sealUntil = request.SealExpiresAtUtc ?? request.NowUtc.AddDays(30);
            await using var seal = Command(connection, tx, """
                INSERT INTO netquota_entitlements (
                    user_id, tier, subscription_start_date_utc, updated_at_utc,
                    sealed, migrated_to, sealed_until_utc)
                VALUES (@source, 'free', @now, @now, true, @target, @until)
                ON CONFLICT (user_id) DO UPDATE SET
                    sealed = true,
                    migrated_to = @target,
                    sealed_until_utc = @until,
                    updated_at_utc = @now,
                    promotion_tier = NULL,
                    promotion_granted_at_utc = NULL,
                    promotion_expires_at_utc = NULL,
                    promotion_source = NULL,
                    promotion_reason = NULL,
                    promotion_idempotency_key = NULL
                """,
                ("target", request.TargetUserId),
                ("until", sealUntil),
                ("now", request.NowUtc),
                ("source", request.SourceUserId));

            await seal.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = Command(connection, tx, """
            INSERT INTO netquota_merge_records (idempotency_key, source_user_id, target_user_id, result, timestamp_utc)
            VALUES (@key, @source, @target, @result, @now)
            """,
            ("key", request.IdempotencyKey),
            ("source", request.SourceUserId),
            ("target", request.TargetUserId),
            ("result", JsonSerializer.Serialize(new MergeRecordPayload(transferredUsage, transferredForever))),
            ("now", request.NowUtc)))
        {
            insert.Parameters["@result"].NpgsqlDbType = NpgsqlDbType.Jsonb;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        return new MergeUserResult(
            request.SourceUserId,
            request.TargetUserId,
            AlreadyMerged: false,
            transferredUsage,
            transferredForever,
            DroppedPromotion: droppedPromotion);
    }

    public async Task<MergeUserResult> MergeUserAsync(
        MergeUserRequest request,
        Func<PeriodType, PeriodWindow> windowResolver,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var items = new List<MergeItem>(request.Resources.Count * request.Periods.Count);
        foreach (var resource in request.Resources)
        {
            foreach (var period in request.Periods)
            {
                if (period == PeriodType.Auto)
                {
                    throw new QuotaConfigValidationException("MergeUserRequest.Periods cannot contain PeriodType.Auto.");
                }

                var window = windowResolver(period);
                items.Add(new MergeItem(resource, period, window, window));
            }
        }

        var sealExpiresAt = request.SealSource
            ? nowUtc.Add(request.SealTtl is { } ttl && ttl > TimeSpan.Zero ? ttl : TimeSpan.FromDays(30))
            : (DateTimeOffset?)null;

        return await MergeUserAsync(
            new StorageMergeRequest(
                SourceUserId: request.SourceUserId,
                TargetUserId: request.TargetUserId,
                Items: items,
                IdempotencyKey: request.IdempotencyKey,
                SealSource: request.SealSource,
                NowUtc: nowUtc,
                SealExpiresAtUtc: sealExpiresAt),
            cancellationToken);
    }

    private static async Task AcquireAdvisoryLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        string key,
        long salt,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, tx,
            "SELECT pg_advisory_xact_lock(hashtextextended(@key, @salt))",
            ("key", key),
            ("salt", salt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<TierPromotion?> ReadPromotionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        string userId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, tx, """
            SELECT promotion_tier, promotion_granted_at_utc, promotion_expires_at_utc,
                   promotion_source, promotion_reason, promotion_idempotency_key
            FROM netquota_entitlements
            WHERE user_id = @user
              AND (promotion_expires_at_utc IS NULL OR promotion_expires_at_utc > @now)
            """,
            ("user", userId),
            ("now", nowUtc));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
        {
            return null;
        }

        return new TierPromotion(
            Tier: reader.GetString(0),
            GrantedAtUtc: ReadDate(reader, 1) ?? DateTimeOffset.UtcNow,
            ExpiresAtUtc: ReadDate(reader, 2) ?? DateTimeOffset.UtcNow,
            Source: reader.IsDBNull(3) ? "admin" : reader.GetString(3),
            Reason: reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            IdempotencyKey: reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private static async Task<(string Source, string Target, MergeRecordPayload Payload)?> ReadMergeRecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, tx,
            "SELECT source_user_id, target_user_id, result FROM netquota_merge_records WHERE idempotency_key = @key",
            ("key", idempotencyKey));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (
            reader.GetString(0),
            reader.GetString(1),
            JsonSerializer.Deserialize<MergeRecordPayload>(reader.GetString(2))!);
    }
}
