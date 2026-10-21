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
