using NetQuota;

namespace NetQuota.Tests;

/// <summary>
/// Regression coverage for Invariant 1: every entitlement rewrite must preserve the fields the
/// caller did not supply — in particular <see cref="QuotaEntitlement.Timezone"/>, which anchors the
/// user's daily/monthly quota windows. A billing sync that omits it must not reset the user to UTC.
/// </summary>
public class EntitlementPreservationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Rewrite_WithoutTimezone_PreservesExistingTimezone()
    {
        var storage = new InMemoryQuotaStorage(() => Now);

        await storage.SetEntitlementAsync(
            new QuotaEntitlement("u1", "free", Now, Now, Timezone: "Europe/Bucharest"));
        await storage.SetEntitlementAsync(
            new QuotaEntitlement("u1", "pro", Now, Now.AddMinutes(1)));

        var stored = await storage.GetEntitlementAsync("u1");

        Assert.Equal("Europe/Bucharest", stored!.Timezone);
        Assert.Equal("pro", stored.Tier);
    }

    [Fact]
    public async Task Rewrite_WithBlankTimezone_PreservesExistingTimezone()
    {
        var storage = new InMemoryQuotaStorage(() => Now);

        await storage.SetEntitlementAsync(
            new QuotaEntitlement("u1", "free", Now, Now, Timezone: "Europe/Bucharest"));
        await storage.SetEntitlementAsync(
            new QuotaEntitlement("u1", "free", Now, Now.AddMinutes(1), Timezone: "   "));

        var stored = await storage.GetEntitlementAsync("u1");

        Assert.Equal("Europe/Bucharest", stored!.Timezone);
    }

    [Fact]
    public async Task Rewrite_WithNewTimezone_OverridesIt()
    {
        var storage = new InMemoryQuotaStorage(() => Now);

        await storage.SetEntitlementAsync(
            new QuotaEntitlement("u1", "free", Now, Now, Timezone: "Europe/Bucharest"));
        await storage.SetEntitlementAsync(
            new QuotaEntitlement("u1", "free", Now, Now.AddMinutes(1), Timezone: "America/New_York"));

        var stored = await storage.GetEntitlementAsync("u1");

        Assert.Equal("America/New_York", stored!.Timezone);
    }

    [Fact]
    public async Task Rewrite_WithoutTimezone_StillPreservesPromotion()
    {
        var storage = new InMemoryQuotaStorage(() => Now);
        var promotion = new TierPromotion(
            "pro", Now, Now.AddDays(3), Source: "test", Reason: "comp", IdempotencyKey: "k1");

        await storage.SetEntitlementAsync(
            new QuotaEntitlement("u1", "free", Now, Now, Promotion: promotion, Timezone: "Europe/Bucharest"));
        await storage.SetEntitlementAsync(
            new QuotaEntitlement("u1", "free", Now, Now.AddMinutes(1)));

        var stored = await storage.GetEntitlementAsync("u1");

        Assert.Equal("pro", stored!.Promotion?.Tier);
        Assert.Equal("Europe/Bucharest", stored.Timezone);
    }
}
