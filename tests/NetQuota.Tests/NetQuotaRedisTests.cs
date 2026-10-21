using NetQuota;
using NetQuota.Redis;
using StackExchange.Redis;

namespace NetQuota.Tests;

/// <summary>
/// Testează adaptorul Redis real. Se skip-uiesc explicit (nu „trec” fără să verifice nimic) când
/// nu există un server Redis:
///   docker run -d --rm --name netquota-redis -p 6379:6379 redis:7-alpine
///   $env:NETQUOTA_REDIS='localhost:6379'
/// </summary>
public class NetQuotaRedisTests
{
    private const string RedisNotConfigured = "NETQUOTA_REDIS nu este setat; testele de integrare Redis sunt sărite.";

    private static readonly Lazy<Task<IConnectionMultiplexer?>> SharedConnection = new(ConnectAsync);

    [Fact]
    public async Task Consume_IsIdempotent_AndConsumptionRecordRoundTrips()
    {
        var ctx = await CreateManagerAsync(monthly: 10);
        Assert.SkipWhen(ctx is null, RedisNotConfigured);
        var (manager, _, prefix) = ctx!.Value;

        var first = await manager.ConsumeWithResultAsync(
            "user_1", "api_calls", 3, PeriodType.Monthly, new ConsumeOptions(IdempotencyKey: "req_1"));
        Assert.True(first.Allowed);
        Assert.False(first.Replayed);
        Assert.Equal(3, first.NewUsed);

        var replay = await manager.ConsumeWithResultAsync(
            "user_1", "api_calls", 3, PeriodType.Monthly, new ConsumeOptions(IdempotencyKey: "req_1"));
        Assert.True(replay.Replayed);
        Assert.Equal(3, replay.NewUsed);
        Assert.Equal(PeriodType.Monthly, replay.Period);

        var usage = await manager.GetQuotaAsync("user_1", "api_calls", PeriodType.Monthly);
        Assert.Equal(3, usage.Used);
        Assert.Equal(10, usage.Limit);

        var record = await manager.Storage.GetConsumptionRecordAsync("user_1:req_1", DateTimeOffset.UtcNow);
        Assert.NotNull(record);
        Assert.Equal(PeriodType.Monthly, record!.Window.PeriodType);
        Assert.Equal(3, record.NewUsed);

        await CleanupAsync(prefix);
    }

    [Fact]
    public async Task ForeverCredits_SurviveTopUpOnlyHash_AndCascadeFromAuto()
    {
        // STORAGE-5: AddLimit writes only the numeric hash fields, so GetUsage must surface them.
        var ctx = await CreateManagerAsync(monthly: 1, foreverOrder: true);
        Assert.SkipWhen(ctx is null, RedisNotConfigured);
        var (manager, storage, prefix) = ctx!.Value;

        await manager.TopUpLimitAsync("user_2", "credits", 5, idempotencyKey: "topup_1");
        await manager.TopUpLimitAsync("user_2", "credits", 5, idempotencyKey: "topup_1");

        var forever = await manager.GetQuotaAsync("user_2", "credits", PeriodType.Forever);
        Assert.Equal(5, forever.Limit);
        Assert.Equal(0, forever.Used);

        var spent = await manager.ConsumeWithResultAsync("user_2", "credits", 2, PeriodType.Forever);
        Assert.True(spent.Allowed);
        Assert.Equal(2, spent.NewUsed);
        Assert.Equal(5, spent.Limit);

        var after = await storage.GetUsageAsync("user_2", "credits", PeriodWindow.Forever, defaultLimit: 0);
        Assert.NotNull(after);
        Assert.Equal(2, after!.Used);
        Assert.Equal(5, after.Limit);

        await CleanupAsync(prefix);
    }

