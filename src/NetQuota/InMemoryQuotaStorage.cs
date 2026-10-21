using System.Collections.Concurrent;

namespace NetQuota;

public sealed class InMemoryQuotaStorage(Func<DateTimeOffset>? clock = null)
    : IQuotaStorage,
      IQuotaTimeSource,
      IQuotaAuditLogger,
      IPromotionStore,
      IPromotionCapabilityReporter,
      IRemainingDrainer,
      IUserMerger
{
    private sealed record UsageState(
        int Used,
        int Limit,
        List<string> DistinctKeys,
        string? Tier = null);

    private sealed record IdempotencyState(
        StorageConsumeOutcome? ConsumeOutcome,
        ConsumptionRecord? ConsumptionRecord,
        RefundRecord? RefundRecord,
        PeriodType? ChargedPeriod,
        DateTimeOffset ExpiresAtUtc);

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly ConcurrentDictionary<string, QuotaEntitlement> _entitlements = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, UsageState> _usages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IdempotencyState> _idempotency = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MergeUserResult> _merges = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sealedUsers = new(StringComparer.Ordinal);
    private readonly List<AuditLogEntry> _auditLogs = [];
    private readonly LocalRateLimitEngine _rateLimitEngine = new();
    private readonly Lock _gate = new();

    public bool PromotionsSupported => true;

    public void Clear()
    {
        lock (_gate)
        {
            // Reset all records including merge idempotency (goquota 41e7df7 / STORAGE-6)
            _entitlements.Clear();
            _usages.Clear();
            _idempotency.Clear();
            _merges.Clear();
            _sealedUsers.Clear();
            _auditLogs.Clear();
            _rateLimitEngine.Clear();
        }
    }

    public Task<DateTimeOffset> GetNowUtcAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_clock());

    public Task<QuotaEntitlement?> GetEntitlementAsync(string userId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_entitlements.TryGetValue(userId, out var ent) ? ent : null);
        }
    }

    public Task SetEntitlementAsync(QuotaEntitlement entitlement, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_entitlements.TryGetValue(entitlement.UserId, out var existing))
            {
                if (entitlement.Promotion is null && existing.Promotion is { } promo)
                {
                    entitlement = entitlement with { Promotion = promo };
                }

                if (!entitlement.Sealed && existing.Sealed)
                {
                    entitlement = entitlement with
                    {
                        Sealed = existing.Sealed,
                        MigratedTo = existing.MigratedTo,
                        SealExpiresAtUtc = existing.SealExpiresAtUtc
                    };
                }
            }

            if (entitlement.UpdatedAtUtc == default)
            {
                entitlement = entitlement with { UpdatedAtUtc = _clock() };
            }

            _entitlements[entitlement.UserId] = entitlement;
            return Task.CompletedTask;
        }
    }

    public Task SetPromotionAsync(string userId, TierPromotion promotion, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_entitlements.TryGetValue(userId, out var existing))
            {
                throw new QuotaException($"Entitlement for user '{userId}' was not found.");
            }

            // Do not mutate UpdatedAtUtc, Tier, SubscriptionStartDateUtc, or ExpiresAtUtc (goquota 599b63f)
            _entitlements[userId] = existing with { Promotion = promotion };
            return Task.CompletedTask;
        }
    }

    public Task ClearPromotionAsync(string userId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_entitlements.TryGetValue(userId, out var existing) && existing.Promotion is not null)
            {
                // Do not mutate UpdatedAtUtc (goquota 599b63f)
                _entitlements[userId] = existing with { Promotion = null };
            }

            return Task.CompletedTask;
        }
    }

    public Task<QuotaUsage?> GetUsageAsync(
        string userId,
        string resource,
        PeriodWindow window,
        int defaultLimit,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var key = UsageKey(userId, resource, window);
            if (!_usages.TryGetValue(key, out var state))
            {
                return Task.FromResult<QuotaUsage?>(new QuotaUsage(
                    userId,
                    resource,
                    window.PeriodType,
                    window.Key,
                    Used: 0,
                    Limit: defaultLimit,
                    window.StartUtc,
                    window.EndUtc,
                    []));
            }

            var effectiveLimit = window.PeriodType == PeriodType.Forever
                ? state.Limit
                : (state.Limit != 0 ? state.Limit : defaultLimit);

            return Task.FromResult<QuotaUsage?>(new QuotaUsage(
                userId,
                resource,
                window.PeriodType,
                window.Key,
                state.Used,
                effectiveLimit,
                window.StartUtc,
                window.EndUtc,
                state.DistinctKeys.ToArray(),
                Tier: state.Tier));
        }
    }

    public Task<StorageConsumeOutcome> TryConsumeAsync(
        StorageConsumeSpec spec,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(spec.IdempotencyKey))
            {
                var idemKey = NormalizeConsumeIdemKey(spec.UserId, spec.IdempotencyKey);
                if (_idempotency.TryGetValue(idemKey, out var cached) &&
                    cached.ExpiresAtUtc > spec.NowUtc &&
                    cached.ConsumeOutcome is not null)
                {
                    // A replay debited nothing; reporting ConsumedNewUnit would inflate metrics.
                    return Task.FromResult(cached.ConsumeOutcome with
                    {
                        WasIdempotencyHit = true,
                        ConsumedNewUnit = false
                    });
                }
            }

            var key = UsageKey(spec.UserId, spec.Resource, spec.Window);
            if (!_usages.TryGetValue(key, out var state))
            {
                state = new UsageState(0, spec.ConfiguredLimit, [], spec.EffectiveTier);
            }

            var effectiveLimit = ResolveEffectiveLimitForConsume(spec, state);

            if (!string.IsNullOrWhiteSpace(spec.DistinctKey) &&
                state.DistinctKeys.Contains(spec.DistinctKey, StringComparer.Ordinal))
            {
                var replay = new StorageConsumeOutcome(
                    Allowed: true,
                    ConsumedNewUnit: false,
                    WasIdempotencyHit: false,
                    NewUsed: state.Used,
                    EffectiveLimit: effectiveLimit,
                    DistinctKeys: state.DistinctKeys.ToArray(),
                    ChargedPeriod: spec.Window.PeriodType);

                SaveConsumeIdempotency(spec, replay);
                return Task.FromResult(replay);
            }

            if (effectiveLimit != -1 && state.Used + spec.Amount > effectiveLimit)
            {
                return Task.FromResult(new StorageConsumeOutcome(
                    Allowed: false,
                    ConsumedNewUnit: false,
                    WasIdempotencyHit: false,
                    NewUsed: state.Used,
                    EffectiveLimit: effectiveLimit,
                    DistinctKeys: state.DistinctKeys.ToArray(),
                    ChargedPeriod: spec.Window.PeriodType));
            }

            var newDistinct = state.DistinctKeys.ToList();
            if (!string.IsNullOrWhiteSpace(spec.DistinctKey))
            {
                newDistinct.Add(spec.DistinctKey);
                newDistinct.Sort(StringComparer.Ordinal);
            }

            var newUsed = state.Used + spec.Amount;
            _usages[key] = new UsageState(newUsed, effectiveLimit, newDistinct, spec.EffectiveTier ?? state.Tier);

            var outcome = new StorageConsumeOutcome(
                Allowed: true,
                ConsumedNewUnit: true,
                WasIdempotencyHit: false,
                NewUsed: newUsed,
                EffectiveLimit: effectiveLimit,
                DistinctKeys: newDistinct.ToArray(),
                ChargedPeriod: spec.Window.PeriodType);

            SaveConsumeIdempotency(spec, outcome);
            return Task.FromResult(outcome);
        }
    }

    public Task ApplyTierChangeAsync(
        TierChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var key = UsageKey(request.UserId, request.Resource, request.Window);
            var existing = _usages.TryGetValue(key, out var s) ? s : null;
            var currentUsed = existing?.Used ?? request.CurrentUsed;
            var distinct = request.DistinctKeys is not null
                ? request.DistinctKeys.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList()
                : (existing?.DistinctKeys ?? []);

            _usages[key] = new UsageState(currentUsed, request.NewLimit, distinct, request.NewTier);
            return Task.CompletedTask;
        }
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
        lock (_gate)
        {
            var key = UsageKey(userId, resource, window);
            var existing = _usages.TryGetValue(key, out var s) ? s : null;
            var existingDistinct = existing?.DistinctKeys ?? [];
            var updatedDistinct = distinctKeys is not null
                ? distinctKeys.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList()
                : (used == 0 ? [] : existingDistinct);

            _usages[key] = new UsageState(Math.Max(0, used), limit, updatedDistinct, existing?.Tier);
            return Task.CompletedTask;
        }
    }

    public Task<StorageRefundOutcome> RefundAsync(
        RefundRequest request,
        PeriodWindow window,
        int defaultLimit,
        TimeSpan idempotencyTtl,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            string? idemKey = null;
            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            {
                idemKey = NormalizeRefundIdemKey(request.UserId, request.IdempotencyKey);
                if (_idempotency.TryGetValue(idemKey, out var cached) && cached.ExpiresAtUtc > nowUtc)
                {
                    // Return Amount = 0 on idempotent replay so callers know nothing moved (goquota d5073e8)
                    return Task.FromResult(new StorageRefundOutcome(
                        cached.ChargedPeriod ?? window.PeriodType,
                        Amount: 0,
                        Replayed: true));
                }
            }

            var key = UsageKey(request.UserId, request.Resource, window);
            if (_usages.TryGetValue(key, out var state))
            {
                var nextDistinct = state.DistinctKeys.ToList();
                if (!string.IsNullOrWhiteSpace(request.DistinctKeyToRemove))
                {
                    nextDistinct.RemoveAll(k => string.Equals(k, request.DistinctKeyToRemove, StringComparison.Ordinal));
                }

                var newUsed = Math.Max(0, state.Used - Math.Max(0, request.Amount));
                _usages[key] = state with { Used = newUsed, DistinctKeys = nextDistinct };
            }

            if (idemKey is not null)
            {
                var refundRecord = new RefundRecord(
                    RefundId: idemKey,
                    UserId: request.UserId,
                    Resource: request.Resource,
                    Amount: request.Amount,
                    Window: window,
                    TimestampUtc: nowUtc,
                    Reason: request.Reason);

                _idempotency[idemKey] = new IdempotencyState(
                    ConsumeOutcome: null,
                    ConsumptionRecord: null,
                    RefundRecord: refundRecord,
                    ChargedPeriod: window.PeriodType,
                    ExpiresAtUtc: nowUtc.Add(idempotencyTtl));
            }

            return Task.FromResult(new StorageRefundOutcome(window.PeriodType, request.Amount, Replayed: false));
        }
    }

    public Task<ConsumptionRecord?> GetConsumptionRecordAsync(
        string scopedIdempotencyKey,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scopedIdempotencyKey))
        {
            return Task.FromResult<ConsumptionRecord?>(null);
        }

        lock (_gate)
        {
            var idemKey = scopedIdempotencyKey.StartsWith("consume:", StringComparison.Ordinal)
                ? scopedIdempotencyKey
                : $"consume:{scopedIdempotencyKey}";
            if (_idempotency.TryGetValue(idemKey, out var cached) &&
                cached.ExpiresAtUtc > nowUtc &&
                cached.ConsumptionRecord is not null)
            {
                return Task.FromResult<ConsumptionRecord?>(cached.ConsumptionRecord);
            }

            return Task.FromResult<ConsumptionRecord?>(null);
        }
    }

    public Task<RefundRecord?> GetRefundRecordAsync(
        string scopedIdempotencyKey,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scopedIdempotencyKey))
        {
            return Task.FromResult<RefundRecord?>(null);
        }

        lock (_gate)
        {
            var idemKey = scopedIdempotencyKey.StartsWith("refund:", StringComparison.Ordinal)
                ? scopedIdempotencyKey
                : $"refund:{scopedIdempotencyKey}";
            if (_idempotency.TryGetValue(idemKey, out var cached) &&
                cached.ExpiresAtUtc > nowUtc &&
                cached.RefundRecord is not null)
            {
                return Task.FromResult<RefundRecord?>(cached.RefundRecord);
            }

            return Task.FromResult<RefundRecord?>(null);
        }
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
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                var idemKey = $"topup:{userId}:{resource}:{idempotencyKey}";
                if (_idempotency.TryGetValue(idemKey, out var cached) && cached.ExpiresAtUtc > nowUtc)
                {
                    return Task.CompletedTask;
                }

                _idempotency[idemKey] = new IdempotencyState(null, null, null, PeriodType.Forever, nowUtc.Add(idempotencyTtl));
            }

            var key = UsageKey(userId, resource, PeriodWindow.Forever);
            var existing = _usages.TryGetValue(key, out var state)
                ? state
                : new UsageState(0, 0, []);

            _usages[key] = existing with { Limit = existing.Limit + Math.Max(0, amount) };
            return Task.CompletedTask;
        }
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
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                var idemKey = $"refund_credits:{userId}:{resource}:{idempotencyKey}";
                if (_idempotency.TryGetValue(idemKey, out var cached) && cached.ExpiresAtUtc > nowUtc)
                {
                    return Task.CompletedTask;
                }

                _idempotency[idemKey] = new IdempotencyState(null, null, null, PeriodType.Forever, nowUtc.Add(idempotencyTtl));
            }

            var key = UsageKey(userId, resource, PeriodWindow.Forever);
            if (!_usages.TryGetValue(key, out var state))
            {
                return Task.CompletedTask;
            }

            _usages[key] = state with { Limit = Math.Max(0, state.Limit - Math.Max(0, amount)) };
            return Task.CompletedTask;
        }
    }

    public Task DrainRemainingAsync(
        string userId,
        string resource,
        PeriodWindow window,
        int defaultLimit,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var key = UsageKey(userId, resource, window);
            var existing = _usages.TryGetValue(key, out var state)
                ? state
                : new UsageState(0, defaultLimit, []);

            _usages[key] = existing with { Limit = Math.Max(0, existing.Used) };
            return Task.CompletedTask;
        }
    }

    public Task<MergeUserResult> MergeUserAsync(
        MergeUserRequest request,
        Func<PeriodType, PeriodWindow> windowResolver,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var items = new List<MergeItem>(request.Resources.Count * request.Periods.Count);
        foreach (var resource in request.Resources)
        {
            foreach (var period in request.Periods)
            {
                var win = windowResolver(period);
                items.Add(new MergeItem(resource, period, win, win));
            }
        }

        var sealExpiresAt = request.SealSource
            ? nowUtc.Add(request.SealTtl is { } ttl && ttl > TimeSpan.Zero ? ttl : TimeSpan.FromDays(30))
            : (DateTimeOffset?)null;

        return MergeUserAsync(
            new StorageMergeRequest(
                SourceUserId: request.SourceUserId,
                TargetUserId: request.TargetUserId,
                Items: items,
                IdempotencyKey: request.IdempotencyKey,
                SealSource: request.SealSource,
                NowUtc: nowUtc,
                SealExpiresAtUtc: sealExpiresAt),
            cancellationToken);
    }

    public Task<MergeUserResult> MergeUserAsync(
        StorageMergeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(request.SourceUserId, request.TargetUserId, StringComparison.Ordinal))
        {
            throw new SameUserMergeException(request.SourceUserId);
        }

        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey) &&
                _merges.TryGetValue(request.IdempotencyKey, out var existingMerge))
            {
                return Task.FromResult(existingMerge with { AlreadyMerged = true });
            }

            if (IsSealedUnderLock(request.SourceUserId, request.NowUtc))
            {
                throw new UserSealedException(request.SourceUserId);
            }

            if (IsSealedUnderLock(request.TargetUserId, request.NowUtc))
            {
                throw new UserSealedException(request.TargetUserId);
            }

            var transferredUsage = new Dictionary<string, int>(StringComparer.Ordinal);
            var transferredForever = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var item in request.Items)
            {
                var sourceKey = UsageKey(request.SourceUserId, item.Resource, item.SourceWindow);
                var targetKey = UsageKey(request.TargetUserId, item.Resource, item.TargetWindow);

                if (!_usages.TryGetValue(sourceKey, out var srcState))
                {
                    continue;
                }

                var tgtState = _usages.TryGetValue(targetKey, out var existingTgt)
                    ? existingTgt
                    : new UsageState(0, item.PeriodType == PeriodType.Forever ? 0 : srcState.Limit, []);

                if (item.PeriodType == PeriodType.Forever)
                {
                    var remaining = Math.Max(0, srcState.Limit - srcState.Used);
                    if (remaining > 0)
                    {
                        _usages[targetKey] = tgtState with { Limit = tgtState.Limit + remaining };
                        _usages[sourceKey] = srcState with { Limit = srcState.Used };
                        transferredForever[item.Resource] = remaining;
                    }
                }
                else
                {
                    var mergedDistinct = tgtState.DistinctKeys
                        .Concat(srcState.DistinctKeys)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(x => x, StringComparer.Ordinal)
                        .ToList();

                    var newTargetUsed = mergedDistinct.Count > 0
                        ? mergedDistinct.Count
                        : tgtState.Used + srcState.Used;

                    var added = Math.Max(0, newTargetUsed - tgtState.Used);
                    _usages[targetKey] = tgtState with
                    {
                        Used = newTargetUsed,
                        DistinctKeys = mergedDistinct
                    };

                    if (request.SealSource)
                    {
                        _usages[sourceKey] = srcState with { Used = 0, DistinctKeys = [] };
                    }

                    transferredUsage[$"{item.Resource}:{item.PeriodType}"] = added;
                }
            }

            if (request.SealSource)
            {
                var sealUntil = request.SealExpiresAtUtc ?? request.NowUtc.AddDays(30);
                _sealedUsers[request.SourceUserId] = sealUntil;
                if (_entitlements.TryGetValue(request.SourceUserId, out var srcEnt))
                {
                    _entitlements[request.SourceUserId] = srcEnt with
                    {
                        Sealed = true,
                        MigratedTo = request.TargetUserId,
                        SealExpiresAtUtc = sealUntil
                    };
                }
            }

            var result = new MergeUserResult(
                request.SourceUserId,
                request.TargetUserId,
                AlreadyMerged: false,
                transferredUsage,
                transferredForever);

            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            {
                _merges[request.IdempotencyKey] = result;
            }

            return Task.FromResult(result);
        }
    }

    public Task<bool> IsUserSealedAsync(string userId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(IsSealedUnderLock(userId, nowUtc));
        }
    }

    public Task<RateLimitStatus> CheckRateLimitAsync(
        string userId,
        string resource,
        RateLimitConfig config,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_rateLimitEngine.Evaluate(userId, resource, config, nowUtc));

    public Task LogAuditEntryAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _auditLogs.Add(entry);
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogsAsync(
        AuditLogFilter filter,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IEnumerable<AuditLogEntry> query = _auditLogs;
            if (!string.IsNullOrWhiteSpace(filter.UserId))
            {
                query = query.Where(e => string.Equals(e.UserId, filter.UserId, StringComparison.Ordinal));
            }

            if (!string.IsNullOrWhiteSpace(filter.Resource))
            {
                query = query.Where(e => string.Equals(e.Resource, filter.Resource, StringComparison.Ordinal));
            }

            if (filter.StartTimeUtc is { } start)
            {
                query = query.Where(e => e.TimestampUtc >= start);
            }

            return Task.FromResult<IReadOnlyList<AuditLogEntry>>(
                query.OrderByDescending(e => e.TimestampUtc)
                    .Take(Math.Max(1, filter.Limit))
                    .ToArray());
        }
    }

    private bool IsSealedUnderLock(string userId, DateTimeOffset nowUtc)
    {
        if (_sealedUsers.TryGetValue(userId, out var until) && until > nowUtc)
        {
            return true;
        }

        if (_entitlements.TryGetValue(userId, out var ent) && ent.IsSealedAt(nowUtc))
        {
            return true;
        }

        return false;
    }

    private static int ResolveEffectiveLimitForConsume(StorageConsumeSpec spec, UsageState state)
    {
        if (spec.Window.PeriodType == PeriodType.Forever)
        {
            return state.Limit;
        }

        // The current tier's unlimited quota wins over any stored cap (goquota 4782f75).
        if (spec.ConfiguredLimit == -1)
        {
            return -1;
        }

        // Preserve prorated limit set by ApplyTierChangeAsync when stored tier matches current tier (goquota 179602a / 20e441a)
        if (!string.IsNullOrWhiteSpace(spec.EffectiveTier) &&
            !string.IsNullOrWhiteSpace(state.Tier) &&
            string.Equals(state.Tier, spec.EffectiveTier, StringComparison.OrdinalIgnoreCase) &&
            state.Limit != 0)
        {
            return state.Limit;
        }

        return spec.ConfiguredLimit;
    }

    private void SaveConsumeIdempotency(StorageConsumeSpec spec, StorageConsumeOutcome outcome)
    {
        if (!string.IsNullOrWhiteSpace(spec.IdempotencyKey))
        {
            var idemKey = NormalizeConsumeIdemKey(spec.UserId, spec.IdempotencyKey);
            var record = new ConsumptionRecord(
                ConsumptionId: idemKey,
                UserId: spec.UserId,
                Resource: spec.Resource,
                Amount: spec.Amount,
                Window: spec.Window,
                NewUsed: outcome.NewUsed,
                EffectiveLimit: outcome.EffectiveLimit,
                DistinctKeys: outcome.DistinctKeys,
                TimestampUtc: spec.NowUtc,
                Allowed: outcome.Allowed,
                ConsumedNewUnit: outcome.ConsumedNewUnit);

            _idempotency[idemKey] = new IdempotencyState(
                ConsumeOutcome: outcome,
                ConsumptionRecord: record,
                RefundRecord: null,
                ChargedPeriod: spec.Window.PeriodType,
                ExpiresAtUtc: spec.NowUtc.Add(spec.IdempotencyTtl));
        }
    }

    private static string NormalizeConsumeIdemKey(string userId, string idempotencyKey)
    {
        var scoped = idempotencyKey.StartsWith($"{userId}:", StringComparison.Ordinal)
            ? idempotencyKey
            : $"{userId}:{idempotencyKey}";
        return $"consume:{scoped}";
    }

    private static string NormalizeRefundIdemKey(string userId, string idempotencyKey)
    {
        var scoped = idempotencyKey.StartsWith($"{userId}:", StringComparison.Ordinal)
            ? idempotencyKey
            : $"{userId}:{idempotencyKey}";
        return $"refund:{scoped}";
    }

    private static string UsageKey(string userId, string resource, PeriodWindow window) =>
        $"{userId}|{resource}|{window.PeriodType}|{window.Key}";
}
