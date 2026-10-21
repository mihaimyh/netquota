using System.Diagnostics.Metrics;
using System.Threading.Channels;

namespace NetQuota;

public sealed class LruQuotaCache<TValue>(int capacity)
{
    private sealed record CacheNode(
        string Key,
        string Group,
        TValue Value,
        DateTimeOffset CachedAtUtc,
        DateTimeOffset ExpiresAtUtc);

    private readonly int _capacity = Math.Max(1, capacity);
    private readonly Dictionary<string, LinkedListNode<CacheNode>> _map = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _groups = new(StringComparer.Ordinal);
    private readonly LinkedList<CacheNode> _list = new();
    private readonly Lock _gate = new();

    public bool TryGet(
        string key,
        DateTimeOffset nowUtc,
        out TValue value,
        out DateTimeOffset cachedAtUtc,
        bool allowStale = false,
        TimeSpan? maxStaleness = null)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out var node))
            {
                value = default!;
                cachedAtUtc = default;
                return false;
            }

            if (nowUtc > node.Value.ExpiresAtUtc)
            {
                if (allowStale && maxStaleness is { } maxStale && nowUtc - node.Value.CachedAtUtc <= maxStale)
                {
                    value = node.Value.Value;
                    cachedAtUtc = node.Value.CachedAtUtc;
                    return true;
                }

                // Reclaim expired entry on read (goquota b8ea1b0 / BUG-4)
                RemoveNode(node);
                value = default!;
                cachedAtUtc = default;
                return false;
            }

            _list.Remove(node);
            _list.AddFirst(node);
            value = node.Value.Value;
            cachedAtUtc = node.Value.CachedAtUtc;
            return true;
        }
    }

    /// <param name="key">Cache key; upserting an existing key refreshes its TTL and LRU position.</param>
    /// <param name="value">Cached value.</param>
    /// <param name="nowUtc">Current time used for the TTL.</param>
    /// <param name="ttl">How long the entry stays valid.</param>
    /// <param name="group">
    /// Optional grouping key so <see cref="RemoveGroup"/> can evict every entry that belongs to one owner
    /// (for example one user) without scanning the whole cache.
    /// </param>
    public void Set(string key, TValue value, DateTimeOffset nowUtc, TimeSpan ttl, string? group = null)
    {
        lock (_gate)
        {
            var entry = new CacheNode(key, group ?? string.Empty, value, nowUtc, nowUtc.Add(ttl));
            if (_map.TryGetValue(key, out var existing))
            {
                Untrack(existing.Value.Group, key);
                existing.Value = entry;
                Track(entry.Group, key);
                _list.Remove(existing);
                _list.AddFirst(existing);
                return;
            }

            if (_map.Count >= _capacity && _list.Last is { } tail)
            {
                // O(1) LRU eviction via intrusive doubly-linked list (goquota 07634ad)
                RemoveNode(tail);
            }

            var added = _list.AddFirst(entry);
            _map[key] = added;
            Track(entry.Group, key);
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                RemoveNode(node);
            }
        }
    }

    /// <summary>Removes every entry registered under <paramref name="group"/> in O(entries in the group).</summary>
    public void RemoveGroup(string group)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(group) || !_groups.TryGetValue(group, out var keys))
            {
                return;
            }

            _groups.Remove(group);
            foreach (var key in keys)
            {
                if (_map.Remove(key, out var node))
                {
                    _list.Remove(node);
                }
            }
        }
    }

    public void RemoveByPrefix(string prefix)
    {
        lock (_gate)
        {
            var keysToRemove = _map.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .ToArray();

            foreach (var key in keysToRemove)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    RemoveNode(node);
                }
            }
        }
    }

    private void RemoveNode(LinkedListNode<CacheNode> node)
    {
        _map.Remove(node.Value.Key);
        Untrack(node.Value.Group, node.Value.Key);
        _list.Remove(node);
    }

    private void Track(string group, string key)
    {
        if (group.Length == 0)
        {
            return;
        }

        if (!_groups.TryGetValue(group, out var keys))
        {
            keys = new HashSet<string>(StringComparer.Ordinal);
            _groups[group] = keys;
        }

        keys.Add(key);
    }

    private void Untrack(string group, string key)
    {
        if (group.Length == 0 || !_groups.TryGetValue(group, out var keys))
        {
            return;
        }

        keys.Remove(key);
        if (keys.Count == 0)
        {
            _groups.Remove(group);
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _map.Count;
            }
        }
    }
}