    [Fact]
    public async Task ApplyTierChange_UsesRequestResource_AndPreservesProratedLimit()
    {
        // c82ff18: the prorated limit must land on request.Resource, never on a hardcoded resource.
        var connection = await SharedConnection.Value;
        Assert.SkipWhen(connection is null, RedisNotConfigured);
        var conn = connection!;

        var prefix = UniquePrefix();
        var storage = new RedisQuotaStorage(conn, new RedisQuotaOptions { KeyPrefix = prefix });
        var now = DateTimeOffset.UtcNow;
        var window = PeriodWindow.ForMonthly(now, now.AddDays(-15), TimeZoneInfo.Utc);

        await storage.SetUsageAsync("user_3", "invoices", window, used: 2, limit: 10);

        await storage.ApplyTierChangeAsync(
            new TierChangeRequest("user_3", "invoices", "free", "pro", window, OldLimit: 10, NewLimit: 42, CurrentUsed: 2));

        var usage = await storage.GetUsageAsync("user_3", "invoices", window, defaultLimit: 10);
        Assert.NotNull(usage);
        Assert.Equal(42, usage!.Limit);
        Assert.Equal("pro", usage.Tier);

        var consumed = await storage.TryConsumeAsync(
            new StorageConsumeSpec(
                UserId: "user_3",
                Resource: "invoices",
                Amount: 1,
                Window: window,
                ConfiguredLimit: 1000,
                IdempotencyKey: null,
                DistinctKey: null,
                IdempotencyTtl: TimeSpan.FromHours(1),
                NowUtc: now,
                EffectiveTier: "pro"));

        Assert.True(consumed.Allowed);
        Assert.Equal(42, consumed.EffectiveLimit);
        Assert.Equal(3, consumed.NewUsed);

        // A different resource must not receive phantom usage from the tier change.
        var untouched = await storage.GetUsageAsync("user_3", "other", window, defaultLimit: 10);
        Assert.Equal(10, untouched!.Limit);
        Assert.Equal(0, untouched.Used);

        await CleanupAsync(prefix);
    }

    [Fact]
    public async Task ApplyTierChange_WritesCurrentUsedAndDistinctKeys()
    {
        var connection = await SharedConnection.Value;
        Assert.SkipWhen(connection is null, RedisNotConfigured);
        var conn = connection!;

        var prefix = UniquePrefix();
        var storage = new RedisQuotaStorage(conn, new RedisQuotaOptions { KeyPrefix = prefix });
        var now = DateTimeOffset.UtcNow;
        var window = PeriodWindow.ForMonthly(now, now.AddDays(-15), TimeZoneInfo.Utc);

        await storage.ApplyTierChangeAsync(
            new TierChangeRequest(
                "user_3b", "invoices", "free", "pro", window,
                OldLimit: 10, NewLimit: 42, CurrentUsed: 7, DistinctKeys: ["b", "a"]));

        var usage = await storage.GetUsageAsync("user_3b", "invoices", window, defaultLimit: 10);
        Assert.NotNull(usage);
        Assert.Equal(7, usage!.Used);
        Assert.Equal(42, usage.Limit);
        Assert.Equal(["a", "b"], usage.DistinctKeys);

        await CleanupAsync(prefix);
    }

    [Fact]
    public async Task Promotion_IsPreservedWhenBaseEntitlementIsRewritten()
    {
        // 599b63f: a billing sync that writes the base entitlement must not erase the overlay.
        var connection = await SharedConnection.Value;
        Assert.SkipWhen(connection is null, RedisNotConfigured);
        var conn = connection!;

        var prefix = UniquePrefix();
        var storage = new RedisQuotaStorage(conn, new RedisQuotaOptions { KeyPrefix = prefix });
        var updatedAt = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
        var now = DateTimeOffset.UtcNow;

        await storage.SetEntitlementAsync(
            new QuotaEntitlement("user_4", "free", updatedAt, updatedAt, Timezone: "Europe/Bucharest"));

        await storage.SetPromotionAsync(
            "user_4",
            new TierPromotion("pro", now, now.AddDays(3), Source: "test", Reason: "comp", IdempotencyKey: "k1"));

        // Webhook rewrites the base tier without carrying the promotion.
        var rewrittenAt = updatedAt.AddHours(1);
        await storage.SetEntitlementAsync(
            new QuotaEntitlement("user_4", "free", updatedAt, rewrittenAt, Timezone: "Europe/Bucharest"));

        var afterSync = await storage.GetEntitlementAsync("user_4");
        Assert.NotNull(afterSync);
        Assert.Equal("pro", afterSync!.Promotion?.Tier);
        Assert.Equal(rewrittenAt, afterSync.UpdatedAtUtc);
        Assert.Equal("free", afterSync.Tier);

        await storage.ClearPromotionAsync("user_4");

        var cleared = await storage.GetEntitlementAsync("user_4");
        Assert.NotNull(cleared);
        Assert.Null(cleared!.Promotion);
        Assert.Equal(rewrittenAt, cleared.UpdatedAtUtc);
        Assert.Equal("free", cleared.Tier);

        await CleanupAsync(prefix);
    }

    [Fact]
    public async Task Entitlement_RewriteWithoutTimezone_PreservesStoredTimezone()
    {
        var connection = await SharedConnection.Value;
        Assert.SkipWhen(connection is null, RedisNotConfigured);
        var conn = connection!;

        var prefix = UniquePrefix();
        var storage = new RedisQuotaStorage(conn, new RedisQuotaOptions { KeyPrefix = prefix });
        var now = DateTimeOffset.UtcNow;

        await storage.SetEntitlementAsync(
            new QuotaEntitlement("user_tz", "free", now, now, Timezone: "Europe/Bucharest"));
        await storage.SetEntitlementAsync(
            new QuotaEntitlement("user_tz", "pro", now, now.AddMinutes(1)));

        var stored = await storage.GetEntitlementAsync("user_tz");
        Assert.Equal("Europe/Bucharest", stored!.Timezone);
        Assert.Equal("pro", stored.Tier);

        await CleanupAsync(prefix);
    }

