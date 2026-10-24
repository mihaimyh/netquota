using System.Net;
using System.Text.Json;

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

    /// <summary>
    /// Identifier of the provider object the event describes (Stripe <c>cs_...</c> for
    /// <c>checkout.session.completed</c>; empty when the event carries none). Consumers key their own
    /// receipts on it, so it must not be confused with <see cref="EventId"/>.
    /// </summary>
    public string ProviderObjectId { get; init; } = string.Empty;

    /// <summary>
    /// Stripe <c>PaymentIntent</c> (<c>pi_...</c>) behind the money, when the event carries one. It is
    /// the only key that ties a refund back to the purchase that granted a product.
    /// </summary>
    public string PaymentIntentId { get; init; } = string.Empty;

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

/// <summary>
/// What a webhook event means for the caller. New members are appended, never inserted: consumers
/// may persist the numeric value (<see cref="CheckoutCompleted"/> == 5).
/// </summary>
public enum BillingWebhookKind
{
    Subscription,
    OneTimePurchase,
    Refund,
    Transfer,
    Ignored,

    /// <summary>
    /// Stripe <c>checkout.session.completed</c> for a paid one-off session. It carries the consumer's
    /// metadata and the amount, but <b>no entitlement change</b>: the caller decides what to grant.
    /// Subscription-mode sessions are ignored because <c>customer.subscription.*</c> owns the tier,
    /// and unpaid sessions (delayed payment methods) are ignored too.
    /// </summary>
    CheckoutCompleted,

    /// <summary>
    /// Bani returnați pentru un produs care nu e credit-pack (raport, deblocare, serviciu). Consumerul
    /// decide ce revocă; <see cref="BillingWebhookEvent.PaymentIntentId"/> leagă refund-ul de cumpărare.
    /// </summary>
    PaymentRefunded
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

/// <summary>
/// Non-success HTTP response from a billing provider's REST API. Inherits <see cref="HttpRequestException"/>
/// so callers catching transport failures continue to work, while exposing the structured provider
/// error (<see cref="ErrorCode"/>, <see cref="ErrorMessage"/>, <see cref="RequestId"/>).
/// </summary>
public sealed class BillingProviderException : HttpRequestException
{
    public string Provider { get; }
    public string? ErrorCode { get; }
    public string? ErrorMessage { get; }
    public string? RequestId { get; }
    public string? ResponseBody { get; }

    public BillingProviderException(
        string provider,
        HttpStatusCode statusCode,
        string? errorCode = null,
        string? errorMessage = null,
        string? requestId = null,
        string? responseBody = null,
        Exception? inner = null)
        : base(FormatMessage(provider, statusCode, errorCode, errorMessage, requestId), inner, statusCode)
    {
        Provider = provider;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        RequestId = requestId;
        ResponseBody = responseBody;
    }

    private static string FormatMessage(
        string provider,
        HttpStatusCode statusCode,
        string? code,
        string? message,
        string? requestId)
    {
        var details = (code, message) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{code}: {message}",
            ({ Length: > 0 }, _) => code,
            (_, { Length: > 0 }) => message,
            _ => "HTTP error"
        };

        var reqPart = string.IsNullOrWhiteSpace(requestId) ? string.Empty : $" (request_id={requestId})";
        return $"{provider} API returned {(int)statusCode} ({statusCode}): {details}{reqPart}";
    }
}

/// <summary>
/// Shared entitlement helpers that enforce NetQuota's billing invariants across providers:
/// Invariant #1 (preserve <c>Promotion</c>, <c>Timezone</c>, <c>Sealed</c>, <c>MigratedTo</c>,
/// <c>SealExpiresAtUtc</c> on every rewrite) and timestamp ordering (strictly older events lose;
/// equal timestamps apply).
/// </summary>
public static class BillingEntitlementTransition
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="eventTimestamp"/> is strictly older than the stored
    /// entitlement's <see cref="QuotaEntitlement.UpdatedAtUtc"/>. Equal timestamps are not stale.
    /// </summary>
    public static bool IsStale(QuotaEntitlement? existing, DateTimeOffset eventTimestamp) =>
        existing is not null && eventTimestamp != default && eventTimestamp < existing.UpdatedAtUtc;

    /// <summary>
    /// Builds a new <see cref="QuotaEntitlement"/> while preserving promotion, timezone and seal
    /// metadata from <paramref name="existing"/> (Invariant #1).
    /// </summary>
    public static QuotaEntitlement Merge(
        string userId,
        string tier,
        QuotaEntitlement? existing,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? subscriptionStartDateUtc = null,
        DateTimeOffset? expiresAtUtc = null,
        bool preserveExistingExpiry = true,
        string? timezoneOverride = null) =>
        new(
            UserId: userId,
            Tier: tier,
            SubscriptionStartDateUtc: subscriptionStartDateUtc ?? existing?.SubscriptionStartDateUtc ?? updatedAtUtc,
            UpdatedAtUtc: updatedAtUtc,
            ExpiresAtUtc: expiresAtUtc ?? (preserveExistingExpiry ? existing?.ExpiresAtUtc : null),
            Promotion: existing?.Promotion,
            Timezone: timezoneOverride ?? existing?.Timezone,
            Sealed: existing?.Sealed ?? false,
            MigratedTo: existing?.MigratedTo,
            SealExpiresAtUtc: existing?.SealExpiresAtUtc);
}

internal static class BillingHttpResponse
{
    public static async Task<JsonDocument> ReadJsonOrThrowAsync(
        HttpResponseMessage response,
        string provider,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateException(response, provider, body);
        }

        return JsonDocument.Parse(body);
    }

    private static BillingProviderException CreateException(
        HttpResponseMessage response,
        string provider,
        string? body)
    {
        var requestId = TryGetHeader(response, "Request-Id")
                        ?? TryGetHeader(response, "X-Request-Id");
        string? code = null;
        string? message = null;

        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    // Stripe nests under { "error": { "code", "type", "message" } };
                    // RevenueCat v1/v2 puts { "code"/"type", "message" } at the root.
                    var source = root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                        ? err
                        : root;

                    code = ReadStringOrNumber(source, "code") ?? ReadStringOrNumber(source, "type");
                    message = ReadStringOrNumber(source, "message");
                }
            }
            catch (JsonException)
            {
                // Non-JSON error body (e.g. upstream 502 HTML); keep raw body on the exception.
            }
        }

        return new BillingProviderException(
            provider,
            response.StatusCode,
            code,
            message,
            requestId,
            body);
    }

    private static string? TryGetHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static string? ReadStringOrNumber(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }
}
