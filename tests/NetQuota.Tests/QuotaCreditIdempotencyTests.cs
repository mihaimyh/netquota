using NetQuota;

namespace NetQuota.Tests;

/// <summary>
/// Regression coverage for Invariant 4: money paths must always carry a provider-derived
/// idempotency key, and the key must be scoped per resource so one billing event can credit or
/// refund several resources without collapsing them into a single grant.
/// </summary>
public class QuotaCreditIdempotencyTests
{
    private const string Resource = "api";

    [Fact]
    public async Task TopUp_WithoutIdempotencyKey_Throws()
    {
        var manager = CreateManager();

        await Assert.ThrowsAsync<QuotaConfigValidationException>(
            () => manager.TopUpLimitAsync("u1", Resource, 10));
    }

    [Fact]
    public async Task Refund_WithoutIdempotencyKey_Throws()
    {
        var manager = CreateManager();

        await Assert.ThrowsAsync<QuotaConfigValidationException>(
            () => manager.RefundCreditsAsync("u1", Resource, 10));
    }

    [Fact]
    public async Task GrantOneTimeCredit_WithoutIdempotencyKey_Throws()
    {
        var manager = CreateManager();

        await Assert.ThrowsAsync<QuotaConfigValidationException>(
            () => manager.GrantOneTimeCreditAsync("u1", Resource, 10));
    }

    [Fact]
    public async Task TopUp_SameEventKeyForTwoResources_CreditsBoth()
    {
        var manager = CreateManager();

        await manager.TopUpLimitAsync("u1", "api", 25, idempotencyKey: "evt_checkout_1");
        await manager.TopUpLimitAsync("u1", "storage", 40, idempotencyKey: "evt_checkout_1");

        Assert.Equal(25, (await manager.GetQuotaAsync("u1", "api", PeriodType.Forever)).Limit);
        Assert.Equal(40, (await manager.GetQuotaAsync("u1", "storage", PeriodType.Forever)).Limit);
    }

    [Fact]
    public async Task TopUp_SameEventKeyForSameResource_GrantsOnce()
    {
        var manager = CreateManager();

        await manager.TopUpLimitAsync("u1", Resource, 50, idempotencyKey: "evt_checkout_1");
        await manager.TopUpLimitAsync("u1", Resource, 50, idempotencyKey: "evt_checkout_1");

        Assert.Equal(50, (await manager.GetQuotaAsync("u1", Resource, PeriodType.Forever)).Limit);
    }

    [Fact]
    public async Task Refund_SameEventKeyForTwoResources_RefundsBoth()
    {
        var manager = CreateManager();
        await manager.TopUpLimitAsync("u1", "api", 100, idempotencyKey: "grant_api");
        await manager.TopUpLimitAsync("u1", "storage", 100, idempotencyKey: "grant_storage");

        await manager.RefundCreditsAsync("u1", "api", 30, idempotencyKey: "evt_refund_1");
        await manager.RefundCreditsAsync("u1", "storage", 70, idempotencyKey: "evt_refund_1");

        Assert.Equal(70, (await manager.GetQuotaAsync("u1", "api", PeriodType.Forever)).Limit);
        Assert.Equal(30, (await manager.GetQuotaAsync("u1", "storage", PeriodType.Forever)).Limit);
    }

    [Fact]
    public async Task TopUp_ConcurrentRetriesOfSameEvent_GrantExactlyOnce()
    {
        var manager = CreateManager();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var workers = Enumerable.Range(0, 50).Select(async _ =>
        {
            await gate.Task;
            await manager.TopUpLimitAsync("u1", Resource, 5, idempotencyKey: "evt_concurrent");
        }).ToArray();

        gate.SetResult();
        await Task.WhenAll(workers);

        Assert.Equal(5, (await manager.GetQuotaAsync("u1", Resource, PeriodType.Forever)).Limit);
    }

    private static QuotaManager CreateManager() =>
        new(
            new InMemoryQuotaStorage(),
            new QuotaConfig
            {
                DefaultTier = "free",
                Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["free"] = new TierConfig()
                }
            });
}
