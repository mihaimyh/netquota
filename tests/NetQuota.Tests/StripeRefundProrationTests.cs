using System.Text;
using NetQuota.Billing;
using NetQuota.Billing.Stripe;

namespace NetQuota.Tests;

/// <summary>
/// A credit pack can be refunded in several partial refunds, and Stripe emits both
/// <c>payment_intent.refunded</c> and <c>charge.refunded</c> for them. Each event must revoke only
/// its own share of the pack (never the cumulative share reported by <c>amount_refunded</c>) and
/// must be idempotent by refund id (full refunds by payment intent), so the credits revoked across
/// every refund of one purchase can never exceed the purchased pack.
/// </summary>
public class StripeRefundProrationTests
{
    private static readonly StripeOptions Options = new() { WebhookSecret = "whsec_test" };

    private static readonly StripeOptions TierOptions = new()
    {
        WebhookSecret = "whsec_test",
        PriceToTier = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["price_pro"] = "pro",
            ["price_start"] = "start"
        }
    };

    private const string PackMetadata =
        """
        "metadata": { "user_id": "u1", "resource": "credits", "credits": "100", "price_id": "price_pack_100" }
        """;

    private static BillingWebhookResult Parse(string json) =>
        StripeWebhookParser.Parse(Encoding.UTF8.GetBytes(json), Options);

    private static int RefundedCredits(BillingWebhookResult result)
    {
        Assert.Equal(BillingWebhookKind.Refund, result.Kind);
        return int.Parse(result.Event!.Metadata["credits"], System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void FullRefund_RevokesTheWholePack()
    {
        var result = Parse($$"""
            {
              "id": "evt_refund_full", "type": "payment_intent.refunded", "created": 1760000000,
              "data": { "object": {
                "id": "pi_pack100", "object": "payment_intent",
                "amount": 1000, "amount_received": 1000, "amount_refunded": 1000,
                "currency": "usd", "status": "succeeded",
                {{PackMetadata}},
                "refunds": { "object": "list", "data": [
                  { "id": "re_full", "object": "refund", "amount": 1000, "currency": "usd",
                    "status": "succeeded", "payment_intent": "pi_pack100" }
                ] }
              } }
            }
            """);

        Assert.Equal(100, RefundedCredits(result));
        Assert.Equal("re_full", result.Event!.EventId);
    }

    [Fact]
    public void PartialRefund_RevokesOnlyItsShareOfThePack()
    {
        // 200 of 1000 cents refunded = 20% of a 100-credit pack.
        var result = Parse($$"""
            {
              "id": "evt_refund_first", "type": "payment_intent.refunded", "created": 1760000000,
              "data": { "object": {
                "id": "pi_pack100", "object": "payment_intent",
                "amount": 1000, "amount_received": 1000, "amount_refunded": 200,
                "currency": "usd", "status": "succeeded",
                {{PackMetadata}},
                "refunds": { "object": "list", "data": [
                  { "id": "re_first", "object": "refund", "amount": 200, "currency": "usd",
                    "status": "succeeded", "payment_intent": "pi_pack100" }
                ] }
              } }
            }
            """);

        Assert.Equal(20, RefundedCredits(result));
        Assert.Equal("re_first", result.Event!.EventId);
    }

    [Fact]
    public void SequentialPartialRefunds_RevokeTheirOwnShares_NeverTheWholePackTwice()
    {
        // Stripe reports amount_refunded cumulatively and lists the newest refund first.
        var first = Parse($$"""
            {
              "id": "evt_refund_first", "type": "payment_intent.refunded", "created": 1760000000,
              "data": { "object": {
                "id": "pi_pack100", "object": "payment_intent",
                "amount": 1000, "amount_received": 1000, "amount_refunded": 200,
                "currency": "usd", "status": "succeeded",
                {{PackMetadata}},
                "refunds": { "object": "list", "data": [
                  { "id": "re_first", "object": "refund", "amount": 200, "currency": "usd",
                    "status": "succeeded", "payment_intent": "pi_pack100" }
                ] }
              } }
            }
            """);

        var second = Parse($$"""
            {
              "id": "evt_refund_second", "type": "payment_intent.refunded", "created": 1760000100,
              "data": { "object": {
                "id": "pi_pack100", "object": "payment_intent",
                "amount": 1000, "amount_received": 1000, "amount_refunded": 400,
                "currency": "usd", "status": "succeeded",
                {{PackMetadata}},
                "refunds": { "object": "list", "data": [
                  { "id": "re_second", "object": "refund", "amount": 200, "currency": "usd",
                    "status": "succeeded", "payment_intent": "pi_pack100" },
                  { "id": "re_first", "object": "refund", "amount": 200, "currency": "usd",
                    "status": "succeeded", "payment_intent": "pi_pack100" }
                ] }
              } }
            }
            """);

        var firstCredits = RefundedCredits(first);
        var secondCredits = RefundedCredits(second);

        Assert.Equal(20, firstCredits);
        Assert.Equal(20, secondCredits);
        Assert.True(
            firstCredits + secondCredits <= 100,
            $"refunds revoked {firstCredits + secondCredits} credits out of a 100-credit pack");
    }

    [Fact]
    public void RedeliveredRefund_KeepsTheSameIdentityAndCreditAmount()
    {
        const string json = $$"""
            {
              "id": "evt_refund_first", "type": "payment_intent.refunded", "created": 1760000000,
              "data": { "object": {
                "id": "pi_pack100", "object": "payment_intent",
                "amount": 1000, "amount_received": 1000, "amount_refunded": 200,
                "currency": "usd", "status": "succeeded",
                {{PackMetadata}},
                "refunds": { "object": "list", "data": [
                  { "id": "re_first", "object": "refund", "amount": 200, "currency": "usd",
                    "status": "succeeded", "payment_intent": "pi_pack100" }
                ] }
              } }
            }
            """;

        var delivered = Parse(json);
        var redelivered = Parse(json);

        Assert.Equal("re_first", delivered.Event!.EventId);
        Assert.Equal(delivered.Event.EventId, redelivered.Event!.EventId);
        Assert.Equal(20, RefundedCredits(delivered));
        Assert.Equal(20, RefundedCredits(redelivered));
    }

    [Fact]
    public void ZeroAmountRefund_IsIgnored()
    {
        var result = Parse($$"""
            {
              "id": "evt_refund_zero", "type": "payment_intent.refunded", "created": 1760000000,
              "data": { "object": {
                "id": "pi_pack100", "object": "payment_intent",
                "amount": 1000, "amount_received": 1000, "amount_refunded": 0,
                "currency": "usd", "status": "succeeded",
                {{PackMetadata}},
                "refunds": { "object": "list", "data": [] }
              } }
            }
            """);

        Assert.Equal(BillingWebhookKind.Ignored, result.Kind);
        Assert.Null(result.Event);
    }

    [Fact]
    public void PartialRefundWithoutRefundEntry_IsIgnored_InsteadOfRevokingTheCumulativeShare()
    {
        // `charge.refunds` is an includable field Stripe does not return in webhook payloads.
        // amount_refunded = 400 is the cumulative total of both refunds, not this refund's $2.
        var result = Parse("""
            {
              "id": "evt_refund_second", "type": "charge.refunded", "created": 1760000100,
              "data": { "object": {
                "id": "ch_pack100", "object": "charge", "payment_intent": "pi_pack100",
                "amount": 1000, "amount_refunded": 400, "currency": "usd", "status": "succeeded",
                "metadata": { "user_id": "u1", "resource": "credits", "credits": "100" }
              } }
            }
            """);

        Assert.Equal(BillingWebhookKind.Ignored, result.Kind);
        Assert.Null(result.Event);
    }

    [Fact]
    public void FullRefundWithoutRefundEntry_RevokesTheWholePack_UnderOnePaymentIntentIdentity()
    {
        // Without an expanded refunds list only a full refund is safe to revoke: the cumulative
        // amount equals the original total. charge.refunded and payment_intent.refunded for the
        // same refund must deduplicate to one identity.
        var charge = Parse("""
            {
              "id": "evt_charge_refund", "type": "charge.refunded", "created": 1760000000,
              "data": { "object": {
                "id": "ch_pack100", "object": "charge", "payment_intent": "pi_pack100",
                "amount": 1000, "amount_refunded": 1000, "currency": "usd", "status": "succeeded",
                "metadata": { "user_id": "u1", "resource": "credits", "credits": "100" }
              } }
            }
            """);

        var intent = Parse("""
            {
              "id": "evt_intent_refund", "type": "payment_intent.refunded", "created": 1760000000,
              "data": { "object": {
                "id": "pi_pack100", "object": "payment_intent",
                "amount": 1000, "amount_received": 1000, "amount_refunded": 1000,
                "currency": "usd", "status": "succeeded",
                "metadata": { "user_id": "u1", "resource": "credits", "credits": "100" }
              } }
            }
            """);

        Assert.Equal(100, RefundedCredits(charge));
        Assert.Equal(100, RefundedCredits(intent));
        Assert.Equal("refund-full:pi_pack100", charge.Event!.EventId);
        Assert.Equal(charge.Event.EventId, intent.Event!.EventId);
    }

    [Fact]
    public void RefundWithoutTheOriginalAmount_IsIgnored_FailSafe()
    {
        // A cumulative amount alone cannot prove the refund was for the whole pack; revoking the
        // pack would over-revoke.
        var result = Parse("""
            {
              "id": "evt_refund_no_total", "type": "payment_intent.refunded", "created": 1760000000,
              "data": { "object": {
                "id": "pi_pack100", "object": "payment_intent",
                "amount_refunded": 500, "currency": "usd",
                "metadata": { "user_id": "u1", "resource": "credits", "credits": "100" }
              } }
            }
            """);

        Assert.Equal(BillingWebhookKind.Ignored, result.Kind);
        Assert.Null(result.Event);
    }

    [Fact]
    public void UpgradeInvoice_ResolvesTheMappedNonProrationLine()
    {
        // The proration credit for the old price is billed first; line 0 names the tier the
        // customer just left.
        var result = StripeWebhookParser.Parse(Encoding.UTF8.GetBytes("""
            {
              "id": "evt_inv_upgrade", "type": "invoice.paid", "created": 1760000000,
              "data": { "object": {
                "id": "in_1", "customer": "cus_1", "subscription": "sub_1",
                "amount_paid": 4200, "currency": "usd",
                "parent": { "subscription_details": { "metadata": { "user_id": "u1" } } },
                "lines": { "data": [
                  { "id": "il_proration", "proration": true, "amount": -3300,
                    "price": { "id": "price_start", "currency": "usd" },
                    "period": { "start": 1760000000, "end": 1762678400 } },
                  { "id": "il_new", "proration": false, "amount": 7500,
                    "price": { "id": "price_pro", "currency": "usd" },
                    "period": { "start": 1760000000, "end": 1762678400 } } ] } } }
            }
            """), TierOptions);

        Assert.Equal(BillingWebhookKind.Subscription, result.Kind);
        Assert.Equal("pro", result.Event!.NewTier);
        Assert.Equal("price_pro", result.Event.ProductId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1762678400), result.Event.ExpiresAt);
    }

    [Fact]
    public void InvoiceWithAnUnmappedAddOnLine_ResolvesTheMappedSubscriptionLine()
    {
        var result = StripeWebhookParser.Parse(Encoding.UTF8.GetBytes("""
            {
              "id": "evt_inv_addon", "type": "invoice.payment_succeeded", "created": 1760000000,
              "data": { "object": {
                "id": "in_2", "customer": "cus_1", "subscription": "sub_1",
                "amount_paid": 9900, "currency": "usd",
                "parent": { "subscription_details": { "metadata": { "user_id": "u1" } } },
                "lines": { "data": [
                  { "id": "il_addon", "proration": false, "amount": 900,
                    "price": { "id": "price_seats", "currency": "usd" },
                    "period": { "start": 1760000000, "end": 1762678400 } },
                  { "id": "il_sub", "proration": false, "amount": 9000,
                    "price": { "id": "price_pro", "currency": "usd" },
                    "period": { "start": 1760000000, "end": 1762678400 } } ] } } }
            }
            """), TierOptions);

        Assert.Equal(BillingWebhookKind.Subscription, result.Kind);
        Assert.Equal("pro", result.Event!.NewTier);
    }

    [Fact]
    public void ChargeRefunded_IsHandled_AndSharesTheRefundIdentity()
    {
        var result = Parse($$"""
            {
              "id": "evt_charge_refund", "type": "charge.refunded", "created": 1760000000,
              "data": { "object": {
                "id": "ch_pack100", "object": "charge", "payment_intent": "pi_pack100",
                "amount": 1000, "amount_refunded": 200, "currency": "usd", "status": "succeeded",
                {{PackMetadata}},
                "refunds": { "object": "list", "data": [
                  { "id": "re_first", "object": "refund", "amount": 200, "currency": "usd",
                    "status": "succeeded", "payment_intent": "pi_pack100" }
                ] }
              } }
            }
            """);

        Assert.Equal(20, RefundedCredits(result));
        Assert.Equal("re_first", result.Event!.EventId);
    }
}