    [Fact]
    public async Task SealAndMigrationTombstone_SurviveEntitlementRewrite()
    {
        var connection = await SharedConnection.Value;
        Assert.SkipWhen(connection is null, RedisNotConfigured);
        var conn = connection!;

        var prefix = UniquePrefix();
        var storage = new RedisQuotaStorage(conn, new RedisQuotaOptions { KeyPrefix = prefix });
        var now = DateTimeOffset.UtcNow;
        var sealedUntil = now.AddDays(30);

        await storage.SetEntitlementAsync(new QuotaEntitlement(
            "user_sealed", "free", now, now,
            Sealed: true,
            MigratedTo: "user_target",
            SealExpiresAtUtc: sealedUntil));

        Assert.True(await storage.IsUserSealedAsync("user_sealed", now));

        // Any later base-entitlement write must not resurrect a merged identity.
        await storage.SetEntitlementAsync(new QuotaEntitlement("user_sealed", "pro", now, now.AddMinutes(5)));

        var after = await storage.GetEntitlementAsync("user_sealed");
        Assert.NotNull(after);
        Assert.True(after!.Sealed);
        Assert.Equal("user_target", after.MigratedTo);
        Assert.Equal(sealedUntil, after.SealExpiresAtUtc);
        Assert.True(await storage.IsUserSealedAsync("user_sealed", now));

        await CleanupAsync(prefix);
    }

    [Fact]
    public async Task Drain_FreezesRemainingCredits()
    {
        var connection = await SharedConnection.Value;
        Assert.SkipWhen(connection is null, RedisNotConfigured);
        var conn = connection!;

        var prefix = UniquePrefix();
        var storage = new RedisQuotaStorage(conn, new RedisQuotaOptions { KeyPrefix = prefix });
        var now = DateTimeOffset.UtcNow;
        var window = PeriodWindow.ForMonthly(now, now.AddDays(-5), TimeZoneInfo.Utc);

        StorageConsumeSpec Spec(int amount) => new(
            "user_drain", "api_calls", amount, window, ConfiguredLimit: 100, IdempotencyKey: null,
            DistinctKey: null, IdempotencyTtl: TimeSpan.FromHours(1), NowUtc: now, EffectiveTier: "free");

        Assert.True((await storage.TryConsumeAsync(Spec(1))).Allowed);

        await storage.DrainRemainingAsync("user_drain", "api_calls", window, defaultLimit: 100);

        var usage = await storage.GetUsageAsync("user_drain", "api_calls", window, defaultLimit: 100);
        Assert.NotNull(usage);
        Assert.Equal(1, usage!.Used);
        Assert.Equal(1, usage.Limit);

        // Once drained, the frozen limit wins over the tier configuration for the same tier.
        Assert.False((await storage.TryConsumeAsync(Spec(1))).Allowed);

        await CleanupAsync(prefix);
    }

    [Fact]
    public async Task ProratedLimit_IsPreservedWhenTierCasingDiffers()
    {
        var connection = await SharedConnection.Value;
        Assert.SkipWhen(connection is null, RedisNotConfigured);
        var conn = connection!;

        var prefix = UniquePrefix();
        var storage = new RedisQuotaStorage(conn, new RedisQuotaOptions { KeyPrefix = prefix });
        var now = DateTimeOffset.UtcNow;
        var window = PeriodWindow.ForMonthly(now, now.AddDays(-15), TimeZoneInfo.Utc);

        var first = await storage.TryConsumeAsync(new StorageConsumeSpec(
            "user_case", "api_calls", 1, window, ConfiguredLimit: 42, IdempotencyKey: null,
            DistinctKey: null, IdempotencyTtl: TimeSpan.FromHours(1), NowUtc: now, EffectiveTier: "PRO"));
        Assert.Equal(42, first.EffectiveLimit);

        var second = await storage.TryConsumeAsync(new StorageConsumeSpec(
            "user_case", "api_calls", 1, window, ConfiguredLimit: 1000, IdempotencyKey: null,
            DistinctKey: null, IdempotencyTtl: TimeSpan.FromHours(1), NowUtc: now, EffectiveTier: "Pro"));

        Assert.Equal(42, second.EffectiveLimit);
        Assert.Equal(2, second.NewUsed);

        await CleanupAsync(prefix);
    }