public sealed class CircuitBreakerQuotaStorage :
    IQuotaStorage,
    IQuotaTimeSource,
    IQuotaAuditLogger,
    IPromotionStore,
    IPromotionCapabilityReporter,
    IRemainingDrainer,
    IUserMerger
{
    private readonly IQuotaStorage _inner;
    private readonly int _failureThreshold;
    private readonly TimeSpan _resetTimeout;
    private readonly Action<CircuitBreakerState>? _onStateChange;
    private readonly Lock _gate = new();

    private CircuitBreakerState _state = CircuitBreakerState.Closed;
    private int _consecutiveFailures;
    private DateTimeOffset _openedAtUtc;

    public CircuitBreakerQuotaStorage(IQuotaStorage inner, CircuitBreakerConfig config)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(config);
        _inner = inner;
        _failureThreshold = Math.Max(1, config.FailureThreshold);
        _resetTimeout = config.ResetTimeout is { } t && t > TimeSpan.Zero ? t : TimeSpan.FromSeconds(30);
        _onStateChange = config.OnStateChange;
    }

    public IQuotaStorage InnerStorage => _inner;

    public CircuitBreakerState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public bool PromotionsSupported =>
        _inner is IPromotionCapabilityReporter reporter
            ? reporter.PromotionsSupported
            : _inner is IPromotionStore;

    public async Task<DateTimeOffset> GetNowUtcAsync(CancellationToken cancellationToken = default)
    {
        if (_inner is IQuotaTimeSource timeSource)
        {
            return await ExecuteAsync(() => timeSource.GetNowUtcAsync(cancellationToken));
        }

        return DateTimeOffset.UtcNow;
    }

    public Task<QuotaEntitlement?> GetEntitlementAsync(string userId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _inner.GetEntitlementAsync(userId, cancellationToken));

    public Task SetEntitlementAsync(QuotaEntitlement entitlement, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            await _inner.SetEntitlementAsync(entitlement, cancellationToken);
            return true;
        });

    public Task SetPromotionAsync(string userId, TierPromotion promotion, CancellationToken cancellationToken = default)
    {
        if (_inner is not IPromotionStore store)
        {
            throw new UnsupportedQuotaOperationException("SetPromotion");
        }

        return ExecuteAsync(async () =>
        {
            await store.SetPromotionAsync(userId, promotion, cancellationToken);
            return true;
        });
    }

    public Task ClearPromotionAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (_inner is not IPromotionStore store)
        {
            throw new UnsupportedQuotaOperationException("ClearPromotion");
        }

        return ExecuteAsync(async () =>
        {
            await store.ClearPromotionAsync(userId, cancellationToken);
            return true;
        });
    }

    public Task<QuotaUsage?> GetUsageAsync(
        string userId,
        string resource,
        PeriodWindow window,
        int defaultLimit,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _inner.GetUsageAsync(userId, resource, window, defaultLimit, cancellationToken));

    public Task<StorageConsumeOutcome> TryConsumeAsync(
        StorageConsumeSpec spec,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _inner.TryConsumeAsync(spec, cancellationToken));

    public Task ApplyTierChangeAsync(
        TierChangeRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            await _inner.ApplyTierChangeAsync(request, cancellationToken);
            return true;
        });

    public Task SetUsageAsync(
        string userId,
        string resource,
        PeriodWindow window,
        int used,
        int limit,
        IReadOnlyList<string>? distinctKeys = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            await _inner.SetUsageAsync(userId, resource, window, used, limit, distinctKeys, cancellationToken);
            return true;
        });

    public Task<StorageRefundOutcome> RefundAsync(
        RefundRequest request,
        PeriodWindow window,
        int defaultLimit,
        TimeSpan idempotencyTtl,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _inner.RefundAsync(request, window, defaultLimit, idempotencyTtl, nowUtc, cancellationToken));

    public Task<ConsumptionRecord?> GetConsumptionRecordAsync(
        string scopedIdempotencyKey,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _inner.GetConsumptionRecordAsync(scopedIdempotencyKey, nowUtc, cancellationToken));

    public Task<RefundRecord?> GetRefundRecordAsync(
        string scopedIdempotencyKey,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _inner.GetRefundRecordAsync(scopedIdempotencyKey, nowUtc, cancellationToken));

    public Task TopUpForeverCreditsAsync(
        string userId,
        string resource,
        int amount,
        string? idempotencyKey,
        TimeSpan idempotencyTtl,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            await _inner.TopUpForeverCreditsAsync(userId, resource, amount, idempotencyKey, idempotencyTtl, nowUtc, cancellationToken);
            return true;
        });

    public Task RefundForeverCreditsAsync(
        string userId,
        string resource,
        int amount,
        string? idempotencyKey,
        TimeSpan idempotencyTtl,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            await _inner.RefundForeverCreditsAsync(userId, resource, amount, idempotencyKey, idempotencyTtl, nowUtc, cancellationToken);
            return true;
        });

    public Task DrainRemainingAsync(
        string userId,
        string resource,
        PeriodWindow window,
        int defaultLimit,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            await _inner.DrainRemainingAsync(userId, resource, window, defaultLimit, cancellationToken);
            return true;
        });

    public Task<MergeUserResult> MergeUserAsync(
        StorageMergeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_inner is not IUserMerger merger)
        {
            throw new UnsupportedQuotaOperationException("MergeUser");
        }

        return ExecuteAsync(() => merger.MergeUserAsync(request, cancellationToken));
    }

    public Task<MergeUserResult> MergeUserAsync(
        MergeUserRequest request,
        Func<PeriodType, PeriodWindow> windowResolver,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _inner.MergeUserAsync(request, windowResolver, nowUtc, cancellationToken));

    public Task<bool> IsUserSealedAsync(string userId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _inner.IsUserSealedAsync(userId, nowUtc, cancellationToken));

    public Task<RateLimitStatus> CheckRateLimitAsync(
        string userId,
        string resource,
        RateLimitConfig config,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _inner.CheckRateLimitAsync(userId, resource, config, nowUtc, cancellationToken));

    public Task LogAuditEntryAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
    {
        if (_inner is not IQuotaAuditLogger audit)
        {
            return Task.CompletedTask;
        }

        return ExecuteAsync(async () =>
        {
            await audit.LogAuditEntryAsync(entry, cancellationToken);
            return true;
        });
    }

    public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogsAsync(
        AuditLogFilter filter,
        CancellationToken cancellationToken = default)
    {
        if (_inner is not IQuotaAuditLogger audit)
        {
            return Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);
        }

        return ExecuteAsync(() => audit.GetAuditLogsAsync(filter, cancellationToken));
    }

    private async Task<T> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        EnsurePermitted();
        try
        {
            var result = await operation();
            OnSuccess();
            return result;
        }
        catch (QuotaException ex) when (ex is not QuotaStorageUnavailableException)
        {
            // Domain exceptions (QuotaExceeded, RateLimitExceeded, UserSealed, Validation) are not storage outages.
            OnSuccess();
            throw;
        }
        catch
        {
            OnFailure();
            throw;
        }
    }

    private void EnsurePermitted()
    {
        CircuitBreakerState? transitionTo = null;
        lock (_gate)
        {
            if (_state == CircuitBreakerState.Open)
            {
                if (DateTimeOffset.UtcNow - _openedAtUtc >= _resetTimeout)
                {
                    _state = CircuitBreakerState.HalfOpen;
                    transitionTo = CircuitBreakerState.HalfOpen;
                }
                else
                {
                    throw new CircuitBreakerOpenException();
                }
            }
        }

        if (transitionTo is { } next)
        {
            _onStateChange?.Invoke(next);
        }
    }

    private void OnSuccess()
    {
        CircuitBreakerState? transitionTo = null;
        lock (_gate)
        {
            _consecutiveFailures = 0;
            if (_state != CircuitBreakerState.Closed)
            {
                _state = CircuitBreakerState.Closed;
                transitionTo = CircuitBreakerState.Closed;
            }
        }

        if (transitionTo is { } next)
        {
            _onStateChange?.Invoke(next);
        }
    }

    private void OnFailure()
    {
        CircuitBreakerState? transitionTo = null;
        lock (_gate)
        {
            _consecutiveFailures++;
            if (_state == CircuitBreakerState.HalfOpen ||
                (_state == CircuitBreakerState.Closed && _consecutiveFailures >= _failureThreshold))
            {
                _state = CircuitBreakerState.Open;
                _openedAtUtc = DateTimeOffset.UtcNow;
                transitionTo = CircuitBreakerState.Open;
            }
        }

        if (transitionTo is { } next)
        {
            _onStateChange?.Invoke(next);
        }
    }
}

