using System.Collections.Concurrent;

namespace NetQuota;

public sealed class LocalRateLimitEngine
{
    private sealed class TokenBucketState
    {
        public double Tokens;
        public long LastRefillTicks;
        public long LastSeenTicks;
        public long WindowTicks;
        public long FullRefillTicks;
    }

    private sealed class SlidingWindowState
    {
        public readonly ConcurrentQueue<long> Queue = new();
        public long WindowTicks;
        public long LastSeenTicks;
    }

    private const int SweepEveryNCalls = 256;
    private readonly ConcurrentDictionary<string, TokenBucketState> _buckets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SlidingWindowState> _slidingWindows = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SlidingWindowState> _globalSlidingWindows = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private int _callCounter;

    public RateLimitStatus Evaluate(
        string userId,
        string resource,
        RateLimitConfig config,
        DateTimeOffset nowUtc)
    {
        var key = $"{resource}:{userId}";
        var nowTicks = nowUtc.UtcTicks;
        var windowTicks = Math.Max(TimeSpan.FromMilliseconds(1).Ticks, config.Window.Ticks);

        lock (_gate)
        {
            if (++_callCounter >= SweepEveryNCalls)
            {
                _callCounter = 0;
                SweepIdleKeys(nowTicks);
            }

            if (config.GlobalLimit > 0)
            {
                var globalState = GetGlobalWindow(resource, windowTicks, config.GlobalLimit);
                globalState.WindowTicks = windowTicks;
                globalState.LastSeenTicks = nowTicks;
                var cutoff = nowTicks - windowTicks;
                Prune(globalState.Queue, cutoff);
                if (globalState.Queue.Count >= config.GlobalLimit)
                {
                    if (globalState.Queue.TryPeek(out var oldestGlobal))
                    {
                        var resetGlobal = new DateTimeOffset(oldestGlobal + windowTicks, TimeSpan.Zero);
                        var retryGlobal = resetGlobal > nowUtc ? resetGlobal - nowUtc : TimeSpan.FromSeconds(1);
                        return new RateLimitStatus(false, config.GlobalLimit, 0, resetGlobal, retryGlobal);
                    }

                    return new RateLimitStatus(false, config.GlobalLimit, 0, nowUtc.Add(config.Window), config.Window);
                }
            }

            RateLimitStatus result = config.Algorithm switch
            {
                RateLimitAlgorithm.TokenBucket => EvaluateTokenBucket(key, config, nowUtc, nowTicks, windowTicks),
                _ => EvaluateSlidingWindow(key, config, nowUtc, nowTicks, windowTicks)
            };

            if (result.Allowed && config.GlobalLimit > 0)
            {
                var globalState = GetGlobalWindow(resource, windowTicks, config.GlobalLimit);
                globalState.WindowTicks = windowTicks;
                globalState.LastSeenTicks = nowTicks;
                globalState.Queue.Enqueue(nowTicks);
            }

            return result;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _buckets.Clear();
            _slidingWindows.Clear();
            _globalSlidingWindows.Clear();
            _callCounter = 0;
        }
    }

    private RateLimitStatus EvaluateTokenBucket(
        string key,
        RateLimitConfig config,
        DateTimeOffset nowUtc,
        long nowTicks,
        long windowTicks)
    {
        var capacity = config.Burst > 0 ? config.Burst : config.Rate;
        if (config.Rate <= 0 || config.Window <= TimeSpan.Zero || capacity <= 0)
        {
            // Zero-rate guard (goquota 4db4858 / b294cdd)
            var fallbackWindow = config.Window > TimeSpan.Zero ? config.Window : TimeSpan.FromSeconds(1);
            return new RateLimitStatus(false, Math.Max(0, capacity), 0, nowUtc.Add(fallbackWindow), fallbackWindow);
        }

        var refillPerSecond = config.Rate / config.Window.TotalSeconds;

        if (!_buckets.TryGetValue(key, out var state))
        {
            state = new TokenBucketState
            {
                Tokens = capacity,
                LastRefillTicks = nowTicks,
                LastSeenTicks = nowTicks,
                WindowTicks = windowTicks,
                FullRefillTicks = ComputeFullRefillTicks(capacity, refillPerSecond)
            };
            _buckets[key] = state;
        }
        else
        {
            var elapsedSeconds = Math.Max(0, TimeSpan.FromTicks(nowTicks - state.LastRefillTicks).TotalSeconds);
            state.Tokens = Math.Min(capacity, state.Tokens + elapsedSeconds * refillPerSecond);
            state.LastRefillTicks = nowTicks;
            state.LastSeenTicks = nowTicks;
            state.WindowTicks = windowTicks;
            state.FullRefillTicks = ComputeFullRefillTicks(capacity, refillPerSecond);
        }

        if (state.Tokens >= 1.0)
        {
            state.Tokens -= 1.0;
            var remaining = (int)Math.Floor(state.Tokens);
            var secondsToFull = refillPerSecond > 0 ? (capacity - state.Tokens) / refillPerSecond : 0;
            return new RateLimitStatus(
                Allowed: true,
                Limit: capacity,
                Remaining: remaining,
                ResetAtUtc: nowUtc.AddSeconds(Math.Ceiling(secondsToFull)),
                RetryAfter: TimeSpan.Zero);
        }

        var missing = 1.0 - state.Tokens;
        var waitSeconds = refillPerSecond > 0 ? Math.Max(1.0, Math.Ceiling(missing / refillPerSecond)) : 1.0;
        var retry = TimeSpan.FromSeconds(waitSeconds);
        return new RateLimitStatus(
            Allowed: false,
            Limit: capacity,
            Remaining: 0,
            ResetAtUtc: nowUtc.Add(retry),
            RetryAfter: retry);
    }

