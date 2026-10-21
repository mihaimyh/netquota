using NetQuota;

namespace NetQuota.Tests;

public class TieredStorageFixTests
{
    [Fact]
    public async Task AsyncSync_GetUsage_WaitsForColdAndDoesNotEraseHotConsume()
    {
        var now = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
        var ct = TestContext.Current.CancellationToken;
        var hot = new InMemoryQuotaStorage(() => now);
        var cold = new InMemoryQuotaStorage(() => now);
        var gatedCold = new GatedColdStorage(cold);
        var tiered = new TieredQuotaStorage(new TieredQuotaStorageConfig(hot, gatedCold, AsyncUsageSync: true));
        var window = PeriodWindow.ForDaily(now, TimeZoneInfo.Utc);

        try
        {
            var first = await tiered.TryConsumeAsync(ConsumeSpec("u1", 1, window, now), ct);
            Assert.True(first.Allowed);
            await gatedCold.Entered.WaitAsync(ct);

            var readTask = tiered.GetUsageAsync("u1", "api", window, defaultLimit: 2, ct);
            await Task.Delay(200, ct);
            Assert.False(readTask.IsCompleted, "the read must wait for the queued cold write");

            gatedCold.Release();
            var usage = await readTask;

            Assert.NotNull(usage);
            Assert.Equal(1, usage!.Used);
            var hotUsage = await hot.GetUsageAsync("u1", "api", window, defaultLimit: 2, ct);
            Assert.Equal(1, hotUsage!.Used);

            var second = await tiered.TryConsumeAsync(ConsumeSpec("u1", 1, window, now), ct);
            Assert.True(second.Allowed);
            Assert.Equal(2, second.NewUsed);
        }
        finally
        {
            gatedCold.Release();
            await tiered.DisposeAsync();
        }
    }

    [Fact]
    public async Task AsyncSync_Refund_WaitsForQueuedConsume_SoHotAndColdStayEqual()
    {
        var now = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
        var ct = TestContext.Current.CancellationToken;
        var hot = new InMemoryQuotaStorage(() => now);
        var cold = new InMemoryQuotaStorage(() => now);
        var gatedCold = new GatedColdStorage(cold);
        var tiered = new TieredQuotaStorage(new TieredQuotaStorageConfig(hot, gatedCold, AsyncUsageSync: true));
        var window = PeriodWindow.ForDaily(now, TimeZoneInfo.Utc);

        try
        {
            var consume = await tiered.TryConsumeAsync(ConsumeSpec("u1", 5, window, now), ct);
            Assert.True(consume.Allowed);
            Assert.Equal(5, consume.NewUsed);
            await gatedCold.Entered.WaitAsync(ct);

            var refundTask = tiered.RefundAsync(
                new RefundRequest("u1", "api", 2, PeriodType.Daily),
                window,
                defaultLimit: 10,
                idempotencyTtl: TimeSpan.FromHours(1),
                nowUtc: now,
                ct);

            await Task.Delay(200, ct);
            Assert.False(
                refundTask.IsCompleted,
                "the refund must not reach cold storage before the queued consume that created the usage");

            gatedCold.Release();
            var refund = await refundTask;
            Assert.Equal(2, refund.Amount);

            var hotUsage = await hot.GetUsageAsync("u1", "api", window, defaultLimit: 10, ct);
            var coldUsage = await cold.GetUsageAsync("u1", "api", window, defaultLimit: 10, ct);
            Assert.Equal(3, hotUsage!.Used);
            Assert.Equal(3, coldUsage!.Used);
        }
        finally
        {
            gatedCold.Release();
            await tiered.DisposeAsync();
        }
    }

    [Fact]
    public async Task AsyncSync_FullQueue_ReportsDroppedColdWrite()
    {
        var now = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
        var ct = TestContext.Current.CancellationToken;
        var hot = new InMemoryQuotaStorage(() => now);
        var cold = new InMemoryQuotaStorage(() => now);
        var gatedCold = new GatedColdStorage(cold);
        var errors = new List<Exception>();
        var tiered = new TieredQuotaStorage(new TieredQuotaStorageConfig(
            hot,
            gatedCold,
            AsyncUsageSync: true,
            AsyncErrorHandler: errors.Add,
            AsyncUsageSyncQueueCapacity: 1));
        var window = PeriodWindow.ForDaily(now, TimeZoneInfo.Utc);

        try
        {
            await tiered.TryConsumeAsync(ConsumeSpec("u1", 1, window, now), ct);
            await gatedCold.Entered.WaitAsync(ct);

            // The pump holds the first write; the second fills the single queue slot.
            await tiered.TryConsumeAsync(ConsumeSpec("u1", 1, window, now), ct);
            var third = await tiered.TryConsumeAsync(ConsumeSpec("u1", 1, window, now), ct);

            Assert.True(third.Allowed);
            Assert.Equal(3, third.NewUsed);
            Assert.NotEmpty(errors);
            Assert.Contains(errors, e => e.Message.Contains("queue is full", StringComparison.OrdinalIgnoreCase));

            gatedCold.Release();
            await tiered.DisposeAsync();

            var coldUsage = await cold.GetUsageAsync("u1", "api", window, defaultLimit: 10, ct);
            Assert.Equal(2, coldUsage!.Used);
        }
        finally
        {
            gatedCold.Release();
            await tiered.DisposeAsync();
        }
    }

