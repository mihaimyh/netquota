using NetQuota;

namespace NetQuota.Tests;

public class LocalRateLimitEngineFixTests
{
    [Fact]
    public void TokenBucket_IdleSweep_KeepsPartiallyRefilledBucket()
    {
        var engine = new LocalRateLimitEngine();
        var t0 = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var config = new RateLimitConfig(
            RateLimitAlgorithm.TokenBucket,
            Rate: 2,
            Window: TimeSpan.FromMinutes(10),
            Burst: 10);

        for (var i = 0; i < 10; i++)
        {
            Assert.True(engine.Evaluate("u1", "api", config, t0).Allowed);
        }

        Assert.False(engine.Evaluate("u1", "api", config, t0).Allowed);

        // 11 calls so far. Drive the sweep counter to 255 with calls for an unrelated resource.
        var fillerConfig = new RateLimitConfig(
            RateLimitAlgorithm.SlidingWindow,
            Rate: 1000,
            Window: TimeSpan.FromMinutes(1));
        for (var i = 0; i < 244; i++)
        {
            engine.Evaluate($"filler-{i}", "other", fillerConfig, t0);
        }

        // 21 minutes at 2 tokens per 10 minutes is 4.2 tokens: only 4 calls may pass. A full reset
        // would allow 10.
        var t1 = t0.AddMinutes(21);
        var allowed = 0;
        for (var i = 0; i < 20; i++)
        {
            if (engine.Evaluate("u1", "api", config, t1).Allowed)
            {
                allowed++;
            }
        }

        Assert.Equal(4, allowed);
    }

    [Fact]
    public void GlobalLimit_IsScopedToTheTierWindow_SoAnotherTierCannotResetIt()
    {
        var engine = new LocalRateLimitEngine();
        var t0 = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var slowTier = new RateLimitConfig(
            RateLimitAlgorithm.SlidingWindow,
            Rate: 100,
            Window: TimeSpan.FromHours(1),
            GlobalLimit: 2);
        var fastTier = new RateLimitConfig(
            RateLimitAlgorithm.SlidingWindow,
            Rate: 100,
            Window: TimeSpan.FromSeconds(1),
            GlobalLimit: 2);

        Assert.True(engine.Evaluate("slow-1", "api", slowTier, t0).Allowed);
        Assert.True(engine.Evaluate("slow-2", "api", slowTier, t0).Allowed);
        Assert.False(engine.Evaluate("slow-3", "api", slowTier, t0).Allowed);

        Assert.True(engine.Evaluate("fast-1", "api", fastTier, t0.AddSeconds(2)).Allowed);

        // The fast tier's one-second window must not prune the slow tier's one-hour global budget.
        Assert.False(engine.Evaluate("slow-4", "api", slowTier, t0.AddSeconds(2).AddMilliseconds(1)).Allowed);
    }
}