    [Fact]
    public async Task KeySegments_DoNotCollideAcrossUserAndResource()
    {
        var connection = await SharedConnection.Value;
        Assert.SkipWhen(connection is null, RedisNotConfigured);
        var conn = connection!;

        var prefix = UniquePrefix();
        var storage = new RedisQuotaStorage(conn, new RedisQuotaOptions { KeyPrefix = prefix });
        var now = DateTimeOffset.UtcNow;
        var window = PeriodWindow.ForDaily(now, TimeZoneInfo.Utc);

        var specA = new StorageConsumeSpec(
            "a:b", "c", 1, window, ConfiguredLimit: 1, IdempotencyKey: null,
            DistinctKey: null, IdempotencyTtl: TimeSpan.FromHours(1), NowUtc: now);
        var specB = new StorageConsumeSpec(
            "a", "b:c", 1, window, ConfiguredLimit: 1, IdempotencyKey: null,
            DistinctKey: null, IdempotencyTtl: TimeSpan.FromHours(1), NowUtc: now);

        Assert.True((await storage.TryConsumeAsync(specA)).Allowed);
        Assert.True((await storage.TryConsumeAsync(specB)).Allowed);

        var usageA = await storage.GetUsageAsync("a:b", "c", window, defaultLimit: 1);
        var usageB = await storage.GetUsageAsync("a", "b:c", window, defaultLimit: 1);
        Assert.Equal(1, usageA!.Used);
        Assert.Equal(1, usageB!.Used);

        await CleanupAsync(prefix);
    }

    [Fact]
    public async Task Refund_IsIdempotent_RemovesDistinctKey_AndResolvesAutoPeriod()
    {
        var ctx = await CreateManagerAsync(monthly: 5, daily: 5);
        Assert.SkipWhen(ctx is null, RedisNotConfigured);
        var (manager, _, prefix) = ctx!.Value;

        var consumed = await manager.ConsumeWithResultAsync(
            "user_5", "consultare", 1, PeriodType.Daily, new ConsumeOptions(DistinctKey: "cui:1"));
        Assert.True(consumed.ConsumedNewUnit);

        var refunded = await manager.RefundWithResultAsync(new RefundRequest(
            "user_5", "consultare", 1, PeriodType.Daily,
            IdempotencyKey: "refund_1",
            DistinctKeyToRemove: "cui:1"));
        Assert.False(refunded.Replayed);
        Assert.Equal(1, refunded.Amount);

        var replay = await manager.RefundWithResultAsync(new RefundRequest(
            "user_5", "consultare", 1, PeriodType.Daily,
            IdempotencyKey: "refund_1",
            DistinctKeyToRemove: "cui:1"));
        Assert.True(replay.Replayed);
        Assert.Equal(0, replay.Amount);

        var usage = await manager.GetQuotaAsync("user_5", "consultare", PeriodType.Daily);
        Assert.Equal(0, usage.Used);
        Assert.Empty(usage.DistinctKeys);

        // PeriodType.Auto refund resolves the original consumption period (monthly here).
        await manager.ConsumeWithResultAsync(
            "user_5", "api_calls", 2, PeriodType.Monthly, new ConsumeOptions(IdempotencyKey: "m1"));

        var autoRefund = await manager.RefundWithResultAsync(new RefundRequest(
            "user_5", "api_calls", 2, PeriodType.Auto,
            IdempotencyKey: "m1_refund",
            RelatedIdempotencyKey: "m1"));
        Assert.Equal(PeriodType.Monthly, autoRefund.Period);

        var monthly = await manager.GetQuotaAsync("user_5", "api_calls", PeriodType.Monthly);
        Assert.Equal(0, monthly.Used);

        await CleanupAsync(prefix);
    }

    [Fact]
    public async Task RateLimit_SlidingWindow_EnforcesPerSubjectAndGlobalLimits()
    {
        var connection = await SharedConnection.Value;
        Assert.SkipWhen(connection is null, RedisNotConfigured);
        var conn = connection!;

        var prefix = UniquePrefix();
        var storage = new RedisQuotaStorage(conn, new RedisQuotaOptions { KeyPrefix = prefix });
        var config = new RateLimitConfig(
            RateLimitAlgorithm.SlidingWindow,
            Rate: 2,
            Window: TimeSpan.FromMinutes(1),
            Backend: RateLimitBackend.Distributed,
            GlobalLimit: 3);
        var now = DateTimeOffset.UtcNow;

        Assert.True((await storage.CheckRateLimitAsync("a", "refresh", config, now)).Allowed);
        Assert.True((await storage.CheckRateLimitAsync("a", "refresh", config, now)).Allowed);
        Assert.False((await storage.CheckRateLimitAsync("a", "refresh", config, now)).Allowed);
        Assert.True((await storage.CheckRateLimitAsync("b", "refresh", config, now)).Allowed);
        Assert.False((await storage.CheckRateLimitAsync("b", "refresh", config, now)).Allowed);

        var otherResource = await storage.CheckRateLimitAsync("b", "other-resource", config, now);
        Assert.True(otherResource.Allowed);

        await CleanupAsync(prefix);
    }

