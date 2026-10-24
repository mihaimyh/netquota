using System.Net;
using System.Text;
using NetQuota.Billing;
using NetQuota.Billing.Stripe;

namespace NetQuota.Tests;

/// <summary>
/// Covers the two generic billing capabilities added next to quota tiers: hosted checkout sessions
/// built from an inline price (<see cref="StripeBillingProvider.CreateHostedCheckoutAsync"/>) and
/// <c>checkout.session.completed</c> parsing (<see cref="StripeWebhookParser"/>).
/// </summary>
public class StripeHostedCheckoutTests
{
    // ---------- doubles ----------

    private sealed class RecordingHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
        : HttpMessageHandler
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

    private static StripeOptions Options() => new()
    {
        WebhookSecret = "whsec_test",
        ApiKey = "sk_test",
        DefaultTier = "free",
        PriceToTier = new Dictionary<string, string>(StringComparer.Ordinal) { ["price_pro"] = "pro" },
        TierToPriceId = new Dictionary<string, string>(StringComparer.Ordinal) { ["pro"] = "price_pro" },
        TierRanking = ["pro"]
    };

    private static StripeBillingProvider Provider(HttpMessageHandler handler) =>
        new(Options(), new NoopStore(), new InMemoryBillingCustomerStore(), new HttpClient(handler));

    private static StripeCheckoutRequest PaymentRequest() => new()
    {
        UserId = "u1",
        AmountMinor = 4900,
        ProductName = "Raport due diligence",
        SuccessUrl = "https://app.test/ok",
        CancelUrl = "https://app.test/cancel"
    };

    private static IReadOnlyDictionary<string, string> Build(StripeCheckoutRequest request) =>
        StripeBillingProvider.BuildHostedCheckoutForm(request);

    private sealed class NoopStore : IBillingEntitlementStore
    {
        public Task<QuotaEntitlement?> GetEntitlementAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<QuotaEntitlement?>(null);

