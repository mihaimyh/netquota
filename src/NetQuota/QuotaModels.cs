using System.Globalization;

namespace NetQuota;

public enum PeriodType
{
    Daily = 0,
    Monthly = 1,
    Forever = 2,
    Auto = 3,
}

public enum RateLimitAlgorithm
{
    TokenBucket = 0,
    SlidingWindow = 1,
}

public enum RateLimitBackend
{
    InMemory = 0,
    Distributed = 1,
}

public enum CircuitBreakerState
{
    Closed = 0,
    Open = 1,
    HalfOpen = 2,
}

public sealed record RateLimitConfig(
    RateLimitAlgorithm Algorithm,
    int Rate,
    TimeSpan Window,
    int Burst = 0,
    RateLimitBackend Backend = RateLimitBackend.InMemory,
    int GlobalLimit = 0);

public sealed record TierConfig
{
    public IReadOnlyDictionary<string, int> DailyQuotas { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, int> MonthlyQuotas { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, int> InitialForeverCredits { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, RateLimitConfig> RateLimits { get; init; } = new Dictionary<string, RateLimitConfig>(StringComparer.Ordinal);
    public IReadOnlyList<PeriodType> ConsumptionOrder { get; init; } = [PeriodType.Daily, PeriodType.Monthly, PeriodType.Forever];
    public IReadOnlyDictionary<string, IReadOnlyList<double>> WarningThresholds { get; init; } = new Dictionary<string, IReadOnlyList<double>>(StringComparer.Ordinal);
}

public sealed record CacheConfig(
    bool Enabled = false,
    TimeSpan? EntitlementTtl = null,
    TimeSpan? UsageTtl = null,
    int MaxEntitlements = 10_000,
    int MaxUsage = 50_000);

public sealed record CircuitBreakerConfig(
    bool Enabled = false,
    int FailureThreshold = 5,
    TimeSpan? ResetTimeout = null,
    Action<CircuitBreakerState>? OnStateChange = null);

public sealed record FallbackConfig(
    bool Enabled = false,
    bool FallbackToCache = false,
    bool OptimisticAllowance = false,
    double OptimisticAllowancePercentage = 10.0,
    IQuotaStorage? SecondaryStorage = null,
    TimeSpan? MaxStaleness = null,
    bool FailOpenOnRateLimitError = true);

public sealed record QuotaConfig
{
    private IReadOnlyDictionary<string, TierConfig> _tiers =
        new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase);

    public string DefaultTier { get; init; } = "free";

    /// <summary>
    /// Tier lookup is case-insensitive by contract (goquota tier-casing regression). Normalize on every
    /// assignment so dictionaries created by System.Text.Json or by hand cannot reintroduce ordinal lookup.
    /// </summary>
    public IReadOnlyDictionary<string, TierConfig> Tiers
    {
        get => _tiers;
        init
        {
            if (value is Dictionary<string, TierConfig> existing &&
                ReferenceEquals(existing.Comparer, StringComparer.OrdinalIgnoreCase))
            {
                _tiers = existing;
                return;
            }

            var normalized = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var (tierName, tierConfig) in value ?? new Dictionary<string, TierConfig>())
            {
                normalized[tierName] = tierConfig;
            }

            _tiers = normalized;
        }
    }

    public TimeZoneInfo PeriodTimeZone { get; init; } = TimeZoneInfo.Utc;
    public TimeSpan IdempotencyTtl { get; init; } = TimeSpan.FromHours(24);
    public CacheConfig? CacheConfig { get; init; }
    public CircuitBreakerConfig? CircuitBreakerConfig { get; init; }
    public FallbackConfig? FallbackConfig { get; init; }
    public IQuotaWarningHandler? WarningHandler { get; init; }
    public IQuotaMetrics? Metrics { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DefaultTier))
        {
            throw new QuotaConfigValidationException("DefaultTier must not be empty.");
        }

        if (Tiers is null || Tiers.Count == 0)
        {
            throw new QuotaConfigValidationException("At least one tier must be configured.");
        }

        if (!Tiers.ContainsKey(DefaultTier))
        {
            throw new QuotaConfigValidationException($"Default tier '{DefaultTier}' was not found in Tiers.");
        }

        if (IdempotencyTtl <= TimeSpan.Zero)
        {
            throw new QuotaConfigValidationException("IdempotencyTtl must be greater than zero.");
        }

        if (CircuitBreakerConfig is { Enabled: true, FailureThreshold: <= 0 })
        {
            throw new QuotaConfigValidationException("CircuitBreakerConfig.FailureThreshold must be > 0 when enabled.");
        }

