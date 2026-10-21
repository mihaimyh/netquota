using NetQuota;

namespace NetQuota.Tests;

/// <summary>
/// Regression coverage for QM-03: a caller-canceled token must surface as
/// <see cref="OperationCanceledException"/>, never as a fabricated clock value, a fail-open
/// rate-limit decision, a storage outage, or a fallback consume.
/// </summary>
public class QuotaCancellationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Canceled = new(canceled: true);

    [Fact]
    public async Task CanceledTimeSource_PropagatesCancellation()
    {
        var storage = new CancellationProbeStorage(Now) { ThrowOnClock = true };
        var manager = new QuotaManager(storage, CreateConfig());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.GetNowUtcAsync(Canceled));
    }

    [Fact]
    public async Task CanceledDistributedRateLimit_PropagatesCancellation()
    {
        var storage = new CancellationProbeStorage(Now) { ThrowOnRateLimit = true };
        var manager = new QuotaManager(storage, CreateConfig(distributedRateLimit: true));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.CheckRateLimitAsync(
                "u1", "api", explicitTier: "free", cancellationToken: Canceled));
    }

    [Fact]
    public async Task CanceledSealCheck_IsNotReportedAsStorageFailure()
    {
        var storage = new CancellationProbeStorage(Now) { ThrowOnSealCheck = true };
        var manager = new QuotaManager(storage, CreateConfig());

        // Before the fix this threw QuotaStorageUnavailableException("Failed to verify sealed state.").
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.PeekAsync("u1", "api", PeriodType.Daily, cancellationToken: Canceled));
    }

    [Fact]
    public async Task CanceledConsume_DoesNotFallBackToSecondaryStorage()
    {
        var secondary = new InMemoryQuotaStorage(() => Now);
        var storage = new CancellationProbeStorage(Now) { ThrowOnConsume = true };
        var manager = new QuotaManager(storage, CreateConfig(secondary: secondary));

        // Before the fix the canceled consume was routed to the secondary storage and allowed.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.ConsumeWithResultAsync(
                "u1",
                "api",
                1,
                PeriodType.Daily,
                options: null,
                explicitTier: "free",
                cancellationToken: Canceled));
    }

    private static QuotaConfig CreateConfig(bool distributedRateLimit = false, IQuotaStorage? secondary = null) =>
        new()
        {
            DefaultTier = "free",
            Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["free"] = new TierConfig
                {
                    DailyQuotas = new Dictionary<string, int> { ["api"] = 10 },
                    RateLimits = distributedRateLimit
                        ? new Dictionary<string, RateLimitConfig>
                        {
                            ["api"] = new RateLimitConfig(
                                RateLimitAlgorithm.SlidingWindow,
                                Rate: 10,
                                Window: TimeSpan.FromMinutes(1),
                                Backend: RateLimitBackend.Distributed)
                        }
                        : new Dictionary<string, RateLimitConfig>()
                }
            },
            FallbackConfig = secondary is null
                ? null
                : new FallbackConfig(Enabled: true, SecondaryStorage: secondary)
        };

    /// <summary>
    /// Storage stub whose fault switches inject a caller cancellation for one specific operation,
    /// so the tests can reach each fallback path without a real outage.
    /// </summary>
    private sealed class CancellationProbeStorage(DateTimeOffset now) : IQuotaStorage, IQuotaTimeSource
    {
        public bool ThrowOnClock { get; init; }
        public bool ThrowOnSealCheck { get; init; }
        public bool ThrowOnRateLimit { get; init; }
        public bool ThrowOnConsume { get; init; }

        public Task<DateTimeOffset> GetNowUtcAsync(CancellationToken cancellationToken = default) =>
            ThrowOnClock ? CanceledTask<DateTimeOffset>(cancellationToken) : Task.FromResult(now);

        public Task<bool> IsUserSealedAsync(
            string userId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
            ThrowOnSealCheck ? CanceledTask<bool>(cancellationToken) : Task.FromResult(false);

        public Task<RateLimitStatus> CheckRateLimitAsync(
            string userId,
            string resource,
            RateLimitConfig config,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            ThrowOnRateLimit
                ? CanceledTask<RateLimitStatus>(cancellationToken)
                : Task.FromResult(new RateLimitStatus(true, config.Rate, config.Rate, nowUtc.Add(config.Window), TimeSpan.Zero));

        public Task<StorageConsumeOutcome> TryConsumeAsync(
            StorageConsumeSpec spec, CancellationToken cancellationToken = default) =>
            ThrowOnConsume
                ? CanceledTask<StorageConsumeOutcome>(cancellationToken)
                : throw new InvalidOperationException("consume was not expected in this test");

        public Task<QuotaEntitlement?> GetEntitlementAsync(
            string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<QuotaEntitlement?>(null);

        public Task SetEntitlementAsync(
            QuotaEntitlement entitlement, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<QuotaUsage?> GetUsageAsync(
            string userId,
            string resource,
            PeriodWindow window,
            int defaultLimit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<QuotaUsage?>(null);

        public Task ApplyTierChangeAsync(
            TierChangeRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SetUsageAsync(
            string userId,
            string resource,
            PeriodWindow window,
            int used,
            int limit,
            IReadOnlyList<string>? distinctKeys = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<StorageRefundOutcome> RefundAsync(
            RefundRequest request,
            PeriodWindow window,
            int defaultLimit,
            TimeSpan idempotencyTtl,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ConsumptionRecord?> GetConsumptionRecordAsync(
            string scopedIdempotencyKey,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ConsumptionRecord?>(null);

        public Task<RefundRecord?> GetRefundRecordAsync(
            string scopedIdempotencyKey,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<RefundRecord?>(null);

        public Task TopUpForeverCreditsAsync(
            string userId,
            string resource,
            int amount,
            string? idempotencyKey,
            TimeSpan idempotencyTtl,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RefundForeverCreditsAsync(
            string userId,
            string resource,
            int amount,
            string? idempotencyKey,
            TimeSpan idempotencyTtl,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DrainRemainingAsync(
            string userId,
            string resource,
            PeriodWindow window,
            int defaultLimit,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<MergeUserResult> MergeUserAsync(
            MergeUserRequest request,
            Func<PeriodType, PeriodWindow> windowResolver,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private static Task<T> CanceledTask<T>(CancellationToken cancellationToken) =>
            Task.FromException<T>(new OperationCanceledException(cancellationToken));
    }
}
