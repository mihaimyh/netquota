using NetQuota;
using NetQuota.Postgres;
using Npgsql;

namespace NetQuota.Tests;

/// <summary>
/// Testează adaptorul PostgreSQL real. Se skip-uiesc explicit când nu există un server:
///   docker run -d --rm --name netquota-postgres -p 5433:5432 \
///     -e POSTGRES_USER=netquota -e POSTGRES_PASSWORD=netquota -e POSTGRES_DB=netquota postgres:16-alpine
///   $env:NETQUOTA_POSTGRES='Host=127.0.0.1;Port=5433;Database=netquota;Username=netquota;Password=netquota'
/// </summary>
public class NetQuotaPostgresTests
{
    private const string PostgresNotConfigured =
        "NETQUOTA_POSTGRES nu este setat; testele de integrare PostgreSQL sunt sărite.";

    private static readonly Lazy<Task<NpgsqlDataSource?>> SharedDataSource = new(ConnectAsync);

    [Fact]
    public async Task Entitlement_RewritePreservesPromotion_AndPromotionDoesNotTouchUpdatedAt()
    {
        var maybeStorage = await CreateAsync();
        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
        var storage = maybeStorage!;

        var userId = NewUser();
        var updatedAt = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
        var now = DateTimeOffset.UtcNow;

        await storage.SetEntitlementAsync(
            new QuotaEntitlement(userId, "free", updatedAt, updatedAt, Timezone: "Europe/Bucharest"));

        await storage.SetPromotionAsync(
            userId,
            new TierPromotion("pro", now, now.AddDays(3), Source: "test", Reason: "comp", IdempotencyKey: "k1"));

        var withPromo = await storage.GetEntitlementAsync(userId);
        Assert.Equal("pro", withPromo!.Promotion?.Tier);
        Assert.Equal(updatedAt, withPromo.UpdatedAtUtc);

        // Billing sync rewrites the base entitlement without carrying the promotion.
        var rewrittenAt = updatedAt.AddHours(1);
        await storage.SetEntitlementAsync(
            new QuotaEntitlement(userId, "free", updatedAt, rewrittenAt, Timezone: "Europe/Bucharest"));

        var afterSync = await storage.GetEntitlementAsync(userId);
        Assert.Equal("pro", afterSync!.Promotion?.Tier);
        Assert.Equal(rewrittenAt, afterSync.UpdatedAtUtc);
        Assert.Equal("free", afterSync.Tier);

        await storage.ClearPromotionAsync(userId);

        var cleared = await storage.GetEntitlementAsync(userId);
        Assert.Null(cleared!.Promotion);
        Assert.Equal(rewrittenAt, cleared.UpdatedAtUtc);
    }

    [Fact]
    public async Task Entitlement_RewriteWithoutTimezone_PreservesStoredTimezone()
    {
        var maybeStorage = await CreateAsync();
        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
        var storage = maybeStorage!;

        var userId = NewUser();
        var now = DateTimeOffset.UtcNow;

        await storage.SetEntitlementAsync(
            new QuotaEntitlement(userId, "free", now, now, Timezone: "Europe/Bucharest"));
        await storage.SetEntitlementAsync(
            new QuotaEntitlement(userId, "pro", now, now.AddMinutes(1)));

        var stored = await storage.GetEntitlementAsync(userId);
        Assert.Equal("Europe/Bucharest", stored!.Timezone);
        Assert.Equal("pro", stored.Tier);
    }