public sealed class OpenTelemetryQuotaMetrics : IQuotaMetrics, IDisposable
{
    public const string DefaultMeterName = "NetQuota";

    private readonly Meter _meter;
    private readonly Counter<long> _consumptionTotal;
    private readonly Histogram<long> _consumptionAmount;
    private readonly Histogram<double> _checkDurationSeconds;
    private readonly Counter<long> _rateLimitExceededTotal;
    private readonly Histogram<double> _rateLimitDurationSeconds;
    private readonly Counter<long> _warningsTotal;
    private readonly Counter<long> _fallbackHitsTotal;
    private readonly Counter<long> _idempotencyHitsTotal;

    public OpenTelemetryQuotaMetrics(string meterName = DefaultMeterName)
    {
        _meter = new Meter(meterName, "1.0.0");
        _consumptionTotal = _meter.CreateCounter<long>("netquota.quota.consumption.total");
        _consumptionAmount = _meter.CreateHistogram<long>("netquota.quota.consumption.amount");
        _checkDurationSeconds = _meter.CreateHistogram<double>("netquota.quota.check.duration", "s");
        _rateLimitExceededTotal = _meter.CreateCounter<long>("netquota.ratelimit.exceeded.total");
        _rateLimitDurationSeconds = _meter.CreateHistogram<double>("netquota.ratelimit.check.duration", "s");
        _warningsTotal = _meter.CreateCounter<long>("netquota.quota.warnings.total");
        _fallbackHitsTotal = _meter.CreateCounter<long>("netquota.fallback.hits.total");
        _idempotencyHitsTotal = _meter.CreateCounter<long>("netquota.idempotency.hits.total");
    }

