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
| `charge.refunded` / `payment_intent.refunded` | refund the individual refund's share, keyed by refund id (a full refund by payment intent) |
| `checkout.session.completed` | emit `CheckoutCompleted` with the session's metadata and amount — no entitlement write |

Rules that matter:

- **Unmapped price → ignored.** The stored tier is kept; a new/renamed Stripe price never downgrades
  a paying customer.
- **Reconciliation wins on delete.** The provider learns the customer link from webhook metadata
  (`customer`), so a customer created outside checkout (Payment Link, import) still reconciles
  against Stripe and keeps a still-active subscription; without any link it applies `DefaultTier`.
- **Credit grants are idempotent by event id.** Refunds are keyed by the individual refund id
  (full refunds by payment intent) because `amount_refunded` is cumulative and `charge.refunds` is
  not included in webhook payloads. A partial refund without that entry is ignored (fail-safe)
  rather than over-revoking; a refund that rounds to zero credits skips the store but still fires
  the callback. Stripe retries; duplicates must not move money twice.
- **Equal timestamps apply**, strictly older events lose. Stripe emits several events in one second.
- Checkout puts `metadata[user_id]` on the session **and** on `payment_intent_data[metadata]` for
  credit packs, so refunds arrive with the fields needed.
- Checkout creates the customer with an `Idempotency-Key`; a persistence failure surfaces instead of
  silently creating a duplicate on the next attempt.
- Outgoing REST errors throw `BillingProviderException` (a subclass of `HttpRequestException`) with
  `StatusCode`, `ErrorCode`, `ErrorMessage` and `RequestId`.
- Entitlement rewrites go through `BillingEntitlementTransition.Merge`, preserving `Promotion`,
  `Timezone`, `Sealed`, `MigratedTo` and `SealExpiresAtUtc`.

### Hosted checkout for a computed amount

`CheckoutUrlAsync` needs a Price that already exists in Stripe. When the amount is computed at
runtime (a report, a seat, a one-off service), use the inline-price variant: any amount, any
metadata, and it returns both the session id and the redirect URL.

```csharp
var session = await stripe.CreateHostedCheckoutAsync(new StripeCheckoutRequest
{
    UserId = "u1",
    AmountMinor = 4900,                       // bani; the amount is yours to compute
    Currency = "ron",
    ProductName = "Raport due diligence",
    SuccessUrl = "https://app.example/report/ok",
    CancelUrl = "https://app.example/report/cancel",
    Metadata = new Dictionary<string, string> { ["report_id"] = "r_42" },
    IdempotencyKey = "report-r-42"            // replaying the call returns the same session
});

return Results.Redirect(session.Url);          // session.SessionId for reconciliation
```

`StripeBillingProvider.BuildHostedCheckoutForm(request)` is the pure part: it returns the
`v1/checkout/sessions` form body, performs no I/O, and validates before any network call. Empty
`UserId` (or longer than 200 characters), non-positive `AmountMinor`, missing `ProductName`, a mode
other than `payment`/`subscription`, a subscription without a valid `RecurringInterval`
(`day`/`week`/`month`/`year`) or a payment that sets one, a currency that is not three lowercase
letters, metadata beyond Stripe's limits (50 pairs, 40-character keys without `[`/`]`,
500-character values) and an `IdempotencyKey` over 255 characters all throw `ArgumentException`.

**Metadata is mirrored, not inherited.** Stripe does not copy session metadata to the PaymentIntent
or to the Subscription, so every pair is written three times: `metadata[...]` on the session,
`payment_intent_data[metadata][...]` for `mode=payment`, `subscription_data[metadata][...]` for
`mode=subscription`. Without that mirroring, the refund or renewal webhook that describes the money
arrives without the fields needed to act on it. `metadata[user_id]` is always present and always the
request's `UserId`, overwriting a caller-supplied value.

Sessions created this way do not create or reuse a Stripe customer (`client_reference_id` plus the
optional `customer_email` instead), so nothing is written to `IBillingCustomerStore`.

### checkout.session.completed

`checkout.session.completed` for a paid one-off session becomes
`BillingWebhookKind.CheckoutCompleted`:

| Field | Source |
| --- | --- |
| `UserId` | `metadata[user_id]`, falling back to `client_reference_id`; both missing → `Ignored("missing user id")` |
| `EventId` / `EventTimestamp` | the event root's `id` / `created` (dedupe key) |
| `Currency` / `PriceCents` | `currency` (lowercase, unlike subscription events) / `amount_total` |
| `ProductId` / `NewTier` | `metadata[product_id]` (empty when absent) / `metadata[tier]` or `""` |
| `Metadata` | the session's metadata dictionary, verbatim — keys are the caller's, never interpreted |
| `Provider` / `Store` / `EventType` | `stripe` / `stripe` / `checkout.session.completed` |

The parser is pure and writes no entitlement: the caller decides what the metadata buys, under its
own idempotency key (the event id). Deliberately not supported, because guessing would double-grant:

- **`mode=subscription` → `Ignored`.** `customer.subscription.*` / `invoice.paid` already carry the
  tier for that purchase; acting on both would apply the same subscription twice.
- **`payment_status != paid` → `Ignored`.** Delayed payment methods (SEPA, Boleto) complete a session
  before the money lands; granting on `completed` alone hands out the product for a payment that may
  still fail. This variant intentionally does not handle `checkout.session.async_payment_succeeded`.

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
