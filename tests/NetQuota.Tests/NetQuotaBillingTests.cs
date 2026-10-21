using System.Net;
using System.Text;
using NetQuota;
using NetQuota.Billing;
using NetQuota.Billing.RevenueCat;
using NetQuota.Billing.Stripe;

namespace NetQuota.Tests;

public class NetQuotaBillingTests
{
    // ---------- shared doubles ----------

    private sealed class FakeStore : IBillingEntitlementStore
    {
        private readonly Dictionary<string, QuotaEntitlement> _entitlements = new(StringComparer.Ordinal);

        public List<QuotaEntitlement> Writes { get; } = [];

        public Dictionary<string, QuotaEntitlement> Entitlements => _entitlements;

        public void Seed(QuotaEntitlement entitlement) => _entitlements[entitlement.UserId] = entitlement;

        public Task<QuotaEntitlement?> GetEntitlementAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_entitlements.TryGetValue(userId, out var entitlement) ? entitlement : null);

        public Task SetEntitlementAsync(QuotaEntitlement entitlement, CancellationToken cancellationToken = default)
        {
            _entitlements[entitlement.UserId] = entitlement;
            Writes.Add(entitlement);
            return Task.CompletedTask;
        }

        public Task<string> GetEffectiveTierAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_entitlements.TryGetValue(userId, out var entitlement)
                ? entitlement.ResolveEffectiveTier(DateTimeOffset.UtcNow, "free")
                : "free");

        public List<string> GrantedKeys { get; } = [];

        public List<string> RefundedKeys { get; } = [];

        public Task GrantOneTimeCreditAsync(
            string userId, string resource, int amount, string reason,
            string? idempotencyKey = null, CancellationToken cancellationToken = default)
        {
            GrantedKeys.Add(idempotencyKey ?? string.Empty);
            return Task.CompletedTask;
        }

        public Task RefundCreditsAsync(
            string userId, string resource, int amount, string reason,
            string? idempotencyKey = null, CancellationToken cancellationToken = default)
        {
            RefundedKeys.Add(idempotencyKey ?? string.Empty);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Path, string Body, HttpRequestMessage Request)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri?.PathAndQuery ?? string.Empty, body, request));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string StripeSign(string secret, string body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = Convert.ToHexString(BillingSignatures.HmacSha256(secret, $"{timestamp}.{body}")).ToLowerInvariant();
        return $"t={timestamp},v1={signature}";
    }

    private static StripeOptions StripeOptionsForTests(string secret = "whsec_test") => new()
    {
        WebhookSecret = secret,
        ApiKey = "sk_test",
        DefaultTier = "free",
        PriceToTier = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["price_pro"] = "pro",
            ["price_start"] = "start"
        },
        TierToPriceId = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pro"] = "price_pro"
        },
        TierRanking = ["pro", "start"],
        SignatureToleranceSeconds = 300
    };

    private static RevenueCatOptions RevenueCatOptionsForTests(string secret = "rc_secret") => new()
    {
        WebhookSecret = secret,
        DefaultTier = "free",
        EntitlementToTier = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pro"] = "pro",
            ["premium"] = "premium"
        },
        ApiKey = "rc_api_key"
    };

    // ---------- periods ----------

    [Theory]
    [InlineData(365, BillingPeriods.Annual)]
    [InlineData(30, BillingPeriods.Monthly)]
    [InlineData(7, "")]
    public void DeriveBillingPeriod_ClassifiesByLength(int days, string expected)
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, BillingPeriods.Derive(start, start.AddDays(days)));
        Assert.Equal(string.Empty, BillingPeriods.Derive(null, start.AddDays(days)));
        Assert.Equal(string.Empty, BillingPeriods.Derive(start, start));
    }

    // ---------- rate limiter ----------

    [Fact]
    public void WebhookRateLimiter_LimitsPerClient_IgnoresForwardedForByDefault()
    {
        var limiter = new WebhookRateLimiter(limit: 2, window: TimeSpan.FromMinutes(1));
        var now = DateTimeOffset.UtcNow;

        Assert.True(limiter.TryAcquire("1.1.1.1", now));
        Assert.True(limiter.TryAcquire("1.1.1.1", now));
        Assert.False(limiter.TryAcquire("1.1.1.1", now));
        Assert.True(limiter.TryAcquire("2.2.2.2", now));

        limiter.TrustProxy = true;
        Assert.Equal("9.9.9.9", WebhookRateLimiter.ResolveClientKey("10.0.0.1:5000", "9.9.9.9, 10.0.0.1", trustProxy: true));
        Assert.Equal("10.0.0.1", WebhookRateLimiter.ResolveClientKey("10.0.0.1:5000", "9.9.9.9", trustProxy: false));
    }

    [Fact]
    public void WebhookRateLimiter_SweepsExpiredBucketsAfterWindow()
    {
        var limiter = new WebhookRateLimiter(limit: 1, window: TimeSpan.FromSeconds(1));
        var now = DateTimeOffset.UtcNow;

        Assert.True(limiter.TryAcquire("1.1.1.1", now));
        Assert.False(limiter.TryAcquire("1.1.1.1", now));
        Assert.True(limiter.TryAcquire("1.1.1.1", now.AddSeconds(2)));
        Assert.Equal(0, limiter.Cleanup());
    }

    // ---------- Stripe signature ----------

    [Fact]
    public void StripeSignature_AcceptsValid_RejectsTamperedAndStale()
    {
        const string secret = "whsec_test";
        var body = Encoding.UTF8.GetBytes("""{"id":"evt_1","type":"x"}""");
        var now = DateTimeOffset.UtcNow;
        var timestamp = now.ToUnixTimeSeconds();
        var signature = Convert.ToHexString(
            BillingSignatures.HmacSha256(secret, Encoding.UTF8.GetBytes($"{timestamp}." + Encoding.UTF8.GetString(body))));

        Assert.True(StripeWebhookSignature.IsValid($"t={timestamp},v1={signature}", secret, body, now));
        Assert.False(StripeWebhookSignature.IsValid($"t={timestamp},v1={signature}", "other", body, now));
        Assert.False(StripeWebhookSignature.IsValid(
            $"t={now.AddMinutes(-10).ToUnixTimeSeconds()},v1={signature}", secret, body, now));
        Assert.False(StripeWebhookSignature.IsValid(string.Empty, secret, body, now));
    }

    // ---------- Stripe parsing ----------

    [Fact]
    public void StripeParser_SubscriptionReadsNestedPeriodAndCancellation()
    {
        var body = Encoding.UTF8.GetBytes("""
            {
              "id": "evt_sub", "type": "customer.subscription.updated", "created": 1760000000,
              "data": { "object": {
                "id": "sub_1", "customer": "cus_1", "status": "active", "cancel_at_period_end": true,
                "metadata": { "user_id": "u1" },
                "items": { "data": [ {
                    "price": { "id": "price_pro", "currency": "ron", "unit_amount": 9900 },
                    "current_period_start": 1760000000, "current_period_end": 1762678400
                } ] }
              } }
            }
            """);

        var result = StripeWebhookParser.Parse(body, StripeOptionsForTests());

        Assert.Equal(BillingWebhookKind.Subscription, result.Kind);
        var evt = result.Event!;
        Assert.Equal("u1", evt.UserId);
        Assert.Equal("pro", evt.NewTier);
        Assert.True(evt.CancelAtPeriodEnd);
        Assert.Equal(9900, evt.PriceCents);
        Assert.Equal("RON", evt.Currency);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1762678400), evt.ExpiresAt);
        Assert.Equal("evt_sub", evt.EventId);
    }

    [Fact]
    public void StripeParser_InvoiceReadsParentMetadata_AndDerivesBillingPeriod()
    {
        var body = Encoding.UTF8.GetBytes("""
            {
              "id": "evt_inv", "type": "invoice.paid", "created": 1760000000,
              "data": { "object": {
                "customer": "cus_1", "subscription": "sub_1", "amount_paid": 9900, "currency": "ron",
                "parent": { "subscription_details": { "metadata": { "user_id": "u1" } } },
                "lines": { "data": [ {
                    "price": { "id": "price_pro", "currency": "ron" },
                    "period": { "start": 1760000000, "end": 1762678400 }
                } ] }
              } }
            }
            """);

        var evt = StripeWebhookParser.Parse(body, StripeOptionsForTests()).Event!;

        Assert.Equal("u1", evt.UserId);
        Assert.Equal("pro", evt.NewTier);
        Assert.Equal(9900, evt.PriceCents);
        Assert.Equal(BillingPeriods.Monthly, evt.Metadata["billing_period"]);
        Assert.Null(evt.CancelAtPeriodEnd);
    }

    [Fact]
    public void StripeParser_DeletedSubscriptionFallsBackToDefault_AndRefundsNeedMetadata()
    {
        var deleted = Encoding.UTF8.GetBytes("""
            {
              "id": "evt_del", "type": "customer.subscription.deleted", "created": 1760000000,
              "data": { "object": { "id": "sub_1", "customer": "cus_1", "status": "canceled",
                "metadata": { "user_id": "u1" },
                "items": { "data": [ { "price": { "id": "price_pro" } } ] } } }
            }
            """);

        var deletedEvent = StripeWebhookParser.Parse(deleted, StripeOptionsForTests()).Event!;
        Assert.Equal("free", deletedEvent.NewTier);

        var refundWithoutMetadata = Encoding.UTF8.GetBytes("""
            { "id": "evt_ref", "type": "payment_intent.refunded", "created": 1760000000,
              "data": { "object": { "id": "pi_1", "amount_refunded": 1000, "metadata": {} } } }
            """);

        Assert.Equal(
            BillingWebhookKind.Ignored,
            StripeWebhookParser.Parse(refundWithoutMetadata, StripeOptionsForTests()).Kind);
    }

    // ---------- Stripe checkout ----------

    [Fact]
    public async Task StripeCheckout_PutsMetadataOnPaymentIntent_AndReusesCustomer()
    {
        var store = new FakeStore();
        var customers = new InMemoryBillingCustomerStore();
        var handler = new RecordingHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/v1/customers" => Json("""{"id":"cus_1"}"""),
            "/v1/checkout/sessions" => Json("""{"url":"https://checkout.test/1"}"""),
            _ => Json("{}", HttpStatusCode.NotFound)
        });

        var provider = new StripeBillingProvider(
            StripeOptionsForTests(), store, customers, new HttpClient(handler));

        var url = await provider.CreditPackCheckoutUrlAsync(
            "u1", "price_pack", "credits", 10, "https://ok", "https://cancel");

        Assert.Equal("https://checkout.test/1", url);
        var session = handler.Requests.Single(r => r.Path == "/v1/checkout/sessions");
        var decoded = Uri.UnescapeDataString(session.Body.Replace('+', ' '));
        Assert.Contains("payment_intent_data[metadata][user_id]=u1", decoded, StringComparison.Ordinal);
        Assert.Contains("payment_intent_data[metadata][resource]=credits", decoded, StringComparison.Ordinal);
        Assert.Contains("metadata[credits]=10", decoded, StringComparison.Ordinal);

        var customerRequest = handler.Requests.Single(r => r.Path == "/v1/customers");
        Assert.True(customerRequest.Request.Headers.Contains("Idempotency-Key"));

        // Second checkout must reuse the stored customer instead of creating a duplicate.
        await provider.CheckoutUrlAsync("u1", "pro", "https://ok", "https://cancel");
        Assert.Single(handler.Requests, r => r.Path == "/v1/customers");
    }

    [Fact]
    public async Task StripeDeletedSubscription_ReconcilesOtherActiveSubscription()
    {
        var secret = "whsec_test";
        var options = StripeOptionsForTests(secret);
        var store = new FakeStore();
        store.Seed(new QuotaEntitlement("u1", "pro", DateTimeOffset.UtcNow.AddDays(-20), DateTimeOffset.UtcNow.AddMinutes(-5)));

        var customers = new InMemoryBillingCustomerStore();
        await customers.SaveCustomerIdAsync("stripe", "u1", "cus_1");

        var handler = new RecordingHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/v1/subscriptions" => Json("""
                { "data": [ { "status": "active",
                    "items": { "data": [ { "price": { "id": "price_pro" } } ] },
                    "current_period_start": 1760000000, "current_period_end": 1762678400 } ] }
                """),
            _ => Json("{}", HttpStatusCode.NotFound)
        });

        var provider = new StripeBillingProvider(options, store, customers, new HttpClient(handler));

        var body = Encoding.UTF8.GetBytes("""
            { "id": "evt_del", "type": "customer.subscription.deleted", "created": 1760000100,
              "data": { "object": { "id": "sub_old", "customer": "cus_1", "status": "canceled",
                "metadata": { "user_id": "u1" },
                "items": { "data": [ { "price": { "id": "price_pro" } } ] } } } }
            """);

        await provider.HandleWebhookAsync(body, StripeSign(secret, Encoding.UTF8.GetString(body)));

        Assert.Equal("pro", store.Entitlements["u1"].Tier);
        Assert.Contains(handler.Requests, r => r.Path.StartsWith("/v1/subscriptions", StringComparison.Ordinal));
    }

    // ---------- RevenueCat parser ----------

    [Fact]
    public void RevenueCatParser_PairsPriceWithCurrency_AndReadsBothTimestampFields()
    {
        var body = Encoding.UTF8.GetBytes("""
            { "event": {
                "id": "rc_1", "type": "INITIAL_PURCHASE", "app_user_id": "u1",
                "product_id": "pro_monthly", "store": "APP_STORE",
                "currency": "RON", "price": 9.99, "price_in_purchased_currency": 45.5,
                "event_timestamp_ms": 1760000000000, "purchased_at_ms": 1759990000000,
                "expiration_at_ms": 1762592000000, "period_type": "NORMAL"
            } }
            """);

        var evt = RevenueCatWebhookParser.Parse(body, RevenueCatOptionsForTests()).Event!;

        Assert.Equal(4550, evt.PriceCents);
        Assert.Equal("RON", evt.Currency);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1760000000000), evt.EventTimestamp);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1759990000000), evt.PurchasedAt);
        Assert.False(evt.CancelAtPeriodEnd);
    }

    [Fact]
    public void RevenueCatParser_FallsBackToUsdPrice_WhenLocalAmountMissing()
    {
        var body = Encoding.UTF8.GetBytes("""
            { "event": { "id": "rc_2", "type": "RENEWAL", "app_user_id": "u1",
                "currency": "RON", "price": 9.99, "timestamp_ms": 1760000000000 } }
            """);

        var evt = RevenueCatWebhookParser.Parse(body, RevenueCatOptionsForTests()).Event!;
        Assert.Equal(999, evt.PriceCents);
        Assert.Equal("USD", evt.Currency);
    }

    [Theory]
    [InlineData("CANCELLATION", "CUSTOMER_SUPPORT", null)]
    [InlineData("CANCELLATION", "BILLING_ERROR", null)]
    [InlineData("CANCELLATION", "UNSUBSCRIBE", true)]
    [InlineData("UNCANCELLATION", "", false)]
    [InlineData("EXPIRATION", "", null)]
    public void RevenueCatParser_CancellationStateIsTriState(string type, string reason, bool? expected)
    {
        Assert.Equal(expected, RevenueCatWebhookParser.CancellationState(type, reason));
    }

    [Fact]
    public void RevenueCatParser_ResolvesActiveEntitlementDeterministically()
    {
        var body = Encoding.UTF8.GetBytes("""
            { "event": { "id": "rc_3", "type": "RENEWAL", "app_user_id": "u1",
                         "timestamp_ms": 1760000000000, "entitlement_ids": [] },
              "subscriber": { "entitlements": {
                  "pro": { "is_active": true, "expires_date": "2099-01-01T00:00:00Z" },
                  "premium": { "is_active": true, "expires_date": "2099-01-01T00:00:00Z" } } } }
            """);

        // "premium" < "pro" lexicographically, so the deterministic answer is premium.
        var evt = RevenueCatWebhookParser.Parse(body, RevenueCatOptionsForTests()).Event!;
        Assert.Equal("premium", evt.NewTier);
    }

    [Fact]
    public void RevenueCatParser_NonRenewingPurchaseIsNotASubscriptionChange()
    {
        var body = Encoding.UTF8.GetBytes("""
            { "event": { "id": "rc_4", "type": "NON_RENEWING_PURCHASE", "app_user_id": "u1",
                         "product_id": "credits_10", "timestamp_ms": 1760000000000,
                         "price": 4.99, "currency": "USD" } }
            """);

        var result = RevenueCatWebhookParser.Parse(body, RevenueCatOptionsForTests());
        Assert.Equal(BillingWebhookKind.OneTimePurchase, result.Kind);
    }

    // ---------- RevenueCat provider ----------

    [Fact]
    public async Task RevenueCatNonRenewingPurchase_DoesNotRewriteEntitlement()
    {
        var store = new FakeStore();
        store.Seed(new QuotaEntitlement("u1", "pro", DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddMinutes(-1)));

        BillingWebhookEvent? callbackEvent = null;
        var provider = new RevenueCatBillingProvider(
            RevenueCatOptionsForTests(), store, new HttpClient(),
            (evt, _) => { callbackEvent = evt; return Task.CompletedTask; });

        var body = Encoding.UTF8.GetBytes("""
            { "event": { "id": "rc_5", "type": "NON_RENEWING_PURCHASE", "app_user_id": "u1",
                         "product_id": "credits_10", "timestamp_ms": 1760000000000 } }
            """);

        await provider.HandleWebhookAsync(body, "Bearer rc_secret", null);

        Assert.Empty(store.Writes);
        Assert.NotNull(callbackEvent);
        Assert.Equal("pro", callbackEvent!.NewTier);
        Assert.Equal("pro", callbackEvent.PreviousTier);
    }

    [Fact]
    public async Task RevenueCatTransfer_MovesTier_AndCarriesEventIdOnBothCallbacks()
    {
        var store = new FakeStore();
        store.Seed(new QuotaEntitlement("source", "pro", DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-1)));
        store.Seed(new QuotaEntitlement("target", "free", DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-1)));

        var callbacks = new List<BillingWebhookEvent>();
        var provider = new RevenueCatBillingProvider(
            RevenueCatOptionsForTests(), store, new HttpClient(),
            (evt, _) => { callbacks.Add(evt); return Task.CompletedTask; });

        var transferTimestamp = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
        var body = Encoding.UTF8.GetBytes($$"""
            { "event": {
                "id": "rc_transfer_1", "type": "TRANSFER",
                "transferred_from": ["source", "$RCAnonymousID:abc"],
                "transferred_to": ["target"],
                "event_timestamp_ms": {{transferTimestamp}} } }
            """);

        await provider.HandleWebhookAsync(body, "Bearer rc_secret", null);

        Assert.Equal("pro", store.Entitlements["target"].Tier);
        Assert.Equal("free", store.Entitlements["source"].Tier);
        Assert.Equal(2, callbacks.Count);
        Assert.All(callbacks, evt => Assert.Equal("rc_transfer_1", evt.EventId));
        Assert.Contains(callbacks, evt => evt.Metadata["transfer_role"] == "to" && evt.UserId == "target");
        Assert.Contains(callbacks, evt => evt.Metadata["transfer_role"] == "from" && evt.UserId == "source");
    }

    [Fact]
    public async Task RevenueCatTransfer_IgnoresStaleEvents()
    {
        var store = new FakeStore();
        store.Seed(new QuotaEntitlement(
            "target", "free", DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow));

        var provider = new RevenueCatBillingProvider(RevenueCatOptionsForTests(), store, new HttpClient());
        var body = Encoding.UTF8.GetBytes("""
            { "event": { "id": "rc_transfer_2", "type": "TRANSFER",
                "transferred_from": ["$RCAnonymousID:abc"], "transferred_to": ["target"],
                "event_timestamp_ms": 1700000000000 } }
            """);

        await provider.HandleWebhookAsync(body, "Bearer rc_secret", null);

        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task RevenueCatSync_EscapesTheSubscriberId()
    {
        var store = new FakeStore();
        var handler = new RecordingHandler((_, _) => Json("""
            { "subscriber": { "entitlements": {
                "pro": { "is_active": true, "expires_date": "2099-01-01T00:00:00Z" } } } }
            """));

        var provider = new RevenueCatBillingProvider(RevenueCatOptionsForTests(), store, new HttpClient(handler));
        var tier = await provider.SyncUserAsync("$RCAnonymousID:a/b?c");

        Assert.Equal("pro", tier);
        var path = handler.Requests.Single().Path;
        Assert.Contains("%24RCAnonymousID%3Aa%2Fb%3Fc", path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevenueCatWebhook_RejectsInvalidSecret()
    {
        var provider = new RevenueCatBillingProvider(
            RevenueCatOptionsForTests(), new FakeStore(), new HttpClient());

        var body = Encoding.UTF8.GetBytes("""{ "event": { "id": "x", "type": "RENEWAL", "app_user_id": "u1" } }""");

        await Assert.ThrowsAsync<BillingSignatureException>(() =>
            provider.HandleWebhookAsync(body, "Bearer wrong", null));
    }

    [Fact]
    public async Task RevenueCatWebhook_RejectsEmptySecret()
    {
        var options = RevenueCatOptionsForTests() with { WebhookSecret = string.Empty, AcceptHmacSignature = true };
        var provider = new RevenueCatBillingProvider(options, new FakeStore(), new HttpClient());
        var body = Encoding.UTF8.GetBytes("""{ "event": { "id": "x", "type": "RENEWAL", "app_user_id": "u1" } }""");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.HandleWebhookAsync(body, "Bearer ", null));
    }

    [Fact]
    public async Task RevenueCatUnknownEventType_IsIgnored_AndKeepsTier()
    {
        var store = new FakeStore();
        store.Seed(new QuotaEntitlement(
            "u1", "pro", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddHours(-1)));

        var provider = new RevenueCatBillingProvider(RevenueCatOptionsForTests(), store, new HttpClient());
        var body = Encoding.UTF8.GetBytes("""
            { "event": { "id": "rc_alias", "type": "SUBSCRIBER_ALIAS", "app_user_id": "u1",
                         "event_timestamp_ms": 1760000000000 } }
            """);

        var result = await provider.HandleWebhookAsync(body, "Bearer rc_secret", null);

        Assert.Equal(BillingWebhookKind.Ignored, result.Kind);
        Assert.Empty(store.Writes);
        Assert.Equal("pro", store.Entitlements["u1"].Tier);
    }

    [Fact]
    public async Task StripeUnknownPrice_IsIgnored_AndKeepsTier()
    {
        var secret = "whsec_test";
        var store = new FakeStore();
        store.Seed(new QuotaEntitlement(
            "u1", "pro", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddHours(-1)));

        var provider = new StripeBillingProvider(
            StripeOptionsForTests(secret), store, new InMemoryBillingCustomerStore(), new HttpClient());

        var body = Encoding.UTF8.GetBytes("""
            { "id": "evt_unknown_price", "type": "customer.subscription.updated", "created": 1760000100,
              "data": { "object": { "id": "sub_1", "customer": "cus_1", "status": "active",
                "metadata": { "user_id": "u1" },
                "items": { "data": [ { "price": { "id": "price_renamed" } } ] } } } }
            """);

        var result = await provider.HandleWebhookAsync(body, StripeSign(secret, Encoding.UTF8.GetString(body)));

        Assert.Equal(BillingWebhookKind.Ignored, result.Kind);
        Assert.Empty(store.Writes);
        Assert.Equal("pro", store.Entitlements["u1"].Tier);
    }

    [Fact]
    public async Task StripeCreditPack_DeliveredTwice_GrantsCreditsOnce()
    {
        const string secret = "whsec_test";
        var storage = new InMemoryQuotaStorage();
        var manager = new QuotaManager(storage, new QuotaConfig
        {
            DefaultTier = "free",
            Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["free"] = new TierConfig()
            }
        });

        var provider = new StripeBillingProvider(
            StripeOptionsForTests(secret),
            new QuotaManagerBillingStore(manager),
            new InMemoryBillingCustomerStore(),
            new HttpClient());

        var bodyBytes = Encoding.UTF8.GetBytes("""
            { "id": "evt_pack_1", "type": "payment_intent.succeeded", "created": 1760000000,
              "data": { "object": { "id": "pi_1", "amount_received": 499, "currency": "ron",
                "metadata": { "user_id": "u1", "resource": "credits", "credits": "10" } } } }
            """);

        await provider.HandleWebhookAsync(bodyBytes, StripeSign(secret, Encoding.UTF8.GetString(bodyBytes)));
        await provider.HandleWebhookAsync(bodyBytes, StripeSign(secret, Encoding.UTF8.GetString(bodyBytes)));

        var usage = await manager.GetQuotaAsync("u1", "credits", PeriodType.Forever);
        Assert.Equal(10, usage.Limit);
    }

    [Fact]
    public async Task StripeRefund_UsesEventIdAsIdempotencyKey()
    {
        const string secret = "whsec_test";
        var store = new FakeStore();
        var provider = new StripeBillingProvider(
            StripeOptionsForTests(secret), store, new InMemoryBillingCustomerStore(), new HttpClient());

        var body = Encoding.UTF8.GetBytes("""
            { "id": "evt_refund_1", "type": "payment_intent.refunded", "created": 1760000000,
              "data": { "object": { "id": "pi_1", "amount_refunded": 499, "currency": "ron",
                "metadata": { "user_id": "u1", "resource": "credits", "credits": "10" } } } }
            """);

        await provider.HandleWebhookAsync(body, StripeSign(secret, Encoding.UTF8.GetString(body)));

        Assert.Equal("evt_refund_1", Assert.Single(store.RefundedKeys));
    }

    [Fact]
    public async Task StripeDeletedSubscription_InvokesCallbackOnce()
    {
        const string secret = "whsec_test";
        var store = new FakeStore();
        store.Seed(new QuotaEntitlement(
            "u1", "pro", DateTimeOffset.UtcNow.AddDays(-20), DateTimeOffset.UtcNow.AddDays(-1)));

        var callbacks = new List<BillingWebhookEvent>();
        var provider = new StripeBillingProvider(
            StripeOptionsForTests(secret),
            store,
            new InMemoryBillingCustomerStore(),
            new HttpClient(),
            (evt, _) => { callbacks.Add(evt); return Task.CompletedTask; });

        var created = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeSeconds();
        var body = Encoding.UTF8.GetBytes($$"""
            { "id": "evt_del_cb", "type": "customer.subscription.deleted", "created": {{created}},
              "data": { "object": { "id": "sub_old", "customer": "cus_1", "status": "canceled",
                "metadata": { "user_id": "u1" },
                "items": { "data": [ { "price": { "id": "price_pro" } } ] } } } }
            """);

        await provider.HandleWebhookAsync(body, StripeSign(secret, Encoding.UTF8.GetString(body)));

        var evt = Assert.Single(callbacks);
        Assert.Equal("customer.subscription.deleted", evt.EventType);
        Assert.Equal("free", evt.NewTier);
    }

    [Fact]
    public async Task SameSecondSubscriptionEvents_AreBothApplied()
    {
        const string secret = "whsec_test";
        var options = StripeOptionsForTests(secret) with
        {
            PriceToTier = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["price_pro"] = "pro",
                ["price_premium"] = "premium"
            }
        };

        var store = new FakeStore();
        store.Seed(new QuotaEntitlement(
            "u1", "free", DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddHours(-1)));

        var provider = new StripeBillingProvider(options, store, new InMemoryBillingCustomerStore(), new HttpClient());

        var eventCreated = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeSeconds();
        async Task DeliverAsync(string priceId)
        {
            var body = Encoding.UTF8.GetBytes($$"""
                { "id": "evt_same_second", "type": "customer.subscription.updated", "created": {{eventCreated}},
                  "data": { "object": { "id": "sub_1", "customer": "cus_1", "status": "active",
                    "metadata": { "user_id": "u1" },
                    "items": { "data": [ { "price": { "id": "{{priceId}}" } } ] } } } }
                """);

            await provider.HandleWebhookAsync(body, StripeSign(secret, Encoding.UTF8.GetString(body)));
        }

        await DeliverAsync("price_pro");
        await DeliverAsync("price_premium");

        Assert.Equal("premium", store.Entitlements["u1"].Tier);
    }

    [Fact]
    public async Task WebhookBodyReader_RejectsEmptyAndOversizedPayloads()
    {
        await Assert.ThrowsAsync<BillingPayloadException>(() =>
            WebhookBodyReader.ReadAsync(new MemoryStream()));

        var oversized = new byte[WebhookBodyReader.DefaultMaxBytes + 1];
        await Assert.ThrowsAsync<BillingPayloadException>(() =>
            WebhookBodyReader.ReadAsync(new MemoryStream(oversized)));

        var ok = await WebhookBodyReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes("{}")));
        Assert.Equal(2, ok.Length);
    }
}
