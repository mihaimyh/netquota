# Billing

`NetQuota.Billing` turns provider webhooks into entitlement changes. Both providers are
framework-agnostic: pass the raw body plus the signature header from your endpoint.

```csharp
app.MapPost("/webhooks/stripe", async (HttpRequest request, StripeBillingProvider provider) =>
{
    var body = await WebhookBodyReader.ReadAsync(request.Body);          // 256 KB cap, empty rejected
    try
    {
        await provider.HandleWebhookAsync(body, request.Headers["Stripe-Signature"]);
        return Results.Ok();
    }
    catch (BillingSignatureException) { return Results.Unauthorized(); }
    catch (BillingPayloadException)   { return Results.BadRequest(); }
});

app.MapPost("/webhooks/revenuecat", async (HttpRequest request, RevenueCatBillingProvider provider) =>
{
    var body = await WebhookBodyReader.ReadAsync(request.Body);
    await provider.HandleWebhookAsync(
        body,
        request.Headers.Authorization,
        request.Headers["X-RevenueCat-Signature"]);
    return Results.Ok();
})
.RequireRateLimiting("billing-webhook"); // see the rate limiter below
```

## Stripe

```csharp
var stripe = new StripeBillingProvider(
    new StripeOptions
    {
        WebhookSecret = configuration["Stripe:WebhookSecret"]!,
        ApiKey = configuration["Stripe:ApiKey"],
        PriceToTier = new Dictionary<string, string> { ["price_pro"] = "pro" },
        TierToPriceId = new Dictionary<string, string> { ["pro"] = "price_pro" },
        TierRanking = ["pro", "start"],
        DefaultTier = "free"
    },
    new QuotaManagerBillingStore(manager),
    customerStore,                 // IBillingCustomerStore: userId <-> customer id
    httpClient,                    // reused, with base address / auth handled by the provider
    callback: (evt, ct) => audit.RecordAsync(evt, ct));
```

Handled events:

| Event | Effect |
| --- | --- |
| `customer.subscription.created/updated` | set tier from the mapped price, period dates, cancel flag |
| `invoice.paid` / `invoice.payment_succeeded` | set tier + billing period facts |
| `customer.subscription.deleted` | reconcile first; downgrade only when nothing else is active |
| `payment_intent.succeeded` | grant credits (`metadata.user_id/resource/credits`), idempotent by event id |
| `payment_intent.refunded` | refund credits, idempotent by event id |

Rules that matter:

- **Unmapped price → ignored.** The stored tier is kept; a new/renamed Stripe price never downgrades
  a paying customer.
- **Reconciliation wins on delete.** If a customer link exists, the provider queries Stripe and
  keeps a still-active subscription; without a link it applies `DefaultTier`.
- **Event id is the idempotency key** for credit grants/refunds. Stripe retries; duplicates must not
  move money twice.
- **Equal timestamps apply**, strictly older events lose. Stripe emits several events in one second.
- Checkout puts `metadata[user_id]` on the session **and** on `payment_intent_data[metadata]` for
  credit packs, so refunds arrive with the fields needed.
- Checkout creates the customer with an `Idempotency-Key`; a persistence failure surfaces instead of
  silently creating a duplicate on the next attempt.

## RevenueCat

```csharp
var revenueCat = new RevenueCatBillingProvider(
    new RevenueCatOptions
    {
        WebhookSecret = configuration["RevenueCat:WebhookSecret"]!,
        EntitlementToTier = new Dictionary<string, string> { ["pro"] = "pro", ["premium"] = "premium" },
        DefaultTier = "free",
        ApiKey = configuration["RevenueCat:ApiKey"],
        AcceptHmacSignature = false
    },
    new QuotaManagerBillingStore(manager),
    httpClient,
    callback: (evt, ct) => audit.RecordAsync(evt, ct));
```

- Verification: constant-time bearer token, or base64 HMAC-SHA256 of the body when
  `AcceptHmacSignature` is enabled. A blank secret is rejected.
- Only known subscription event types are applied. `SUBSCRIBER_ALIAS`, `TEST` and future types are
  ignored instead of resetting the tier.
- `NON_RENEWING_PURCHASE` never rewrites the entitlement; the callback receives
  `NewTier == PreviousTier` so you can grant credits without a downgrade.
- `TRANSFER` omits `app_user_id`: `transferred_from` / `transferred_to` are used, anonymous aliases
  (`$RCAnonymousID:`) are filtered, and **both** callbacks carry the event id so consumers can
  deduplicate; the source is downgraded only after the destination is entitled.
- `SyncUserAsync` path-escapes the subscriber id (`$RCAnonymousID:a/b?c` → `%24RCAnonymousID%3Aa%2Fb%3Fc`).
- `CANCELLATION` with `CUSTOMER_SUPPORT` / `BILLING_ERROR` leaves `CancelAtPeriodEnd` null (refunds),
  never `true`.

## Webhook rate limiter

```csharp
var limiter = new WebhookRateLimiter(limit: 120, window: TimeSpan.FromMinutes(1))
{
    TrustProxy = false            // keep false unless a proxy overwrites X-Forwarded-For
};

var key = WebhookRateLimiter.ResolveClientKey(
    context.Connection.RemoteIpAddress?.ToString(),
    context.Request.Headers["X-Forwarded-For"],
    limiter.TrustProxy);

if (!limiter.TryAcquire(key))
{
    return Results.StatusCode(StatusCodes.Status429TooManyRequests);
}
```

Expired buckets are swept every 100 requests, not on every call. The limiter is per process; use a
shared store if webhook limits must hold across instances.

## Metrics

`BillingMetrics.Shared` (or your own instance registered once as a singleton) exposes:

- `billing.webhook.received` / `billing.webhook.rejected` / `billing.webhook.applied`
- `billing.transfer.applied`
- `billing.hybrid_billing_users` (counter; exporters may append `_total`)

Tag values never include raw user ids. Register the meter with OpenTelemetry:

```csharp
builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(BillingMetrics.DefaultMeterName));
```

## Wiring checklist

1. `IBillingEntitlementStore` implemented over your `QuotaManager` (`QuotaManagerBillingStore`).
2. `IBillingCustomerStore` persisted (Firestore table, SQL, etc.) with unique constraint on
   `(provider, user_id)`.
3. Webhook endpoints read the raw body **before** any JSON middleware rewrites it.
4. Provider secrets come from configuration/secret manager, validated at startup.
5. `QuotaConfig.DefaultTier` matches both providers' `DefaultTier`.
