using NetQuota;

namespace NetQuota.Tests;

public class QuotaManagerTests
{
    [Fact]
    public async Task DistinctKey_DeduplicatesWithinPeriod_AndPeekAllowsReloadAtCap()
    {
        var now = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
        var storage = new InMemoryQuotaStorage(() => now);
        var config = new QuotaConfig
        {
            DefaultTier = "free",
            Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["free"] = new TierConfig
                {
                    DailyQuotas = new Dictionary<string, int> { ["consultare"] = 2 }
                }
            }
        };
        var manager = new QuotaManager(storage, config);

        // First distinct CUI consumes 1
        var first = await manager.ConsumeWithResultAsync(
            "user_1",
            "consultare",
            1,
            PeriodType.Daily,
            new ConsumeOptions(DistinctKey: "cui:35703208"));
        Assert.True(first.Allowed);
        Assert.True(first.ConsumedNewUnit);
        Assert.Equal(1, first.NewUsed);

        // Reloading same CUI does not consume extra quota
        var reload = await manager.ConsumeWithResultAsync(
            "user_1",
            "consultare",
            1,
            PeriodType.Daily,
            new ConsumeOptions(DistinctKey: "cui:35703208"));
        Assert.True(reload.Allowed);
        Assert.False(reload.ConsumedNewUnit);
        Assert.Equal(1, reload.NewUsed);

        // Second distinct CUI hits cap (2/2)
        var second = await manager.ConsumeWithResultAsync(
            "user_1",
            "consultare",
            1,
            PeriodType.Daily,
            new ConsumeOptions(DistinctKey: "cui:38183029"));
        Assert.True(second.Allowed);
        Assert.True(second.ConsumedNewUnit);
        Assert.Equal(2, second.NewUsed);
        Assert.Equal(0, second.Remaining);

        // Peek on already-seen CUI at cap is still allowed
        var peekSeen = await manager.PeekAsync("user_1", "consultare", PeriodType.Daily, "cui:35703208");
        Assert.True(peekSeen.Allowed);
        Assert.True(peekSeen.AlreadySeenDistinctKey);