    [Fact]
    public async Task Consume_IsIdempotent_PreservesProratedLimit_AndRecordsForeverPeriod()
    {
        var maybeStorage = await CreateAsync();
        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
        var storage = maybeStorage!;

        var userId = NewUser();
        var now = DateTimeOffset.UtcNow;
        var window = PeriodWindow.ForMonthly(now, now.AddDays(-15), TimeZoneInfo.Utc);

        StorageConsumeSpec Spec(int amount, int configured, string? key = null, string tier = "pro", PeriodWindow? w = null) =>
            new(userId, "api_calls", amount, w ?? window, configured, key, null, TimeSpan.FromHours(1), now, tier);

        Assert.True((await storage.TryConsumeAsync(Spec(2, 42))).Allowed);

        // Prorated limit survives a later consume for the same tier (goquota 59c0d23).
        var second = await storage.TryConsumeAsync(Spec(1, 1000));
        Assert.True(second.Allowed);
        Assert.Equal(42, second.EffectiveLimit);
        Assert.Equal(3, second.NewUsed);

        var replay = await storage.TryConsumeAsync(Spec(1, 1000, key: "req_1"));
        Assert.False(replay.WasIdempotencyHit);
        var replayAgain = await storage.TryConsumeAsync(Spec(1, 1000, key: "req_1"));
        Assert.True(replayAgain.WasIdempotencyHit);
        Assert.Equal(replay.NewUsed, replayAgain.NewUsed);

        // Unlimited (-1) never denies.
        Assert.True((await storage.TryConsumeAsync(Spec(1000, -1))).Allowed);
        Assert.True((await storage.TryConsumeAsync(Spec(1000, -1))).Allowed);

        // Forever credits: top-up then consume, record round-trips the Forever period.
        await storage.TopUpForeverCreditsAsync(userId, "credits", 5, null, TimeSpan.FromHours(1), now);
        var forever = await storage.TryConsumeAsync(
            new StorageConsumeSpec(userId, "credits", 2, PeriodWindow.Forever, 0, "credits_1", null,
                TimeSpan.FromHours(1), now, "pro"));
        Assert.True(forever.Allowed);
        Assert.Equal(5, forever.EffectiveLimit);
        Assert.Equal(PeriodType.Forever, forever.ChargedPeriod);

        var record = await storage.GetConsumptionRecordAsync($"{userId}:credits_1", now);
        Assert.NotNull(record);
        Assert.Equal(PeriodType.Forever, record!.Window.PeriodType);
    }

    [Fact]
    public async Task Refund_IsIdempotent_AndRemovesDistinctKey()
    {
        var maybeStorage = await CreateAsync();
        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
        var storage = maybeStorage!;

        var userId = NewUser();
        var now = DateTimeOffset.UtcNow;
        var window = PeriodWindow.ForDaily(now, TimeZoneInfo.Utc);

        var consumed = await storage.TryConsumeAsync(
            new StorageConsumeSpec(userId, "consultare", 1, window, 10, null, "cui:1",
                TimeSpan.FromHours(1), now, "free"));
        Assert.True(consumed.ConsumedNewUnit);

        var refunded = await storage.RefundAsync(
            new RefundRequest(userId, "consultare", 1, PeriodType.Daily,
                IdempotencyKey: "r1", DistinctKeyToRemove: "cui:1"),
            window, 10, TimeSpan.FromHours(1), now);
        Assert.False(refunded.Replayed);
        Assert.Equal(1, refunded.Amount);

        var replay = await storage.RefundAsync(
            new RefundRequest(userId, "consultare", 1, PeriodType.Daily,
                IdempotencyKey: "r1", DistinctKeyToRemove: "cui:1"),
            window, 10, TimeSpan.FromHours(1), now);
        Assert.True(replay.Replayed);
        Assert.Equal(0, replay.Amount);

        var usage = await storage.GetUsageAsync(userId, "consultare", window, 10);
        Assert.Equal(0, usage!.Used);
        Assert.Empty(usage.DistinctKeys);
    }

    [Fact]
    public async Task Drain_FreezesRemainingCredits()
    {
        var maybeStorage = await CreateAsync();
        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
        var storage = maybeStorage!;

        var userId = NewUser();
        var now = DateTimeOffset.UtcNow;
        var window = PeriodWindow.ForMonthly(now, now.AddDays(-5), TimeZoneInfo.Utc);

        StorageConsumeSpec Spec(int amount) => new(
            userId, "api_calls", amount, window, 100, null, null, TimeSpan.FromHours(1), now, "free");

        Assert.True((await storage.TryConsumeAsync(Spec(1))).Allowed);
        await storage.DrainRemainingAsync(userId, "api_calls", window, 100);

        var usage = await storage.GetUsageAsync(userId, "api_calls", window, 100);
        Assert.Equal(1, usage!.Used);
        Assert.Equal(1, usage.Limit);
        Assert.False((await storage.TryConsumeAsync(Spec(1))).Allowed);
    }