    private RateLimitStatus EvaluateSlidingWindow(
        string key,
        RateLimitConfig config,
        DateTimeOffset nowUtc,
        long nowTicks,
        long windowTicks)
    {
        if (config.Rate <= 0 || config.Window <= TimeSpan.Zero)
        {
            // Zero-rate guard (goquota 4db4858 / b294cdd)
            var fallbackWindow = config.Window > TimeSpan.Zero ? config.Window : TimeSpan.FromSeconds(1);
            return new RateLimitStatus(false, 0, 0, nowUtc.Add(fallbackWindow), fallbackWindow);
        }

        var state = _slidingWindows.GetOrAdd(key, _ => new SlidingWindowState());
        state.WindowTicks = windowTicks;
        state.LastSeenTicks = nowTicks;

        var cutoff = nowTicks - windowTicks;
        Prune(state.Queue, cutoff);

        if (state.Queue.Count >= config.Rate)
        {
            if (state.Queue.TryPeek(out var oldest))
            {
                var resetUtc = new DateTimeOffset(oldest + windowTicks, TimeSpan.Zero);
                var retry = resetUtc > nowUtc ? resetUtc - nowUtc : TimeSpan.FromSeconds(1);
                return new RateLimitStatus(
                    Allowed: false,
                    Limit: config.Rate,
                    Remaining: 0,
                    ResetAtUtc: resetUtc,
                    RetryAfter: retry);
            }

            return new RateLimitStatus(
                Allowed: false,
                Limit: config.Rate,
                Remaining: 0,
                ResetAtUtc: nowUtc.Add(config.Window),
                RetryAfter: config.Window);
        }

        state.Queue.Enqueue(nowTicks);
        var remaining = Math.Max(0, config.Rate - state.Queue.Count);
        var windowReset = state.Queue.TryPeek(out var firstTick) && firstTick > 0
            ? new DateTimeOffset(firstTick + windowTicks, TimeSpan.Zero)
            : nowUtc.Add(config.Window);

        return new RateLimitStatus(
            Allowed: true,
            Limit: config.Rate,
            Remaining: remaining,
            ResetAtUtc: windowReset,
            RetryAfter: TimeSpan.Zero);
    }

    private static void Prune(ConcurrentQueue<long> queue, long cutoffTicks)
    {
        while (queue.TryPeek(out var oldest) && oldest <= cutoffTicks)
        {
            queue.TryDequeue(out _);
        }
    }

    private SlidingWindowState GetGlobalWindow(string resource, long windowTicks, int globalLimit)
    {
        // The global budget belongs to a (resource, window, limit) configuration: one tier's shorter
        // window must not prune another tier's exhausted budget for the same resource.
        var globalKey = $"{resource}|{windowTicks}|{globalLimit}";
        return _globalSlidingWindows.GetOrAdd(globalKey, _ => new SlidingWindowState());
    }

    private static long ComputeFullRefillTicks(int capacity, double refillPerSecond)
    {
        if (refillPerSecond <= 0)
        {
            return 0;
        }

        var ticks = capacity / refillPerSecond * TimeSpan.TicksPerSecond;
        return ticks >= long.MaxValue ? long.MaxValue : (long)ticks;
    }

    private void SweepIdleKeys(long nowTicks)
    {
        var minTtlTicks = TimeSpan.FromMinutes(10).Ticks;

        foreach (var (k, b) in _buckets)
        {
            // Keep a bucket until it would have refilled completely. A bucket that is dropped earlier
            // is recreated full and grants a free burst when Burst > 2 * Rate.
            var idleWindow = Math.Max(Math.Max(b.WindowTicks * 2, b.FullRefillTicks), minTtlTicks);
            if (nowTicks - b.LastSeenTicks > idleWindow)
            {
                _buckets.TryRemove(k, out _);
            }
        }

        foreach (var (k, s) in _slidingWindows)
        {
            Prune(s.Queue, nowTicks - s.WindowTicks);
            if (s.Queue.IsEmpty)
            {
                _slidingWindows.TryRemove(k, out _);
            }
        }

        foreach (var (k, g) in _globalSlidingWindows)
        {
            Prune(g.Queue, nowTicks - g.WindowTicks);
            if (g.Queue.IsEmpty)
            {
                _globalSlidingWindows.TryRemove(k, out _);
            }
        }
    }
}
