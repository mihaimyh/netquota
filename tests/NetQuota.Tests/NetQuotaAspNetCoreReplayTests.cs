using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetQuota.AspNetCore;

namespace NetQuota.Tests;

/// <summary>
/// Regression coverage for ASP-01: a client-supplied request id must never become the consume
/// idempotency key, because a replayed consumption is reported as allowed and the filter would
/// execute the protected endpoint again without charging quota.
/// </summary>
public class NetQuotaAspNetCoreReplayTests
{
    private const string RequestId = "replayed-client-request-id";

    [Fact]
    public async Task ClientRequestIdHeader_IsNotUsedAsIdempotencyKey()
    {
        var executions = 0;
        await using var app = await StartAppAsync(quotaPerDay: 1, () => Interlocked.Increment(ref executions));
        using var client = new HttpClient { BaseAddress = new Uri(Address(app)) };

        var first = await PostAsync(client);
        var replay = await PostAsync(client);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, replay.StatusCode);
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task ConcurrentReplays_DoNotExecuteHandlerAfterQuotaIsExhausted()
    {
        var executions = 0;
        await using var app = await StartAppAsync(quotaPerDay: 1, () => Interlocked.Increment(ref executions));
        using var client = new HttpClient { BaseAddress = new Uri(Address(app)) };

        // Seed the idempotency record so every subsequent request is a pure replay pre-fix.
        var seed = await PostAsync(client);
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 50).Select(_ => PostAsync(client)));

        var allowed = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var denied = responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests);

        Assert.Equal(0, allowed);
        Assert.Equal(50, denied);
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task RateLimiter_AcquireAsync_HonoursPermitCount()
    {
        using var limiter = new NetQuotaDistributedRateLimiter(CreateRateLimitManager(rate: 3), "api", "u1");

        using var first = await limiter.AcquireAsync(3, TestContext.Current.CancellationToken);
        using var second = await limiter.AcquireAsync(3, TestContext.Current.CancellationToken);

        Assert.True(first.IsAcquired);
        Assert.False(second.IsAcquired);
    }

    [Fact]
    public void RateLimiter_AttemptAcquire_HonoursPermitCount()
    {
        using var limiter = new NetQuotaDistributedRateLimiter(CreateRateLimitManager(rate: 3), "api", "u1");

        using var lease = limiter.AttemptAcquire(1_000_000);

        Assert.False(lease.IsAcquired);
    }

    [Fact]
    public void RateLimiter_NegativePermitCount_IsRejected()
    {
        using var limiter = new NetQuotaDistributedRateLimiter(CreateRateLimitManager(), "api", "u1");

        Assert.Throws<ArgumentOutOfRangeException>(() => limiter.AttemptAcquire(-1));
    }

    [Fact]
    public async Task RateLimiter_NegativePermitCountAsync_IsRejected()
    {
        using var limiter = new NetQuotaDistributedRateLimiter(CreateRateLimitManager(), "api", "u1");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await limiter.AcquireAsync(-1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RateLimiter_UsesSubjectEntitlementTierInsteadOfAnonymous()
    {
        var manager = CreateRateLimitManager(rate: 1, proRate: 1_000);
        await manager.SetEntitlementAsync(new QuotaEntitlement(
            "u1",
            "pro",
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        // The entitlement is readable and grants the pro rate.
        Assert.True((await manager.CheckRateLimitAsync("u1", "api", cancellationToken: TestContext.Current.CancellationToken)).Allowed);
        Assert.True((await manager.CheckRateLimitAsync("u1", "api", cancellationToken: TestContext.Current.CancellationToken)).Allowed);

        using var limiter = new NetQuotaDistributedRateLimiter(manager, "api", "u1");
        using var first = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        using var second = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        Assert.True(first.IsAcquired);
        Assert.True(second.IsAcquired);
    }

    [Fact]
    public async Task UsageEndpoint_PropagatesCancellationAndStopsReadingStorage()
    {
        var storage = new NeverCompletingUsageStorage(new InMemoryQuotaStorage());
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddNetQuota(CreateUsageConfig(), _ => storage);
        await using var app = builder.Build();
        app.MapNetQuotaUsageEndpoint("/usage", new NetQuotaUsageApiOptions(
            ResolveUserIdAsync: _ => ValueTask.FromResult<string?>("user-1"),
            KnownResources: ["reports", "api_calls", "credits"]));

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single();
        var requestDelegate = Assert.IsType<RequestDelegate>(endpoint.RequestDelegate);

        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.RequestAborted = aborted.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requestDelegate(context));
        Assert.Equal(1, storage.UsageReads);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/work");
        request.Headers.TryAddWithoutValidation(NetQuotaHeaderNames.RequestId, RequestId);
        return client.SendAsync(request);
    }

    private static string Address(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        return Assert.Single(addresses!);
    }

    private static async Task<WebApplication> StartAppAsync(int quotaPerDay, Action onExecute)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddNetQuota(new QuotaConfig
        {
            DefaultTier = "free",
            Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["free"] = new TierConfig
                {
                    DailyQuotas = new Dictionary<string, int>(StringComparer.Ordinal) { ["api"] = quotaPerDay }
                }
            }
        });

        var app = builder.Build();
        app.MapPost("/work", (HttpContext _) =>
        {
            onExecute();
            return Results.Ok(new { ok = true });
        }).WithNetQuota(new NetQuotaEnforcementOptions
        {
            ResolveUserIdAsync = _ => ValueTask.FromResult<string?>("user-1"),
            ResolveResourceAsync = _ => ValueTask.FromResult("api")
        });

        await app.StartAsync();
        return app;
    }

    private static QuotaManager CreateRateLimitManager(int rate = 3, int? proRate = null)
    {
        var window = TimeSpan.FromMinutes(1);
        var tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["free"] = new TierConfig
            {
                RateLimits = new Dictionary<string, RateLimitConfig>(StringComparer.Ordinal)
                {
                    ["api"] = new RateLimitConfig(RateLimitAlgorithm.SlidingWindow, rate, window)
                }
            }
        };

        if (proRate is { } pro)
        {
            tiers["pro"] = new TierConfig
            {
                RateLimits = new Dictionary<string, RateLimitConfig>(StringComparer.Ordinal)
                {
                    ["api"] = new RateLimitConfig(RateLimitAlgorithm.SlidingWindow, pro, window)
                }
            };
        }

        return new QuotaManager(
            new InMemoryQuotaStorage(),
            new QuotaConfig { DefaultTier = "free", Tiers = tiers });
    }

    private static QuotaConfig CreateUsageConfig() => new()
    {
        DefaultTier = "free",
        Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["free"] = new TierConfig
            {
                DailyQuotas = new Dictionary<string, int>(StringComparer.Ordinal) { ["api"] = 10 }
            }
        }
    };

    /// <summary>
    /// Simulates a cancellation-aware storage backend whose read never completes: the usage endpoint
    /// can only stop through the caller's cancellation token.
    /// </summary>
    private sealed class NeverCompletingUsageStorage(InMemoryQuotaStorage inner) : IQuotaStorage
    {
        private readonly TaskCompletionSource<QuotaUsage?> _pending =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int UsageReads { get; private set; }

        public Task<QuotaUsage?> GetUsageAsync(
            string userId,
            string resource,
            PeriodWindow window,
            int defaultLimit,
            CancellationToken cancellationToken = default)
        {
            UsageReads++;
            return _pending.Task;
        }

        public Task<QuotaEntitlement?> GetEntitlementAsync(string userId, CancellationToken cancellationToken = default) =>
            inner.GetEntitlementAsync(userId, cancellationToken);

        public Task SetEntitlementAsync(QuotaEntitlement entitlement, CancellationToken cancellationToken = default) =>
            inner.SetEntitlementAsync(entitlement, cancellationToken);

        public Task<StorageConsumeOutcome> TryConsumeAsync(
            StorageConsumeSpec spec,
            CancellationToken cancellationToken = default) =>
            inner.TryConsumeAsync(spec, cancellationToken);

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

        public Task<bool> IsUserSealedAsync(string userId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
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
