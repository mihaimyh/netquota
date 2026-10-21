namespace NetQuota;

public class QuotaException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class QuotaConfigValidationException(string message)
    : QuotaException(message);

public sealed class QuotaExceededException(
    string userId,
    string resource,
    PeriodType period,
    int used,
    int limit,
    DateTimeOffset? resetAtUtc = null)
    : QuotaException($"Quota exceeded for user '{userId}' on resource '{resource}' ({period}): {used}/{limit}.")
{
    public string UserId { get; } = userId;
    public string Resource { get; } = resource;
    public PeriodType Period { get; } = period;
    public int Used { get; } = used;
    public int Limit { get; } = limit;
    public DateTimeOffset? ResetAtUtc { get; } = resetAtUtc;
}

public sealed class RateLimitExceededException : QuotaException
{
    public RateLimitExceededException(
        string userId,
        string resource,
        RateLimitStatus status)
        : base($"Rate limit exceeded for user '{userId}' on resource '{resource}'. Retry after {status.RetryAfter.TotalSeconds:0}s.")
    {
        UserId = userId;
        Resource = resource;
        Status = status;
    }

    public string UserId { get; }
    public string Resource { get; }
    public RateLimitStatus Status { get; }
    public TimeSpan RetryAfter => Status.RetryAfter;
}

public class QuotaStorageUnavailableException(string message, Exception? innerException = null)
    : QuotaException(message, innerException);

public sealed class CircuitBreakerOpenException()
    : QuotaStorageUnavailableException("Quota storage circuit breaker is open.");

public sealed class UnsupportedQuotaOperationException(string operation)
    : QuotaException($"Storage does not support operation '{operation}'.");

public sealed class SameUserMergeException(string userId)
    : QuotaException($"Cannot merge user '{userId}' into itself.");

public sealed class UserSealedException(string userId)
    : QuotaException($"User '{userId}' has been merged and sealed.");

public interface IQuotaTimeSource
{
    Task<DateTimeOffset> GetNowUtcAsync(CancellationToken cancellationToken = default);
}

public interface IQuotaWarningHandler
{
    Task OnWarningAsync(QuotaUsage usage, double threshold, CancellationToken cancellationToken = default);
}

public interface IQuotaAuditLogger
{
    Task LogAuditEntryAsync(AuditLogEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AuditLogEntry>> GetAuditLogsAsync(AuditLogFilter filter, CancellationToken cancellationToken = default);
}

public interface IPromotionStore
{
    Task SetPromotionAsync(string userId, TierPromotion promotion, CancellationToken cancellationToken = default);
    Task ClearPromotionAsync(string userId, CancellationToken cancellationToken = default);
}

public interface IPromotionCapabilityReporter
{
    bool PromotionsSupported { get; }
}

public interface IRemainingDrainer
{
    Task DrainRemainingAsync(
        string userId,
        string resource,
        PeriodWindow window,
        int defaultLimit,
        CancellationToken cancellationToken = default);
}

public interface IUserMerger
{
    Task<MergeUserResult> MergeUserAsync(
        StorageMergeRequest request,
        CancellationToken cancellationToken = default);
}

public interface IQuotaMetrics
{
    void RecordConsumption(string resource, string tier, bool success, int amount, TimeSpan duration);
    void RecordRateLimitCheck(string resource, bool allowed, TimeSpan duration);
    void RecordWarning(string resource, string tier, double threshold);
    void RecordFallback(string strategy);
    void RecordIdempotencyHit(string operationType);
}

public sealed record StorageConsumeSpec(
    string UserId,
    string Resource,
    int Amount,
    PeriodWindow Window,
    int ConfiguredLimit,
    string? IdempotencyKey,
    string? DistinctKey,
    TimeSpan IdempotencyTtl,
    DateTimeOffset NowUtc,
    string? EffectiveTier = null);

public sealed record StorageConsumeOutcome(
    bool Allowed,
    bool ConsumedNewUnit,
    bool WasIdempotencyHit,
    int NewUsed,
    int EffectiveLimit,
    IReadOnlyList<string> DistinctKeys,
    PeriodType ChargedPeriod = PeriodType.Daily);

public sealed record StorageRefundOutcome(
    PeriodType Period,
    int Amount,
    bool Replayed);

public interface IQuotaStorage
{
    Task<QuotaEntitlement?> GetEntitlementAsync(string userId, CancellationToken cancellationToken = default);
    Task SetEntitlementAsync(QuotaEntitlement entitlement, CancellationToken cancellationToken = default);

    Task<QuotaUsage?> GetUsageAsync(
        string userId,
        string resource,
        PeriodWindow window,
        int defaultLimit,
        CancellationToken cancellationToken = default);

    Task<StorageConsumeOutcome> TryConsumeAsync(
        StorageConsumeSpec spec,
        CancellationToken cancellationToken = default);

    Task ApplyTierChangeAsync(
        TierChangeRequest request,
        CancellationToken cancellationToken = default);

    Task SetUsageAsync(
        string userId,
        string resource,
        PeriodWindow window,
        int used,
        int limit,
        IReadOnlyList<string>? distinctKeys = null,
        CancellationToken cancellationToken = default);

    Task<StorageRefundOutcome> RefundAsync(
        RefundRequest request,
        PeriodWindow window,
        int defaultLimit,
        TimeSpan idempotencyTtl,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task<ConsumptionRecord?> GetConsumptionRecordAsync(
        string scopedIdempotencyKey,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task<RefundRecord?> GetRefundRecordAsync(
        string scopedIdempotencyKey,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task TopUpForeverCreditsAsync(
        string userId,
        string resource,
        int amount,
        string? idempotencyKey,
        TimeSpan idempotencyTtl,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task RefundForeverCreditsAsync(
        string userId,
        string resource,
        int amount,
        string? idempotencyKey,
        TimeSpan idempotencyTtl,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task DrainRemainingAsync(
        string userId,
        string resource,
        PeriodWindow window,
        int defaultLimit,
        CancellationToken cancellationToken = default);

    Task<MergeUserResult> MergeUserAsync(
        MergeUserRequest request,
        Func<PeriodType, PeriodWindow> windowResolver,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task<bool> IsUserSealedAsync(string userId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);

    Task<RateLimitStatus> CheckRateLimitAsync(
        string userId,
        string resource,
        RateLimitConfig config,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
}
