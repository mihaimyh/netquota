using System.Collections.Concurrent;
using System.Diagnostics;

namespace NetQuota;

public sealed class QuotaManager
{
    /// <summary>
    /// Largest amount a single consume may charge. Larger amounts are rejected rather than trusted,
    /// because the storage adapters add amounts to an <see cref="int"/> usage counter (A1).
    /// </summary>
    public const int MaxConsumeAmount = 1_000_000;

    private readonly IQuotaStorage _storage;
    private readonly QuotaConfig _config;
    private readonly LocalRateLimitEngine _localRateLimits = new();
    private readonly LruQuotaCache<QuotaEntitlement> _entitlementCache;
    private readonly LruQuotaCache<QuotaUsage> _usageCache;
    private readonly ConcurrentDictionary<string, Task<QuotaEntitlement?>> _entitlementFlights = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<QuotaUsage>> _usageFlights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _optimisticUsage = new(StringComparer.Ordinal);
    private readonly Lock _optimisticGate = new();

    public QuotaManager(IQuotaStorage storage, QuotaConfig config)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();

        _storage = config.CircuitBreakerConfig is { Enabled: true } cbConfig
            ? new CircuitBreakerQuotaStorage(storage, cbConfig)
            : storage;
        _config = config;
        _entitlementCache = new LruQuotaCache<QuotaEntitlement>(config.CacheConfig?.MaxEntitlements ?? 10_000);
        _usageCache = new LruQuotaCache<QuotaUsage>(config.CacheConfig?.MaxUsage ?? 50_000);
    }

    public QuotaConfig Config => _config;
    public IQuotaStorage Storage => _storage;
    public bool PromotionsSupported => SupportsPromotions(_storage);

    public static bool SupportsPromotions(IQuotaStorage? storage)
    {
        if (storage is null)
        {
            return false;
        }

        if (storage is IPromotionCapabilityReporter reporter)
        {
            return reporter.PromotionsSupported;
        }

        return storage is IPromotionStore;
    }

    /// <summary>
    /// Wrapper storages advertise every capability interface, so merge support must be checked on the
    /// wrapped storage; otherwise a plain <see cref="IQuotaStorage"/> behind a circuit breaker would throw
    /// <see cref="UnsupportedQuotaOperationException"/> instead of falling back to the legacy merge API.
    /// </summary>
    private static bool SupportsMerge(IQuotaStorage storage) =>
        storage is IUserMerger && UnwrapStorage(storage) is IUserMerger;

    private static IQuotaStorage UnwrapStorage(IQuotaStorage storage) =>
        storage is CircuitBreakerQuotaStorage { InnerStorage: var inner } ? UnwrapStorage(inner) : storage;

    /// <summary>
    /// Scopes a caller-supplied idempotency key to one user. Each part is percent-encoded before the
    /// join, so <c>("a:b", "c")</c> and <c>("a", "b:c")</c> no longer produce the same scoped key.
    /// </summary>
    public static string ScopedIdempotencyKey(string userId, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        var prefix = $"{Uri.EscapeDataString(userId)}:";
        return key.StartsWith(prefix, StringComparison.Ordinal)
            ? key
            : prefix + Uri.EscapeDataString(key);
    }

    public async Task<DateTimeOffset> GetNowUtcAsync(CancellationToken cancellationToken = default)
    {
        if (_storage is IQuotaTimeSource timeSource)
        {
            try
            {
                return await timeSource.GetNowUtcAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return DateTimeOffset.UtcNow;
            }
        }

        return DateTimeOffset.UtcNow;
    }

    public async Task<QuotaEntitlement?> GetEntitlementAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var now = await GetNowUtcAsync(cancellationToken);
        return await GetEntitlementWithFallbackAsync(userId, now, cancellationToken);
    }

    public async Task<string> GetEffectiveTierAsync(string userId, CancellationToken cancellationToken = default)
    {
        var now = await GetNowUtcAsync(cancellationToken);
        var ent = await GetEntitlementWithFallbackAsync(userId, now, cancellationToken);
        return ResolveTierName(ent, now);
    }

    public async Task<PeriodWindow> GetCurrentCycleAsync(string userId, CancellationToken cancellationToken = default)
    {
        var now = await GetNowUtcAsync(cancellationToken);
        var ent = await GetEntitlementWithFallbackAsync(userId, now, cancellationToken);
        return BuildWindow(PeriodType.Monthly, now, ent);
    }

    public async Task SetEntitlementAsync(QuotaEntitlement entitlement, CancellationToken cancellationToken = default)
    {
        if (!_config.Tiers.TryGetValue(entitlement.Tier, out var tierConfig))
        {
            throw new QuotaConfigValidationException($"Unknown tier '{entitlement.Tier}'.");
        }

        var now = await GetNowUtcAsync(cancellationToken);
        await _storage.SetEntitlementAsync(entitlement, cancellationToken);
        InvalidateEntitlement(entitlement.UserId);

        foreach (var (resource, initialCredits) in tierConfig.InitialForeverCredits)
        {
            if (initialCredits > 0)
            {
                // Per-resource deterministic key (goquota 2dd730a / BUG-1)
                var deterministicKey = $"initial_bonus_{entitlement.UserId}_{resource}";
                await _storage.TopUpForeverCreditsAsync(
                    entitlement.UserId,
                    resource,
                    initialCredits,
                    deterministicKey,
                    TimeSpan.FromDays(3650),
                    now,
                    cancellationToken);
                InvalidateUsageCache(entitlement.UserId, resource, PeriodWindow.Forever);
            }
        }
    }

    /// <summary>
    /// Prorates current monthly usage/limits when a user changes tiers mid-cycle on an anniversary subscription.
    /// Preserves existing Timezone, ExpiresAtUtc, and Promotion (goquota 375fbcf / 599b63f).
    /// </summary>
    public async Task ApplyTierChangeAsync(
        string userId,
        string newTier,
        string resource,
        CancellationToken cancellationToken = default)
    {
        if (!_config.Tiers.TryGetValue(newTier, out var newTierCfg))
        {
            throw new QuotaConfigValidationException($"Unknown tier '{newTier}'.");
        }

        var now = await GetNowUtcAsync(cancellationToken);
        var existing = await _storage.GetEntitlementAsync(userId, cancellationToken);
        var anchor = existing?.SubscriptionStartDateUtc ?? StartOfDayUtc(now);
        var window = BuildWindow(PeriodType.Monthly, now, existing);

        var oldTierName = ResolveTierName(existing, now);
        var oldTierCfg = GetTierConfig(oldTierName);
        var oldLimit = GetConfiguredLimit(oldTierCfg, resource, PeriodType.Monthly);
        var newLimit = GetConfiguredLimit(newTierCfg, resource, PeriodType.Monthly);

        var currentUsage = await _storage.GetUsageAsync(userId, resource, window, oldLimit, cancellationToken);
        var used = currentUsage?.Used ?? 0;

        // Prorated new limit: used + Round(newLimit * remainingFrac) (goquota ApplyTierChange formula)
        var proratedLimit = newLimit;
        if (newLimit > 0 && window.StartUtc is { } start && window.EndUtc is { } end && end > start)
        {
            var totalTicks = (double)(end - start).Ticks;
            var remainingTicks = Math.Max(0.0, (end - now).Ticks);
            var remainingFraction = Math.Clamp(remainingTicks / totalTicks, 0.0, 1.0);
            var proratedNew = (int)Math.Round(newLimit * remainingFraction, MidpointRounding.AwayFromZero);
            proratedLimit = used + proratedNew;
        }

        var updatedEnt = new QuotaEntitlement(
            UserId: userId,
            Tier: newTier,
            SubscriptionStartDateUtc: anchor,
            UpdatedAtUtc: now,
            ExpiresAtUtc: existing?.ExpiresAtUtc,
            Promotion: existing?.Promotion,
            Timezone: existing?.Timezone,
            Sealed: existing?.Sealed ?? false,
            MigratedTo: existing?.MigratedTo,
            SealExpiresAtUtc: existing?.SealExpiresAtUtc);

        await SetEntitlementAsync(updatedEnt, cancellationToken);
        await _storage.ApplyTierChangeAsync(
            new TierChangeRequest(
                UserId: userId,
                Resource: resource,
                OldTier: oldTierName,
                NewTier: newTier,
                Window: window,
                OldLimit: oldLimit,
                NewLimit: proratedLimit,
                CurrentUsed: used,
                DistinctKeys: currentUsage?.DistinctKeys),
            cancellationToken);
        InvalidateUsageCache(userId, resource, window);
    }

    public async Task<QuotaEntitlement> GrantPromotionAsync(
        PromotionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.UserId))
        {
            throw new QuotaConfigValidationException("PromotionRequest.UserId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Tier) || !_config.Tiers.ContainsKey(request.Tier))
        {
            throw new QuotaConfigValidationException($"Promotion tier '{request.Tier}' does not exist in Config.Tiers.");
        }

        if (_storage is not IPromotionStore promoStore || !PromotionsSupported)
        {
            throw new UnsupportedQuotaOperationException("GrantPromotion");
        }

        var now = await GetNowUtcAsync(cancellationToken);
        var expiresAt = request.ExpiresAtUtc
            ?? (request.Duration is { } d && d > TimeSpan.Zero
                ? now.Add(d)
                : throw new QuotaConfigValidationException("Either ExpiresAtUtc or a positive Duration must be provided."));

        if (expiresAt <= now)
        {
            throw new QuotaConfigValidationException("Promotion expiry must be in the future.");
        }

        var existing = await _storage.GetEntitlementAsync(request.UserId, cancellationToken);
        if (existing is null)
        {
            existing = new QuotaEntitlement(
                UserId: request.UserId,
                Tier: _config.DefaultTier,
                SubscriptionStartDateUtc: StartOfDayUtc(now),
                UpdatedAtUtc: now);
            await _storage.SetEntitlementAsync(existing, cancellationToken);
        }

        if (existing.Promotion is { } activePromo &&
            activePromo.IsActiveAt(now) &&
            !string.IsNullOrWhiteSpace(request.IdempotencyKey) &&
            string.Equals(activePromo.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal))
        {
            _config.Metrics?.RecordIdempotencyHit("grant_promotion");
            return existing;
        }

        var promo = new TierPromotion(
            Tier: request.Tier,
            GrantedAtUtc: now,
            ExpiresAtUtc: expiresAt.ToUniversalTime(),
            Source: request.Source,
            Reason: request.Reason,
            IdempotencyKey: request.IdempotencyKey);

        // Write only the promotion overlay without modifying base UpdatedAtUtc (goquota 599b63f)
        await promoStore.SetPromotionAsync(request.UserId, promo, cancellationToken);
        InvalidateEntitlement(request.UserId);
        await WriteAuditAsync(request.UserId, "tier_promotion", "grant_promotion", 1, request.Reason, now, cancellationToken);

        return existing with { Promotion = promo };
    }

    public async Task RevokePromotionAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new QuotaConfigValidationException("UserId is required.");
        }

        if (_storage is not IPromotionStore promoStore || !PromotionsSupported)
        {
            throw new UnsupportedQuotaOperationException("RevokePromotion");
        }

        var now = await GetNowUtcAsync(cancellationToken);
        await promoStore.ClearPromotionAsync(userId, cancellationToken);
        InvalidateEntitlement(userId);
        await WriteAuditAsync(userId, "tier_promotion", "revoke_promotion", 0, "revoke_promotion", now, cancellationToken);
    }

    public async Task<QuotaUsage> GetQuotaAsync(
        string userId,
        string resource,
        PeriodType periodType,
        string? explicitTier = null,
        CancellationToken cancellationToken = default)
    {
        if (periodType == PeriodType.Auto)
        {
            throw new QuotaConfigValidationException("GetQuotaAsync requires a concrete PeriodType (Daily, Monthly, or Forever).");
        }

        var now = await GetNowUtcAsync(cancellationToken);
        var explicitName = ValidateExplicitTier(explicitTier);
        var ent = await GetEntitlementWithFallbackAsync(userId, now, cancellationToken);
        var tierName = explicitName ?? ResolveTierName(ent, now);
        var tierCfg = GetTierConfig(tierName);
        var promo = ent?.ActivePromotion(now);

        var window = BuildWindow(periodType, now, ent);
        var configuredLimit = GetConfiguredLimit(tierCfg, resource, periodType);
        var usage = await GetUsageWithFallbackAsync(userId, resource, window, configuredLimit, tierName, promo, now, cancellationToken);

        if (periodType == PeriodType.Forever &&
            usage.Limit == 0 &&
            usage.Used == 0 &&
            tierCfg.InitialForeverCredits.TryGetValue(resource, out var initialBonus) &&
            initialBonus > 0)
        {
            return usage with { Limit = initialBonus, Tier = tierName, Promotion = promo };
        }

        return usage;
    }

    public async Task<RateLimitStatus> CheckRateLimitAsync(
        string userId,
        string resource,
        string? explicitTier = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var now = await GetNowUtcAsync(cancellationToken);
        var explicitName = ValidateExplicitTier(explicitTier);
        var tierName = explicitName;
        if (tierName is null)
        {
            var ent = await GetEntitlementWithFallbackAsync(userId, now, cancellationToken);
            tierName = ResolveTierName(ent, now);
        }

        var tierCfg = GetTierConfig(tierName);
        if (!tierCfg.RateLimits.TryGetValue(resource, out var rlConfig))
        {
            return new RateLimitStatus(true, int.MaxValue, int.MaxValue, now, TimeSpan.Zero);
        }

        RateLimitStatus status;
        if (rlConfig.Backend == RateLimitBackend.InMemory)
        {
            status = _localRateLimits.Evaluate(userId, resource, rlConfig, now);
        }
        else
        {
            try
            {
                status = await _storage.CheckRateLimitAsync(userId, resource, rlConfig, now, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (_config.FallbackConfig?.FailOpenOnRateLimitError ?? true)
                {
                    _config.Metrics?.RecordFallback("ratelimit_fail_open");
                    status = new RateLimitStatus(true, rlConfig.Rate, rlConfig.Rate, now.Add(rlConfig.Window), TimeSpan.Zero);
                }
                else
                {
                    throw new QuotaStorageUnavailableException($"Rate limit storage failed for '{resource}'.", ex);
                }
            }
        }

        sw.Stop();
        _config.Metrics?.RecordRateLimitCheck(resource, status.Allowed, sw.Elapsed);
        return status;
    }

    public async Task<PeekResult> PeekAsync(
        string userId,
        string resource,
        PeriodType periodType = PeriodType.Daily,
        string? distinctKey = null,
        string? explicitTier = null,
        CancellationToken cancellationToken = default)
    {
        var now = await GetNowUtcAsync(cancellationToken);
        await EnsureNotSealedAsync(userId, now, cancellationToken);

        var explicitName = ValidateExplicitTier(explicitTier);
        var ent = await GetEntitlementWithFallbackAsync(userId, now, cancellationToken);
        var tierName = explicitName ?? ResolveTierName(ent, now);
        var tierCfg = GetTierConfig(tierName);
        var promo = ent?.ActivePromotion(now);

        var resolvedPeriod = periodType == PeriodType.Auto
            ? tierCfg.ConsumptionOrder.FirstOrDefault()
            : periodType;

        var window = BuildWindow(resolvedPeriod, now, ent);
        var configuredLimit = GetConfiguredLimit(tierCfg, resource, resolvedPeriod);

        if (configuredLimit == -1)
        {
            return new PeekResult(true, false, 0, -1, int.MaxValue, resolvedPeriod, window.Key, window.EndUtc);
        }

        var usage = await GetUsageWithFallbackAsync(userId, resource, window, configuredLimit, tierName, promo, now, cancellationToken);
        var alreadySeen = !string.IsNullOrWhiteSpace(distinctKey) &&
                          usage.DistinctKeys.Contains(distinctKey, StringComparer.Ordinal);

        if (alreadySeen)
        {
            return new PeekResult(
                Allowed: true,
                AlreadySeenDistinctKey: true,
                Used: usage.Used,
                Limit: usage.Limit,
                Remaining: usage.Remaining,
                Period: resolvedPeriod,
                PeriodKey: window.Key,
                ResetAtUtc: window.EndUtc);
        }

        var allowed = usage.Limit == -1 || usage.Used < usage.Limit;
        return new PeekResult(
            Allowed: allowed,
            AlreadySeenDistinctKey: false,
            Used: usage.Used,
            Limit: usage.Limit,
            Remaining: usage.Remaining,
            Period: resolvedPeriod,
            PeriodKey: window.Key,
            ResetAtUtc: window.EndUtc);
    }

    public async Task<int> ConsumeAsync(
        string userId,
        string resource,
        int amount,
        PeriodType periodType,
        ConsumeOptions? options = null,
        string? explicitTier = null,
        CancellationToken cancellationToken = default)
    {
        var res = await ConsumeWithResultAsync(userId, resource, amount, periodType, options, explicitTier, cancellationToken);
        if (!res.Allowed && !(options?.DryRun ?? false))
        {
            throw new QuotaExceededException(userId, resource, res.Period, res.NewUsed, res.Limit, res.ResetAtUtc);
        }

        return res.NewUsed;
    }

    public async Task<TryConsumeResult> TryConsumeAsync(
        string userId,
        string resource,
        int amount,
        PeriodType periodType,
        ConsumeOptions? options = null,
        string? explicitTier = null,
        CancellationToken cancellationToken = default)
    {
        if (amount <= 0)
        {
            throw new QuotaConfigValidationException("Consume amount must be positive.");
        }

        if (periodType is not PeriodType.Daily and not PeriodType.Monthly)
        {
            throw new QuotaConfigValidationException("TryConsumeAsync supports Daily and Monthly period types.");
        }

        var res = await ConsumeWithResultAsync(userId, resource, amount, periodType, options, explicitTier, cancellationToken);
        var rem = res.Limit == -1 ? -1 : res.Remaining;
        return new TryConsumeResult(
            Success: res.Allowed,
            Remaining: rem,
            Consumed: res.Allowed ? amount : 0,
            NewUsed: res.NewUsed);
    }

    public async Task<QuotaUsage> GetUsageAfterConsumeAsync(
        string userId,
        string resource,
        int amount,
        PeriodType periodType,
        ConsumeOptions? options = null,
        string? explicitTier = null,
        CancellationToken cancellationToken = default)
    {
        var res = await ConsumeWithResultAsync(userId, resource, amount, periodType, options, explicitTier, cancellationToken);
        if (!res.Allowed && !(options?.DryRun ?? false))
        {
            throw new QuotaExceededException(userId, resource, res.Period, res.NewUsed, res.Limit, res.ResetAtUtc);
        }

        var chargedPeriod = res.Period == PeriodType.Auto ? PeriodType.Daily : res.Period;
        return await GetQuotaAsync(userId, resource, chargedPeriod, explicitTier, cancellationToken);
    }

    public async Task<ConsumeResult> ConsumeWithResultAsync(
        string userId,
        string resource,
        int amount,
        PeriodType periodType,
        ConsumeOptions? options = null,
        string? explicitTier = null,
        CancellationToken cancellationToken = default)
    {
        if (amount <= 0 || amount > MaxConsumeAmount)
        {
            throw new QuotaConfigValidationException(
                $"Consume amount must be between 1 and {MaxConsumeAmount}.");
        }

        var sw = Stopwatch.StartNew();
        options ??= new ConsumeOptions();
        var now = await GetNowUtcAsync(cancellationToken);

        await EnsureNotSealedAsync(userId, now, cancellationToken);

        var explicitName = ValidateExplicitTier(explicitTier);
        var ent = await GetEntitlementWithFallbackAsync(userId, now, cancellationToken);
        if (ent is not null && ent.IsSealedAt(now))
        {
            throw new UserSealedException(userId);
        }

        var tierName = explicitName ?? ResolveTierName(ent, now);
        var tierCfg = GetTierConfig(tierName);
        var activePromo = ent?.ActivePromotion(now);

        // 0. Check idempotency BEFORE PeriodType.Auto cascade so replays return the exact charged period
        // without polling or caching against the first candidate period (goquota 2bbff1c / 9828c99 / 153b331).
        var scopedKey = ScopedIdempotencyKey(userId, options.IdempotencyKey);
        if (!string.IsNullOrWhiteSpace(scopedKey))
        {
            var existingRecord = await _storage.GetConsumptionRecordAsync(scopedKey, now, cancellationToken);
            if (existingRecord is not null)
            {
                _config.Metrics?.RecordIdempotencyHit("consume");
                var replayRem = existingRecord.EffectiveLimit == -1
                    ? int.MaxValue
                    : Math.Max(0, existingRecord.EffectiveLimit - existingRecord.NewUsed);
                var replayPct = existingRecord.EffectiveLimit > 0
                    ? Math.Round(existingRecord.NewUsed * 100.0 / existingRecord.EffectiveLimit, 2)
                    : 0.0;

                return new ConsumeResult(
                    Allowed: existingRecord.Allowed,
                    NewUsed: existingRecord.NewUsed,
                    Limit: existingRecord.EffectiveLimit,
                    Remaining: replayRem,
                    Percentage: replayPct,
                    Period: existingRecord.Window.PeriodType,
                    PeriodKey: existingRecord.Window.Key,
                    ConsumedNewUnit: existingRecord.ConsumedNewUnit,
                    ResetAtUtc: existingRecord.Window.EndUtc,
                    DistinctKeys: existingRecord.DistinctKeys,
                    WasDryRun: false,
                    UsedFallback: false,
                    Replayed: true,
                    EffectiveTier: tierName,
                    Promotion: activePromo);
            }
        }

        // 1. Check Rate Limit before consuming quota
        if (tierCfg.RateLimits.ContainsKey(resource))
        {
            var rlStatus = await CheckRateLimitAsync(userId, resource, tierName, cancellationToken);
            if (!rlStatus.Allowed)
            {
                throw new RateLimitExceededException(userId, resource, rlStatus);
            }
        }

        // 2. Resolve cascading periods
        var periodsToTry = periodType == PeriodType.Auto
            ? tierCfg.ConsumptionOrder
            : [periodType];

        ConsumeResult? firstDenied = null;
        foreach (var candidatePeriod in periodsToTry)
        {
            if (candidatePeriod == PeriodType.Auto)
            {
                continue;
            }

            var configuredLimit = GetConfiguredLimit(tierCfg, resource, candidatePeriod);
            if (periodType == PeriodType.Auto && candidatePeriod != PeriodType.Forever && configuredLimit == 0)
            {
                continue;
            }

            var window = BuildWindow(candidatePeriod, now, ent);
            if (configuredLimit == -1)
            {
                sw.Stop();
                _config.Metrics?.RecordConsumption(resource, tierName, true, amount, sw.Elapsed);
                return new ConsumeResult(
                    Allowed: true,
                    NewUsed: 0,
                    Limit: -1,
                    Remaining: int.MaxValue,
                    Percentage: 0,
                    Period: candidatePeriod,
                    PeriodKey: window.Key,
                    ConsumedNewUnit: false,
                    ResetAtUtc: window.EndUtc,
                    DistinctKeys: [],
                    EffectiveTier: tierName,
                    Promotion: activePromo);
            }

            if (options.DryRun)
            {
                var peek = await PeekAsync(userId, resource, candidatePeriod, options.DistinctKey, tierName, cancellationToken);
                var dryNewUsed = peek.AlreadySeenDistinctKey ? peek.Used : peek.Used + amount;
                var dryAllowed = peek.AlreadySeenDistinctKey || peek.Limit == -1 || dryNewUsed <= peek.Limit;
                var dryRemaining = peek.Limit == -1 ? int.MaxValue : Math.Max(0, peek.Limit - dryNewUsed);
                var dryPct = peek.Limit > 0 ? Math.Round(dryNewUsed * 100.0 / peek.Limit, 2) : 0;
                return new ConsumeResult(
                    Allowed: dryAllowed,
                    NewUsed: dryNewUsed,
                    Limit: peek.Limit,
                    Remaining: dryRemaining,
                    Percentage: dryPct,
                    Period: candidatePeriod,
                    PeriodKey: window.Key,
                    ConsumedNewUnit: !peek.AlreadySeenDistinctKey && dryAllowed,
                    ResetAtUtc: window.EndUtc,
                    DistinctKeys: [],
                    WasDryRun: true,
                    EffectiveTier: tierName,
                    Promotion: activePromo);
            }

            var spec = new StorageConsumeSpec(
                UserId: userId,
                Resource: resource,
                Amount: amount,
                Window: window,
                ConfiguredLimit: configuredLimit,
                IdempotencyKey: options.IdempotencyKey,
                DistinctKey: options.DistinctKey,
                IdempotencyTtl: _config.IdempotencyTtl,
                NowUtc: now,
                EffectiveTier: tierName);

            var (outcome, usedFallback) = await ExecuteConsumeWithFallbackAsync(spec, cancellationToken);
            if (outcome.WasIdempotencyHit)
            {
                _config.Metrics?.RecordIdempotencyHit("consume");
            }

            var chargedPeriod = outcome.WasIdempotencyHit ? outcome.ChargedPeriod : candidatePeriod;
            var chargedWindow = chargedPeriod == candidatePeriod ? window : BuildWindow(chargedPeriod, now, ent);

            var remaining = outcome.EffectiveLimit == -1
                ? int.MaxValue
                : Math.Max(0, outcome.EffectiveLimit - outcome.NewUsed);
            var pct = outcome.EffectiveLimit > 0
                ? Math.Round(outcome.NewUsed * 100.0 / outcome.EffectiveLimit, 2)
                : 0.0;

            var currentResult = new ConsumeResult(
                Allowed: outcome.Allowed,
                NewUsed: outcome.NewUsed,
                Limit: outcome.EffectiveLimit,
                Remaining: remaining,
                Percentage: pct,
                Period: chargedPeriod,
                PeriodKey: chargedWindow.Key,
                ConsumedNewUnit: outcome.ConsumedNewUnit,
                ResetAtUtc: chargedWindow.EndUtc,
                DistinctKeys: outcome.DistinctKeys,
                WasDryRun: false,
                UsedFallback: usedFallback,
                Replayed: outcome.WasIdempotencyHit,
                EffectiveTier: tierName,
                Promotion: activePromo);

            if (outcome.Allowed)
            {
                var previousUsed = outcome.WasIdempotencyHit || !outcome.ConsumedNewUnit
                    ? outcome.NewUsed
                    : Math.Max(0, outcome.NewUsed - amount);
                var updatedUsage = new QuotaUsage(
                    userId,
                    resource,
                    chargedPeriod,
                    chargedWindow.Key,
                    outcome.NewUsed,
                    outcome.EffectiveLimit,
                    chargedWindow.StartUtc,
                    chargedWindow.EndUtc,
                    outcome.DistinctKeys,
                    Tier: tierName,
                    Promotion: activePromo);

                UpdateUsageCache(updatedUsage, now);
                if (!outcome.WasIdempotencyHit && outcome.ConsumedNewUnit)
                {
                    await EvaluateWarningsAsync(updatedUsage, previousUsed, tierName, tierCfg, options.WarningHandler, cancellationToken);
                }

                sw.Stop();
                _config.Metrics?.RecordConsumption(resource, tierName, true, outcome.ConsumedNewUnit ? amount : 0, sw.Elapsed);
                return currentResult;
            }

            // Keep the first denial: it is the pool the caller hit first (e.g. Daily) and carries
            // the limit they care about, instead of the trailing Forever candidate with limit 0.
            firstDenied ??= currentResult;
        }

        sw.Stop();
        _config.Metrics?.RecordConsumption(resource, tierName, false, 0, sw.Elapsed);
        return firstDenied ?? new ConsumeResult(
            Allowed: false,
            NewUsed: 0,
            Limit: 0,
            Remaining: 0,
            Percentage: 100.0,
            Period: periodType == PeriodType.Auto ? PeriodType.Daily : periodType,
            PeriodKey: "",
            ConsumedNewUnit: false,
            ResetAtUtc: null,
            DistinctKeys: [],
            EffectiveTier: tierName,
            Promotion: activePromo);
    }

    public Task RefundAsync(RefundRequest request, CancellationToken cancellationToken = default) =>
        RefundWithResultAsync(request, cancellationToken);

    public async Task<RefundResult> RefundWithResultAsync(
        RefundRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount < 0)
        {
            throw new QuotaConfigValidationException("Refund amount cannot be negative.");
        }

        if (request.Amount == 0)
        {
            return new RefundResult(request.Period, 0, Replayed: false);
        }

        var now = await GetNowUtcAsync(cancellationToken);
        var scopedRefundKey = ScopedIdempotencyKey(request.UserId, request.IdempotencyKey);
        if (!string.IsNullOrWhiteSpace(scopedRefundKey))
        {
            var existingRefund = await _storage.GetRefundRecordAsync(scopedRefundKey, now, cancellationToken);
            if (existingRefund is not null)
            {
                _config.Metrics?.RecordIdempotencyHit("refund");
                return new RefundResult(existingRefund.Window.PeriodType, Amount: 0, Replayed: true);
            }
        }

        var ent = await GetEntitlementWithFallbackAsync(request.UserId, now, cancellationToken);
        var tierName = ResolveTierName(ent, now);
        var tierCfg = GetTierConfig(tierName);

        PeriodWindow window;
        PeriodType targetPeriod;
        if (request.Period == PeriodType.Auto)
        {
            // Resolve PeriodType.Auto from original consumption record (goquota 12d666f / 9828c99)
            window = await ResolveRefundWindowAsync(request, now, cancellationToken);
            targetPeriod = window.PeriodType;
        }
        else
        {
            targetPeriod = request.Period;
            window = BuildWindow(targetPeriod, now, ent);
        }

        var defaultLimit = GetConfiguredLimit(tierCfg, request.Resource, targetPeriod);
        var resolvedRequest = request with { Period = targetPeriod };

        var outcome = await _storage.RefundAsync(resolvedRequest, window, defaultLimit, _config.IdempotencyTtl, now, cancellationToken);
        if (outcome.Replayed)
        {
            _config.Metrics?.RecordIdempotencyHit("refund");
            return new RefundResult(outcome.Period, Amount: 0, Replayed: true);
        }

        InvalidateUsageCache(request.UserId, request.Resource, window);
        return new RefundResult(outcome.Period, outcome.Amount, Replayed: false);
    }

    public Task<RefundResult> RefundFromConsumeAsync(
        RefundFromConsumeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ConsumeResult);
        if (request.ConsumeResult.Period == PeriodType.Auto)
        {
            throw new QuotaConfigValidationException("ConsumeResult.Period cannot be PeriodType.Auto for RefundFromConsume.");
        }

        var refundKey = request.RefundIdempotencyKey;
        if (string.IsNullOrWhiteSpace(refundKey) && !string.IsNullOrWhiteSpace(request.ConsumeIdempotencyKey))
        {
            refundKey = $"{request.ConsumeIdempotencyKey}_refund";
        }

        return RefundWithResultAsync(
            new RefundRequest(
                UserId: request.UserId,
                Resource: request.Resource,
                Amount: request.Amount,
                Period: request.ConsumeResult.Period,
                IdempotencyKey: refundKey,
                RelatedIdempotencyKey: request.ConsumeIdempotencyKey,
                Reason: request.Reason),
            cancellationToken);
    }

    /// <summary>
    /// Updates the user's timezone by reading directly from storage (never the in-memory cache)
    /// so a stale cached free tier cannot overwrite a premium upgrade written by another instance.
    /// </summary>
    public async Task UpdateTimezoneAsync(
        string userId,
        string timezoneId,
        CancellationToken cancellationToken = default)
    {
        _ = ResolveTimeZoneOrThrow(timezoneId);
        var now = await GetNowUtcAsync(cancellationToken);
        var existing = await _storage.GetEntitlementAsync(userId, cancellationToken);
        if (existing is null)
        {
            var created = new QuotaEntitlement(
                UserId: userId,
                Tier: _config.DefaultTier,
                SubscriptionStartDateUtc: StartOfDayUtc(now),
                UpdatedAtUtc: now,
                Timezone: timezoneId.Trim());
            await SetEntitlementAsync(created, cancellationToken);
            return;
        }

        if (string.Equals(existing.Timezone, timezoneId.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        await SetEntitlementAsync(
            existing with { Timezone = timezoneId.Trim(), UpdatedAtUtc = now },
            cancellationToken);
    }

    public void InvalidateEntitlement(string userId)
    {
        _entitlementCache.Remove(userId);
        _usageCache.RemoveGroup(userId);
    }

    public async Task TopUpLimitAsync(
        string userId,
        string resource,
        int amount,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        if (amount <= 0)
        {
            throw new QuotaConfigValidationException("TopUp amount must be positive.");
        }

        RequireIdempotencyKey(idempotencyKey);
        var now = await GetNowUtcAsync(cancellationToken);
        await _storage.TopUpForeverCreditsAsync(userId, resource, amount, idempotencyKey, _config.IdempotencyTtl, now, cancellationToken);
        InvalidateUsageCache(userId, resource, PeriodWindow.Forever);
    }

    public async Task RefundCreditsAsync(
        string userId,
        string resource,
        int amount,
        string reason = "",
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        if (amount <= 0)
        {
            throw new QuotaConfigValidationException("RefundCredits amount must be positive.");
        }

        RequireIdempotencyKey(idempotencyKey);
        var now = await GetNowUtcAsync(cancellationToken);
        await _storage.RefundForeverCreditsAsync(userId, resource, amount, idempotencyKey, _config.IdempotencyTtl, now, cancellationToken);
        InvalidateUsageCache(userId, resource, PeriodWindow.Forever);
    }

    public async Task SetUsageAsync(
        string userId,
        string resource,
        PeriodType periodType,
        int used,
        IReadOnlyList<string>? distinctKeys = null,
        CancellationToken cancellationToken = default)
    {
        var now = await GetNowUtcAsync(cancellationToken);
        var ent = await GetEntitlementWithFallbackAsync(userId, now, cancellationToken);
        var tierName = ResolveTierName(ent, now);
        var tierCfg = GetTierConfig(tierName);
        var window = BuildWindow(periodType, now, ent);
        var limit = GetConfiguredLimit(tierCfg, resource, periodType);

        await _storage.SetUsageAsync(userId, resource, window, used, limit, distinctKeys, cancellationToken);
        InvalidateUsageCache(userId, resource, window);
        await WriteAuditAsync(userId, resource, "admin_set", used, "SetUsage", now, cancellationToken);
    }

    public Task ResetUsageAsync(
        string userId,
        string resource,
        PeriodType periodType,
        CancellationToken cancellationToken = default) =>
        SetUsageAsync(userId, resource, periodType, 0, [], cancellationToken);

    public async Task GrantOneTimeCreditAsync(
        string userId,
        string resource,
        int amount,
        string reason = "admin_grant_credit",
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        RequireIdempotencyKey(idempotencyKey);
        var now = await GetNowUtcAsync(cancellationToken);
        await _storage.TopUpForeverCreditsAsync(userId, resource, amount, idempotencyKey, _config.IdempotencyTtl, now, cancellationToken);
        InvalidateUsageCache(userId, resource, PeriodWindow.Forever);
        await WriteAuditAsync(userId, resource, "admin_grant_credit", amount, reason, now, cancellationToken);
    }

    private static void RequireIdempotencyKey(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new QuotaConfigValidationException(
                "An idempotency key derived from the provider event id is required for credit grants and refunds.");
        }
    }

    public async Task DrainRemainingAsync(
        string userId,
        string resource,
        PeriodType periodType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(resource))
        {
            throw new QuotaConfigValidationException("UserId and Resource are required for DrainRemaining.");
        }

        var now = await GetNowUtcAsync(cancellationToken);
        var ent = await GetEntitlementWithFallbackAsync(userId, now, cancellationToken);
        var tierName = ResolveTierName(ent, now);
        var tierCfg = GetTierConfig(tierName);
        var window = BuildWindow(periodType, now, ent);
        var limit = GetConfiguredLimit(tierCfg, resource, periodType);

        if (_storage is IRemainingDrainer drainer)
        {
            await drainer.DrainRemainingAsync(userId, resource, window, limit, cancellationToken);
        }
        else
        {
            await _storage.DrainRemainingAsync(userId, resource, window, limit, cancellationToken);
        }

        InvalidateUsageCache(userId, resource, window);
        await WriteAuditAsync(userId, resource, "drain_remaining", 0, "DrainRemaining", now, cancellationToken);
    }

    public async Task<MergeUserResult> MergeUserAsync(
        MergeUserRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SourceUserId) ||
            string.IsNullOrWhiteSpace(request.TargetUserId) ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            throw new QuotaConfigValidationException("SourceUserId, TargetUserId, and IdempotencyKey are required.");
        }

        if (string.Equals(request.SourceUserId, request.TargetUserId, StringComparison.Ordinal))
        {
            throw new SameUserMergeException(request.SourceUserId);
        }

        var now = await GetNowUtcAsync(cancellationToken);
        var sourceEnt = await _storage.GetEntitlementAsync(request.SourceUserId, cancellationToken);
        var targetEnt = await _storage.GetEntitlementAsync(request.TargetUserId, cancellationToken);

        var items = new List<MergeItem>(request.Resources.Count * request.Periods.Count);
        foreach (var resource in request.Resources)
        {
            foreach (var period in request.Periods)
            {
                if (period == PeriodType.Auto)
                {
                    throw new QuotaConfigValidationException("MergeUserRequest.Periods cannot contain PeriodType.Auto.");
                }

                var srcWindow = BuildWindow(period, now, sourceEnt);
                var tgtWindow = BuildWindow(period, now, targetEnt);
                items.Add(new MergeItem(resource, period, srcWindow, tgtWindow));
            }
        }

        var sealExpiresAt = request.SealSource
            ? now.Add(request.SealTtl is { } ttl && ttl > TimeSpan.Zero ? ttl : TimeSpan.FromDays(30))
            : (DateTimeOffset?)null;

        MergeUserResult res;
        if (_storage is IUserMerger merger && SupportsMerge(_storage))
        {
            res = await merger.MergeUserAsync(
                new StorageMergeRequest(
                    SourceUserId: request.SourceUserId,
                    TargetUserId: request.TargetUserId,
                    Items: items,
                    IdempotencyKey: request.IdempotencyKey,
                    SealSource: request.SealSource,
                    NowUtc: now,
                    SealExpiresAtUtc: sealExpiresAt),
                cancellationToken);
        }
        else
        {
            res = await _storage.MergeUserAsync(
                request,
                period => BuildWindow(period, now, targetEnt),
                now,
                cancellationToken);
        }

        // Surface un-migrated source promotion instead of dropping it silently (goquota 599b63f)
        if (sourceEnt?.Promotion is { } droppedPromo)
        {
            res = res with { DroppedPromotion = droppedPromo };
        }

        InvalidateEntitlement(request.SourceUserId);
        InvalidateEntitlement(request.TargetUserId);

        if (res.AlreadyMerged)
        {
            _config.Metrics?.RecordIdempotencyHit("merge_user");
        }
        else
        {
            await WriteAuditAsync(request.TargetUserId, "all", "merge_user", 1, $"Merged from {request.SourceUserId}", now, cancellationToken);
        }

        return res;
    }

    /// <summary>
    /// GetMeterQuota: recurring used fills the bar, forever adds only remaining balance to Limit so spent bonus drops off.
    /// </summary>
    public async Task<ResourceQuotaView> GetMeterQuotaAsync(
        string userId,
        string resource,
        string? explicitTier = null,
        CancellationToken cancellationToken = default)
    {
        return await BuildQuotaViewAsync(userId, resource, includeSpentForeverInLimit: false, explicitTier, cancellationToken);
    }

    /// <summary>
    /// GetEffectiveQuota: ledger view where Limit stays at recurring + total granted forever credits.
    /// </summary>
    public async Task<ResourceQuotaView> GetEffectiveQuotaAsync(
        string userId,
        string resource,
        string? explicitTier = null,
        CancellationToken cancellationToken = default)
    {
        return await BuildQuotaViewAsync(userId, resource, includeSpentForeverInLimit: true, explicitTier, cancellationToken);
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetAuditLogsAsync(
        AuditLogFilter filter,
        CancellationToken cancellationToken = default)
    {
        if (_storage is IQuotaAuditLogger auditLogger)
        {
            return await auditLogger.GetAuditLogsAsync(filter, cancellationToken);
        }

        return [];
    }

    private async Task<PeriodWindow> ResolveRefundWindowAsync(
        RefundRequest request,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var searchKey = ScopedIdempotencyKey(request.UserId, request.RelatedIdempotencyKey);
        if (string.IsNullOrWhiteSpace(searchKey) &&
            !string.IsNullOrWhiteSpace(request.IdempotencyKey) &&
            request.IdempotencyKey.EndsWith("_refund", StringComparison.Ordinal))
        {
            var rawConsumeKey = request.IdempotencyKey[..^"_refund".Length];
            searchKey = ScopedIdempotencyKey(request.UserId, rawConsumeKey);
        }

        if (!string.IsNullOrWhiteSpace(searchKey))
        {
            var record = await _storage.GetConsumptionRecordAsync(searchKey, nowUtc, cancellationToken);
            if (record is not null)
            {
                return record.Window;
            }
        }

        throw new QuotaConfigValidationException(
            "Could not resolve original consumption for PeriodType.Auto refund; provide RelatedIdempotencyKey or a concrete PeriodType.");
    }

    private async Task<ResourceQuotaView> BuildQuotaViewAsync(
        string userId,
        string resource,
        bool includeSpentForeverInLimit,
        string? explicitTier,
        CancellationToken cancellationToken)
    {
        var now = await GetNowUtcAsync(cancellationToken);
        var explicitName = ValidateExplicitTier(explicitTier);
        var ent = await GetEntitlementWithFallbackAsync(userId, now, cancellationToken);
        var tierName = explicitName ?? ResolveTierName(ent, now);
        var tierCfg = GetTierConfig(tierName);
        var promo = ent?.ActivePromotion(now);

        var breakdown = new List<PeriodBreakdown>();
        var totalLimit = 0;
        var totalUsed = 0;
        DateTimeOffset? resetAt = null;
        var unlimited = false;

        var order = tierCfg.ConsumptionOrder.ToList();
        if (!order.Contains(PeriodType.Forever))
        {
            order.Add(PeriodType.Forever);
        }

        foreach (var period in order.Distinct())
        {
            if (period == PeriodType.Auto)
            {
                continue;
            }

            var configuredLimit = GetConfiguredLimit(tierCfg, resource, period);
            if (configuredLimit == -1)
            {
                unlimited = true;
                var win = BuildWindow(period, now, ent);
                breakdown.Add(new PeriodBreakdown(period, -1, 0, -1, win.EndUtc, []));
                resetAt ??= win.EndUtc;
                break;
            }

            var window = BuildWindow(period, now, ent);
            var usage = await GetUsageWithFallbackAsync(userId, resource, window, configuredLimit, tierName, promo, now, cancellationToken);

            if (period == PeriodType.Forever)
            {
                if (usage.Limit <= 0 && usage.Used <= 0)
                {
                    continue;
                }

                var balance = Math.Max(0, usage.Limit - usage.Used);
                breakdown.Add(new PeriodBreakdown(PeriodType.Forever, usage.Limit, usage.Used, balance, null, usage.DistinctKeys));

                if (includeSpentForeverInLimit)
                {
                    totalLimit += usage.Limit;
                    totalUsed += usage.Used;
                }
                else
                {
                    totalLimit += balance;
                }
            }
            else
            {
                if (configuredLimit <= 0 && usage.Used <= 0)
                {
                    continue;
                }

                var bal = Math.Max(0, usage.Limit - usage.Used);
                breakdown.Add(new PeriodBreakdown(period, usage.Limit, usage.Used, bal, window.EndUtc, usage.DistinctKeys));
                totalLimit += usage.Limit;
                totalUsed += usage.Used;
                resetAt ??= window.EndUtc;
            }
        }

        if (unlimited)
        {
            return new ResourceQuotaView(resource, -1, 0, -1, resetAt, breakdown, Tier: tierName, Promotion: promo);
        }

        var remaining = Math.Max(0, totalLimit - totalUsed);
        return new ResourceQuotaView(resource, totalLimit, totalUsed, remaining, resetAt, breakdown, Tier: tierName, Promotion: promo);
    }

    private PeriodWindow BuildWindow(PeriodType periodType, DateTimeOffset nowUtc, QuotaEntitlement? ent)
    {
        var tz = _config.PeriodTimeZone;
        if (!string.IsNullOrWhiteSpace(ent?.Timezone))
        {
            try
            {
                tz = TimeZoneInfo.FindSystemTimeZoneById(ent.Timezone);
            }
            catch
            {
                tz = _config.PeriodTimeZone;
            }
        }

        // Without a subscription anchor, use the calendar month. Anchoring on "today" changes the
        // period key every day and resets monthly quotas daily (QM-01).
        var monthlyAnchor = ent?.SubscriptionStartDateUtc;

        return periodType switch
        {
            PeriodType.Daily => PeriodWindow.ForDaily(nowUtc, tz),
            PeriodType.Monthly => PeriodWindow.ForMonthly(nowUtc, monthlyAnchor, tz),
            PeriodType.Forever => PeriodWindow.Forever,
            _ => PeriodWindow.ForDaily(nowUtc, tz)
        };
    }

    private static DateTimeOffset StartOfDayUtc(DateTimeOffset nowUtc)
    {
        var u = nowUtc.ToUniversalTime();
        return new DateTimeOffset(u.Year, u.Month, u.Day, 0, 0, 0, TimeSpan.Zero);
    }

    private string ResolveTierName(QuotaEntitlement? ent, DateTimeOffset nowUtc)
    {
        var candidate = ent?.ResolveEffectiveTier(nowUtc, _config.DefaultTier) ?? _config.DefaultTier;
        return _config.Tiers.ContainsKey(candidate) ? candidate : _config.DefaultTier;
    }

    /// <summary>
    /// Validates a caller-supplied tier override. The override selects quota limits only; the
    /// entitlement still supplies the period anchor, timezone, promotion, and seal state (QM-02).
    /// </summary>
    private string? ValidateExplicitTier(string? explicitTier)
    {
        if (string.IsNullOrWhiteSpace(explicitTier))
        {
            return null;
        }

        if (!_config.Tiers.ContainsKey(explicitTier))
        {
            throw new QuotaConfigValidationException($"Unknown tier '{explicitTier}'.");
        }

        return explicitTier;
    }

    private TierConfig GetTierConfig(string tierName) =>
        _config.Tiers.TryGetValue(tierName, out var cfg)
            ? cfg
            : _config.Tiers[_config.DefaultTier];

    private static int GetConfiguredLimit(TierConfig tier, string resource, PeriodType periodType) =>
        periodType switch
        {
            PeriodType.Daily => tier.DailyQuotas.TryGetValue(resource, out var d) ? d : 0,
            PeriodType.Monthly => tier.MonthlyQuotas.TryGetValue(resource, out var m) ? m : 0,
            PeriodType.Forever => 0,
            _ => 0
        };

    private async Task EnsureNotSealedAsync(string userId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        try
        {
            if (await _storage.IsUserSealedAsync(userId, nowUtc, cancellationToken))
            {
                throw new UserSealedException(userId);
            }
        }
        catch (UserSealedException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_config.FallbackConfig is { Enabled: true })
            {
                return;
            }

            throw new QuotaStorageUnavailableException("Failed to verify sealed state.", ex);
        }
    }

    private async Task<QuotaEntitlement?> GetEntitlementWithFallbackAsync(
        string userId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (_config.CacheConfig is { Enabled: true } &&
            _entitlementCache.TryGet(userId, nowUtc, out var cached, out _))
        {
            return cached with { };
        }

        // Singleflight deduplication on concurrent cache misses (goquota f6109df).
        // The shared fetch is not tied to any single caller's token, otherwise one cancelled
        // request would fail every concurrent waiter; each waiter can still cancel its own await.
        var flightTask = _entitlementFlights.GetOrAdd(
            userId,
            _ => FetchEntitlementCoreAsync(userId, nowUtc, CancellationToken.None));

        try
        {
            var ent = await flightTask.WaitAsync(cancellationToken);
            return ent is not null ? ent with { } : null;
        }
        finally
        {
            if (flightTask.IsCompleted)
            {
                RemoveFlight(_entitlementFlights, userId, flightTask);
            }
        }
    }

    private static void RemoveFlight<T>(
        ConcurrentDictionary<string, Task<T>> flights,
        string key,
        Task<T> completed)
    {
        // Only remove the flight this caller created; a newer flight may already be registered.
        if (flights.TryGetValue(key, out var current) && ReferenceEquals(current, completed))
        {
            flights.TryRemove(key, out _);
        }
    }

    private async Task<QuotaEntitlement?> FetchEntitlementCoreAsync(
        string userId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var ent = await _storage.GetEntitlementAsync(userId, cancellationToken);
            if (ent is not null)
            {
                UpdateEntitlementCache(ent, nowUtc);
            }

            return ent;
        }
        catch (Exception ex)
        {
            if (_config.FallbackConfig is { Enabled: true } fb)
            {
                var maxStale = fb.MaxStaleness ?? TimeSpan.FromMinutes(5);
                if (fb.FallbackToCache &&
                    _entitlementCache.TryGet(userId, nowUtc, out var stale, out _, allowStale: true, maxStaleness: maxStale))
                {
                    _config.Metrics?.RecordFallback("cache_entitlement");
                    return stale;
                }

                if (fb.SecondaryStorage is not null)
                {
                    _config.Metrics?.RecordFallback("secondary_storage_entitlement");
                    return await fb.SecondaryStorage.GetEntitlementAsync(userId, cancellationToken);
                }
            }

            throw new QuotaStorageUnavailableException($"Failed to read entitlement for '{userId}'.", ex);
        }
    }

    private async Task<QuotaUsage> GetUsageWithFallbackAsync(
        string userId,
        string resource,
        PeriodWindow window,
        int defaultLimit,
        string tierName,
        TierPromotion? activePromo,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"{userId}|{resource}|{window.PeriodType}|{window.Key}";
        if (_config.CacheConfig is { Enabled: true } &&
            _usageCache.TryGet(cacheKey, nowUtc, out var cached, out _))
        {
            return NormalizeUsageForTier(cached, window.PeriodType, defaultLimit, tierName, activePromo);
        }

        // Singleflight deduplication on concurrent usage cache misses (goquota f6109df / e43f01e).
        // Shared fetch uses an independent token; cancellation stays per waiter.
        var flightTask = _usageFlights.GetOrAdd(
            cacheKey,
            _ => FetchUsageCoreAsync(userId, resource, window, defaultLimit, cacheKey, nowUtc, CancellationToken.None));

        try
        {
            var usage = await flightTask.WaitAsync(cancellationToken);
            // Clone record via 'with' before mutating tier/limit to prevent race across singleflight waiters (goquota e43f01e / CORE-RACE-1)
            return NormalizeUsageForTier(usage, window.PeriodType, defaultLimit, tierName, activePromo);
        }
        finally
        {
            if (flightTask.IsCompleted)
            {
                RemoveFlight(_usageFlights, cacheKey, flightTask);
            }
        }
    }

    private async Task<QuotaUsage> FetchUsageCoreAsync(
        string userId,
        string resource,
        PeriodWindow window,
        int defaultLimit,
        string cacheKey,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var usage = await _storage.GetUsageAsync(userId, resource, window, defaultLimit, cancellationToken)
                ?? new QuotaUsage(userId, resource, window.PeriodType, window.Key, 0, defaultLimit, window.StartUtc, window.EndUtc, []);
            UpdateUsageCache(usage, nowUtc);
            return usage;
        }
        catch (Exception ex)
        {
            if (_config.FallbackConfig is { Enabled: true } fb)
            {
                var maxStale = fb.MaxStaleness ?? TimeSpan.FromMinutes(5);
                if (fb.FallbackToCache &&
                    _usageCache.TryGet(cacheKey, nowUtc, out var stale, out _, allowStale: true, maxStaleness: maxStale))
                {
                    _config.Metrics?.RecordFallback("cache_usage");
                    return stale;
                }

                if (fb.SecondaryStorage is not null)
                {
                    _config.Metrics?.RecordFallback("secondary_storage_usage");
                    var sec = await fb.SecondaryStorage.GetUsageAsync(userId, resource, window, defaultLimit, cancellationToken);
                    if (sec is not null)
                    {
                        return sec;
                    }
                }
            }

            throw new QuotaStorageUnavailableException($"Failed to read usage for '{userId}' on '{resource}'.", ex);
        }
    }

    private static QuotaUsage NormalizeUsageForTier(
        QuotaUsage usage,
        PeriodType periodType,
        int configuredLimit,
        string tierName,
        TierPromotion? activePromo)
    {
        if (periodType == PeriodType.Forever)
        {
            return usage with { Tier = tierName, Promotion = activePromo };
        }

        // Recalculate limit when the stored usage was recorded under a different tier (upgrade/downgrade via SetEntitlement);
        // preserve stored limit when recorded under the current tier (including prorated limits from ApplyTierChange) or admin SetUsage/DrainRemaining.
        var effectiveLimit = !string.IsNullOrWhiteSpace(usage.Tier) &&
                             !string.Equals(usage.Tier, tierName, StringComparison.OrdinalIgnoreCase)
            ? configuredLimit
            : usage.Limit;

        return usage with
        {
            Limit = effectiveLimit,
            Tier = tierName,
            Promotion = activePromo
        };
    }

    private async Task<(StorageConsumeOutcome Outcome, bool UsedFallback)> ExecuteConsumeWithFallbackAsync(
        StorageConsumeSpec spec,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await _storage.TryConsumeAsync(spec, cancellationToken);
            return (outcome, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_config.FallbackConfig is { Enabled: true } fb)
            {
                if (fb.SecondaryStorage is not null)
                {
                    _config.Metrics?.RecordFallback("secondary_storage_consume");
                    var secOutcome = await fb.SecondaryStorage.TryConsumeAsync(spec, cancellationToken);
                    return (secOutcome, true);
                }

                if (fb.OptimisticAllowance && spec.ConfiguredLimit > 0)
                {
                    var optKey = $"{spec.UserId}|{spec.Resource}|{spec.Window.Key}";
                    var maxOptimistic = Math.Max(1, (int)Math.Floor(spec.ConfiguredLimit * (fb.OptimisticAllowancePercentage / 100.0)));
                    // Atomic check-and-increment under a single lock (goquota 368ac82 / BUG-3)
                    lock (_optimisticGate)
                    {
                        var currentOpt = _optimisticUsage.GetValueOrDefault(optKey, 0);
                        if (currentOpt + spec.Amount <= maxOptimistic)
                        {
                            var nextOpt = currentOpt + spec.Amount;
                            _optimisticUsage[optKey] = nextOpt;
                            _config.Metrics?.RecordFallback("optimistic_allowance");
                            return (new StorageConsumeOutcome(
                                Allowed: true,
                                ConsumedNewUnit: true,
                                WasIdempotencyHit: false,
                                NewUsed: nextOpt,
                                EffectiveLimit: spec.ConfiguredLimit,
                                DistinctKeys: string.IsNullOrWhiteSpace(spec.DistinctKey) ? [] : [spec.DistinctKey],
                                ChargedPeriod: spec.Window.PeriodType), true);
                        }
                    }
                }
            }

            throw new QuotaStorageUnavailableException($"Failed to consume quota for '{spec.UserId}' on '{spec.Resource}'.", ex);
        }
    }

    private async Task EvaluateWarningsAsync(
        QuotaUsage usage,
        int previousUsed,
        string tierName,
        TierConfig tierCfg,
        IQuotaWarningHandler? perRequestHandler,
        CancellationToken cancellationToken)
    {
        if (usage.Limit <= 0)
        {
            return;
        }

        if (!tierCfg.WarningThresholds.TryGetValue(usage.Resource, out var thresholds) || thresholds.Count == 0)
        {
            return;
        }

        foreach (var threshold in thresholds.OrderBy(x => x))
        {
            var boundary = usage.Limit * threshold;
            if (previousUsed < boundary && usage.Used >= boundary)
            {
                _config.Metrics?.RecordWarning(usage.Resource, tierName, threshold);
                if (_config.WarningHandler is not null)
                {
                    await _config.WarningHandler.OnWarningAsync(usage, threshold, cancellationToken);
                }

                if (perRequestHandler is not null && !ReferenceEquals(perRequestHandler, _config.WarningHandler))
                {
                    await perRequestHandler.OnWarningAsync(usage, threshold, cancellationToken);
                }
            }
        }
    }

    private static TimeZoneInfo ResolveTimeZoneOrThrow(string timezoneId)
    {
        if (string.IsNullOrWhiteSpace(timezoneId))
        {
            throw new QuotaConfigValidationException("Timezone cannot be empty.");
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezoneId.Trim());
        }
        catch (Exception ex)
        {
            throw new QuotaConfigValidationException($"Invalid timezone '{timezoneId}': {ex.Message}");
        }
    }

    private void UpdateEntitlementCache(QuotaEntitlement entitlement, DateTimeOffset nowUtc)
    {
        if (_config.CacheConfig is { Enabled: true } cacheCfg)
        {
            var ttl = cacheCfg.EntitlementTtl ?? TimeSpan.FromMinutes(5);
            _entitlementCache.Set(entitlement.UserId, entitlement, nowUtc, ttl);
        }

        // Upgrading/downgrading tiers must evict stale cached usage limits for this user (goquota 179602a / 20e441a).
        // Group-scoped removal keeps this O(entries for one user) instead of scanning the whole cache on every fetch.
        _usageCache.RemoveGroup(entitlement.UserId);
    }

    private void UpdateUsageCache(QuotaUsage usage, DateTimeOffset nowUtc)
    {
        if (_config.CacheConfig is { Enabled: true } cacheCfg)
        {
            var ttl = cacheCfg.UsageTtl ?? TimeSpan.FromSeconds(30);
            var cacheKey = $"{usage.UserId}|{usage.Resource}|{usage.Period}|{usage.PeriodKey}";
            _usageCache.Set(cacheKey, usage, nowUtc, ttl, group: usage.UserId);
        }
    }

    private void InvalidateUsageCache(string userId, string resource, PeriodWindow window)
    {
        var cacheKey = $"{userId}|{resource}|{window.PeriodType}|{window.Key}";
        _usageCache.Remove(cacheKey);
    }

    private async Task WriteAuditAsync(
        string userId,
        string resource,
        string action,
        int amount,
        string reason,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (_storage is IQuotaAuditLogger auditLogger)
        {
            await auditLogger.LogAuditEntryAsync(
                new AuditLogEntry(userId, resource, action, amount, nowUtc, "system", reason),
                cancellationToken);
        }
    }
}