        if (CacheConfig is { Enabled: true } cacheCfg && (cacheCfg.MaxEntitlements <= 0 || cacheCfg.MaxUsage <= 0))
        {
            throw new QuotaConfigValidationException("CacheConfig.MaxEntitlements and MaxUsage must be > 0 when enabled.");
        }

        foreach (var (tierName, tier) in Tiers)
        {
            if (string.IsNullOrWhiteSpace(tierName))
            {
                throw new QuotaConfigValidationException("Tier name cannot be empty.");
            }

            foreach (var (res, quota) in tier.DailyQuotas)
            {
                if (quota < -1)
                {
                    throw new QuotaConfigValidationException($"Tier '{tierName}' daily quota for '{res}' cannot be less than -1 (unlimited).");
                }
            }

            foreach (var (res, quota) in tier.MonthlyQuotas)
            {
                if (quota < -1)
                {
                    throw new QuotaConfigValidationException($"Tier '{tierName}' monthly quota for '{res}' cannot be less than -1 (unlimited).");
                }
            }

            foreach (var (res, credits) in tier.InitialForeverCredits)
            {
                if (credits < 0)
                {
                    throw new QuotaConfigValidationException($"Tier '{tierName}' initial forever credits for '{res}' cannot be negative.");
                }
            }

            foreach (var (res, rl) in tier.RateLimits)
            {
                if (rl.Rate <= 0)
                {
                    throw new QuotaConfigValidationException($"Tier '{tierName}' rate limit for '{res}' must have Rate > 0.");
                }

                if (rl.Window <= TimeSpan.Zero)
                {
                    throw new QuotaConfigValidationException($"Tier '{tierName}' rate limit for '{res}' must have Window > 0.");
                }

                if (rl.Algorithm == RateLimitAlgorithm.TokenBucket && rl.Burst < 0)
                {
                    throw new QuotaConfigValidationException($"Tier '{tierName}' token bucket burst for '{res}' cannot be negative.");
                }
            }

            foreach (var period in tier.ConsumptionOrder)
            {
                if (period is PeriodType.Auto)
                {
                    throw new QuotaConfigValidationException($"Tier '{tierName}' ConsumptionOrder cannot contain PeriodType.Auto.");
                }
            }

            foreach (var (res, thresholds) in tier.WarningThresholds)
            {
                foreach (var threshold in thresholds)
                {
                    if (!double.IsFinite(threshold) || threshold is <= 0.0 or > 1.0)
                    {
                        throw new QuotaConfigValidationException($"Tier '{tierName}' warning threshold for '{res}' must be in (0, 1].");
                    }
                }
            }
        }
    }
}

public sealed record TierPromotion(
    string Tier,
    DateTimeOffset GrantedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string Source = "admin",
    string Reason = "",
    string? IdempotencyKey = null)
{
    public bool IsActiveAt(DateTimeOffset nowUtc) => ExpiresAtUtc > nowUtc;
}

public sealed record QuotaEntitlement(
    string UserId,
    string Tier,
    DateTimeOffset SubscriptionStartDateUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ExpiresAtUtc = null,
    TierPromotion? Promotion = null,
    string? Timezone = null,
    bool Sealed = false,
    string? MigratedTo = null,
    DateTimeOffset? SealExpiresAtUtc = null)
{
    public string ResolveEffectiveTier(DateTimeOffset nowUtc, string defaultTier)
    {
        if (Promotion is { } promo && promo.IsActiveAt(nowUtc))
        {
            return promo.Tier;
        }

        if (ExpiresAtUtc is { } exp && exp <= nowUtc)
        {
            return defaultTier;
        }

        return string.IsNullOrWhiteSpace(Tier) ? defaultTier : Tier;
    }

    public TierPromotion? ActivePromotion(DateTimeOffset nowUtc) =>
        Promotion is { } promo && promo.IsActiveAt(nowUtc) ? promo : null;

    public bool IsSealedAt(DateTimeOffset nowUtc) =>
        Sealed && (SealExpiresAtUtc is null || SealExpiresAtUtc > nowUtc);
}