    public void RecordConsumption(string resource, string tier, bool success, int amount, TimeSpan duration)
    {
        _consumptionTotal.Add(
            1,
            new KeyValuePair<string, object?>("resource", resource),
            new KeyValuePair<string, object?>("tier", tier),
            new KeyValuePair<string, object?>("success", success));

        if (success && amount > 0)
        {
            _consumptionAmount.Record(
                amount,
                new KeyValuePair<string, object?>("resource", resource),
                new KeyValuePair<string, object?>("tier", tier));
        }

        _checkDurationSeconds.Record(
            duration.TotalSeconds,
            new KeyValuePair<string, object?>("resource", resource));
    }

    public void RecordRateLimitCheck(string resource, bool allowed, TimeSpan duration)
    {
        _rateLimitDurationSeconds.Record(
            duration.TotalSeconds,
            new KeyValuePair<string, object?>("resource", resource));

        if (!allowed)
        {
            _rateLimitExceededTotal.Add(1, new KeyValuePair<string, object?>("resource", resource));
        }
    }

    public void RecordWarning(string resource, string tier, double threshold) =>
        _warningsTotal.Add(
            1,
            new KeyValuePair<string, object?>("resource", resource),
            new KeyValuePair<string, object?>("tier", tier),
            new KeyValuePair<string, object?>("threshold", threshold));