    [Fact]
    public async Task RateLimit_TokenBucket_AllowsBurstThenDenies_AndSurvivesZeroRate()
    {
        var connection = await SharedConnection.Value;
        Assert.SkipWhen(connection is null, RedisNotConfigured);
        var conn = connection!;

        var prefix = UniquePrefix();
        var storage = new RedisQuotaStorage(conn, new RedisQuotaOptions { KeyPrefix = prefix });
        var config = new RateLimitConfig(
            RateLimitAlgorithm.TokenBucket,
            Rate: 6,
            Window: TimeSpan.FromMinutes(1),
            Burst: 2,
            Backend: RateLimitBackend.Distributed);
        var now = DateTimeOffset.UtcNow;

        Assert.True((await storage.CheckRateLimitAsync("a", "semantic", config, now)).Allowed);
        Assert.True((await storage.CheckRateLimitAsync("a", "semantic", config, now)).Allowed);
        var denied = await storage.CheckRateLimitAsync("a", "semantic", config, now);
        Assert.False(denied.Allowed);
        Assert.True(denied.RetryAfter > TimeSpan.Zero);

        var zeroRate = await storage.CheckRateLimitAsync("a", "semantic", config with { Rate = 0 }, now);
        Assert.False(zeroRate.Allowed);

        await CleanupAsync(prefix);
    }

    [Fact]
    public async Task MergeIsUnsupported_AndServerTimeIsUsed()
    {
        var ctx = await CreateManagerAsync(monthly: 5);
        Assert.SkipWhen(ctx is null, RedisNotConfigured);
        var (manager, storage, prefix) = ctx!.Value;

        await Assert.ThrowsAsync<UnsupportedQuotaOperationException>(() =>
            manager.MergeUserAsync(new MergeUserRequest(
                "source", "target", ["api_calls"], [PeriodType.Monthly], "merge_1")));

        var now = await storage.GetNowUtcAsync();
        Assert.True((now - DateTimeOffset.UtcNow).Duration() < TimeSpan.FromMinutes(2));

        await CleanupAsync(prefix);
    }

    [Fact]
    public async Task ConsumeIdempotencyKey_DoesNotCollideAcrossUsers_WhenIdsContainColon()
    {
        // REDIS-01: scoping joined raw fields, so ("tenant:1", "req-1") and ("tenant", "1:req-1")
        // produced the same record key and the second user replayed the first user's charge.
        var connection = await SharedConnection.Value;
        Assert.SkipWhen(connection is null, RedisNotConfigured);
        var conn = connection!;
        var ct = TestContext.Current.CancellationToken;

        var prefix = UniquePrefix();
        var storage = new RedisQuotaStorage(conn, new RedisQuotaOptions { KeyPrefix = prefix });
        var now = DateTimeOffset.UtcNow;
        var window = PeriodWindow.ForDaily(now, TimeZoneInfo.Utc);

        StorageConsumeSpec Spec(string user, string key) => new(
            user, "api_calls", 1, window, ConfiguredLimit: 10, IdempotencyKey: key,
            DistinctKey: null, IdempotencyTtl: TimeSpan.FromHours(1), NowUtc: now, EffectiveTier: "free");

        var first = await storage.TryConsumeAsync(Spec("tenant:1", "req-1"), ct);
        Assert.True(first.Allowed);
        Assert.False(first.WasIdempotencyHit);

        var second = await storage.TryConsumeAsync(Spec("tenant", "1:req-1"), ct);
        Assert.True(second.Allowed);
        Assert.False(second.WasIdempotencyHit);
        Assert.Equal(1, second.NewUsed);

        var secondUsage = await storage.GetUsageAsync("tenant", "api_calls", window, defaultLimit: 10, ct);
        Assert.NotNull(secondUsage);
        Assert.Equal(1, secondUsage!.Used);

        // The record must still be reachable through the scoped key QuotaManager passes to storage.
        var scoped = QuotaManager.ScopedIdempotencyKey("tenant:1", "req-1");
        var record = await storage.GetConsumptionRecordAsync(scoped, now, ct);
        Assert.NotNull(record);
        Assert.Equal("tenant:1", record!.UserId);

        await CleanupAsync(prefix);
    }