        // Peek on a 3rd unseen CUI at cap is denied
        var peekUnseen = await manager.PeekAsync("user_1", "consultare", PeriodType.Daily, "cui:14399840");
        Assert.False(peekUnseen.Allowed);
        Assert.False(peekUnseen.AlreadySeenDistinctKey);
    }

    [Fact]
    public async Task ForeverCredits_CascadeAfterMonthly_AndMeterQuotaDropsSpentBonus()
    {
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var storage = new InMemoryQuotaStorage(() => now);
        var config = new QuotaConfig
        {
            DefaultTier = "pro",
            Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["pro"] = new TierConfig
                {
                    MonthlyQuotas = new Dictionary<string, int> { ["api_calls"] = 10 },
                    InitialForeverCredits = new Dictionary<string, int> { ["api_calls"] = 5 },
                    ConsumptionOrder = [PeriodType.Monthly, PeriodType.Forever]
                }
            }
        };
        var manager = new QuotaManager(storage, config);

        await manager.SetEntitlementAsync(new QuotaEntitlement("u1", "pro", now, now));
        await manager.TopUpLimitAsync("u1", "api_calls", 5, idempotencyKey: "topup_1");
        // Idempotent retry does not double-grant
        await manager.TopUpLimitAsync("u1", "api_calls", 5, idempotencyKey: "topup_1");

        // Total forever credits = 5 (initial) + 5 (topup) = 10; monthly = 10
        var initialMeter = await manager.GetMeterQuotaAsync("u1", "api_calls");
        Assert.Equal(20, initialMeter.Limit);
        Assert.Equal(20, initialMeter.Remaining);

        // Consume all 10 monthly
        var c1 = await manager.ConsumeWithResultAsync("u1", "api_calls", 10, PeriodType.Auto);
        Assert.True(c1.Allowed);
        Assert.Equal(PeriodType.Monthly, c1.Period);

        // Next 4 cascade automatically into Forever credits
        var c2 = await manager.ConsumeWithResultAsync("u1", "api_calls", 4, PeriodType.Auto);
        Assert.True(c2.Allowed);
        Assert.Equal(PeriodType.Forever, c2.Period);

        // GetMeterQuota: recurring Limit (10) + remaining forever (6) = 16, used = 10, remaining = 6
        var meter = await manager.GetMeterQuotaAsync("u1", "api_calls");
        Assert.Equal(16, meter.Limit);
        Assert.Equal(10, meter.Used);
        Assert.Equal(6, meter.Remaining);

        // GetEffectiveQuota: ledger keeps full granted Limit (10 + 10 = 20) and total used (14)
        var effective = await manager.GetEffectiveQuotaAsync("u1", "api_calls");
        Assert.Equal(20, effective.Limit);
        Assert.Equal(14, effective.Used);
        Assert.Equal(6, effective.Remaining);
    }

    [Fact]
    public async Task Promotions_OverrideBaseTierUntilExpiry_AndSurviveEntitlementSync()
    {
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var storage = new InMemoryQuotaStorage(() => now);
        var config = CreateConfig();
        var manager = new QuotaManager(storage, config);

        await manager.SetEntitlementAsync(new QuotaEntitlement("u_promo", "FREE", now, now));
        Assert.Equal("FREE", await manager.GetEffectiveTierAsync("u_promo"));

        await manager.GrantPromotionAsync(new PromotionRequest(
            UserId: "u_promo",
            Tier: "PRO",
            Duration: TimeSpan.FromDays(7),
            Reason: "comp",
            IdempotencyKey: "promo_1"));

        Assert.Equal("PRO", await manager.GetEffectiveTierAsync("u_promo"));

        // Webhook syncing base tier without promotion does not erase active promotion
        await manager.SetEntitlementAsync(new QuotaEntitlement("u_promo", "FREE", now, now));
        Assert.Equal("PRO", await manager.GetEffectiveTierAsync("u_promo"));

        // Advance clock past promotion expiry -> auto-reverts to FREE
        now = now.AddDays(8);
        Assert.Equal("FREE", await manager.GetEffectiveTierAsync("u_promo"));
    }

    [Fact]
    public async Task MergeUser_CombinesDistinctKeysIdempotently_AndTieredStorageSyncs()
    {
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var hot = new InMemoryQuotaStorage(() => now);
        var cold = new InMemoryQuotaStorage(() => now);
        var tiered = new TieredQuotaStorage(new TieredQuotaStorageConfig(hot, cold));
        var manager = new QuotaManager(tiered, CreateConfig());

        await manager.ConsumeWithResultAsync(
            "anon:1",
            ConsultareResource,
            1,
            PeriodType.Daily,
            new ConsumeOptions(DistinctKey: "cui:35703208"));
        await manager.ConsumeWithResultAsync(
            "account:acc_1",
            ConsultareResource,
            1,
            PeriodType.Daily,
            new ConsumeOptions(DistinctKey: "cui:38183029"),
            explicitTier: "FREE");

        var merge = await manager.MergeUserAsync(new MergeUserRequest(
            SourceUserId: "anon:1",
            TargetUserId: "account:acc_1",
            Resources: [ConsultareResource],
            Periods: [PeriodType.Daily],
            IdempotencyKey: "merge_day_1",
            SealSource: false));

        Assert.False(merge.AlreadyMerged);
        var peek1 = await manager.PeekAsync("account:acc_1", ConsultareResource, PeriodType.Daily, "cui:35703208", "FREE");
        var peek2 = await manager.PeekAsync("account:acc_1", ConsultareResource, PeriodType.Daily, "cui:38183029", "FREE");
        Assert.True(peek1.AlreadySeenDistinctKey);
        Assert.True(peek2.AlreadySeenDistinctKey);
        Assert.Equal(2, peek1.Used);
    }

    [Fact]
    public async Task GoQuotaEdgeCases_SnapBackAnniversary_Proration_IdempotentReplay_AndTimezone()
    {
        // 1. Anniversary snap-back (Jan 31 -> Feb 28 -> Mar 31, no drift to Mar 28)
        var jan31 = new DateTimeOffset(2026, 1, 31, 14, 0, 0, TimeSpan.Zero);
        var feb15 = new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero);
        var mar15 = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);

        var cycle1 = PeriodWindow.ForMonthly(feb15, jan31, TimeZoneInfo.Utc);
        Assert.Equal(new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero), cycle1.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero), cycle1.EndUtc);

        var cycle2 = PeriodWindow.ForMonthly(mar15, jan31, TimeZoneInfo.Utc);
        Assert.Equal(new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero), cycle2.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 3, 31, 0, 0, 0, TimeSpan.Zero), cycle2.EndUtc);

        // 2. ConsumeResult.Replayed + RefundFromConsume + RefundResult.Replayed
        var now = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var storage = new InMemoryQuotaStorage(() => now);
        var config = new QuotaConfig
        {
            DefaultTier = "free",
            Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["free"] = new TierConfig
                {
                    MonthlyQuotas = new Dictionary<string, int> { ["api_calls"] = 100 }
                },
                ["pro"] = new TierConfig
                {
                    MonthlyQuotas = new Dictionary<string, int> { ["api_calls"] = 1000 }
                }
            }
        };
        var manager = new QuotaManager(storage, config);

        // The monthly anchor must exist before usage is recorded: an entitlement created mid-cycle
        // starts a new anniversary window, which is the contract QM-01 fixes.
        await manager.SetEntitlementAsync(
            new QuotaEntitlement("u_edge", "free", now, now, Timezone: "Europe/Bucharest"));

        var firstConsume = await manager.ConsumeWithResultAsync(
            "u_edge",
            "api_calls",
            20,
            PeriodType.Auto,
            new ConsumeOptions(IdempotencyKey: "req_1"));
        Assert.False(firstConsume.Replayed);
        Assert.Equal(PeriodType.Monthly, firstConsume.Period);

        var replayConsume = await manager.ConsumeWithResultAsync(
            "u_edge",
            "api_calls",
            20,
            PeriodType.Auto,
            new ConsumeOptions(IdempotencyKey: "req_1"));
        Assert.True(replayConsume.Replayed);
        Assert.Equal(20, replayConsume.NewUsed);

        var firstRefund = await manager.RefundFromConsumeAsync(new RefundFromConsumeRequest(
            UserId: "u_edge",
            Resource: "api_calls",
            Amount: 10,
            ConsumeResult: firstConsume,
            ConsumeIdempotencyKey: "req_1"));
        Assert.False(firstRefund.Replayed);
        Assert.Equal(PeriodType.Monthly, firstRefund.Period);

        var replayRefund = await manager.RefundFromConsumeAsync(new RefundFromConsumeRequest(
            UserId: "u_edge",
            Resource: "api_calls",
            Amount: 10,
            ConsumeResult: firstConsume,
            ConsumeIdempotencyKey: "req_1"));
        Assert.True(replayRefund.Replayed);
        Assert.Equal(0, replayRefund.Amount);

        // 3. ApplyTierChange proration: used (10) + Round(newLimit (1000) * remainingFrac (0.5)) = 510
        await manager.SetEntitlementAsync(new QuotaEntitlement("u_edge", "free", now, now, Timezone: "Europe/Bucharest"));
        now = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero); // exactly half of 31-day Oct
        await manager.ApplyTierChangeAsync("u_edge", "pro", "api_calls");
        var entAfterUpgrade = await manager.GetEntitlementAsync("u_edge");
        Assert.Equal("Europe/Bucharest", entAfterUpgrade?.Timezone);
        var meterAfterUpgrade = await manager.GetMeterQuotaAsync("u_edge", "api_calls");
        Assert.Equal(510, meterAfterUpgrade.Limit);
        Assert.Equal(10, meterAfterUpgrade.Used);
    }

    [Fact]
    public async Task GoQuotaCommitRegressions_PromotionContract_AutoReplay_AutoRefund_Lru_AndCircuitBreaker()
    {
        var now = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
        var storage = new InMemoryQuotaStorage(() => now);
        var cbStates = new List<CircuitBreakerState>();
        var config = new QuotaConfig
        {
            DefaultTier = "free",
            CacheConfig = new CacheConfig(
                Enabled: true,
                EntitlementTtl: TimeSpan.FromMinutes(5),
                UsageTtl: TimeSpan.FromMinutes(1),
                MaxEntitlements: 2,
                MaxUsage: 4),
            CircuitBreakerConfig = new CircuitBreakerConfig(
                Enabled: true,
                FailureThreshold: 3,
                ResetTimeout: TimeSpan.FromSeconds(10),
                OnStateChange: state => cbStates.Add(state)),
            Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["free"] = new TierConfig
                {
                    DailyQuotas = new Dictionary<string, int> { ["req"] = 2 },
                    MonthlyQuotas = new Dictionary<string, int> { ["req"] = 10 },
                    InitialForeverCredits = new Dictionary<string, int> { ["req"] = 5, ["secondary"] = 3 },
                    ConsumptionOrder = [PeriodType.Daily, PeriodType.Monthly, PeriodType.Forever]
                },
                ["pro"] = new TierConfig
                {
                    DailyQuotas = new Dictionary<string, int> { ["req"] = 20 },
                    MonthlyQuotas = new Dictionary<string, int> { ["req"] = 100 },
                    ConsumptionOrder = [PeriodType.Daily, PeriodType.Monthly, PeriodType.Forever]
                }
            }
        };

        var manager = new QuotaManager(storage, config);
        Assert.True(manager.PromotionsSupported);

        // 1. BUG-1 (2dd730a): InitialForeverCredits applied per resource
        var baseUpdatedAt = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
        await manager.SetEntitlementAsync(new QuotaEntitlement("u1", "free", baseUpdatedAt, baseUpdatedAt));
        var foreverReq = await manager.GetQuotaAsync("u1", "req", PeriodType.Forever);
        var foreverSec = await manager.GetQuotaAsync("u1", "secondary", PeriodType.Forever);
        Assert.Equal(5, foreverReq.Limit);
        Assert.Equal(3, foreverSec.Limit);

        // 2. Promotion contract (599b63f): GrantPromotion & RevokePromotion preserve base UpdatedAtUtc
        await manager.GrantPromotionAsync(new PromotionRequest("u1", "pro", Duration: TimeSpan.FromDays(3), IdempotencyKey: "p1"));
        var entWithPromo = await manager.GetEntitlementAsync("u1");
        Assert.NotNull(entWithPromo);
        Assert.Equal(baseUpdatedAt, entWithPromo!.UpdatedAtUtc);
        Assert.Equal("pro", await manager.GetEffectiveTierAsync("u1"));

        await manager.RevokePromotionAsync("u1");
        var entAfterRevoke = await manager.GetEntitlementAsync("u1");
        Assert.Equal(baseUpdatedAt, entAfterRevoke!.UpdatedAtUtc);
        Assert.Null(entAfterRevoke.Promotion);

        // 3. PeriodType.Auto cascade into Monthly + idempotent replay (2bbff1c & 9828c99) + PeriodType.Auto refund (12d666f)
        // Consume 2 from Daily (exhausting Daily)
        var d1 = await manager.ConsumeWithResultAsync("u1", "req", 2, PeriodType.Auto, new ConsumeOptions(IdempotencyKey: "k_daily"));
        Assert.Equal(PeriodType.Daily, d1.Period);

        // Next Auto consumption overflows into Monthly
        var m1 = await manager.ConsumeWithResultAsync("u1", "req", 3, PeriodType.Auto, new ConsumeOptions(IdempotencyKey: "k_monthly"));
        Assert.True(m1.Allowed);
        Assert.Equal(PeriodType.Monthly, m1.Period);
        Assert.False(m1.Replayed);

        // Replaying k_monthly on PeriodType.Auto must report PeriodType.Monthly and NOT pollute Daily usage
        var m1Replay = await manager.ConsumeWithResultAsync("u1", "req", 3, PeriodType.Auto, new ConsumeOptions(IdempotencyKey: "k_monthly"));
        Assert.True(m1Replay.Replayed);
        Assert.Equal(PeriodType.Monthly, m1Replay.Period);
        Assert.Equal(3, m1Replay.NewUsed);
        var dailyAfterReplay = await manager.GetQuotaAsync("u1", "req", PeriodType.Daily);
        Assert.Equal(2, dailyAfterReplay.Used);

        // Refund with PeriodType.Auto resolves PeriodType.Monthly via RelatedIdempotencyKey (12d666f)
        var autoRefund = await manager.RefundWithResultAsync(new RefundRequest(
            UserId: "u1",
            Resource: "req",
            Amount: 2,
            Period: PeriodType.Auto,
            IdempotencyKey: "k_monthly_refund",
            RelatedIdempotencyKey: "k_monthly"));
        Assert.False(autoRefund.Replayed);
        Assert.Equal(PeriodType.Monthly, autoRefund.Period);
        Assert.Equal(2, autoRefund.Amount);
        var monthlyAfterRefund = await manager.GetQuotaAsync("u1", "req", PeriodType.Monthly);
        Assert.Equal(1, monthlyAfterRefund.Used);

        // 4. MergeUser surfaces DroppedPromotion (599b63f) and rejects SameUserMerge (8a60bcb)
        await manager.GrantPromotionAsync(new PromotionRequest("u1", "pro", Duration: TimeSpan.FromDays(2)));
        var mergeRes = await manager.MergeUserAsync(new MergeUserRequest(
            SourceUserId: "u1",
            TargetUserId: "u2",
            Resources: ["req"],
            Periods: [PeriodType.Daily, PeriodType.Forever],
            IdempotencyKey: "m_u1_u2",
            SealSource: true));
        Assert.NotNull(mergeRes.DroppedPromotion);
        Assert.Equal("pro", mergeRes.DroppedPromotion!.Tier);
        Assert.Equal(5, mergeRes.TransferredForeverCredits["req"]);
        var foreverAfterMerge = await manager.GetQuotaAsync("u2", "req", PeriodType.Forever);
        Assert.Equal(5, foreverAfterMerge.Limit);
        await Assert.ThrowsAsync<UserSealedException>(() =>
            manager.ConsumeWithResultAsync("u1", "req", 1, PeriodType.Daily));
        await Assert.ThrowsAsync<SameUserMergeException>(() =>
            manager.MergeUserAsync(new MergeUserRequest("u2", "u2", ["req"], [PeriodType.Daily], "m_same")));

        // Auto denial must report the first pool the caller hit (Daily), not the trailing Forever candidate with limit 0.
        var autoDenied = await manager.ConsumeWithResultAsync("u2", "req", 11, PeriodType.Auto);
        Assert.False(autoDenied.Allowed);
        Assert.Equal(PeriodType.Daily, autoDenied.Period);
        Assert.Equal(2, autoDenied.Limit);

        // 5. TryConsumeAsync & GetUsageAfterConsumeAsync parity
        // u2 received 2/2 Daily usage from u1 merge, so Daily TryConsume returns Success = false without throwing
        var tryDailyExhausted = await manager.TryConsumeAsync("u2", "req", 1, PeriodType.Daily);
        Assert.False(tryDailyExhausted.Success);
        Assert.Equal(0, tryDailyExhausted.Remaining);
        Assert.Equal(2, tryDailyExhausted.NewUsed);

        var tryMonthlyOk = await manager.TryConsumeAsync("u2", "req", 1, PeriodType.Monthly);
        Assert.True(tryMonthlyOk.Success);
        var usageAfter = await manager.GetUsageAfterConsumeAsync("u2", "req", 1, PeriodType.Monthly);
        Assert.Equal(2, usageAfter.Used);
    }

    [Fact]
    public async Task CircuitBreaker_OpensAfterThreshold_AndClosesAfterResetTimeout()
    {
        var now = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
        var inner = new InMemoryQuotaStorage(() => now);
        var flaky = new ToggleFailingStorage(inner);
        var transitions = new List<CircuitBreakerState>();

        var config = new QuotaConfig
        {
            DefaultTier = "free",
            CircuitBreakerConfig = new CircuitBreakerConfig(
                Enabled: true,
                FailureThreshold: 2,
                ResetTimeout: TimeSpan.FromMilliseconds(50),
                OnStateChange: state => transitions.Add(state)),
            Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["free"] = new TierConfig
                {
                    DailyQuotas = new Dictionary<string, int> { ["req"] = 5 }
                }
            }
        };

        var manager = new QuotaManager(flaky, config);
        var breaker = Assert.IsType<CircuitBreakerQuotaStorage>(manager.Storage);
        Assert.Equal(CircuitBreakerState.Closed, breaker.State);

        flaky.Failing = true;
        await Assert.ThrowsAsync<QuotaStorageUnavailableException>(() => manager.GetEntitlementAsync("u_cb"));
        await Assert.ThrowsAsync<QuotaStorageUnavailableException>(() => manager.GetEntitlementAsync("u_cb"));
        Assert.Equal(CircuitBreakerState.Open, breaker.State);

        // While open, calls fail fast without touching the inner storage.
        await Assert.ThrowsAnyAsync<QuotaException>(() => manager.GetEntitlementAsync("u_cb"));
        Assert.Contains(CircuitBreakerState.Open, transitions);

        // After the reset timeout the breaker goes half-open, then closes on success.
        flaky.Failing = false;
        await Task.Delay(80);
        var ent = await manager.GetEntitlementAsync("u_cb");
        Assert.Null(ent);
        Assert.Equal(CircuitBreakerState.Closed, breaker.State);
        Assert.Equal(
            [CircuitBreakerState.Open, CircuitBreakerState.HalfOpen, CircuitBreakerState.Closed],
            transitions);
    }

    [Fact]
    public void LruQuotaCache_RemoveGroup_EvictsOnlyThatGroup_AndCapacityEvictionDropsGroupMembership()
    {
        var now = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
        var cache = new LruQuotaCache<string>(2);

        cache.Set("u1|a", "v1", now, TimeSpan.FromMinutes(1), group: "u1");
        cache.Set("u2|b", "v2", now, TimeSpan.FromMinutes(1), group: "u2");
        cache.RemoveGroup("u1");

        Assert.False(cache.TryGet("u1|a", now, out _, out _));
        Assert.True(cache.TryGet("u2|b", now, out _, out _));

        // Capacity eviction must drop the evicted key from its group so RemoveGroup leaves survivors intact.
        cache.Set("u1|a", "v1", now, TimeSpan.FromMinutes(1), group: "u1");
        cache.Set("u3|c", "v3", now, TimeSpan.FromMinutes(1), group: "u3");
        cache.RemoveGroup("u2");

        Assert.True(cache.TryGet("u1|a", now, out _, out _));
        Assert.True(cache.TryGet("u3|c", now, out _, out _));
        Assert.Equal(2, cache.Count);

        cache.RemoveGroup("u1");
        Assert.False(cache.TryGet("u1|a", now, out _, out _));
        Assert.True(cache.TryGet("u3|c", now, out _, out _));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task MergeUser_ThroughCircuitBreaker_UsesLegacyMergeWhenInnerStorageIsNotIUserMerger()
    {
        var now = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
        var storage = new ToggleFailingStorage(new InMemoryQuotaStorage(() => now));
        var config = new QuotaConfig
        {
            DefaultTier = "free",
            CircuitBreakerConfig = new CircuitBreakerConfig(Enabled: true, FailureThreshold: 5),
            Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["free"] = new TierConfig
                {
                    DailyQuotas = new Dictionary<string, int> { ["req"] = 5 }
                }
            }
        };

        var manager = new QuotaManager(storage, config);
        await manager.ConsumeWithResultAsync(
            "src",
            "req",
            1,
            PeriodType.Daily,
            new ConsumeOptions(DistinctKey: "a"));

        // ToggleFailingStorage implements IQuotaStorage only, so the breaker must not advertise IUserMerger.
        var merge = await manager.MergeUserAsync(
            new MergeUserRequest("src", "dst", ["req"], [PeriodType.Daily], "merge_legacy"));

        Assert.False(merge.AlreadyMerged);
        var targetQuota = await manager.GetQuotaAsync("dst", "req", PeriodType.Daily);
        Assert.Equal(1, targetQuota.Used);
    }
    [Fact]
    public async Task TieredStorage_AsyncSync_FlushesColdWritesOnDispose()
    {
        var now = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
        var hot = new InMemoryQuotaStorage(() => now);
        var cold = new InMemoryQuotaStorage(() => now);
        var tiered = new TieredQuotaStorage(new TieredQuotaStorageConfig(hot, cold, AsyncUsageSync: true));
        var window = PeriodWindow.ForDaily(now, TimeZoneInfo.Utc);
        var ct = TestContext.Current.CancellationToken;

        var outcome = await tiered.TryConsumeAsync(
            new StorageConsumeSpec(
                UserId: "u_async",
                Resource: "req",
                Amount: 1,
                Window: window,
                ConfiguredLimit: 10,
                IdempotencyKey: null,
                DistinctKey: null,
                IdempotencyTtl: TimeSpan.FromHours(1),
                NowUtc: now),
            ct);
        Assert.True(outcome.Allowed);

        await tiered.DisposeAsync();

        var coldUsage = await cold.GetUsageAsync("u_async", "req", window, defaultLimit: 10, ct);
        Assert.NotNull(coldUsage);
        Assert.Equal(1, coldUsage!.Used);
    }

    [Fact]
    public async Task UnlimitedTier_OverridesStoredProratedLimit()
    {
        var now = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
        var storage = new InMemoryQuotaStorage(() => now);
        var config = new QuotaConfig
        {
            DefaultTier = "pro",
            Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["pro"] = new TierConfig
                {
                    MonthlyQuotas = new Dictionary<string, int> { ["api_calls"] = -1 }
                }
            }
        };
        var manager = new QuotaManager(storage, config);

        var window = PeriodWindow.ForMonthly(now, null, TimeZoneInfo.Utc);
        await storage.ApplyTierChangeAsync(new TierChangeRequest(
            "u_unlimited", "api_calls", "pro", "pro", window,
            OldLimit: 42, NewLimit: 42, CurrentUsed: 0));

        var outcome = await manager.ConsumeWithResultAsync("u_unlimited", "api_calls", 1000, PeriodType.Monthly);
        Assert.True(outcome.Allowed);
        Assert.Equal(-1, outcome.Limit);
        Assert.Equal(int.MaxValue, outcome.Remaining);
    }

    [Fact]
    public async Task AnonymousMonthlyQuota_UsesCalendarMonth_AndDoesNotResetTheNextDay()
    {
        var now = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var storage = new InMemoryQuotaStorage(() => now);
        var config = new QuotaConfig
        {
            DefaultTier = "free",
            Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["free"] = new TierConfig
                {
                    MonthlyQuotas = new Dictionary<string, int> { ["api"] = 10 }
                }
            }
        };
        var manager = new QuotaManager(storage, config);

        var first = await manager.ConsumeWithResultAsync("anon", "api", 10, PeriodType.Monthly);
        Assert.True(first.Allowed);
        Assert.Equal(10, first.NewUsed);

        var second = await manager.ConsumeWithResultAsync("anon", "api", 1, PeriodType.Monthly);
        Assert.False(second.Allowed);

        // Before the fix the window key was anchored to "today", so the next day granted a fresh quota.
        now = now.AddDays(1);
        var nextDay = await manager.ConsumeWithResultAsync("anon", "api", 1, PeriodType.Monthly);
        Assert.False(nextDay.Allowed);
        Assert.Equal(10, nextDay.NewUsed);

        // The calendar month rolls over on the 1st.
        now = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var nextMonth = await manager.ConsumeWithResultAsync("anon", "api", 1, PeriodType.Monthly);
        Assert.True(nextMonth.Allowed);
        Assert.Equal(1, nextMonth.NewUsed);
    }

    [Fact]
    public async Task ExplicitTier_KeepsEntitlementAnchor_AndRejectsUnknownTier()
    {
        var anchor = new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 15, 12, 0, 0, TimeSpan.Zero);
        var storage = new InMemoryQuotaStorage(() => now);
        var config = new QuotaConfig
        {
            DefaultTier = "free",
            Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["free"] = new TierConfig
                {
                    MonthlyQuotas = new Dictionary<string, int> { ["api"] = 100 }
                },
                ["pro"] = new TierConfig
                {
                    MonthlyQuotas = new Dictionary<string, int> { ["api"] = 1000 }
                }
            }
        };
        var manager = new QuotaManager(storage, config);
        await manager.SetEntitlementAsync(new QuotaEntitlement("u1", "free", anchor, anchor));

        // Consume inside the anniversary window 2026-01-31 -> 2026-02-28.
        var consumed = await manager.ConsumeWithResultAsync("u1", "api", 5, PeriodType.Monthly);
        Assert.True(consumed.Allowed);

        // Before the fix the explicit tier discarded the entitlement and read a different window (Used = 0).
        var quota = await manager.GetQuotaAsync("u1", "api", PeriodType.Monthly, explicitTier: "pro");
        Assert.Equal(5, quota.Used);
        Assert.Equal(1000, quota.Limit);

        var peek = await manager.PeekAsync("u1", "api", PeriodType.Monthly, explicitTier: "pro");
        Assert.Equal(5, peek.Used);
        Assert.Equal(1000, peek.Limit);

        await Assert.ThrowsAsync<QuotaConfigValidationException>(
            () => manager.GetQuotaAsync("u1", "api", PeriodType.Monthly, explicitTier: "does-not-exist"));
    }

    [Fact]
    public async Task MergeUser_NonForeverUsage_NeverReducesTargetUsed()
    {
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var storage = new InMemoryQuotaStorage(() => now);
        var config = new QuotaConfig
        {
            DefaultTier = "free",
            Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["free"] = new TierConfig
                {
                    DailyQuotas = new Dictionary<string, int> { ["docs"] = 100 }
                }
            }
        };
        var manager = new QuotaManager(storage, config);

        await manager.ConsumeWithResultAsync(
            "target", "docs", 5, PeriodType.Daily, new ConsumeOptions(DistinctKey: "doc-1"));
        await manager.ConsumeWithResultAsync(
            "source", "docs", 1, PeriodType.Daily, new ConsumeOptions(DistinctKey: "doc-2"));

        var merge = await manager.MergeUserAsync(new MergeUserRequest(
            SourceUserId: "source",
            TargetUserId: "target",
            Resources: ["docs"],
            Periods: [PeriodType.Daily],
            IdempotencyKey: "merge_docs"));

        var target = await manager.GetQuotaAsync("target", "docs", PeriodType.Daily);
        Assert.Equal(6, target.Used);
        Assert.Equal(1, merge.TransferredUsage["docs:Daily"]);
    }

    [Fact]
    public async Task DrainRemaining_WithZeroUsed_FreezesTheQuota()
    {
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var storage = new InMemoryQuotaStorage(() => now);
        var config = new QuotaConfig
        {
            DefaultTier = "free",
            Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["free"] = new TierConfig
                {
                    DailyQuotas = new Dictionary<string, int> { ["api"] = 10 }
                }
            }
        };
        var manager = new QuotaManager(storage, config);

        await manager.DrainRemainingAsync("u1", "api", PeriodType.Daily);

        var quota = await manager.GetQuotaAsync("u1", "api", PeriodType.Daily);
        Assert.Equal(0, quota.Limit);
        Assert.Equal(0, quota.Remaining);

        // Before the fix a zero drain wrote Limit = 0, which every read treated as "use the tier default".
        var denied = await manager.ConsumeWithResultAsync("u1", "api", 1, PeriodType.Daily);
        Assert.False(denied.Allowed);

        // An explicit admin reset lifts the drain marker.
        await manager.ResetUsageAsync("u1", "api", PeriodType.Daily);
        var afterReset = await manager.ConsumeWithResultAsync("u1", "api", 1, PeriodType.Daily);
        Assert.True(afterReset.Allowed);
    }

    private sealed class ToggleFailingStorage(InMemoryQuotaStorage inner) : IQuotaStorage
    {
        public bool Failing { get; set; }

        private void Guard()
        {
            if (Failing)
            {
                throw new InvalidOperationException("simulated storage outage");
            }
        }

        public Task<QuotaEntitlement?> GetEntitlementAsync(string userId, CancellationToken cancellationToken = default)
        {
            Guard();
            return inner.GetEntitlementAsync(userId, cancellationToken);
        }

        public Task SetEntitlementAsync(QuotaEntitlement entitlement, CancellationToken cancellationToken = default)
        {
            Guard();
            return inner.SetEntitlementAsync(entitlement, cancellationToken);
        }

        public Task<QuotaUsage?> GetUsageAsync(
            string userId,
            string resource,
            PeriodWindow window,
            int defaultLimit,
            CancellationToken cancellationToken = default)
        {
            Guard();
            return inner.GetUsageAsync(userId, resource, window, defaultLimit, cancellationToken);
        }

        public Task<StorageConsumeOutcome> TryConsumeAsync(
            StorageConsumeSpec spec,
            CancellationToken cancellationToken = default)
        {
            Guard();
            return inner.TryConsumeAsync(spec, cancellationToken);
        }

        public Task ApplyTierChangeAsync(TierChangeRequest request, CancellationToken cancellationToken = default)
        {
            Guard();
            return inner.ApplyTierChangeAsync(request, cancellationToken);
        }

        public Task SetUsageAsync(
            string userId,
            string resource,
            PeriodWindow window,
            int used,
            int limit,
            IReadOnlyList<string>? distinctKeys = null,
            CancellationToken cancellationToken = default)
        {
            Guard();
            return inner.SetUsageAsync(userId, resource, window, used, limit, distinctKeys, cancellationToken);
        }

        public Task<StorageRefundOutcome> RefundAsync(
            RefundRequest request,
            PeriodWindow window,
            int defaultLimit,
            TimeSpan idempotencyTtl,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            Guard();
            return inner.RefundAsync(request, window, defaultLimit, idempotencyTtl, nowUtc, cancellationToken);
        }

        public Task<ConsumptionRecord?> GetConsumptionRecordAsync(
            string scopedIdempotencyKey,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            Guard();
            return inner.GetConsumptionRecordAsync(scopedIdempotencyKey, nowUtc, cancellationToken);
        }

        public Task<RefundRecord?> GetRefundRecordAsync(
            string scopedIdempotencyKey,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            Guard();
            return inner.GetRefundRecordAsync(scopedIdempotencyKey, nowUtc, cancellationToken);
        }

        public Task TopUpForeverCreditsAsync(
            string userId,
            string resource,
            int amount,
            string? idempotencyKey,
            TimeSpan idempotencyTtl,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            Guard();
            return inner.TopUpForeverCreditsAsync(userId, resource, amount, idempotencyKey, idempotencyTtl, nowUtc, cancellationToken);
        }

        public Task RefundForeverCreditsAsync(
            string userId,
            string resource,
            int amount,
            string? idempotencyKey,
            TimeSpan idempotencyTtl,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            Guard();
            return inner.RefundForeverCreditsAsync(userId, resource, amount, idempotencyKey, idempotencyTtl, nowUtc, cancellationToken);
        }

        public Task DrainRemainingAsync(
            string userId,
            string resource,
            PeriodWindow window,
            int defaultLimit,
            CancellationToken cancellationToken = default)
        {
            Guard();
            return inner.DrainRemainingAsync(userId, resource, window, defaultLimit, cancellationToken);
        }

        public Task<MergeUserResult> MergeUserAsync(
            MergeUserRequest request,
            Func<PeriodType, PeriodWindow> windowResolver,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            Guard();
            return inner.MergeUserAsync(request, windowResolver, nowUtc, cancellationToken);
        }

        public Task<bool> IsUserSealedAsync(string userId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        {
            Guard();
            return inner.IsUserSealedAsync(userId, nowUtc, cancellationToken);
        }

        public Task<RateLimitStatus> CheckRateLimitAsync(
            string userId,
            string resource,
            RateLimitConfig config,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            Guard();
            return inner.CheckRateLimitAsync(userId, resource, config, nowUtc, cancellationToken);
        }
    }

    private const string ConsultareResource = "consultare";

    private static QuotaConfig CreateConfig() => new()
    {
        DefaultTier = "ANONYMOUS",
        PeriodTimeZone = TimeZoneInfo.Utc,
        Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["ANONYMOUS"] = new TierConfig
            {
                DailyQuotas = new Dictionary<string, int> { [ConsultareResource] = 3 },
                ConsumptionOrder = [PeriodType.Daily]
            },
            ["FREE"] = new TierConfig
            {
                DailyQuotas = new Dictionary<string, int> { [ConsultareResource] = 5 },
                MonthlyQuotas = new Dictionary<string, int> { ["api_calls"] = 100 },
                ConsumptionOrder = [PeriodType.Daily, PeriodType.Monthly]
            },
            ["PRO"] = new TierConfig
            {
                DailyQuotas = new Dictionary<string, int> { [ConsultareResource] = -1 },
                ConsumptionOrder = [PeriodType.Daily]
            }
        }
    };
}
