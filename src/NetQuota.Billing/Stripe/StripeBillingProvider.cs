using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace NetQuota.Billing.Stripe;

/// <summary>
/// Stripe provider: verifies webhooks, applies entitlement / credit changes and exposes checkout,
/// portal and reconciliation over the Stripe REST API.
/// </summary>
public sealed class StripeBillingProvider
{
    private readonly StripeOptions _options;
    private readonly IBillingEntitlementStore _store;
    private readonly IBillingCustomerStore _customers;
    private readonly HttpClient _http;
    private readonly BillingWebhookCallback? _callback;
    private readonly BillingMetrics? _metrics;
    private readonly TimeProvider _time;

    public StripeBillingProvider(
        StripeOptions options,
        IBillingEntitlementStore store,
        IBillingCustomerStore customers,
        HttpClient httpClient,
        BillingWebhookCallback? callback = null,
        BillingMetrics? metrics = null,
        TimeProvider? timeProvider = null)
    {
        _options = options;
        _options.Validate();
        _store = store;
        _customers = customers;
        _http = httpClient;
        _callback = callback;
        _metrics = metrics;
        _time = timeProvider ?? TimeProvider.System;

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = new Uri(_options.ApiBaseUrl);
        }

        if (!string.IsNullOrWhiteSpace(_options.ApiKey) &&
            _http.DefaultRequestHeaders.Authorization is null)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
    }

    public string Name => "stripe";

    public async Task<BillingWebhookResult> HandleWebhookAsync(
        byte[] body,
        string? signatureHeader,
        CancellationToken cancellationToken = default)
    {
        if (!StripeWebhookSignature.IsValid(
                signatureHeader, _options.WebhookSecret, body, _time.GetUtcNow(), _options.SignatureToleranceSeconds))
        {
            _metrics?.RecordRejected(Name, "invalid_signature");
            throw new BillingSignatureException("Invalid Stripe signature.");
        }

        var result = StripeWebhookParser.Parse(body, _options);
        _metrics?.RecordReceived(Name, result.Event?.EventType ?? "unknown");

        // Provider ids are idempotency keys: Stripe retries any non-2xx (and duplicates happen),
        // so a replay must not grant or refund credits twice.
        if (result.Kind == BillingWebhookKind.OneTimePurchase && result.Event is { } purchase)
        {
            var credits = StripeWebhookParser.ParseInt(purchase.Metadata.GetValueOrDefault("credits")) ?? 0;
            await _store.GrantOneTimeCreditAsync(
                purchase.UserId,
                purchase.Metadata["resource"],
                credits,
                purchase.EventType,
                idempotencyKey: purchase.EventId,
                cancellationToken);
            await InvokeAsync(purchase, cancellationToken);
            return result;
        }

        if (result.Kind == BillingWebhookKind.Refund && result.Event is { } refund)
        {
            var credits = StripeWebhookParser.ParseInt(refund.Metadata.GetValueOrDefault("credits")) ?? 0;
            await _store.RefundCreditsAsync(
                refund.UserId,
                refund.Metadata["resource"],
                credits,
                refund.EventType,
                idempotencyKey: refund.EventId,
                cancellationToken);
            await InvokeAsync(refund, cancellationToken);
            return result;
        }

        if (result.Kind != BillingWebhookKind.Subscription || result.Event is not { } subscription)
        {
            return result;
        }

        // A cancelled subscription must not drop the user if another one is still active
        // (goquota 48c14ee): reconcile against Stripe before downgrading, and use the event's own
        // timestamp so the reconciliation does not swallow the audit callback.
        if (subscription.EventType == "customer.subscription.deleted")
        {
            var customerId = await _customers.GetCustomerIdAsync(Name, subscription.UserId, cancellationToken);
            if (string.IsNullOrWhiteSpace(customerId))
            {
                // Without a customer link there is nothing to reconcile against: honour the event.
                var appliedFallback = await ApplySubscriptionAsync(
                    subscription, _options.DefaultTier, cancellationToken);
                if (appliedFallback is not null)
                {
                    await InvokeAsync(appliedFallback, cancellationToken);
                }

                return result;
            }

            var before = await _store.GetEntitlementAsync(subscription.UserId, cancellationToken);
            var reconciled = await TrySyncTierAsync(subscription.UserId, subscription.EventTimestamp, cancellationToken);
            var targetTier = reconciled ?? _options.DefaultTier;

            if (reconciled is null)
            {
                var applied = await ApplySubscriptionAsync(subscription, targetTier, cancellationToken);
                if (applied is not null)
                {
                    await InvokeAsync(applied, cancellationToken);
                }

                return result;
            }

            _metrics?.RecordApplied(Name, subscription.EventType, targetTier);
            await InvokeAsync(
                subscription with
                {
                    NewTier = targetTier,
                    PreviousTier = before?.Tier ?? _options.DefaultTier
                },
                cancellationToken);
            return result;
        }

        var appliedEvent = await ApplySubscriptionAsync(subscription, subscription.NewTier, cancellationToken);
        if (appliedEvent is not null)
        {
            await InvokeAsync(appliedEvent, cancellationToken);
        }

        return result;
    }

    /// <summary>
    /// Reconciles the user's tier from Stripe. Picks one active subscription deterministically:
    /// the first tier in <see cref="StripeOptions.TierRanking"/> that has an active subscription.
    /// When every active subscription uses an unmapped price the stored tier is kept instead of
    /// downgrading the customer.
    /// </summary>
    public async Task<string> SyncUserAsync(
        string userId,
        DateTimeOffset? updatedAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        var customerId = await _customers.GetCustomerIdAsync(Name, userId, cancellationToken);
        var existing = await _store.GetEntitlementAsync(userId, cancellationToken);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return existing?.Tier ?? _options.DefaultTier;
        }

        using var response = await _http.GetAsync(
            $"v1/subscriptions?customer={Uri.EscapeDataString(customerId)}&status=all&limit=20",
            cancellationToken);
        using var document = await BillingHttpResponse.ReadJsonOrThrowAsync(response, Name, cancellationToken);
        var bestTier = _options.DefaultTier;
        var bestRank = -1;
        var sawActive = false;
        var sawMapped = false;
        DateTimeOffset? start = null;
        DateTimeOffset? end = null;

        if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var subscription in data.EnumerateArray())
            {
                var status = StripeWebhookParser.Text(subscription, "status");
                if (status is not ("active" or "trialing"))
                {
                    continue;
                }

                sawActive = true;
                var priceId = StripeWebhookParser.NestedText(subscription, "items", "data", "0", "price", "id");
                if (priceId is null || !_options.PriceToTier.TryGetValue(priceId, out var tier))
                {
                    continue;
                }

                sawMapped = true;
                var rank = RankOf(tier);
                if (rank <= bestRank)
                {
                    continue;
                }

                bestRank = rank;
                bestTier = tier;
                start = StripeWebhookParser.UnixSeconds(subscription, "current_period_start")
                        ?? StripeWebhookParser.NestedUnixSeconds(subscription, "items", "data", "0", "current_period_start");
                end = StripeWebhookParser.UnixSeconds(subscription, "current_period_end")
                      ?? StripeWebhookParser.NestedUnixSeconds(subscription, "items", "data", "0", "current_period_end");
            }
        }

        if (sawActive && !sawMapped)
        {
            // Unknown prices: fail closed and keep the current entitlement.
            return existing?.Tier ?? _options.DefaultTier;
        }

        if (!sawActive)
        {
            bestTier = _options.DefaultTier;
        }

        var now = updatedAtUtc ?? _time.GetUtcNow();
        await _store.SetEntitlementAsync(
            BillingEntitlementTransition.Merge(
                userId: userId,
                tier: bestTier,
                existing: existing,
                updatedAtUtc: now,
                subscriptionStartDateUtc: start,
                expiresAtUtc: end),
            cancellationToken);

        return bestTier;
    }

    public async Task<string?> TrySyncTierAsync(
        string userId,
        DateTimeOffset? updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await SyncUserAsync(userId, updatedAtUtc, cancellationToken);
        }
        catch (BillingNotSupportedException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            // Transport failure on the delete path must not fail the webhook: fall back to the
            // default tier instead of leaving the subscription entitled forever.
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<string> CheckoutUrlAsync(
        string userId,
        string tier,
        string successUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default)
    {
        if (!_options.TierToPriceId.TryGetValue(tier, out var priceId))
        {
            throw new BillingNotSupportedException($"No Stripe price configured for tier '{tier}'.");
        }

        var customerId = await EnsureCustomerAsync(userId, cancellationToken);
        var form = new Dictionary<string, string>
        {
            ["mode"] = "subscription",
            ["customer"] = customerId,
            ["line_items[0][price]"] = priceId,
            ["line_items[0][quantity]"] = "1",
            ["success_url"] = successUrl,
            ["cancel_url"] = cancelUrl,
            ["client_reference_id"] = userId,
            ["metadata[user_id]"] = userId,
            ["metadata[tier]"] = tier,
            ["subscription_data[metadata][user_id]"] = userId,
            ["subscription_data[metadata][tier]"] = tier
        };

        return await CreateSessionAsync(form, cancellationToken);
    }

    /// <summary>
    /// One-time credit-pack checkout. The metadata must live on the PaymentIntent too, otherwise
    /// <c>payment_intent.refunded</c> arrives without the fields needed to refund credits
    /// (goquota 2bba743).
    /// </summary>
    public async Task<string> CreditPackCheckoutUrlAsync(
        string userId,
        string priceId,
        string resource,
        int credits,
        string successUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default)
    {
        var customerId = await EnsureCustomerAsync(userId, cancellationToken);
        var creditText = credits.ToString(CultureInfo.InvariantCulture);
        var form = new Dictionary<string, string>
        {
            ["mode"] = "payment",
            ["customer"] = customerId,
            ["line_items[0][price]"] = priceId,
            ["line_items[0][quantity]"] = "1",
            ["success_url"] = successUrl,
            ["cancel_url"] = cancelUrl,
            ["client_reference_id"] = userId,
            ["metadata[user_id]"] = userId,
            ["metadata[resource]"] = resource,
            ["metadata[credits]"] = creditText,
            ["metadata[price_id]"] = priceId,
            ["payment_intent_data[metadata][user_id]"] = userId,
            ["payment_intent_data[metadata][resource]"] = resource,
            ["payment_intent_data[metadata][credits]"] = creditText,
            ["payment_intent_data[metadata][price_id]"] = priceId
        };

        return await CreateSessionAsync(form, cancellationToken);
    }

    public async Task<string> PortalUrlAsync(
        string userId,
        string returnUrl,
        CancellationToken cancellationToken = default)
    {
        var customerId = await _customers.GetCustomerIdAsync(Name, userId, cancellationToken)
                         ?? throw new BillingNotSupportedException($"User '{userId}' has no Stripe customer.");

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["customer"] = customerId,
            ["return_url"] = returnUrl
        });

        using var response = await _http.PostAsync("v1/billing_portal/sessions", content, cancellationToken);
        return await ReadUrlAsync(response, cancellationToken);
    }

    private async Task<string> EnsureCustomerAsync(string userId, CancellationToken cancellationToken)
    {
        var existing = await _customers.GetCustomerIdAsync(Name, userId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["metadata[user_id]"] = userId,
            ["description"] = $"NetQuota user {userId}"
        });

        // Idempotency key prevents two concurrent checkouts from creating two customers
        // (goquota 7578e35 fails closed instead of silently continuing).
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/customers") { Content = content };
        request.Headers.Add("Idempotency-Key", $"netquota-customer-{userId}");

        using var response = await _http.SendAsync(request, cancellationToken);
        using var document = await BillingHttpResponse.ReadJsonOrThrowAsync(response, Name, cancellationToken);
        var customerId = StripeWebhookParser.Text(document.RootElement, "id")
                         ?? throw new BillingNotSupportedException("Stripe customer creation returned no id.");

        // If persisting the link fails, surface it: continuing would create a second customer later.
        await _customers.SaveCustomerIdAsync(Name, userId, customerId, cancellationToken);
        return customerId;
    }

    private async Task<string> CreateSessionAsync(
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync("v1/checkout/sessions", content, cancellationToken);
        return await ReadUrlAsync(response, cancellationToken);
    }

    private async Task<string> ReadUrlAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var document = await BillingHttpResponse.ReadJsonOrThrowAsync(response, Name, cancellationToken);
        return StripeWebhookParser.Text(document.RootElement, "url")
               ?? throw new BillingNotSupportedException("Stripe returned no redirect URL.");
    }

    private async Task<BillingWebhookEvent?> ApplySubscriptionAsync(
        BillingWebhookEvent evt,
        string tier,
        CancellationToken cancellationToken)
    {
        var existing = await _store.GetEntitlementAsync(evt.UserId, cancellationToken);

        // Strictly older events lose. Equal timestamps are applied because Stripe emits several
        // events in the same second (`subscription.updated` + `invoice.paid`); replays at the same
        // timestamp rewrite the same values, and money paths are idempotent by event id.
        if (BillingEntitlementTransition.IsStale(existing, evt.EventTimestamp))
        {
            return null;
        }

        var now = evt.EventTimestamp == default ? _time.GetUtcNow() : evt.EventTimestamp;
        var entitlement = BillingEntitlementTransition.Merge(
            userId: evt.UserId,
            tier: tier,
            existing: existing,
            updatedAtUtc: now,
            subscriptionStartDateUtc: evt.PurchasedAt,
            expiresAtUtc: evt.ExpiresAt);

        await _store.SetEntitlementAsync(entitlement, cancellationToken);
        _metrics?.RecordApplied(Name, evt.EventType, tier);

        return evt with
        {
            NewTier = tier,
            PreviousTier = existing?.Tier ?? _options.DefaultTier
        };
    }

    private int RankOf(string tier)
    {
        for (var i = 0; i < _options.TierRanking.Count; i++)
        {
            if (string.Equals(_options.TierRanking[i], tier, StringComparison.OrdinalIgnoreCase))
            {
                return _options.TierRanking.Count - i;
            }
        }

        return 0;
    }

    private Task InvokeAsync(BillingWebhookEvent evt, CancellationToken cancellationToken) =>
        _callback is null ? Task.CompletedTask : _callback(evt, cancellationToken);
}