    [Fact]
    public async Task ForeverCreditIdempotencyKeys_DoNotCollide_WhenUserOrResourceContainsColon()
    {
        // REDIS-01: topup/refund_credits templates joined raw fields, so ("a:b", "c") and ("a", "b:c")
        // shared one idempotency record and the second grant was silently skipped.
        var connection = await SharedConnection.Value;
        Assert.SkipWhen(connection is null, RedisNotConfigured);
        var conn = connection!;
        var ct = TestContext.Current.CancellationToken;

        var prefix = UniquePrefix();
        var storage = new RedisQuotaStorage(conn, new RedisQuotaOptions { KeyPrefix = prefix });
        var now = DateTimeOffset.UtcNow;
        var ttl = TimeSpan.FromHours(1);

        await storage.TopUpForeverCreditsAsync("a:b", "c", 5, "evt-1", ttl, now, ct);
        await storage.TopUpForeverCreditsAsync("a", "b:c", 7, "evt-1", ttl, now, ct);

        var firstUsage = await storage.GetUsageAsync("a:b", "c", PeriodWindow.Forever, defaultLimit: 0, ct);
        var secondUsage = await storage.GetUsageAsync("a", "b:c", PeriodWindow.Forever, defaultLimit: 0, ct);
        Assert.Equal(5, firstUsage!.Limit);
        Assert.Equal(7, secondUsage!.Limit);

        await storage.RefundForeverCreditsAsync("a:b", "c", 2, "evt-r", ttl, now, ct);
        await storage.RefundForeverCreditsAsync("a", "b:c", 2, "evt-r", ttl, now, ct);

        Assert.Equal(3, (await storage.GetUsageAsync("a:b", "c", PeriodWindow.Forever, 0, ct))!.Limit);
        Assert.Equal(5, (await storage.GetUsageAsync("a", "b:c", PeriodWindow.Forever, 0, ct))!.Limit);

        await CleanupAsync(prefix);
    }

    [Fact]
    public async Task DistinctKeySets_ExpireWithTheirWindow_OnEveryWritePath()
    {
        // REDIS-02: consume, tier change and SetUsage all wrote the distinct set without a TTL, so one
        // permanent key accumulated per user/resource/window.
        var connection = await SharedConnection.Value;
        Assert.SkipWhen(connection is null, RedisNotConfigured);
        var conn = connection!;
        var ct = TestContext.Current.CancellationToken;

        var prefix = UniquePrefix();
        var storage = new RedisQuotaStorage(conn, new RedisQuotaOptions { KeyPrefix = prefix });
        var db = conn.GetDatabase();
        var now = DateTimeOffset.UtcNow;
        var window = PeriodWindow.ForMonthly(now, now.AddDays(-15), TimeZoneInfo.Utc);

        await storage.TryConsumeAsync(new StorageConsumeSpec(
            "user_distinct_consume", "api_calls", 1, window, ConfiguredLimit: 10, IdempotencyKey: null,
            DistinctKey: "cui:1", IdempotencyTtl: TimeSpan.FromHours(1), NowUtc: now, EffectiveTier: "free"), ct);

        await storage.ApplyTierChangeAsync(new TierChangeRequest(
            "user_distinct_tier", "api_calls", "free", "pro", window,
            OldLimit: 10, NewLimit: 42, CurrentUsed: 0, DistinctKeys: ["a"]), ct);

        await storage.SetUsageAsync(
            "user_distinct_set", "api_calls", window, used: 1, limit: 10, distinctKeys: ["b"], ct);

        var distinctKeys = await FindKeysAsync(db, $"{prefix}distinct:*");
        Assert.Equal(3, distinctKeys.Count);
        foreach (var key in distinctKeys)
        {
            Assert.NotNull(await db.KeyTimeToLiveAsync(key));
        }

        await CleanupAsync(prefix);
    }

    [Fact]
    public async Task TokenBucket_TtlCoversFullRefill_AndIdleRefillIsPreserved()
    {
        // REDIS-03: the TTL was window + 5 minutes even when a full refill needed 100 minutes, so an
        // idle depleted bucket expired and reset to a full burst.
        var connection = await SharedConnection.Value;
        Assert.SkipWhen(connection is null, RedisNotConfigured);
        var conn = connection!;
        var ct = TestContext.Current.CancellationToken;

        var prefix = UniquePrefix();
        var storage = new RedisQuotaStorage(conn, new RedisQuotaOptions { KeyPrefix = prefix });
        var db = conn.GetDatabase();
        var now = DateTimeOffset.UtcNow;
        var config = new RateLimitConfig(
            RateLimitAlgorithm.TokenBucket,
            Rate: 1,
            Window: TimeSpan.FromMinutes(1),
            Burst: 100,
            Backend: RateLimitBackend.Distributed);

        for (var i = 0; i < 100; i++)
        {
            Assert.True((await storage.CheckRateLimitAsync("user_bucket", "semantic", config, now, ct)).Allowed);
        }

        Assert.False((await storage.CheckRateLimitAsync("user_bucket", "semantic", config, now, ct)).Allowed);

        var key = Assert.Single(await FindKeysAsync(db, $"{prefix}ratelimit:*"));
        var ttl = await db.KeyTimeToLiveAsync(key);
        Assert.NotNull(ttl);
        Assert.True(ttl >= TimeSpan.FromMinutes(100), $"bucket TTL is {ttl}, full refill needs 100 minutes");

        // Six minutes later only ~6 tokens have refilled; a reset bucket would allow the whole burst.
        var later = now.AddMinutes(6);
        var allowed = 0;
        for (var i = 0; i < 100; i++)
        {
            if ((await storage.CheckRateLimitAsync("user_bucket", "semantic", config, later, ct)).Allowed)
            {
                allowed++;
            }
        }

        Assert.InRange(allowed, 1, 7);

        await CleanupAsync(prefix);
    }