    private static StorageConsumeSpec ConsumeSpec(
        string userId,
        int amount,
        PeriodWindow window,
        DateTimeOffset now) =>
        new(
            UserId: userId,
            Resource: "api",
            Amount: amount,
            Window: window,
            ConfiguredLimit: 10,
            IdempotencyKey: null,
            DistinctKey: null,
            IdempotencyTtl: TimeSpan.FromHours(1),
            NowUtc: now);

    /// <summary>Holds the cold-sync pump inside <see cref="TryConsumeAsync"/> until released.</summary>
    private sealed class GatedColdStorage(IQuotaStorage inner) : IQuotaStorage
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public async Task<StorageConsumeOutcome> TryConsumeAsync(
            StorageConsumeSpec spec,
            CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return await inner.TryConsumeAsync(spec, cancellationToken);
        }

        public Task<QuotaEntitlement?> GetEntitlementAsync(string userId, CancellationToken cancellationToken = default) =>
            inner.GetEntitlementAsync(userId, cancellationToken);

        public Task SetEntitlementAsync(QuotaEntitlement entitlement, CancellationToken cancellationToken = default) =>
            inner.SetEntitlementAsync(entitlement, cancellationToken);

        public Task<QuotaUsage?> GetUsageAsync(
            string userId,
            string resource,
            PeriodWindow window,
            int defaultLimit,
            CancellationToken cancellationToken = default) =>
            inner.GetUsageAsync(userId, resource, window, defaultLimit, cancellationToken);

        public Task ApplyTierChangeAsync(TierChangeRequest request, CancellationToken cancellationToken = default) =>
            inner.ApplyTierChangeAsync(request, cancellationToken);

        public Task SetUsageAsync(
            string userId,
            string resource,
            PeriodWindow window,
            int used,
            int limit,
            IReadOnlyList<string>? distinctKeys = null,
            CancellationToken cancellationToken = default) =>
            inner.SetUsageAsync(userId, resource, window, used, limit, distinctKeys, cancellationToken);

        public Task<StorageRefundOutcome> RefundAsync(
            RefundRequest request,
            PeriodWindow window,
            int defaultLimit,
            TimeSpan idempotencyTtl,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            inner.RefundAsync(request, window, defaultLimit, idempotencyTtl, nowUtc, cancellationToken);

        public Task<ConsumptionRecord?> GetConsumptionRecordAsync(
            string scopedIdempotencyKey,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            inner.GetConsumptionRecordAsync(scopedIdempotencyKey, nowUtc, cancellationToken);

        public Task<RefundRecord?> GetRefundRecordAsync(
            string scopedIdempotencyKey,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            inner.GetRefundRecordAsync(scopedIdempotencyKey, nowUtc, cancellationToken);

        public Task TopUpForeverCreditsAsync(
            string userId,
            string resource,
            int amount,
            string? idempotencyKey,
            TimeSpan idempotencyTtl,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            inner.TopUpForeverCreditsAsync(userId, resource, amount, idempotencyKey, idempotencyTtl, nowUtc, cancellationToken);

        public Task RefundForeverCreditsAsync(
            string userId,
            string resource,
            int amount,
            string? idempotencyKey,
            TimeSpan idempotencyTtl,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            inner.RefundForeverCreditsAsync(userId, resource, amount, idempotencyKey, idempotencyTtl, nowUtc, cancellationToken);

        public Task DrainRemainingAsync(
            string userId,
            string resource,
            PeriodWindow window,
            int defaultLimit,
            CancellationToken cancellationToken = default) =>
            inner.DrainRemainingAsync(userId, resource, window, defaultLimit, cancellationToken);

        public Task<MergeUserResult> MergeUserAsync(
            MergeUserRequest request,
            Func<PeriodType, PeriodWindow> windowResolver,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            inner.MergeUserAsync(request, windowResolver, nowUtc, cancellationToken);

        public Task<bool> IsUserSealedAsync(
            string userId,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            inner.IsUserSealedAsync(userId, nowUtc, cancellationToken);

        public Task<RateLimitStatus> CheckRateLimitAsync(
            string userId,
            string resource,
            RateLimitConfig config,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            inner.CheckRateLimitAsync(userId, resource, config, nowUtc, cancellationToken);
    }
}
