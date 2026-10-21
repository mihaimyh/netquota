namespace NetQuota.Billing;

public static class BillingPeriods
{
    public const string Annual = "annual";
    public const string Monthly = "monthly";

    /// <summary>
    /// Classifies a subscription interval as annual or monthly, or an empty string when it cannot
    /// be determined (trials, weekly plans, lifetime access, or missing/invalid timestamps).
    /// </summary>
    public static string Derive(DateTimeOffset? purchasedAt, DateTimeOffset? expiresAt)
    {
        if (purchasedAt is not { } start || expiresAt is not { } end || end <= start)
        {
            return string.Empty;
        }

        var days = (end - start).TotalDays;
        return days switch
        {
            >= 180 => Annual,
            >= 20 => Monthly,
            _ => string.Empty
        };
    }
}

/// <summary>
/// Everything a consumer needs to record a billing event after the entitlement was applied.
/// Mirrors goquota's billing.WebhookEvent, including the billing facts added for analytics.
/// </summary>
public sealed record BillingWebhookEvent
{
    public required string UserId { get; init; }
    public string PreviousTier { get; init; } = string.Empty;
    public required string NewTier { get; init; }
    public required string Provider { get; init; }
    public required string EventType { get; init; }

    /// <summary>Provider event id (Stripe <c>evt_...</c>, RevenueCat <c>event.id</c>) for deduplication.</summary>
    public string EventId { get; init; } = string.Empty;
    public DateTimeOffset EventTimestamp { get; init; } = DateTimeOffset.UnixEpoch;

    public string ProductId { get; init; } = string.Empty;
    public string Store { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public long PriceCents { get; init; }
    public string PeriodType { get; init; } = string.Empty;

    public DateTimeOffset? PurchasedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Auto-renew state. <c>true</c> = will not renew, <c>false</c> = renews, <c>null</c> = the event
    /// says nothing about cancellation (consumers must not clear stored state on null).
    /// </summary>
    public bool? CancelAtPeriodEnd { get; init; }
    public string CancelReason { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

public enum BillingWebhookKind
{
    Subscription,
    OneTimePurchase,
    Refund,
    Transfer,
    Ignored
}

public sealed record BillingWebhookResult(
    BillingWebhookKind Kind,
    BillingWebhookEvent? Event = null,
    string? Reason = null)
{
    public static BillingWebhookResult Ignored(string reason) => new(BillingWebhookKind.Ignored, null, reason);
}

/// <summary>Entitlement operations a billing integration needs, independent of the storage backend.</summary>
public interface IBillingEntitlementStore
{
    Task<QuotaEntitlement?> GetEntitlementAsync(string userId, CancellationToken cancellationToken = default);

    Task SetEntitlementAsync(QuotaEntitlement entitlement, CancellationToken cancellationToken = default);

    Task<string> GetEffectiveTierAsync(string userId, CancellationToken cancellationToken = default);

    Task GrantOneTimeCreditAsync(
        string userId,
        string resource,
        int amount,
        string reason,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default);

    Task RefundCreditsAsync(
        string userId,
        string resource,
        int amount,
        string reason,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Adapter over a NetQuota manager, so billing code has no storage dependency.</summary>
public sealed class QuotaManagerBillingStore(QuotaManager manager) : IBillingEntitlementStore
{
    private readonly QuotaManager _manager = manager ?? throw new ArgumentNullException(nameof(manager));

    public Task<QuotaEntitlement?> GetEntitlementAsync(string userId, CancellationToken cancellationToken = default) =>
        _manager.GetEntitlementAsync(userId, cancellationToken);

    public Task SetEntitlementAsync(QuotaEntitlement entitlement, CancellationToken cancellationToken = default) =>
        _manager.SetEntitlementAsync(entitlement, cancellationToken);

    public Task<string> GetEffectiveTierAsync(string userId, CancellationToken cancellationToken = default) =>
        _manager.GetEffectiveTierAsync(userId, cancellationToken);

    public Task GrantOneTimeCreditAsync(
        string userId,
        string resource,
        int amount,
        string reason,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default) =>
        _manager.GrantOneTimeCreditAsync(userId, resource, amount, reason, idempotencyKey, cancellationToken);

    public Task RefundCreditsAsync(
        string userId,
        string resource,
        int amount,
        string reason,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default) =>
        _manager.RefundCreditsAsync(userId, resource, amount, reason, idempotencyKey, cancellationToken);
}

/// <summary>Invoked after an entitlement change (and after transfers), for audit / analytics.</summary>
public delegate Task BillingWebhookCallback(BillingWebhookEvent webhookEvent, CancellationToken cancellationToken);

/// <summary>Persists the provider-customer link needed for checkout, portal and reconciliation.</summary>
public interface IBillingCustomerStore
{
    Task<string?> GetCustomerIdAsync(string provider, string userId, CancellationToken cancellationToken = default);

    Task SaveCustomerIdAsync(string provider, string userId, string customerId, CancellationToken cancellationToken = default);
}

public sealed class InMemoryBillingCustomerStore : IBillingCustomerStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Provider, string UserId), string> _links = new();

    public Task<string?> GetCustomerIdAsync(string provider, string userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_links.TryGetValue((provider, userId), out var id) ? id : null);

    public Task SaveCustomerIdAsync(
        string provider,
        string userId,
        string customerId,
        CancellationToken cancellationToken = default)
    {
        _links[(provider, userId)] = customerId;
        return Task.CompletedTask;
    }
}

public sealed class BillingNotSupportedException(string message) : Exception(message);

public sealed class BillingSignatureException(string message) : Exception(message);

/// <summary>Malformed (empty or oversized) webhook payload; map it to 400/413 at the edge.</summary>
public sealed class BillingPayloadException(string message) : Exception(message);