    [Fact]
    public async Task TokenBucket_EnforcesGlobalLimit()
    {
        // REDIS-04: the token-bucket branch never passed GlobalLimit, so every subject got a fresh
        // bucket while LocalRateLimitEngine and Firestore enforced the shared budget.
        var connection = await SharedConnection.Value;
        Assert.SkipWhen(connection is null, RedisNotConfigured);
        var conn = connection!;
        var ct = TestContext.Current.CancellationToken;

        var prefix = UniquePrefix();
        var storage = new RedisQuotaStorage(conn, new RedisQuotaOptions { KeyPrefix = prefix });
        var now = DateTimeOffset.UtcNow;
        var config = new RateLimitConfig(
            RateLimitAlgorithm.TokenBucket,
            Rate: 10,
            Window: TimeSpan.FromMinutes(1),
            Burst: 1,
            Backend: RateLimitBackend.Distributed,
            GlobalLimit: 1);

        Assert.True((await storage.CheckRateLimitAsync("user_global_a", "semantic", config, now, ct)).Allowed);

        var second = await storage.CheckRateLimitAsync("user_global_b", "semantic", config, now, ct);
        Assert.False(second.Allowed);
        Assert.True(second.RetryAfter > TimeSpan.Zero);

        await CleanupAsync(prefix);
    }

    [Fact]
    public async Task ConsumeWithoutEffectiveTier_PreservesStoredTierAndProratedLimit()
    {
        // REDIS-05: EffectiveTier is optional on the storage contract, but an omitted tier was written
        // as an empty string and erased the prorated cap written by ApplyTierChange.
        var connection = await SharedConnection.Value;
        Assert.SkipWhen(connection is null, RedisNotConfigured);
        var conn = connection!;
        var ct = TestContext.Current.CancellationToken;

        var prefix = UniquePrefix();
        var storage = new RedisQuotaStorage(conn, new RedisQuotaOptions { KeyPrefix = prefix });
        var now = DateTimeOffset.UtcNow;
        var window = PeriodWindow.ForMonthly(now, now.AddDays(-15), TimeZoneInfo.Utc);

        await storage.ApplyTierChangeAsync(new TierChangeRequest(
            "user_tier", "api_calls", "free", "pro", window,
            OldLimit: 10, NewLimit: 42, CurrentUsed: 0), ct);

        StorageConsumeSpec Spec(string? tier) => new(
            "user_tier", "api_calls", 1, window, ConfiguredLimit: 1000, IdempotencyKey: null,
            DistinctKey: null, IdempotencyTtl: TimeSpan.FromHours(1), NowUtc: now, EffectiveTier: tier);

        var withoutTier = await storage.TryConsumeAsync(Spec(null), ct);
        Assert.True(withoutTier.Allowed);
        Assert.Equal(42, withoutTier.EffectiveLimit);

        var usage = await storage.GetUsageAsync("user_tier", "api_calls", window, defaultLimit: 1000, ct);
        Assert.Equal("pro", usage!.Tier);
        Assert.Equal(42, usage.Limit);

        // A later caller that does supply the tier must still find the prorated cap.
        var withTier = await storage.TryConsumeAsync(Spec("pro"), ct);
        Assert.Equal(42, withTier.EffectiveLimit);
        Assert.Equal(2, withTier.NewUsed);

        await CleanupAsync(prefix);
    }