    public void RecordFallback(string strategy) =>
        _fallbackHitsTotal.Add(1, new KeyValuePair<string, object?>("strategy", strategy));

    public void RecordIdempotencyHit(string operationType) =>
        _idempotencyHitsTotal.Add(1, new KeyValuePair<string, object?>("operation_type", operationType));

    public void Dispose() => _meter.Dispose();
}

public sealed record TieredQuotaStorageConfig(
    IQuotaStorage Hot,
    IQuotaStorage Cold,
    bool AsyncUsageSync = false,
    Action<Exception>? AsyncErrorHandler = null,
    int AsyncUsageSyncQueueCapacity = 1024);

public sealed class TieredQuotaStorage :
    IQuotaStorage,
    IQuotaTimeSource,
    IQuotaAuditLogger,
    IPromotionStore,
    IPromotionCapabilityReporter,
    IRemainingDrainer,
    IUserMerger,
    IAsyncDisposable
{
    private static readonly TimeSpan ColdSyncDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly TieredQuotaStorageConfig _config;
    private readonly Channel<StorageConsumeSpec>? _coldSyncQueue;
    private readonly Task? _coldSyncPump;
    private readonly CancellationTokenSource? _coldSyncCts;

    public TieredQuotaStorage(TieredQuotaStorageConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(config.Hot);
        ArgumentNullException.ThrowIfNull(config.Cold);
        _config = config;

        if (!config.AsyncUsageSync)
        {
            return;
        }

        // Bounded queue drained by a single background reader: no unbounded Task.Run fan-out and
        // pending cold writes are flushed on shutdown instead of being dropped mid-flight.
        _coldSyncQueue = Channel.CreateBounded<StorageConsumeSpec>(
            new BoundedChannelOptions(Math.Max(1, config.AsyncUsageSyncQueueCapacity))
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropWrite
            });
        _coldSyncCts = new CancellationTokenSource();
        _coldSyncPump = Task.Run(() => PumpColdSyncAsync(_coldSyncQueue.Reader, _coldSyncCts.Token));
    }

    public bool PromotionsSupported =>
        _config.Cold is IPromotionStore && _config.Hot is IPromotionStore;

    public async ValueTask DisposeAsync()
    {
        if (_coldSyncQueue is null)
        {
            return;
        }

        _coldSyncQueue.Writer.TryComplete();
        try
        {
            if (_coldSyncPump is not null)
            {
                await _coldSyncPump.WaitAsync(ColdSyncDrainTimeout);
            }
        }
        catch (TimeoutException)
        {
            _coldSyncCts?.Cancel();
        }
        finally
        {
            _coldSyncCts?.Dispose();
        }
    }

    private async Task PumpColdSyncAsync(ChannelReader<StorageConsumeSpec> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var spec in reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await _config.Cold.TryConsumeAsync(spec, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _config.AsyncErrorHandler?.Invoke(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown with a full queue: pending cold writes were already reported as dropped.
        }
    }

    public async Task<DateTimeOffset> GetNowUtcAsync(CancellationToken cancellationToken = default)
    {
        if (_config.Cold is IQuotaTimeSource coldTime)
        {
            return await coldTime.GetNowUtcAsync(cancellationToken);
        }

        if (_config.Hot is IQuotaTimeSource hotTime)
        {
            return await hotTime.GetNowUtcAsync(cancellationToken);
        }

        return DateTimeOffset.UtcNow;
    }

    public async Task<QuotaEntitlement?> GetEntitlementAsync(string userId, CancellationToken cancellationToken = default)
    {
        var hot = await _config.Hot.GetEntitlementAsync(userId, cancellationToken);
        if (hot is not null)
        {
            return hot;
        }

        var cold = await _config.Cold.GetEntitlementAsync(userId, cancellationToken);
        if (cold is not null)
        {
            await _config.Hot.SetEntitlementAsync(cold, cancellationToken);
        }

        return cold;
    }

    public async Task SetEntitlementAsync(QuotaEntitlement entitlement, CancellationToken cancellationToken = default)
    {
        await _config.Cold.SetEntitlementAsync(entitlement, cancellationToken);
        await _config.Hot.SetEntitlementAsync(entitlement, cancellationToken);
    }

    public async Task SetPromotionAsync(string userId, TierPromotion promotion, CancellationToken cancellationToken = default)
    {
        if (_config.Cold is not IPromotionStore coldPromo || _config.Hot is not IPromotionStore hotPromo)
        {
            throw new UnsupportedQuotaOperationException("SetPromotion");
        }

        await coldPromo.SetPromotionAsync(userId, promotion, cancellationToken);
        await hotPromo.SetPromotionAsync(userId, promotion, cancellationToken);
    }

    public async Task ClearPromotionAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (_config.Cold is not IPromotionStore coldPromo || _config.Hot is not IPromotionStore hotPromo)
        {
            throw new UnsupportedQuotaOperationException("ClearPromotion");
        }

        await coldPromo.ClearPromotionAsync(userId, cancellationToken);
        await hotPromo.ClearPromotionAsync(userId, cancellationToken);
    }

    public async Task<QuotaUsage?> GetUsageAsync(
        string userId,
        string resource,
        PeriodWindow window,
        int defaultLimit,
        CancellationToken cancellationToken = default)
    {
        var cold = await _config.Cold.GetUsageAsync(userId, resource, window, defaultLimit, cancellationToken);
        if (cold is not null)
        {
            await _config.Hot.SetUsageAsync(userId, resource, window, cold.Used, cold.Limit, cold.DistinctKeys, cancellationToken);
            return cold;
        }

        return await _config.Hot.GetUsageAsync(userId, resource, window, defaultLimit, cancellationToken);
    }

    public async Task<StorageConsumeOutcome> TryConsumeAsync(
        StorageConsumeSpec spec,
        CancellationToken cancellationToken = default)
    {
        if (!_config.AsyncUsageSync)
        {
            var outcome = await _config.Cold.TryConsumeAsync(spec, cancellationToken);
            await _config.Hot.SetUsageAsync(
                spec.UserId,
                spec.Resource,
                spec.Window,
                outcome.NewUsed,
                outcome.EffectiveLimit,
                outcome.DistinctKeys,
                cancellationToken);
            return outcome;
        }

        var hotOutcome = await _config.Hot.TryConsumeAsync(spec, cancellationToken);
        if (_coldSyncQueue is not null &&
            !_coldSyncQueue.Writer.TryWrite(spec))
        {
            _config.AsyncErrorHandler?.Invoke(
                new QuotaException("Cold usage sync queue is full; the consumption was not mirrored to cold storage."));
        }

        return hotOutcome;
    }

    public async Task ApplyTierChangeAsync(
        TierChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        await _config.Cold.ApplyTierChangeAsync(request, cancellationToken);
        await _config.Hot.ApplyTierChangeAsync(request, cancellationToken);
    }

    public async Task SetUsageAsync(
        string userId,
        string resource,
        PeriodWindow window,
        int used,
        int limit,
        IReadOnlyList<string>? distinctKeys = null,
        CancellationToken cancellationToken = default)
    {
        await _config.Cold.SetUsageAsync(userId, resource, window, used, limit, distinctKeys, cancellationToken);
        await _config.Hot.SetUsageAsync(userId, resource, window, used, limit, distinctKeys, cancellationToken);
    }

    public async Task<StorageRefundOutcome> RefundAsync(
        RefundRequest request,
        PeriodWindow window,
        int defaultLimit,
        TimeSpan idempotencyTtl,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var outcome = await _config.Cold.RefundAsync(request, window, defaultLimit, idempotencyTtl, nowUtc, cancellationToken);
        await _config.Hot.RefundAsync(request, window, defaultLimit, idempotencyTtl, nowUtc, cancellationToken);
        return outcome;
    }

    public async Task<ConsumptionRecord?> GetConsumptionRecordAsync(
        string scopedIdempotencyKey,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var hot = await _config.Hot.GetConsumptionRecordAsync(scopedIdempotencyKey, nowUtc, cancellationToken);
        if (hot is not null)
        {
            return hot;
        }

        return await _config.Cold.GetConsumptionRecordAsync(scopedIdempotencyKey, nowUtc, cancellationToken);
    }

    public async Task<RefundRecord?> GetRefundRecordAsync(
        string scopedIdempotencyKey,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var hot = await _config.Hot.GetRefundRecordAsync(scopedIdempotencyKey, nowUtc, cancellationToken);
        if (hot is not null)
        {
            return hot;
        }

        return await _config.Cold.GetRefundRecordAsync(scopedIdempotencyKey, nowUtc, cancellationToken);
    }

    public async Task TopUpForeverCreditsAsync(
        string userId,
        string resource,
        int amount,
        string? idempotencyKey,
        TimeSpan idempotencyTtl,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        await _config.Cold.TopUpForeverCreditsAsync(userId, resource, amount, idempotencyKey, idempotencyTtl, nowUtc, cancellationToken);
        await _config.Hot.TopUpForeverCreditsAsync(userId, resource, amount, idempotencyKey, idempotencyTtl, nowUtc, cancellationToken);
    }

    public async Task RefundForeverCreditsAsync(
        string userId,
        string resource,
        int amount,
        string? idempotencyKey,
        TimeSpan idempotencyTtl,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        await _config.Cold.RefundForeverCreditsAsync(userId, resource, amount, idempotencyKey, idempotencyTtl, nowUtc, cancellationToken);
        await _config.Hot.RefundForeverCreditsAsync(userId, resource, amount, idempotencyKey, idempotencyTtl, nowUtc, cancellationToken);
    }

    public async Task DrainRemainingAsync(
        string userId,
        string resource,
        PeriodWindow window,
        int defaultLimit,
        CancellationToken cancellationToken = default)
    {
        await _config.Cold.DrainRemainingAsync(userId, resource, window, defaultLimit, cancellationToken);
        await _config.Hot.DrainRemainingAsync(userId, resource, window, defaultLimit, cancellationToken);
    }

    public async Task<MergeUserResult> MergeUserAsync(
        StorageMergeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_config.Cold is not IUserMerger coldMerger || _config.Hot is not IUserMerger hotMerger)
        {
            throw new UnsupportedQuotaOperationException("MergeUser");
        }

        var res = await coldMerger.MergeUserAsync(request, cancellationToken);
        await hotMerger.MergeUserAsync(request, cancellationToken);
        return res;
    }

    public async Task<MergeUserResult> MergeUserAsync(
        MergeUserRequest request,
        Func<PeriodType, PeriodWindow> windowResolver,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var res = await _config.Cold.MergeUserAsync(request, windowResolver, nowUtc, cancellationToken);
        await _config.Hot.MergeUserAsync(request, windowResolver, nowUtc, cancellationToken);
        return res;
    }

    public Task<bool> IsUserSealedAsync(string userId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
        _config.Cold.IsUserSealedAsync(userId, nowUtc, cancellationToken);

    public Task<RateLimitStatus> CheckRateLimitAsync(
        string userId,
        string resource,
        RateLimitConfig config,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        config.Backend == RateLimitBackend.Distributed
            ? _config.Cold.CheckRateLimitAsync(userId, resource, config, nowUtc, cancellationToken)
            : _config.Hot.CheckRateLimitAsync(userId, resource, config, nowUtc, cancellationToken);

    public async Task LogAuditEntryAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
    {
        if (_config.Cold is IQuotaAuditLogger coldAudit)
        {
            await coldAudit.LogAuditEntryAsync(entry, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetAuditLogsAsync(
        AuditLogFilter filter,
        CancellationToken cancellationToken = default)
    {
        if (_config.Cold is IQuotaAuditLogger coldAudit)
        {
            return await coldAudit.GetAuditLogsAsync(filter, cancellationToken);
        }

        return [];
    }
}