        public Task SetEntitlementAsync(QuotaEntitlement entitlement, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string> GetEffectiveTierAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult("free");

        public Task GrantOneTimeCreditAsync(
            string userId, string resource, int amount, string reason,
            string? idempotencyKey = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RefundCreditsAsync(
            string userId, string resource, int amount, string reason,
            string? idempotencyKey = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    // ---------- checkout.session.completed parsing ----------

    private const string CheckoutCompletedJson = """
        {
          "id": "evt_checkout_1",
          "object": "event",
          "api_version": "2025-03-31.basil",
          "created": 1760000500,
          "livemode": false,
          "type": "checkout.session.completed",
          "data": {
            "object": {
              "id": "cs_test_a1",
              "object": "checkout.session",
              "amount_subtotal": 4900,
              "amount_total": 4900,
              "client_reference_id": "u_client_ref",
              "currency": "ron",
              "customer": "cus_1",
              "customer_email": "buyer@example.test",
              "metadata": {
                "user_id": "u1",
                "product_id": "prod_report",
                "report_id": "r_42"
              },
              "mode": "payment",
              "payment_status": "paid",
              "status": "complete",
              "subscription": null,
              "url": null
            }
          }
        }
        """;

    private static BillingWebhookResult Parse(string json) =>
        StripeWebhookParser.Parse(Encoding.UTF8.GetBytes(json), Options());

    [Fact]
    public void StripeParser_CheckoutCompleted_ReturnsPopulatedEvent()
    {
        var result = Parse(CheckoutCompletedJson);

        Assert.Equal(BillingWebhookKind.CheckoutCompleted, result.Kind);
        var evt = Assert.IsType<BillingWebhookEvent>(result.Event);

        Assert.Equal("u1", evt.UserId);
        Assert.Equal("stripe", evt.Provider);
        Assert.Equal("stripe", evt.Store);
        Assert.Equal("checkout.session.completed", evt.EventType);
        Assert.Equal("evt_checkout_1", evt.EventId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760000500), evt.EventTimestamp);
        Assert.Equal("ron", evt.Currency);
        Assert.Equal(4900, evt.PriceCents);
        Assert.Equal("prod_report", evt.ProductId);
        Assert.Equal(string.Empty, evt.NewTier);
        Assert.Null(evt.CancelAtPeriodEnd);
    }

    [Fact]
    public void StripeParser_CheckoutCompleted_KeepsTheSessionMetadataVerbatim()
    {
        var evt = Parse(CheckoutCompletedJson).Event!;

        Assert.Equal(
            new[] { "product_id", "report_id", "user_id" },
            evt.Metadata.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("r_42", evt.Metadata["report_id"]);
        Assert.Equal("prod_report", evt.Metadata["product_id"]);
        Assert.Equal("u1", evt.Metadata["user_id"]);
    }

    [Fact]
    public void StripeParser_CheckoutCompleted_ReadsTheMetadataTier_WhenPresent()
    {
        var json = CheckoutCompletedJson.Replace(
            "\"report_id\": \"r_42\"",
            "\"report_id\": \"r_42\",\n                \"tier\": \"pro\"",
            StringComparison.Ordinal);

        Assert.Equal("pro", Parse(json).Event!.NewTier);
    }

    [Fact]
    public void StripeParser_CheckoutCompleted_FallsBackToClientReferenceId()
    {
        const string json = """
            {
              "id": "evt_checkout_2", "type": "checkout.session.completed", "created": 1760000500,
              "data": { "object": {
                "id": "cs_test_a2", "mode": "payment", "payment_status": "paid",
                "amount_total": 4900, "currency": "ron",
                "client_reference_id": "u7"
              } }
            }
            """;

        var evt = Parse(json).Event!;

        Assert.Equal("u7", evt.UserId);
        Assert.Empty(evt.Metadata);
        Assert.Equal(string.Empty, evt.ProductId);
    }

    [Fact]
    public void StripeParser_CheckoutCompleted_WithoutUserId_IsIgnored()
    {
        const string json = """
            {
              "id": "evt_checkout_3", "type": "checkout.session.completed", "created": 1760000500,
              "data": { "object": {
                "id": "cs_test_a3", "mode": "payment", "payment_status": "paid",
                "amount_total": 4900, "currency": "ron", "metadata": {}
              } }
            }
            """;

        var result = Parse(json);

        Assert.Equal(BillingWebhookKind.Ignored, result.Kind);
        Assert.Equal("missing user id", result.Reason);
        Assert.Null(result.Event);
    }

    [Fact]
    public void StripeParser_CheckoutSubscriptionMode_IsIgnored()
    {
        // customer.subscription.* / invoice.paid own the entitlement: granting here would double-grant.
        const string json = """
            {
              "id": "evt_checkout_4", "type": "checkout.session.completed", "created": 1760000500,
              "data": { "object": {
                "id": "cs_test_a4", "mode": "subscription", "payment_status": "paid",
                "amount_total": 9900, "currency": "ron",
                "metadata": { "user_id": "u1", "tier": "pro" },
                "subscription": "sub_1"
              } }
            }
            """;

        var result = Parse(json);

        Assert.Equal(BillingWebhookKind.Ignored, result.Kind);
        Assert.Contains("subscription mode", result.Reason, StringComparison.Ordinal);
        Assert.Null(result.Event);
    }

    [Fact]
    public void StripeParser_CheckoutUnpaidSession_IsIgnored()
    {
        // Delayed payment methods complete a session with payment_status=unpaid before the money lands.
        const string json = """
            {
              "id": "evt_checkout_5", "type": "checkout.session.completed", "created": 1760000500,
              "data": { "object": {
                "id": "cs_test_a5", "mode": "payment", "payment_status": "unpaid",
                "amount_total": 4900, "currency": "ron",
                "metadata": { "user_id": "u1" }
              } }
            }
            """;

        var result = Parse(json);

        Assert.Equal(BillingWebhookKind.Ignored, result.Kind);
        Assert.Equal("checkout session not paid", result.Reason);
        Assert.Null(result.Event);
    }

    [Fact]
    public void BillingWebhookKind_ExistingValuesAreStable()
    {
        // Consumers may persist the numeric value: new kinds are appended, never inserted.
        Assert.Equal(0, (int)BillingWebhookKind.Subscription);
        Assert.Equal(1, (int)BillingWebhookKind.OneTimePurchase);
        Assert.Equal(2, (int)BillingWebhookKind.Refund);
        Assert.Equal(3, (int)BillingWebhookKind.Transfer);
        Assert.Equal(4, (int)BillingWebhookKind.Ignored);
        Assert.Equal(5, (int)BillingWebhookKind.CheckoutCompleted);
    }

    [Fact]
    public void StripeParser_ExistingKindsStillParse()
    {
        const string subscription = """
            {
              "id": "evt_sub", "type": "customer.subscription.updated", "created": 1760000000,
              "data": { "object": { "id": "sub_1", "customer": "cus_1", "status": "active",
                "metadata": { "user_id": "u1" },
                "items": { "data": [ { "price": { "id": "price_pro", "currency": "ron", "unit_amount": 9900 } } ] } } }
            }
            """;

        const string creditPack = """
            { "id": "evt_pack", "type": "payment_intent.succeeded", "created": 1760000000,
              "data": { "object": { "id": "pi_1", "amount_received": 499, "currency": "ron",
                "metadata": { "user_id": "u1", "resource": "credits", "credits": "10" } } } }
            """;

        Assert.Equal(BillingWebhookKind.Subscription, Parse(subscription).Kind);
        Assert.Equal(BillingWebhookKind.OneTimePurchase, Parse(creditPack).Kind);
        Assert.Equal(BillingWebhookKind.Ignored, Parse("""{"id":"e","type":"charge.dispute.created"}""").Kind);
    }

    // ---------- hosted checkout form ----------

    [Fact]
    public void BuildHostedCheckoutForm_Payment_MirrorsMetadataOntoThePaymentIntent()
    {
        var request = PaymentRequest() with
        {
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["report_id"] = "r_42" }
        };

        var form = Build(request);

        Assert.Equal("payment", form["mode"]);
        Assert.Equal("https://app.test/ok", form["success_url"]);
        Assert.Equal("https://app.test/cancel", form["cancel_url"]);
        Assert.Equal("u1", form["client_reference_id"]);
        Assert.Equal("1", form["line_items[0][quantity]"]);
        Assert.Equal("ron", form["line_items[0][price_data][currency]"]);
        Assert.Equal("4900", form["line_items[0][price_data][unit_amount]"]);
        Assert.Equal("Raport due diligence", form["line_items[0][price_data][product_data][name]"]);
        Assert.Equal("u1", form["metadata[user_id]"]);
        Assert.Equal("r_42", form["metadata[report_id]"]);

        // Session metadata does NOT copy to the PaymentIntent: mirror every pair explicitly.
        Assert.Equal("u1", form["payment_intent_data[metadata][user_id]"]);
        Assert.Equal("r_42", form["payment_intent_data[metadata][report_id]"]);
        Assert.DoesNotContain(form.Keys, key => key.StartsWith("subscription_data", StringComparison.Ordinal));
        Assert.DoesNotContain("line_items[0][price_data][recurring][interval]", form.Keys);
        Assert.DoesNotContain("customer_email", form.Keys);
    }

    [Fact]
    public void BuildHostedCheckoutForm_Subscription_RequiresIntervalAndMirrorsToSubscriptionData()
    {
        var request = PaymentRequest() with
        {
            Mode = "subscription",
            RecurringInterval = "month",
            Currency = "eur",
            AmountMinor = 1200,
            CustomerEmail = "buyer@example.test",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["plan"] = "pro" }
        };

        var form = Build(request);

        Assert.Equal("subscription", form["mode"]);
        Assert.Equal("month", form["line_items[0][price_data][recurring][interval]"]);
        Assert.Equal("eur", form["line_items[0][price_data][currency]"]);
        Assert.Equal("1200", form["line_items[0][price_data][unit_amount]"]);
        Assert.Equal("buyer@example.test", form["customer_email"]);
        Assert.Equal("u1", form["subscription_data[metadata][user_id]"]);
        Assert.Equal("pro", form["subscription_data[metadata][plan]"]);
        Assert.Equal("u1", form["metadata[user_id]"]);
        Assert.Equal("pro", form["metadata[plan]"]);
        Assert.DoesNotContain(form.Keys, key => key.StartsWith("payment_intent_data", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildHostedCheckoutForm_UserReplacesAnyUserIdMetadataFromTheCaller()
    {
        // A caller must not be able to redirect the entitlement to another user.
        var request = PaymentRequest() with
        {
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["user_id"] = "someone-else" }
        };

        var form = Build(request);

        Assert.Equal("u1", form["metadata[user_id]"]);
        Assert.Equal("u1", form["payment_intent_data[metadata][user_id]"]);
    }

    [Fact]
    public void BuildHostedCheckoutForm_EmptyMetadataStillCarriesTheUserId()
    {
        var form = Build(PaymentRequest());

        Assert.Equal("u1", form["metadata[user_id]"]);
        Assert.Equal("u1", form["payment_intent_data[metadata][user_id]"]);
        Assert.Single(form.Keys, key => key.StartsWith("metadata[", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildHostedCheckoutForm_RejectsInvalidRequests()
    {
        var valid = PaymentRequest();
        var tooMuchMetadata = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < 51; i++)
        {
            tooMuchMetadata[$"k{i}"] = "v";
        }

        static void Reject(StripeCheckoutRequest request, string expectedFragment)
        {
            var ex = Assert.Throws<ArgumentException>(() => Build(request));
            Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
        }

        Reject(valid with { UserId = " " }, "UserId");
        Reject(valid with { UserId = new string('u', 201) }, "UserId");
        Reject(valid with { AmountMinor = 0 }, "AmountMinor");
        Reject(valid with { AmountMinor = -1 }, "AmountMinor");
        Reject(valid with { ProductName = "  " }, "ProductName");
        Reject(valid with { Mode = "setup" }, "Mode");
        Reject(valid with { Mode = "subscription" }, "RecurringInterval");
        Reject(valid with { Mode = "subscription", RecurringInterval = "fortnight" }, "RecurringInterval");
        Reject(valid with { Mode = "subscription", RecurringInterval = "MONTH" }, "RecurringInterval");
        Reject(valid with { Mode = "payment", RecurringInterval = "month" }, "RecurringInterval");
        Reject(valid with { Currency = "RON" }, "Currency");
        Reject(valid with { Currency = "ro" }, "Currency");
        Reject(valid with { Currency = "romanian" }, "Currency");
        Reject(valid with { Metadata = tooMuchMetadata }, "Metadata");
        Reject(
            valid with { Metadata = new Dictionary<string, string> { [new string('k', 41)] = "v" } },
            "Metadata");
        Reject(
            valid with { Metadata = new Dictionary<string, string> { ["bad[key]"] = "v" } },
            "Metadata");
        Reject(
            valid with { Metadata = new Dictionary<string, string> { ["k"] = new string('v', 501) } },
            "Metadata");
        Reject(valid with { IdempotencyKey = new string('i', 256) }, "IdempotencyKey");
    }

    [Fact]
    public void BuildHostedCheckoutForm_AcceptsTheDocumentedLimits()
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < 49; i++)
        {
            metadata[$"k{i:D2}"] = "v";
        }

        // 49 numbered pairs + one at every documented limit == exactly the 50 pairs Stripe accepts.
        metadata[new string('k', 40)] = new string('v', 500);

        var form = Build(PaymentRequest() with
        {
            IdempotencyKey = new string('i', 255),
            Metadata = metadata
        });

        Assert.Equal(new string('v', 500), form[$"metadata[{new string('k', 40)}]"]);
    }

    // ---------- hosted checkout over HTTP ----------

    [Fact]
    public async Task CreateHostedCheckoutAsync_PostsTheFormAndParsesTheSession()
    {
        var handler = new RecordingHandler((_, _) => Json(
            """{"id":"cs_test_1","object":"checkout.session","url":"https://checkout.stripe.test/c/1"}"""));
        var provider = Provider(handler);

        var session = await provider.CreateHostedCheckoutAsync(
            PaymentRequest() with
            {
                IdempotencyKey = "idem-42",
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["report_id"] = "r_42" }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("cs_test_1", session.SessionId);
        Assert.Equal("https://checkout.stripe.test/c/1", session.Url);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Request.Method);
        Assert.Equal("/v1/checkout/sessions", request.Path);
        Assert.Equal("Bearer sk_test", request.Request.Headers.Authorization?.ToString());
        Assert.Equal("idem-42", request.Request.Headers.GetValues("Idempotency-Key").Single());
        Assert.Equal("application/x-www-form-urlencoded", request.Request.Content!.Headers.ContentType!.MediaType);

        var decoded = Uri.UnescapeDataString(request.Body.Replace('+', ' '));
        Assert.Contains("mode=payment", decoded, StringComparison.Ordinal);
        Assert.Contains("line_items[0][price_data][unit_amount]=4900", decoded, StringComparison.Ordinal);
        Assert.Contains("payment_intent_data[metadata][report_id]=r_42", decoded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateHostedCheckoutAsync_WithoutIdempotencyKey_SendsNoHeader()
    {
        var handler = new RecordingHandler((_, _) => Json(
            """{"id":"cs_test_2","url":"https://checkout.stripe.test/c/2"}"""));
        var provider = Provider(handler);

        await provider.CreateHostedCheckoutAsync(PaymentRequest(), TestContext.Current.CancellationToken);

        Assert.False(handler.Requests.Single().Request.Headers.Contains("Idempotency-Key"));
    }

    [Fact]
    public async Task CreateHostedCheckoutAsync_ValidatesBeforeAnyNetworkCall()
    {
        var handler = new RecordingHandler((_, _) => Json("""{"id":"cs_test_3","url":"https://x"}"""));
        var provider = Provider(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.CreateHostedCheckoutAsync(
            PaymentRequest() with { AmountMinor = 0 },
            TestContext.Current.CancellationToken));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreateHostedCheckoutAsync_SurfacesTheStripeErrorCode()
    {
        var handler = new RecordingHandler((_, _) => Json(
            """{"error":{"code":"parameter_invalid_integer","type":"invalid_request_error","message":"Invalid integer: 0"}}""",
            HttpStatusCode.BadRequest));

        var provider = Provider(handler);

        var ex = await Assert.ThrowsAsync<BillingProviderException>(() => provider.CreateHostedCheckoutAsync(
            PaymentRequest(),
            TestContext.Current.CancellationToken));

        Assert.Equal("stripe", ex.Provider);
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal("parameter_invalid_integer", ex.ErrorCode);
        Assert.Equal("Invalid integer: 0", ex.ErrorMessage);
    }
}
