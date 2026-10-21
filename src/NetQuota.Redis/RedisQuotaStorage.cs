using System.Globalization;
using StackExchange.Redis;

namespace NetQuota.Redis;

public sealed record RedisQuotaOptions
{
    /// <summary>Prefix for every Redis key owned by this adapter.</summary>
    public string KeyPrefix { get; init; } = "netquota:";

    /// <summary>
    /// How long recurring usage hashes survive past the end of their window. Forever credit hashes
    /// never expire.
    /// </summary>
    public TimeSpan UsageRetentionPastWindowEnd { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// TTL applied to entitlement hashes. <c>null</c> (default) keeps them forever: expiring an
    /// entitlement silently downgrades a paying user if the billing sync is late.
    /// </summary>
    public TimeSpan? EntitlementTtl { get; init; }
}

/// <summary>
/// Redis storage for NetQuota. Every mutation runs as a Lua script, so consume / refund / top-up
/// / drain are atomic across instances. Implements the same capability set as goquota's Redis
/// adapter: entitlements, promotions, drain and rate limiting, but not cross-identity merges.
/// </summary>
/// <remarks>
/// Targets a single Redis instance (or a non-clustered replica/sentinel setup). Redis Cluster
/// requires every key of one script to live in the same slot, which the idempotency-record keys
/// (scoped by user + request id) cannot guarantee without hash tags that would collapse unrelated
/// tenants into one slot.
/// </remarks>
public sealed partial class RedisQuotaStorage :
    IQuotaStorage,
    IQuotaTimeSource,
    IPromotionStore,
    IPromotionCapabilityReporter,
    IRemainingDrainer
{
    private static readonly TimeSpan MinimumUsageTtl = TimeSpan.FromHours(1);

    private readonly IDatabase _db;
    private readonly RedisQuotaOptions _options;

    public RedisQuotaStorage(IConnectionMultiplexer connection, RedisQuotaOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _db = connection.GetDatabase();
        _options = options ?? new RedisQuotaOptions();
    }

    public bool PromotionsSupported => true;

    public async Task<DateTimeOffset> GetNowUtcAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = AsArray(await _db.ExecuteAsync("TIME"));
            var seconds = (long)result[0];
            var micros = (long)result[1];
            return DateTimeOffset.FromUnixTimeMilliseconds(seconds * 1000 + micros / 1000);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return DateTimeOffset.UtcNow;
        }
    }

    public async Task<QuotaEntitlement?> GetEntitlementAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entries = await _db.HashGetAllAsync(EntitlementKey(userId));
        if (entries.Length == 0)
        {
            return null;
        }

        var map = entries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString(), StringComparer.Ordinal);
        var tier = map.GetValueOrDefault("tier", "free");
        var subStart = ParseDate(map.GetValueOrDefault("subscriptionStartDateUtc")) ?? DateTimeOffset.UtcNow;
        var updated = ParseDate(map.GetValueOrDefault("updatedAtUtc")) ?? DateTimeOffset.UtcNow;

        TierPromotion? promotion = null;
        if (map.TryGetValue("promotionTier", out var promoTier) && !string.IsNullOrWhiteSpace(promoTier))
        {
            promotion = new TierPromotion(
                Tier: promoTier,
                GrantedAtUtc: ParseDate(map.GetValueOrDefault("promotionGrantedAtUtc")) ?? updated,
                ExpiresAtUtc: ParseDate(map.GetValueOrDefault("promotionExpiresAtUtc")) ?? updated,
                Source: map.GetValueOrDefault("promotionSource", "admin"),
                Reason: map.GetValueOrDefault("promotionReason", string.Empty),
                IdempotencyKey: string.IsNullOrWhiteSpace(map.GetValueOrDefault("promotionIdempotencyKey"))
                    ? null
                    : map["promotionIdempotencyKey"]);
        }

        return new QuotaEntitlement(
            UserId: userId,
            Tier: tier,
            SubscriptionStartDateUtc: subStart,
            UpdatedAtUtc: updated,
            ExpiresAtUtc: ParseDate(map.GetValueOrDefault("expiresAtUtc")),
            Promotion: promotion,
            Timezone: string.IsNullOrWhiteSpace(map.GetValueOrDefault("timezone")) ? null : map["timezone"],
            Sealed: map.GetValueOrDefault("sealed") == "1",
            MigratedTo: string.IsNullOrWhiteSpace(map.GetValueOrDefault("migratedTo")) ? null : map["migratedTo"],
            SealExpiresAtUtc: ParseDate(map.GetValueOrDefault("sealedUntilUtc")));
    }

    public async Task SetEntitlementAsync(
        QuotaEntitlement entitlement,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var effective = entitlement.UpdatedAtUtc == default
            ? entitlement with { UpdatedAtUtc = DateTimeOffset.UtcNow }
            : entitlement;

        var ttlSeconds = _options.EntitlementTtl is { } ttl && ttl > TimeSpan.Zero
            ? (long)ttl.TotalSeconds
            : 0L;

        await _db.ScriptEvaluateAsync(
            SetEntitlementScript,
            [EntitlementKey(effective.UserId)],
            [
                effective.UserId,
                effective.Tier,
                FormatDate(effective.SubscriptionStartDateUtc),
                FormatDate(effective.UpdatedAtUtc),
                effective.ExpiresAtUtc is { } exp ? FormatDate(exp) : string.Empty,
                effective.Timezone ?? string.Empty,
                effective.Sealed ? "1" : "0",
                effective.MigratedTo ?? string.Empty,
                effective.SealExpiresAtUtc is { } seal ? FormatDate(seal) : string.Empty,
                effective.Promotion is null ? "0" : "1",
                effective.Promotion?.Tier ?? string.Empty,
                effective.Promotion is { } granted ? FormatDate(granted.GrantedAtUtc) : string.Empty,
                effective.Promotion is { } expires ? FormatDate(expires.ExpiresAtUtc) : string.Empty,
                effective.Promotion?.Source ?? string.Empty,
                effective.Promotion?.Reason ?? string.Empty,
                effective.Promotion?.IdempotencyKey ?? string.Empty,
                ttlSeconds
            ]);
    }

    public async Task SetPromotionAsync(
        string userId,
        TierPromotion promotion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var written = (long)await _db.ScriptEvaluateAsync(
            SetPromotionScript,
            [EntitlementKey(userId)],
            [
                promotion.Tier,
                FormatDate(promotion.GrantedAtUtc),
                FormatDate(promotion.ExpiresAtUtc),
                promotion.Source,
                promotion.Reason,
                promotion.IdempotencyKey ?? string.Empty
            ]);

        if (written == 0)
        {
            throw new QuotaException($"Entitlement for user '{userId}' was not found.");
        }
    }

    public async Task ClearPromotionAsync(string userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _db.ScriptEvaluateAsync(ClearPromotionScript, [EntitlementKey(userId)]);
    }

    public async Task<QuotaUsage?> GetUsageAsync(
        string userId,
        string resource,
        PeriodWindow window,
        int defaultLimit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // One round trip for the hash + distinct set.
        var batch = _db.CreateBatch();
        var hashTask = batch.HashGetAsync(
            UsageKey(userId, resource, window),
            ["used", "limit", "tier", "periodStartUtc", "periodEndUtc"]);
        var setTask = batch.SetMembersAsync(DistinctKey(userId, resource, window));
        batch.Execute();

        var values = await hashTask;
        var members = await setTask;

        var storedLimit = values[1].IsNull ? (int?)null : (int)values[1];
        var storedTier = values[2].IsNull ? null : values[2].ToString();

        var used = values[0].IsNull ? 0 : (int)values[0];
        var effectiveLimit = window.PeriodType == PeriodType.Forever
            ? storedLimit ?? 0
            : (storedLimit is { } limit && limit != 0 ? limit : defaultLimit);

        var distinct = members
            .Select(m => m.ToString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        return new QuotaUsage(
            userId,
            resource,
            window.PeriodType,
            window.Key,
            used,
            effectiveLimit,
            ParseDate(values[3].IsNull ? null : values[3].ToString()) ?? window.StartUtc,
            ParseDate(values[4].IsNull ? null : values[4].ToString()) ?? window.EndUtc,
            distinct,
            Tier: string.IsNullOrWhiteSpace(storedTier) ? null : storedTier);
    }

    public async Task<StorageConsumeOutcome> TryConsumeAsync(
        StorageConsumeSpec spec,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var consumptionKey = string.IsNullOrWhiteSpace(spec.IdempotencyKey)
            ? string.Empty
            : ConsumptionKey(spec.UserId, spec.IdempotencyKey);

        var result = AsArray(await _db.ScriptEvaluateAsync(
            ConsumeScript,
            [
                UsageKey(spec.UserId, spec.Resource, spec.Window),
                DistinctKey(spec.UserId, spec.Resource, spec.Window),
                consumptionKey
            ],
            [
                Math.Max(0, spec.Amount),
                spec.ConfiguredLimit,
                spec.EffectiveTier ?? string.Empty,
                spec.DistinctKey ?? string.Empty,
                FormatDate(spec.NowUtc),
                (long)Math.Max(0, spec.IdempotencyTtl.TotalSeconds),
                UsageTtlSeconds(spec.Window, spec.NowUtc),
                spec.UserId,
                spec.Resource,
                spec.Window.PeriodType.ToString(),
                spec.Window.Key,
                spec.Window.StartUtc is { } start ? FormatDate(start) : string.Empty,
                spec.Window.EndUtc is { } end ? FormatDate(end) : string.Empty
            ]));

        var status = (long)result[0];
        var newUsed = (int)(long)result[1];
        var effectiveLimit = (int)(long)result[2];
        var chargedPeriod = Enum.TryParse<PeriodType>(result[3].ToString(), out var parsed)
            ? parsed
            : spec.Window.PeriodType;
        var distinct = ParseMembers(result[4].ToString());

        return status switch
        {
            2 => new StorageConsumeOutcome(true, false, true, newUsed, effectiveLimit, distinct, chargedPeriod),
            3 => new StorageConsumeOutcome(true, false, false, newUsed, effectiveLimit, distinct, chargedPeriod),
            _ => new StorageConsumeOutcome(status == 1, status == 1, false, newUsed, effectiveLimit, distinct, chargedPeriod)
        };
    }

    public async Task ApplyTierChangeAsync(
        TierChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var distinct = (request.DistinctKeys ?? [])
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        var args = new List<RedisValue>
        {
            request.NewLimit,
            request.NewTier,
            request.OldTier,
            FormatDate(DateTimeOffset.UtcNow),
            UsageTtlSeconds(request.Window, DateTimeOffset.UtcNow),
            Math.Max(0, request.CurrentUsed),
            request.DistinctKeys is null ? "0" : "1"
        };
        args.AddRange(distinct.Select(k => (RedisValue)k));

        await _db.ScriptEvaluateAsync(
            ApplyTierChangeScript,
            [
                UsageKey(request.UserId, request.Resource, request.Window),
                DistinctKey(request.UserId, request.Resource, request.Window)
            ],
            [.. args]);
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
        cancellationToken.ThrowIfCancellationRequested();

        var distinct = (distinctKeys ?? [])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => (RedisValue)k)
            .ToArray();

        var args = new List<RedisValue>
        {
            Math.Max(0, used),
            limit,
            FormatDate(DateTimeOffset.UtcNow),
            RedisValue.EmptyString,
            UsageTtlSeconds(window, DateTimeOffset.UtcNow)
        };
        args.AddRange(distinct);

        await _db.ScriptEvaluateAsync(
            SetUsageScript,
            [UsageKey(userId, resource, window), DistinctKey(userId, resource, window)],
            [.. args]);
    }

    public async Task<StorageRefundOutcome> RefundAsync(
        RefundRequest request,
        PeriodWindow window,
        int defaultLimit,
        TimeSpan idempotencyTtl,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var refundKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? string.Empty
            : RefundKey(request.UserId, request.IdempotencyKey);

        var result = AsArray(await _db.ScriptEvaluateAsync(
            RefundScript,
            [
                UsageKey(request.UserId, request.Resource, window),
                DistinctKey(request.UserId, request.Resource, window),
                refundKey
            ],
            [
                Math.Max(0, request.Amount),
                request.DistinctKeyToRemove ?? string.Empty,
                FormatDate(nowUtc),
                (long)Math.Max(0, idempotencyTtl.TotalSeconds),
                request.UserId,
                request.Resource,
                window.PeriodType.ToString(),
                window.Key,
                window.StartUtc is { } start ? FormatDate(start) : string.Empty,
                window.EndUtc is { } end ? FormatDate(end) : string.Empty,
                request.Reason
            ]));

        var status = (long)result[0];
        var amount = (int)(long)result[1];
        var period = Enum.TryParse<PeriodType>(result[2].ToString(), out var parsed) ? parsed : window.PeriodType;

        return status == 2
            ? new StorageRefundOutcome(period, Amount: 0, Replayed: true)
            : new StorageRefundOutcome(period, amount, Replayed: false);
    }

    public async Task<ConsumptionRecord?> GetConsumptionRecordAsync(
        string scopedIdempotencyKey,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(scopedIdempotencyKey))
        {
            return null;
        }

        var logicalKey = scopedIdempotencyKey.StartsWith("consume:", StringComparison.Ordinal)
            ? scopedIdempotencyKey
            : $"consume:{scopedIdempotencyKey}";

        var map = await ReadHashAsync(RecordKey(logicalKey));
        return map is null ? null : ToConsumptionRecord(logicalKey, map, nowUtc);
    }

    public async Task<RefundRecord?> GetRefundRecordAsync(
        string scopedIdempotencyKey,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(scopedIdempotencyKey))
        {
            return null;
        }

        var logicalKey = scopedIdempotencyKey.StartsWith("refund:", StringComparison.Ordinal)
            ? scopedIdempotencyKey
            : $"refund:{scopedIdempotencyKey}";

        var map = await ReadHashAsync(RecordKey(logicalKey));
        if (map is null)
        {
            return null;
        }

        var periodType = ParsePeriod(map.GetValueOrDefault("periodType"), PeriodType.Monthly);
        return new RefundRecord(
            RefundId: logicalKey,
            UserId: map.GetValueOrDefault("userId", string.Empty),
            Resource: map.GetValueOrDefault("resource", string.Empty),
            Amount: ParseInt(map.GetValueOrDefault("amount")),
            Window: new PeriodWindow(
                periodType,
                map.GetValueOrDefault("periodKey", periodType == PeriodType.Forever ? "forever" : string.Empty),
                ParseDate(map.GetValueOrDefault("periodStartUtc")),
                ParseDate(map.GetValueOrDefault("periodEndUtc"))),
            TimestampUtc: ParseDate(map.GetValueOrDefault("updatedAtUtc")) ?? nowUtc,
            Reason: map.GetValueOrDefault("reason", string.Empty));
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
        cancellationToken.ThrowIfCancellationRequested();

        var topUpKey = string.IsNullOrWhiteSpace(idempotencyKey)
            ? string.Empty
            : RecordKey($"topup:{userId}:{idempotencyKey}");

        await _db.ScriptEvaluateAsync(
            TopUpScript,
            [UsageKey(userId, resource, PeriodWindow.Forever), topUpKey],
            [Math.Max(0, amount), (long)Math.Max(0, idempotencyTtl.TotalSeconds), FormatDate(nowUtc)]);
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
        cancellationToken.ThrowIfCancellationRequested();

        var refundKey = string.IsNullOrWhiteSpace(idempotencyKey)
            ? string.Empty
            : RecordKey($"refund_credits:{userId}:{idempotencyKey}");

        await _db.ScriptEvaluateAsync(
            RefundCreditsScript,
            [UsageKey(userId, resource, PeriodWindow.Forever), refundKey],
            [Math.Max(0, amount), (long)Math.Max(0, idempotencyTtl.TotalSeconds), FormatDate(nowUtc)]);
    }

    public async Task DrainRemainingAsync(
        string userId,
        string resource,
        PeriodWindow window,
        int defaultLimit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _db.ScriptEvaluateAsync(
            DrainScript,
            [UsageKey(userId, resource, window)],
            [FormatDate(DateTimeOffset.UtcNow)]);
    }

    public Task<MergeUserResult> MergeUserAsync(
        StorageMergeRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<MergeUserResult>(new UnsupportedQuotaOperationException("MergeUser"));

    public Task<MergeUserResult> MergeUserAsync(
        MergeUserRequest request,
        Func<PeriodType, PeriodWindow> windowResolver,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromException<MergeUserResult>(new UnsupportedQuotaOperationException("MergeUser"));

    public Task<bool> IsUserSealedAsync(string userId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return IsSealedCoreAsync(userId, nowUtc);
    }

    public async Task<RateLimitStatus> CheckRateLimitAsync(
        string userId,
        string resource,
        RateLimitConfig config,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var nowMs = nowUtc.ToUnixTimeMilliseconds();
        var windowMs = Math.Max(1, (long)config.Window.TotalMilliseconds);
        var ttlSeconds = (long)Math.Ceiling((config.Window + TimeSpan.FromMinutes(5)).TotalSeconds);

        RedisResult[] result;
        if (config.Algorithm == RateLimitAlgorithm.TokenBucket)
        {
            result = AsArray(await _db.ScriptEvaluateAsync(
                TokenBucketScript,
                [RateLimitKey(resource, userId)],
                [
                    nowMs,
                    config.Rate,
                    windowMs,
                    config.Burst > 0 ? config.Burst : config.Rate,
                    ttlSeconds
                ]));
        }
        else
        {
            var globalKey = config.GlobalLimit > 0
                ? RateLimitGlobalKey(resource)
                : string.Empty;

            result = AsArray(await _db.ScriptEvaluateAsync(
                SlidingWindowScript,
                [RateLimitKey(resource, userId), globalKey],
                [
                    nowMs,
                    config.Rate,
                    windowMs,
                    ttlSeconds,
                    $"{nowMs}:{Guid.NewGuid():N}",
                    config.GlobalLimit
                ]));
        }

        var allowed = (long)result[0] == 1;
        var remaining = (int)(long)result[1];
        var resetAtUtc = DateTimeOffset.FromUnixTimeMilliseconds((long)result[2]);
        var retryAfter = resetAtUtc > nowUtc ? resetAtUtc - nowUtc : TimeSpan.Zero;

        return new RateLimitStatus(
            allowed,
            config.Algorithm == RateLimitAlgorithm.TokenBucket
                ? (config.Burst > 0 ? config.Burst : config.Rate)
                : config.Rate,
            remaining,
            resetAtUtc,
            allowed ? TimeSpan.Zero : retryAfter);
    }

    private async Task<bool> IsSealedCoreAsync(string userId, DateTimeOffset nowUtc)
    {
        var map = await ReadHashAsync(EntitlementKey(userId));
        if (map is null || map.GetValueOrDefault("sealed") != "1")
        {
            return false;
        }

        var until = ParseDate(map.GetValueOrDefault("sealedUntilUtc"));
        return until is null || until > nowUtc;
    }

    private async Task<Dictionary<string, string>?> ReadHashAsync(string key)
    {
        var entries = await _db.HashGetAllAsync(key);
        if (entries.Length == 0)
        {
            return null;
        }

        return entries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString(), StringComparer.Ordinal);
    }

    private static ConsumptionRecord ToConsumptionRecord(
        string logicalKey,
        Dictionary<string, string> map,
        DateTimeOffset nowUtc)
    {
        var periodType = ParsePeriod(map.GetValueOrDefault("periodType"), PeriodType.Monthly);
        return new ConsumptionRecord(
            ConsumptionId: logicalKey,
            UserId: map.GetValueOrDefault("userId", string.Empty),
            Resource: map.GetValueOrDefault("resource", string.Empty),
            Amount: ParseInt(map.GetValueOrDefault("amount")),
            Window: new PeriodWindow(
                periodType,
                map.GetValueOrDefault("periodKey", periodType == PeriodType.Forever ? "forever" : string.Empty),
                ParseDate(map.GetValueOrDefault("periodStartUtc")),
                ParseDate(map.GetValueOrDefault("periodEndUtc"))),
            NewUsed: ParseInt(map.GetValueOrDefault("newUsed")),
            EffectiveLimit: ParseInt(map.GetValueOrDefault("effectiveLimit")),
            DistinctKeys: ParseMembers(map.GetValueOrDefault("distinctKeys")),
            TimestampUtc: ParseDate(map.GetValueOrDefault("updatedAtUtc")) ?? nowUtc,
            Allowed: map.GetValueOrDefault("allowed") != "0",
            ConsumedNewUnit: map.GetValueOrDefault("consumedNewUnit") != "0");
    }

    private long UsageTtlSeconds(PeriodWindow window, DateTimeOffset nowUtc)
    {
        if (window.PeriodType == PeriodType.Forever || window.EndUtc is not { } end)
        {
            return 0;
        }

        // A late consume on an already-expired window must not create a key that never expires.
        var ttl = end + _options.UsageRetentionPastWindowEnd - nowUtc;
        if (ttl < MinimumUsageTtl)
        {
            ttl = MinimumUsageTtl;
        }

        return (long)Math.Ceiling(ttl.TotalSeconds);
    }

    private static RedisResult[] AsArray(RedisResult? result)
    {
        if (result is null)
        {
            throw new QuotaStorageUnavailableException("Redis returned no result for the script.");
        }

        return (RedisResult[]?)result
            ?? throw new QuotaStorageUnavailableException("Redis returned an unexpected result shape.");
    }

    private static string[] ParseMembers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<string[]>(json) ?? [];
            return parsed
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(v => v, StringComparer.Ordinal)
                .ToArray();
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static PeriodType ParsePeriod(string? raw, PeriodType fallback) =>
        Enum.TryParse<PeriodType>(raw, ignoreCase: true, out var parsed) && parsed != PeriodType.Auto
            ? parsed
            : fallback;

    private static int ParseInt(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static DateTimeOffset? ParseDate(string? raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;

    private static string FormatDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