    [Fact]
    public async Task DrainBeforeFirstConsume_CreatesUsageHashWithTtl()
    {
        // REDIS-06: draining before the first consume created a recurring usage hash with no expiry,
        // and the drained limit of 0 meant the consume path could never set one.
        var connection = await SharedConnection.Value;
        Assert.SkipWhen(connection is null, RedisNotConfigured);
        var conn = connection!;
        var ct = TestContext.Current.CancellationToken;

        var prefix = UniquePrefix();
        var storage = new RedisQuotaStorage(conn, new RedisQuotaOptions { KeyPrefix = prefix });
        var db = conn.GetDatabase();
        var now = DateTimeOffset.UtcNow;
        var window = PeriodWindow.ForMonthly(now, now.AddDays(-5), TimeZoneInfo.Utc);

        await storage.DrainRemainingAsync("user_drain_ttl", "api_calls", window, defaultLimit: 10, ct);

        var usageKey = Assert.Single(await FindKeysAsync(db, $"{prefix}usage:*"));
        Assert.NotNull(await db.KeyTimeToLiveAsync(usageKey));

        var usage = await storage.GetUsageAsync("user_drain_ttl", "api_calls", window, defaultLimit: 10, ct);
        Assert.Equal(0, usage!.Used);

        await CleanupAsync(prefix);
    }

    [Fact]
    public async Task ZeroIdempotencyTtl_DoesNotRetainConsumeRecord()
    {
        // REDIS-07: a zero TTL wrote a consume record with no expiry, so the request was replayed
        // forever. InMemoryQuotaStorage expires the record immediately; Redis must not remember it.
        var connection = await SharedConnection.Value;
        Assert.SkipWhen(connection is null, RedisNotConfigured);
        var conn = connection!;
        var ct = TestContext.Current.CancellationToken;

        var prefix = UniquePrefix();
        var storage = new RedisQuotaStorage(conn, new RedisQuotaOptions { KeyPrefix = prefix });
        var db = conn.GetDatabase();
        var now = DateTimeOffset.UtcNow;
        var window = PeriodWindow.ForDaily(now, TimeZoneInfo.Utc);
        var spec = new StorageConsumeSpec(
            "user_zero_ttl", "api_calls", 1, window, ConfiguredLimit: 10, IdempotencyKey: "req-zero-ttl",
            DistinctKey: null, IdempotencyTtl: TimeSpan.Zero, NowUtc: now, EffectiveTier: "free");

        var first = await storage.TryConsumeAsync(spec, ct);
        Assert.True(first.Allowed);
        Assert.False(first.WasIdempotencyHit);

        var later = await storage.TryConsumeAsync(spec with { NowUtc = now.AddDays(400) }, ct);
        Assert.False(later.WasIdempotencyHit);
        Assert.Equal(2, later.NewUsed);

        Assert.Empty(await FindKeysAsync(db, $"{prefix}record:*"));

        await CleanupAsync(prefix);
    }

    private static async Task<List<RedisKey>> FindKeysAsync(IDatabase db, string pattern)
    {
        var server = db.Multiplexer.GetServer(db.Multiplexer.GetEndPoints().First());
        var keys = new List<RedisKey>();
        await foreach (var key in server.KeysAsync(pattern: pattern))
        {
            keys.Add(key);
        }

        return keys;
    }

    private static async Task<(QuotaManager Manager, RedisQuotaStorage Storage, string Prefix)?> CreateManagerAsync(
        int monthly,
        int daily = 0,
        bool foreverOrder = false)
    {
        var connection = await SharedConnection.Value;
        if (connection is null)
        {
            return null;
        }

        var prefix = UniquePrefix();
        var storage = new RedisQuotaStorage(connection, new RedisQuotaOptions { KeyPrefix = prefix });

        var order = foreverOrder
            ? new[] { PeriodType.Monthly, PeriodType.Forever }
            : new[] { PeriodType.Monthly };

        var config = new QuotaConfig
        {
            DefaultTier = "free",
            Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["free"] = new TierConfig
                {
                    DailyQuotas = new Dictionary<string, int> { ["consultare"] = daily, ["credits"] = daily },
                    MonthlyQuotas = new Dictionary<string, int>
                    {
                        ["api_calls"] = monthly,
                        ["credits"] = monthly
                    },
                    ConsumptionOrder = order
                }
            }
        };

        return (new QuotaManager(storage, config), storage, prefix);
    }

    private static string UniquePrefix() => $"netquota_test:{Guid.NewGuid():N}:";

    private static async Task CleanupAsync(string prefix)
    {
        var connection = await SharedConnection.Value;
        if (connection is null)
        {
            return;
        }

        var db = connection.GetDatabase();
        foreach (var endpoint in connection.GetEndPoints())
        {
            var server = connection.GetServer(endpoint);
            if (!server.IsConnected || server.IsReplica)
            {
                continue;
            }

            await foreach (var key in server.KeysAsync(pattern: $"{prefix}*"))
            {
                await db.KeyDeleteAsync(key);
            }
        }
    }

    private static async Task<IConnectionMultiplexer?> ConnectAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("NETQUOTA_REDIS");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        // A configured-but-unreachable server must fail loudly instead of silently skipping.
        var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        await connection.GetDatabase().PingAsync();
        return connection;
    }
}
