using System.Text.Json;
using NetQuota;

namespace NetQuota.Tests;

/// <summary>
/// Regression coverage for the fail-fast configuration contract (CCD-01, CCD-02, CCD-03) and the
/// consume-amount guard that keeps storage usage counters out of `int` overflow (A1-CORE).
/// </summary>
public class QuotaConfigValidationTests
{
    private const int MaxConsumeAmount = 1_000_000;

    private static readonly DateTimeOffset Now = new(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveIdempotencyTtl_Throws(int seconds)
    {
        var config = new QuotaConfig
        {
            IdempotencyTtl = TimeSpan.FromSeconds(seconds),
            Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["free"] = new TierConfig()
            }
        };

        Assert.Throws<QuotaConfigValidationException>(config.Validate);
    }

    [Fact]
    public void Validate_DefaultIdempotencyTtl_IsAccepted()
    {
        var config = new QuotaConfig
        {
            Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["free"] = new TierConfig()
            }
        };

        config.Validate();
    }

    [Fact]
    public void Tiers_ExplicitCaseSensitiveDictionary_IsNormalized()
    {
        var config = new QuotaConfig
        {
            DefaultTier = "free",
            Tiers = new Dictionary<string, TierConfig>(StringComparer.Ordinal)
            {
                ["free"] = new TierConfig(),
                ["pro"] = new TierConfig()
            }
        };

        Assert.True(config.Tiers.ContainsKey("FREE"));
        Assert.True(config.Tiers.ContainsKey("Pro"));
    }

    [Fact]
    public async Task JsonDeserializedTiers_ResolveMixedCaseEntitlement()
    {
        const string json = """
            {
              "DefaultTier": "free",
              "Tiers": {
                "free": { "MonthlyQuotas": { "api": 100 } },
                "pro": { "MonthlyQuotas": { "api": 1000 } }
              }
            }
            """;

        var config = JsonSerializer.Deserialize<QuotaConfig>(json);

        Assert.NotNull(config);
        config!.Validate();
        var manager = new QuotaManager(new InMemoryQuotaStorage(() => Now), config);

        // Before the fix the case-sensitive deserialized dictionary rejected "PRO" outright.
        await manager.SetEntitlementAsync(new QuotaEntitlement("u1", "PRO", Now, Now));

        Assert.Equal("PRO", await manager.GetEffectiveTierAsync("u1"));
        var quota = await manager.GetQuotaAsync("u1", "api", PeriodType.Monthly);
        Assert.Equal(1000, quota.Limit);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Validate_NonFiniteWarningThreshold_Throws(double threshold)
    {
        var config = new QuotaConfig
        {
            Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["free"] = new TierConfig
                {
                    WarningThresholds = new Dictionary<string, IReadOnlyList<double>>(StringComparer.Ordinal)
                    {
                        ["api"] = [threshold]
                    }
                }
            }
        };

        Assert.Throws<QuotaConfigValidationException>(config.Validate);
    }

    [Fact]
    public async Task UnknownExplicitTier_IsRejectedByQuotaAndRateLimitReads()
    {
        var manager = CreateManager(limit: 100);

        await Assert.ThrowsAsync<QuotaConfigValidationException>(
            () => manager.GetQuotaAsync("u1", "api", PeriodType.Daily, explicitTier: "does-not-exist"));
        await Assert.ThrowsAsync<QuotaConfigValidationException>(
            () => manager.CheckRateLimitAsync("u1", "api", explicitTier: "does-not-exist"));
    }

    [Theory]
    [InlineData(MaxConsumeAmount + 1)]
    [InlineData(int.MaxValue)]
    public async Task Consume_AboveMaxConsumeAmount_IsRejected(int amount)
    {
        var manager = CreateManager(limit: 100);

        await Assert.ThrowsAsync<QuotaConfigValidationException>(
            () => manager.ConsumeWithResultAsync("u1", "api", amount, PeriodType.Daily));
    }

    [Fact]
    public async Task Consume_AtMaxConsumeAmount_IsAccepted()
    {
        var manager = CreateManager(limit: MaxConsumeAmount);

        var result = await manager.ConsumeWithResultAsync(
            "u1", "api", MaxConsumeAmount, PeriodType.Daily);

        Assert.True(result.Allowed);
        Assert.Equal(MaxConsumeAmount, result.NewUsed);
    }

    [Fact]
    public async Task InMemoryConsume_ThatWouldOverflowUsed_IsDenied()
    {
        var storage = new InMemoryQuotaStorage(() => Now);
        var window = PeriodWindow.ForDaily(Now, TimeZoneInfo.Utc);

        var first = await storage.TryConsumeAsync(Spec(window, amount: 1, configuredLimit: -1));
        var overflow = await storage.TryConsumeAsync(Spec(window, amount: int.MaxValue, configuredLimit: -1));

        Assert.True(first.Allowed);
        Assert.Equal(1, first.NewUsed);
        Assert.False(overflow.Allowed);
        Assert.Equal(1, overflow.NewUsed);
    }

    private static StorageConsumeSpec Spec(PeriodWindow window, int amount, int configuredLimit) =>
        new(
            UserId: "u1",
            Resource: "api",
            Amount: amount,
            Window: window,
            ConfiguredLimit: configuredLimit,
            IdempotencyKey: null,
            DistinctKey: null,
            IdempotencyTtl: TimeSpan.FromHours(1),
            NowUtc: Now);

    private static QuotaManager CreateManager(int limit) =>
        new(
            new InMemoryQuotaStorage(() => Now),
            new QuotaConfig
            {
                DefaultTier = "free",
                Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["free"] = new TierConfig
                    {
                        DailyQuotas = new Dictionary<string, int> { ["api"] = limit }
                    }
                }
            });
}