    [Fact]
    public async Task ConcurrentTopUp_SameIdempotencyKey_CreditsOnce()
    {
        var maybeStorage = await CreateAsync();
        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
        var storage = maybeStorage!;

        var userId = NewUser();
        var now = DateTimeOffset.UtcNow;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            storage.TopUpForeverCreditsAsync(userId, "credits", 10, "same-key", TimeSpan.FromHours(1), now)));

        var usage = await storage.GetUsageAsync(userId, "credits", PeriodWindow.Forever, 0);
        Assert.Equal(10, usage!.Limit);
    }

    [Fact]
    public async Task ConcurrentRefund_SameIdempotencyKey_ReplaysWithoutThrowing()
    {
        var maybeStorage = await CreateAsync();
        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
        var storage = maybeStorage!;

        var userId = NewUser();
        var now = DateTimeOffset.UtcNow;
        var window = PeriodWindow.ForDaily(now, TimeZoneInfo.Utc);

        await storage.TopUpForeverCreditsAsync(userId, "credits", 10, null, TimeSpan.FromHours(1), now);
        await storage.TryConsumeAsync(new StorageConsumeSpec(
            userId, "credits", 5, PeriodWindow.Forever, 0, null, null, TimeSpan.FromHours(1), now, "free"));

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            storage.RefundAsync(
                new RefundRequest(userId, "credits", 1, PeriodType.Forever, IdempotencyKey: "same-refund"),
                PeriodWindow.Forever, 0, TimeSpan.FromHours(1), now)));

        Assert.All(outcomes, outcome => Assert.True(outcome.Amount is 0 or 1));
        Assert.Contains(outcomes, outcome => outcome.Replayed);

        var usage = await storage.GetUsageAsync(userId, "credits", PeriodWindow.Forever, 0);
        Assert.Equal(4, usage!.Used);
    }

    [Fact]
    public async Task ApplyTierChangeAndSetUsage_WriteCountersAndDistinctKeys()
    {
        var maybeStorage = await CreateAsync();
        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
        var storage = maybeStorage!;

        var userId = NewUser();
        var now = DateTimeOffset.UtcNow;
        var window = PeriodWindow.ForMonthly(now, now.AddDays(-15), TimeZoneInfo.Utc);

        await storage.ApplyTierChangeAsync(
            new TierChangeRequest(
                userId, "api_calls", "free", "pro", window,
                OldLimit: 10, NewLimit: 42, CurrentUsed: 7, DistinctKeys: ["b", "a"]));

        var afterTierChange = await storage.GetUsageAsync(userId, "api_calls", window, 10);
        Assert.Equal(7, afterTierChange!.Used);
        Assert.Equal(42, afterTierChange.Limit);
        Assert.Equal(["a", "b"], afterTierChange.DistinctKeys);

        await storage.SetUsageAsync(userId, "api_calls", window, used: 3, limit: 50, ["x"]);

        var afterSet = await storage.GetUsageAsync(userId, "api_calls", window, 10);
        Assert.Equal(3, afterSet!.Used);
        Assert.Equal(50, afterSet.Limit);
        Assert.Equal(["x"], afterSet.DistinctKeys);

        var refund = await storage.RefundAsync(
            new RefundRequest(userId, "api_calls", 2, PeriodType.Monthly, IdempotencyKey: "refund_once"),
            window, 50, TimeSpan.FromHours(1), now);

        Assert.False(refund.Replayed);
        Assert.Equal(2, refund.Amount);

        var record = await storage.GetRefundRecordAsync($"{userId}:refund_once", now);
        Assert.NotNull(record);
        Assert.Equal(2, record!.Amount);
        Assert.Equal(PeriodType.Monthly, record.Window.PeriodType);
    }

    [Fact]
    public async Task MergeUser_TransfersUsageAndForeverCredits_SealsSource_AndReplays()
    {
        var maybeStorage = await CreateAsync();
        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
        var storage = maybeStorage!;

        var source = NewUser();
        var target = NewUser();
        var now = DateTimeOffset.UtcNow;
        var daily = PeriodWindow.ForDaily(now, TimeZoneInfo.Utc);

        foreach (var distinct in new[] { "cui:1", "cui:2" })
        {
            await storage.TryConsumeAsync(new StorageConsumeSpec(
                source, "consultare", 1, daily, 10, null, distinct, TimeSpan.FromHours(1), now, "free"));
        }

        await storage.TryConsumeAsync(new StorageConsumeSpec(
            target, "consultare", 1, daily, 10, null, "cui:3", TimeSpan.FromHours(1), now, "free"));

        await storage.TopUpForeverCreditsAsync(source, "credits", 5, null, TimeSpan.FromHours(1), now);
        await storage.TryConsumeAsync(new StorageConsumeSpec(
            source, "credits", 2, PeriodWindow.Forever, 0, null, null, TimeSpan.FromHours(1), now, "free"));

        await storage.SetEntitlementAsync(new QuotaEntitlement(source, "free", now, now));
        await storage.SetPromotionAsync(
            source, new TierPromotion("pro", now, now.AddDays(2), Source: "test", Reason: "comp"));

        var request = new StorageMergeRequest(
            SourceUserId: source,
            TargetUserId: target,
            Items:
            [
                new MergeItem("consultare", PeriodType.Daily, daily, daily),
                new MergeItem("credits", PeriodType.Forever, PeriodWindow.Forever, PeriodWindow.Forever)
            ],
            IdempotencyKey: $"merge:{source}",
            SealSource: true,
            NowUtc: now,
            SealExpiresAtUtc: now.AddDays(30));

        var merged = await storage.MergeUserAsync(request);
        Assert.False(merged.AlreadyMerged);
        Assert.Equal(2, merged.TransferredUsage["consultare:Daily"]);
        Assert.Equal(3, merged.TransferredForeverCredits["credits"]);
        Assert.Equal("pro", merged.DroppedPromotion?.Tier);

        var targetDaily = await storage.GetUsageAsync(target, "consultare", daily, 10);
        Assert.Equal(3, targetDaily!.Used);
        Assert.Equal(["cui:1", "cui:2", "cui:3"], targetDaily.DistinctKeys);

        var targetForever = await storage.GetUsageAsync(target, "credits", PeriodWindow.Forever, 0);
        Assert.Equal(3, targetForever!.Limit);

        Assert.True(await storage.IsUserSealedAsync(source, now));
        var sourceEntitlement = await storage.GetEntitlementAsync(source);
        Assert.True(sourceEntitlement!.Sealed);
        Assert.Equal(target, sourceEntitlement.MigratedTo);

        var replay = await storage.MergeUserAsync(request);
        Assert.True(replay.AlreadyMerged);
        Assert.Equal(3, replay.TransferredForeverCredits["credits"]);

        await Assert.ThrowsAsync<UserSealedException>(() =>
            storage.MergeUserAsync(request with { SourceUserId = target, TargetUserId = source, IdempotencyKey = $"{source}:back" }));

        await Assert.ThrowsAsync<SameUserMergeException>(() =>
            storage.MergeUserAsync(request with { TargetUserId = source, IdempotencyKey = $"{source}:same" }));
    }

    [Fact]
    public async Task AuditLog_FiltersAndOrdersNewestFirst()
    {
        var maybeStorage = await CreateAsync();
        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
        var storage = maybeStorage!;

        var userId = NewUser();
        var now = DateTimeOffset.UtcNow;

        await storage.LogAuditEntryAsync(new AuditLogEntry(userId, "api_calls", "admin_set", 1, now, "tester", "first"));
        await storage.LogAuditEntryAsync(new AuditLogEntry(userId, "credits", "admin_grant_credit", 5, now.AddSeconds(1), "tester", "second"));

        var all = await storage.GetAuditLogsAsync(new AuditLogFilter(UserId: userId, Limit: 10));
        Assert.Equal(2, all.Count);
        Assert.Equal("credits", all[0].Resource);

        var filtered = await storage.GetAuditLogsAsync(new AuditLogFilter(UserId: userId, Resource: "api_calls", Limit: 10));
        Assert.Single(filtered);
        Assert.Equal("admin_set", filtered[0].Action);
    }

    [Fact]
    public async Task ConcurrentConsume_RespectsLimitExactly()
    {
        var maybeStorage = await CreateAsync();
        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
        var storage = maybeStorage!;

        var userId = NewUser();
        var now = DateTimeOffset.UtcNow;
        var window = PeriodWindow.ForDaily(now, TimeZoneInfo.Utc);
        var allowed = 0;

        await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
        {
            var outcome = await storage.TryConsumeAsync(new StorageConsumeSpec(
                userId, "consultare", 1, window, ConfiguredLimit: 5, IdempotencyKey: null,
                DistinctKey: $"cui:{i}", IdempotencyTtl: TimeSpan.FromHours(1),
                NowUtc: now, EffectiveTier: "free"));

            if (outcome.Allowed)
            {
                Interlocked.Increment(ref allowed);
            }
        }));

        Assert.Equal(5, allowed);
        var usage = await storage.GetUsageAsync(userId, "consultare", window, 5);
        Assert.Equal(5, usage!.Used);
        Assert.Equal(5, usage.DistinctKeys.Count);
    }

    [Fact]
    public async Task RateLimit_DelegatesToPerInstanceWindows()
    {
        var maybeStorage = await CreateAsync();
        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
        var storage = maybeStorage!;

        var config = new RateLimitConfig(
            RateLimitAlgorithm.SlidingWindow, Rate: 2, Window: TimeSpan.FromMinutes(1),
            Backend: RateLimitBackend.Distributed);
        var now = DateTimeOffset.UtcNow;

        Assert.True((await storage.CheckRateLimitAsync("a", "refresh", config, now)).Allowed);
        Assert.True((await storage.CheckRateLimitAsync("a", "refresh", config, now)).Allowed);
        Assert.False((await storage.CheckRateLimitAsync("a", "refresh", config, now)).Allowed);

        var zeroRate = await storage.CheckRateLimitAsync("a", "refresh", config with { Rate = 0 }, now);
        Assert.False(zeroRate.Allowed);
    }

    [Fact]
    public async Task Consume_ReusingIdempotencyKeyAfterItsTtl_ReplacesTheExpiredRecord()
    {
        var maybeStorage = await CreateAsync();
        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
        var storage = maybeStorage!;

        var userId = NewUser();
        var now = DateTimeOffset.UtcNow;
        var window = PeriodWindow.ForDaily(now, TimeZoneInfo.Utc);
        var ttl = TimeSpan.FromMilliseconds(500);
        var token = TestContext.Current.CancellationToken;

        StorageConsumeSpec Spec(DateTimeOffset at) => new(
            userId, "api_calls", 1, window, 10, "ttl_key", null, ttl, at, "free");

        Assert.True((await storage.TryConsumeAsync(Spec(now), token)).Allowed);

        // The key lapsed: this is a new charge, and it must not throw 23505 on the stale row.
        var afterExpiry = await storage.TryConsumeAsync(Spec(now.AddSeconds(1)), token);
        Assert.True(afterExpiry.Allowed);
        Assert.False(afterExpiry.WasIdempotencyHit);
        Assert.Equal(2, afterExpiry.NewUsed);

        // The refreshed record now replays.
        var immediateRetry = await storage.TryConsumeAsync(Spec(now.AddSeconds(1)), token);
        Assert.True(immediateRetry.WasIdempotencyHit);
        Assert.Equal(2, immediateRetry.NewUsed);
    }

    [Fact]
    public async Task Refund_ReusingIdempotencyKeyAfterItsTtl_ReplacesTheExpiredRecord()
    {
        var maybeStorage = await CreateAsync();
        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
        var storage = maybeStorage!;

        var userId = NewUser();
        var now = DateTimeOffset.UtcNow;
        var window = PeriodWindow.ForDaily(now, TimeZoneInfo.Utc);
        var ttl = TimeSpan.FromMilliseconds(500);
        var token = TestContext.Current.CancellationToken;

        await storage.SetUsageAsync(userId, "consultare", window, used: 5, limit: 10, cancellationToken: token);

        RefundRequest Request() => new(userId, "consultare", 1, PeriodType.Daily, IdempotencyKey: "refund_ttl");

        var first = await storage.RefundAsync(Request(), window, 10, ttl, now, token);
        Assert.False(first.Replayed);

        var afterExpiry = await storage.RefundAsync(Request(), window, 10, ttl, now.AddSeconds(1), token);
        Assert.False(afterExpiry.Replayed);
        Assert.Equal(1, afterExpiry.Amount);

        var immediateRetry = await storage.RefundAsync(Request(), window, 10, ttl, now.AddSeconds(1), token);
        Assert.True(immediateRetry.Replayed);
        Assert.Equal(0, immediateRetry.Amount);

        var usage = await storage.GetUsageAsync(userId, "consultare", window, 10, token);
        Assert.Equal(3, usage!.Used);
    }

    [Fact]
    public async Task Consume_PersistsWindowBounds_SoPruneDeletesConsumedRows()
    {
        var maybeStorage = await CreateAsync();
        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
        var storage = maybeStorage!;

        var userId = NewUser();
        var windowStart = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var window = PeriodWindow.ForDaily(windowStart, TimeZoneInfo.Utc);
        var token = TestContext.Current.CancellationToken;

        var outcome = await storage.TryConsumeAsync(new StorageConsumeSpec(
            userId, "api_calls", 1, window, 10, "consume_1", "doc-1", TimeSpan.FromHours(1),
            windowStart.AddHours(10), "free"), token);
        Assert.True(outcome.Allowed);

        var pruned = await storage.PruneExpiredAsync(windowStart.AddDays(30), token);
        Assert.True(pruned >= 1, $"pruned={pruned}");

        var usage = await storage.GetUsageAsync(userId, "api_calls", window, 10, token);
        Assert.Equal(0, usage!.Used);
        Assert.Empty(usage.DistinctKeys);
    }

    [Fact]
    public async Task Consume_UserIdsContainingSeparator_DoNotShareIdempotencyRecords()
    {
        var maybeStorage = await CreateAsync();
        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
        var storage = maybeStorage!;

        var suffix = Guid.NewGuid().ToString("N");
        var scopedUser = $"acme:{suffix}";
        var plainUser = "acme";
        var resource = $"api_{suffix}";
        var now = DateTimeOffset.UtcNow;
        var window = PeriodWindow.ForDaily(now, TimeZoneInfo.Utc);
        var ttl = TimeSpan.FromHours(1);
        var token = TestContext.Current.CancellationToken;

        var first = await storage.TryConsumeAsync(new StorageConsumeSpec(
            scopedUser, resource, 1, window, 10, "order-1", null, ttl, now, "free"), token);
        Assert.True(first.Allowed);
        Assert.False(first.WasIdempotencyHit);

        // ("acme:<id>", "order-1") and ("acme", "<id>:order-1") used to map to the same record.
        var second = await storage.TryConsumeAsync(new StorageConsumeSpec(
            plainUser, resource, 1, window, 10, $"{suffix}:order-1", null, ttl, now, "free"), token);
        Assert.True(second.Allowed);
        Assert.False(second.WasIdempotencyHit);
        Assert.Equal(1, second.NewUsed);

        var secondUsage = await storage.GetUsageAsync(plainUser, resource, window, 10, token);
        Assert.Equal(1, secondUsage!.Used);

        var firstUsage = await storage.GetUsageAsync(scopedUser, resource, window, 10, token);
        Assert.Equal(1, firstUsage!.Used);
    }

    [Fact]
    public async Task TopUp_ReusingCreditKeyAfterItsTtl_RefreshesTheRecordInsteadOfGrantingAgain()
    {
        var maybeStorage = await CreateAsync();
        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
        var storage = maybeStorage!;

        var userId = NewUser();
        var now = DateTimeOffset.UtcNow;
        var ttl = TimeSpan.FromMilliseconds(500);
        var token = TestContext.Current.CancellationToken;

        await storage.TopUpForeverCreditsAsync(userId, "credits", 10, "topup_ttl", ttl, now, token);
        await storage.TopUpForeverCreditsAsync(userId, "credits", 10, "topup_ttl", ttl, now.AddSeconds(1), token);

        // Immediate retry after the refreshed grant must be treated as a replay.
        await storage.TopUpForeverCreditsAsync(userId, "credits", 10, "topup_ttl", ttl, now.AddSeconds(1), token);

        var usage = await storage.GetUsageAsync(userId, "credits", PeriodWindow.Forever, 0, token);
        Assert.Equal(20, usage!.Limit);
    }

    [Fact]
    public async Task RefundCredits_ReusingCreditKeyAfterItsTtl_RefreshesTheRecordInsteadOfDebitingAgain()
    {
        var maybeStorage = await CreateAsync();
        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
        var storage = maybeStorage!;

        var userId = NewUser();
        var now = DateTimeOffset.UtcNow;
        var ttl = TimeSpan.FromMilliseconds(500);
        var token = TestContext.Current.CancellationToken;

        await storage.TopUpForeverCreditsAsync(userId, "credits", 20, null, TimeSpan.FromHours(1), now, token);
        await storage.RefundForeverCreditsAsync(userId, "credits", 5, "refund_ttl", ttl, now, token);
        await storage.RefundForeverCreditsAsync(userId, "credits", 5, "refund_ttl", ttl, now.AddSeconds(1), token);

        await storage.RefundForeverCreditsAsync(userId, "credits", 5, "refund_ttl", ttl, now.AddSeconds(1), token);

        var usage = await storage.GetUsageAsync(userId, "credits", PeriodWindow.Forever, 0, token);
        Assert.Equal(10, usage!.Limit);
    }

    [Fact]
    public async Task SetUsage_WithoutDistinctKeys_KeepsTheStoredKeys()
    {
        var maybeStorage = await CreateAsync();
        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
        var storage = maybeStorage!;

        var userId = NewUser();
        var now = DateTimeOffset.UtcNow;
        var window = PeriodWindow.ForDaily(now, TimeZoneInfo.Utc);
        var token = TestContext.Current.CancellationToken;

        foreach (var distinct in new[] { "b", "a" })
        {
            await storage.TryConsumeAsync(new StorageConsumeSpec(
                userId, "api_calls", 1, window, 10, null, distinct, TimeSpan.FromHours(1), now, "free"), token);
        }

        await storage.SetUsageAsync(userId, "api_calls", window, used: 2, limit: 10, cancellationToken: token);

        var preserved = await storage.GetUsageAsync(userId, "api_calls", window, 10, token);
        Assert.Equal(2, preserved!.Used);
        Assert.Equal(["a", "b"], preserved.DistinctKeys);

        // Resetting the usage to zero still clears the dedupe set.
        await storage.SetUsageAsync(userId, "api_calls", window, used: 0, limit: 10, cancellationToken: token);

        var reset = await storage.GetUsageAsync(userId, "api_calls", window, 10, token);
        Assert.Empty(reset!.DistinctKeys);
    }

    [Fact]
    public async Task Merge_ReusingAKeyForADifferentUserPair_ThrowsInsteadOfReportingAlreadyMerged()
    {
        var maybeStorage = await CreateAsync();
        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
        var storage = maybeStorage!;

        var source = NewUser();
        var target = NewUser();
        var otherSource = NewUser();
        var otherTarget = NewUser();
        var now = DateTimeOffset.UtcNow;
        var daily = PeriodWindow.ForDaily(now, TimeZoneInfo.Utc);
        var token = TestContext.Current.CancellationToken;

        foreach (var (user, amount) in new[] { (source, 2), (otherSource, 3) })
        {
            await storage.TryConsumeAsync(new StorageConsumeSpec(
                user, "consultare", amount, daily, 10, null, null, TimeSpan.FromHours(1), now, "free"), token);
        }

        StorageMergeRequest Request(string from, string to, string key) => new(
            SourceUserId: from,
            TargetUserId: to,
            Items: [new MergeItem("consultare", PeriodType.Daily, daily, daily)],
            IdempotencyKey: key,
            SealSource: false,
            NowUtc: now);

        // The record is global, so the key must be unique per run against a shared database.
        var sharedKey = $"merge_shared_{Guid.NewGuid():N}";

        var merged = await storage.MergeUserAsync(Request(source, target, sharedKey), token);
        Assert.False(merged.AlreadyMerged);

        await Assert.ThrowsAsync<QuotaException>(() =>
            storage.MergeUserAsync(Request(otherSource, otherTarget, sharedKey), token));

        var untouched = await storage.GetUsageAsync(otherTarget, "consultare", daily, 10, token);
        Assert.Equal(0, untouched!.Used);
        Assert.False(await storage.IsUserSealedAsync(otherSource, now, token));

        // The same pair still replays.
        var replay = await storage.MergeUserAsync(Request(source, target, sharedKey), token);
        Assert.True(replay.AlreadyMerged);
    }

    [Fact]
    public async Task Merge_WithoutSeal_DoesNotReportADroppedPromotion()
    {
        var maybeStorage = await CreateAsync();
        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
        var storage = maybeStorage!;

        var source = NewUser();
        var target = NewUser();
        var now = DateTimeOffset.UtcNow;
        var daily = PeriodWindow.ForDaily(now, TimeZoneInfo.Utc);
        var token = TestContext.Current.CancellationToken;

        await storage.TryConsumeAsync(new StorageConsumeSpec(
            source, "consultare", 1, daily, 10, null, null, TimeSpan.FromHours(1), now, "free"), token);
        await storage.SetEntitlementAsync(new QuotaEntitlement(source, "free", now, now), token);
        await storage.SetPromotionAsync(
            source, new TierPromotion("pro", now, now.AddDays(2), Source: "test", Reason: "comp"), token);

        var merged = await storage.MergeUserAsync(
            new StorageMergeRequest(
                SourceUserId: source,
                TargetUserId: target,
                Items: [new MergeItem("consultare", PeriodType.Daily, daily, daily)],
                IdempotencyKey: $"merge:{source}",
                SealSource: false,
                NowUtc: now),
            token);

        Assert.Null(merged.DroppedPromotion);
        Assert.Equal("pro", (await storage.GetEntitlementAsync(source, token))!.Promotion?.Tier);
    }

    [Fact]
    public async Task Merge_ExpiredPromotion_IsNotReportedAsDropped()
    {
        var maybeStorage = await CreateAsync();
        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
        var storage = maybeStorage!;

        var source = NewUser();
        var target = NewUser();
        var now = DateTimeOffset.UtcNow;
        var daily = PeriodWindow.ForDaily(now, TimeZoneInfo.Utc);
        var token = TestContext.Current.CancellationToken;

        await storage.TryConsumeAsync(new StorageConsumeSpec(
            source, "consultare", 1, daily, 10, null, null, TimeSpan.FromHours(1), now, "free"), token);
        await storage.SetEntitlementAsync(new QuotaEntitlement(source, "free", now, now), token);
        await storage.SetPromotionAsync(
            source, new TierPromotion("pro", now.AddDays(-3), now.AddMinutes(-1), Source: "test", Reason: "old"), token);

        var merged = await storage.MergeUserAsync(
            new StorageMergeRequest(
                SourceUserId: source,
                TargetUserId: target,
                Items: [new MergeItem("consultare", PeriodType.Daily, daily, daily)],
                IdempotencyKey: $"merge:{source}",
                SealSource: true,
                NowUtc: now,
                SealExpiresAtUtc: now.AddDays(30)),
            token);

        Assert.Null(merged.DroppedPromotion);
    }

    [Fact]
    public async Task AuditEntriesWithEqualTimestamps_ReturnTheNewestRows()
    {
        var maybeStorage = await CreateAsync();
        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
        var storage = maybeStorage!;

        var userId = NewUser();
        var timestamp = new DateTimeOffset(2021, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var token = TestContext.Current.CancellationToken;

        for (var i = 0; i < 20; i++)
        {
            await storage.LogAuditEntryAsync(
                new AuditLogEntry(userId, "api_calls", "consume", i, timestamp, "tester", $"entry-{i}"), token);
        }

        var page = await storage.GetAuditLogsAsync(new AuditLogFilter(UserId: userId, Limit: 5), token);

        Assert.Equal(new[] { 19, 18, 17, 16, 15 }, page.Select(entry => entry.Amount).ToArray());
    }

    [Fact]
    public async Task Merge_PreservesTargetUsage_WhenAmountsExceedTheDistinctKeyCount()
    {
        var maybeStorage = await CreateAsync();
        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
        var storage = maybeStorage!;

        var source = NewUser();
        var target = NewUser();
        var now = DateTimeOffset.UtcNow;
        var daily = PeriodWindow.ForDaily(now, TimeZoneInfo.Utc);
        var token = TestContext.Current.CancellationToken;

        // Five units charged under one key: the key count is not the used-unit count.
        await storage.TryConsumeAsync(new StorageConsumeSpec(
            target, "consultare", 5, daily, 100, null, "doc-1", TimeSpan.FromHours(1), now, "free"), token);
        await storage.TryConsumeAsync(new StorageConsumeSpec(
            source, "consultare", 1, daily, 100, null, "doc-2", TimeSpan.FromHours(1), now, "free"), token);

        var merged = await storage.MergeUserAsync(
            new StorageMergeRequest(
                SourceUserId: source,
                TargetUserId: target,
                Items: [new MergeItem("consultare", PeriodType.Daily, daily, daily)],
                IdempotencyKey: $"merge:{source}",
                SealSource: false,
                NowUtc: now),
            token);

        Assert.Equal(6, (await storage.GetUsageAsync(target, "consultare", daily, 100, token))!.Used);
        Assert.Equal(1, merged.TransferredUsage["consultare:Daily"]);
        Assert.Equal(["doc-1", "doc-2"], (await storage.GetUsageAsync(target, "consultare", daily, 100, token))!.DistinctKeys);
    }

    private static string NewUser() => $"test_{Guid.NewGuid():N}";

    private static async Task<PostgresQuotaStorage?> CreateAsync()
    {
        var dataSource = await SharedDataSource.Value;
        return dataSource is null
            ? null
            : await PostgresQuotaStorage.CreateAsync(dataSource);
    }

    private static async Task<NpgsqlDataSource?> ConnectAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("NETQUOTA_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        // A configured-but-unreachable server must fail loudly instead of silently skipping.
        var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        await command.ExecuteScalarAsync();
        return dataSource;
    }
}