public sealed record PeriodWindow(
    PeriodType PeriodType,
    string Key,
    DateTimeOffset? StartUtc,
    DateTimeOffset? EndUtc)
{
    public static PeriodWindow Forever { get; } = new(PeriodType.Forever, "forever", null, null);

    public static PeriodWindow ForDaily(DateTimeOffset nowUtc, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        var startLocal = new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var endLocal = startLocal.AddDays(1);
        var startUtc = new DateTimeOffset(startLocal, timeZone.GetUtcOffset(startLocal)).ToUniversalTime();
        var endUtc = new DateTimeOffset(endLocal, timeZone.GetUtcOffset(endLocal)).ToUniversalTime();
        var key = startLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new PeriodWindow(PeriodType.Daily, key, startUtc, endUtc);
    }

    public static PeriodWindow ForMonthly(DateTimeOffset nowUtc, DateTimeOffset? anniversaryAnchorUtc, TimeZoneInfo timeZone)
    {
        if (anniversaryAnchorUtc is null)
        {
            var local = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
            var startLocal = new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
            var endLocal = startLocal.AddMonths(1);
            var startUtc = new DateTimeOffset(startLocal, timeZone.GetUtcOffset(startLocal)).ToUniversalTime();
            var endUtc = new DateTimeOffset(endLocal, timeZone.GetUtcOffset(endLocal)).ToUniversalTime();
            var key = startLocal.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            return new PeriodWindow(PeriodType.Monthly, key, startUtc, endUtc);
        }

        var anchor = anniversaryAnchorUtc.Value.ToUniversalTime();
        var anchorStartOfDay = new DateTimeOffset(anchor.Year, anchor.Month, anchor.Day, 0, 0, 0, TimeSpan.Zero);
        var originalDay = anchorStartOfDay.Day;

        if (nowUtc < anchorStartOfDay)
        {
            var endFirst = AddClampedMonthsWithDay(anchorStartOfDay, 1, originalDay);
            return new PeriodWindow(
                PeriodType.Monthly,
                $"{anchorStartOfDay:yyyy-MM-dd}_{endFirst:yyyy-MM-dd}",
                anchorStartOfDay,
                endFirst);
        }

        var monthsDiff = (nowUtc.Year - anchorStartOfDay.Year) * 12 + (nowUtc.Month - anchorStartOfDay.Month);
        var cycleStart = AddClampedMonthsWithDay(anchorStartOfDay, monthsDiff, originalDay);
        while (cycleStart > nowUtc)
        {
            monthsDiff--;
            cycleStart = AddClampedMonthsWithDay(anchorStartOfDay, monthsDiff, originalDay);
        }

        var cycleEnd = AddClampedMonthsWithDay(anchorStartOfDay, monthsDiff + 1, originalDay);
        while (cycleEnd <= nowUtc)
        {
            monthsDiff++;
            cycleStart = cycleEnd;
            cycleEnd = AddClampedMonthsWithDay(anchorStartOfDay, monthsDiff + 1, originalDay);
        }

        return new PeriodWindow(
            PeriodType.Monthly,
            $"{cycleStart:yyyy-MM-dd}_{cycleEnd:yyyy-MM-dd}",
            cycleStart,
            cycleEnd);
    }

    private static DateTimeOffset AddClampedMonthsWithDay(DateTimeOffset anchorUtc, int months, int targetDay)
    {
        var targetMonthDate = new DateTime(anchorUtc.Year, anchorUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(months);
        var daysInTargetMonth = DateTime.DaysInMonth(targetMonthDate.Year, targetMonthDate.Month);
        var clampedDay = Math.Min(targetDay, daysInTargetMonth);
        return new DateTimeOffset(
            targetMonthDate.Year,
            targetMonthDate.Month,
            clampedDay,
            anchorUtc.Hour,
            anchorUtc.Minute,
            anchorUtc.Second,
            anchorUtc.Millisecond,
            TimeSpan.Zero);
    }
}

public sealed record QuotaUsage(
    string UserId,
    string Resource,
    PeriodType Period,
    string PeriodKey,
    int Used,
    int Limit,
    DateTimeOffset? PeriodStartUtc,
    DateTimeOffset? PeriodEndUtc,
    IReadOnlyList<string> DistinctKeys,
    string? Tier = null,
    TierPromotion? Promotion = null)
{
    public int Remaining => Limit == -1 ? int.MaxValue : Math.Max(0, Limit - Used);
}

public sealed record ConsumeOptions(
    string? IdempotencyKey = null,
    string? DistinctKey = null,
    bool DryRun = false,
    IQuotaWarningHandler? WarningHandler = null);

public sealed record ConsumeResult(
    bool Allowed,
    int NewUsed,
    int Limit,
    int Remaining,
    double Percentage,
    PeriodType Period,
    string PeriodKey,
    bool ConsumedNewUnit,
    DateTimeOffset? ResetAtUtc,
    IReadOnlyList<string> DistinctKeys,
    bool WasDryRun = false,
    bool UsedFallback = false,
    bool Replayed = false,
    string? EffectiveTier = null,
    TierPromotion? Promotion = null);

public sealed record TryConsumeResult(
    bool Success,
    int Remaining,
    int Consumed,
    int NewUsed);

public sealed record PeekResult(
    bool Allowed,
    bool AlreadySeenDistinctKey,
    int Used,
    int Limit,
    int Remaining,
    PeriodType Period,
    string PeriodKey,
    DateTimeOffset? ResetAtUtc);

public sealed record RateLimitStatus(
    bool Allowed,
    int Limit,
    int Remaining,
    DateTimeOffset ResetAtUtc,
    TimeSpan RetryAfter);

public sealed record ConsumptionRecord(
    string ConsumptionId,
    string UserId,
    string Resource,
    int Amount,
    PeriodWindow Window,
    int NewUsed,
    int EffectiveLimit,
    IReadOnlyList<string> DistinctKeys,
    DateTimeOffset TimestampUtc,
    bool Allowed = true,
    bool ConsumedNewUnit = true);

public sealed record RefundRecord(
    string RefundId,
    string UserId,
    string Resource,
    int Amount,
    PeriodWindow Window,
    DateTimeOffset TimestampUtc,
    string Reason = "");

public sealed record TierChangeRequest(
    string UserId,
    string Resource,
    string OldTier,
    string NewTier,
    PeriodWindow Window,
    int OldLimit,
    int NewLimit,
    int CurrentUsed,
    IReadOnlyList<string>? DistinctKeys = null);

public sealed record RefundRequest(
    string UserId,
    string Resource,
    int Amount,
    PeriodType Period,
    string? IdempotencyKey = null,
    string? RelatedIdempotencyKey = null,
    string? DistinctKeyToRemove = null,
    string Reason = "");

public sealed record RefundFromConsumeRequest(
    string UserId,
    string Resource,
    int Amount,
    ConsumeResult ConsumeResult,
    string? ConsumeIdempotencyKey = null,
    string? RefundIdempotencyKey = null,
    string Reason = "");

public sealed record RefundResult(
    PeriodType Period,
    int Amount,
    bool Replayed);

public sealed record PromotionRequest(
    string UserId,
    string Tier,
    DateTimeOffset? ExpiresAtUtc = null,
    TimeSpan? Duration = null,
    string Source = "admin",
    string Reason = "",
    string? IdempotencyKey = null);

public sealed record MergeItem(
    string Resource,
    PeriodType PeriodType,
    PeriodWindow SourceWindow,
    PeriodWindow TargetWindow);

public sealed record StorageMergeRequest(
    string SourceUserId,
    string TargetUserId,
    IReadOnlyList<MergeItem> Items,
    string IdempotencyKey,
    bool SealSource,
    DateTimeOffset NowUtc,
    DateTimeOffset? SealExpiresAtUtc = null);

public sealed record MergeUserRequest(
    string SourceUserId,
    string TargetUserId,
    IReadOnlyList<string> Resources,
    IReadOnlyList<PeriodType> Periods,
    string IdempotencyKey,
    bool SealSource = false,
    TimeSpan? SealTtl = null);

public sealed record MergeUserResult(
    string SourceUserId,
    string TargetUserId,
    bool AlreadyMerged,
    IReadOnlyDictionary<string, int> TransferredUsage,
    IReadOnlyDictionary<string, int> TransferredForeverCredits,
    TierPromotion? DroppedPromotion = null);

public sealed record PeriodBreakdown(
    PeriodType Source,
    int Limit,
    int Used,
    int Balance,
    DateTimeOffset? ResetAtUtc,
    IReadOnlyList<string> DistinctKeys);

public sealed record ResourceQuotaView(
    string Resource,
    int Limit,
    int Used,
    int Remaining,
    DateTimeOffset? ResetAtUtc,
    IReadOnlyList<PeriodBreakdown> Breakdown,
    string? Tier = null,
    TierPromotion? Promotion = null);

public sealed record AuditLogEntry(
    string UserId,
    string Resource,
    string Action,
    int Amount,
    DateTimeOffset TimestampUtc,
    string Actor = "system",
    string Reason = "");

public sealed record AuditLogFilter(
    string? UserId = null,
    string? Resource = null,
    DateTimeOffset? StartTimeUtc = null,
    int Limit = 100);
