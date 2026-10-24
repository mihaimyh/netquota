using System.Globalization;
using System.Text.Json;

namespace NetQuota.Billing.Stripe;

/// <summary>
/// Pure parser for Stripe webhooks. Tolerates both the legacy and the 2025 API shapes
/// (<c>current_period_*</c> at the subscription level and under <c>items.data[0]</c>,
/// <c>subscription_details.metadata</c> and <c>parent.subscription_details.metadata</c>).
/// An unmapped price is never treated as the default tier: the event is ignored so the customer
/// keeps their current entitlement instead of being silently downgraded.
/// <para>
/// <c>checkout.session.completed</c> is parsed into <see cref="BillingWebhookKind.CheckoutCompleted"/>
/// (event only, never an entitlement write). Subscription-mode sessions and unpaid sessions are
/// ignored: their entitlement arrives through <c>customer.subscription.*</c> / the payment webhooks.
/// </para>
/// </summary>
public static class StripeWebhookParser
{
    public static BillingWebhookResult Parse(byte[] body, StripeOptions options)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return BillingWebhookResult.Ignored("invalid json");
        }

        using (document)
        {
            var root = document.RootElement;
            var type = Text(root, "type");
            var eventId = Text(root, "id") ?? string.Empty;
            var timestamp = UnixSeconds(root, "created") ?? DateTimeOffset.UtcNow;

            if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("object", out var obj))
            {
                return BillingWebhookResult.Ignored("missing data.object");
            }

            return type switch
            {
                "customer.subscription.created" or "customer.subscription.updated" or "customer.subscription.deleted" =>
                    ParseSubscription(type, eventId, timestamp, obj, options),
                "invoice.paid" or "invoice.payment_succeeded" =>
                    ParseInvoice(type, eventId, timestamp, obj, options),
                "payment_intent.succeeded" =>
                    ParseCreditPurchase(BillingWebhookKind.OneTimePurchase, "amount_received", type, eventId, timestamp, obj, options),
                "payment_intent.refunded" or "charge.refunded" =>
                    ParseCreditPurchase(BillingWebhookKind.Refund, "amount_refunded", type, eventId, timestamp, obj, options),
                "checkout.session.completed" =>
                    ParseCheckoutCompleted(type, eventId, timestamp, obj),
                _ => BillingWebhookResult.Ignored($"unhandled event type {type}")
            };
        }
    }

    private static BillingWebhookResult ParseSubscription(
        string type,
        string eventId,
        DateTimeOffset timestamp,
        JsonElement obj,
        StripeOptions options)
    {
        var userId = SubscriptionUserId(obj);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return BillingWebhookResult.Ignored("missing user id");
        }

        var deleted = type == "customer.subscription.deleted";
        var status = Text(obj, "status");
        var priceId = NestedText(obj, "items", "data", "0", "price", "id");
        var (tier, known) = ResolveTier(priceId, obj, options);

        if (!deleted && !known)
        {
            // Fail closed: an unmapped price must not downgrade a paying customer.
            return BillingWebhookResult.Ignored($"unknown price '{priceId}'");
        }

        var (periodStart, periodEnd) = SubscriptionPeriod(obj);
        var price = Nested(obj, "items", "data", "0", "price");
        var currency = price is { } p ? Text(p, "currency") : Text(obj, "currency");
        var unitAmount = price is { } p2 ? Long(p2, "unit_amount") : null;

        var effectiveTier = deleted || !IsActiveStatus(status) ? options.DefaultTier : tier;

        return new BillingWebhookResult(BillingWebhookKind.Subscription, new BillingWebhookEvent
        {
            UserId = userId,
            NewTier = effectiveTier,
            Provider = "stripe",
            EventType = type,
            EventId = eventId,
            EventTimestamp = timestamp,
            ProductId = priceId ?? string.Empty,
            Store = "stripe",
            Currency = (currency ?? string.Empty).ToUpperInvariant(),
            PriceCents = unitAmount ?? 0,
            PurchasedAt = periodStart,
            ExpiresAt = periodEnd,
            CancelAtPeriodEnd = Bool(obj, "cancel_at_period_end"),
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["customer_id"] = Text(obj, "customer") ?? string.Empty,
                ["subscription_id"] = Text(obj, "id") ?? string.Empty,
                ["status"] = status ?? string.Empty
            }
        });
    }

    private static BillingWebhookResult ParseInvoice(
        string type,
        string eventId,
        DateTimeOffset timestamp,
        JsonElement obj,
        StripeOptions options)
    {
        var userId =
            NestedText(obj, "subscription_details", "metadata", "user_id")
            ?? NestedText(obj, "parent", "subscription_details", "metadata", "user_id")
            ?? NestedText(obj, "lines", "data", "0", "metadata", "user_id");

        if (string.IsNullOrWhiteSpace(userId))
        {
            return BillingWebhookResult.Ignored("missing user id");
        }

        var line = InvoiceLine(obj, options);
        var priceId = line is { } selected ? NestedText(selected, "price", "id") : null;
        var (tier, known) = ResolveTier(priceId, obj, options);
        if (!known)
        {
            return BillingWebhookResult.Ignored($"unknown price '{priceId}'");
        }

        var start = line is { } periodStart ? NestedUnixSeconds(periodStart, "period", "start") : null;
        var end = line is { } periodEnd ? NestedUnixSeconds(periodEnd, "period", "end") : null;
        var currency = (line is { } priced ? NestedText(priced, "price", "currency") : null) ?? Text(obj, "currency");

        return new BillingWebhookResult(BillingWebhookKind.Subscription, new BillingWebhookEvent
        {
            UserId = userId,
            NewTier = tier,
            Provider = "stripe",
            EventType = type,
            EventId = eventId,
            EventTimestamp = timestamp,
            ProductId = priceId ?? string.Empty,
            Store = "stripe",
            Currency = (currency ?? string.Empty).ToUpperInvariant(),
            PriceCents = Long(obj, "amount_paid") ?? 0,
            PurchasedAt = start,
            ExpiresAt = end,
            CancelAtPeriodEnd = null,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["customer_id"] = Text(obj, "customer") ?? string.Empty,
                ["subscription_id"] = Text(obj, "subscription") ?? string.Empty,
                ["billing_reason"] = Text(obj, "billing_reason") ?? string.Empty,
                ["billing_period"] = BillingPeriods.Derive(start, end)
            }
        });
    }

    private static BillingWebhookResult ParseCreditPurchase(
        BillingWebhookKind kind,
        string amountField,
        string type,
        string eventId,
        DateTimeOffset timestamp,
        JsonElement obj,
        StripeOptions options)
    {
        var metadata = obj.TryGetProperty("metadata", out var m) ? m : default;
        var userId = Text(metadata, "user_id");
        var resource = Text(metadata, "resource");
        var packCredits = ParseInt(Text(metadata, "credits")) ?? ParseInt(Text(metadata, "amount"));

        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(resource) || packCredits is null or <= 0)
        {
            if (kind == BillingWebhookKind.Refund && !string.IsNullOrWhiteSpace(userId))
            {
                // Refund pentru un produs care nu e credit-pack: nu există credite de revertit, dar
                // consumerul trebuie să afle, ca să-și revoce propriul Drept.
                return new BillingWebhookResult(BillingWebhookKind.PaymentRefunded, new BillingWebhookEvent
                {
                    UserId = userId,
                    NewTier = options.DefaultTier,
                    Provider = "stripe",
                    EventType = type,
                    EventId = eventId,
                    ProviderObjectId = Text(obj, "id") ?? string.Empty,
                    PaymentIntentId = Text(obj, "payment_intent") ?? Text(obj, "id") ?? string.Empty,
                    EventTimestamp = timestamp,
                    ProductId = Text(metadata, "price_id") ?? string.Empty,
                    Store = "stripe",
                    Currency = (Text(obj, "currency") ?? string.Empty).ToUpperInvariant(),
                    PriceCents = Long(obj, amountField) ?? Long(obj, "amount_refunded") ?? 0,
                    CancelAtPeriodEnd = null,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["cui"] = Text(metadata, "cui") ?? string.Empty,
                        ["targetCui"] = Text(metadata, "targetCui") ?? string.Empty,
                        ["planCode"] = Text(metadata, "planCode") ?? string.Empty,
                        ["productType"] = Text(metadata, "productType") ?? string.Empty
                    }
                });
            }

            // Refunds of subscription invoices must not touch prepaid credits.
            return BillingWebhookResult.Ignored(kind == BillingWebhookKind.Refund
                ? "refund without credit-pack metadata"
                : "one-time payment without user_id/resource/credits metadata");
        }

        var credits = packCredits.Value;
        var priceCents = Long(obj, amountField) ?? Long(obj, "amount") ?? 0;
        var identity = eventId;

        if (kind == BillingWebhookKind.Refund)
        {
            // `amount_refunded` is CUMULATIVE, and `charge.refunds` is an includable field that Stripe
            // does not return in webhook payloads. Only the individual refund entry describes this
            // refund's share; using the cumulative amount revokes 20 + 40 + 60 ... credits for three
            // 20% refunds. A partial refund without that entry is ignored (fail-safe) instead of
            // over-revoking, and a full refund revokes the pack once under a payment-intent identity
            // so charge.refunded and payment_intent.refunded for the same refund deduplicate.
            var entry = Nested(obj, "refunds", "data", "0");
            var total = Long(obj, "amount_received") ?? Long(obj, "amount");
            var cumulative = Long(obj, "amount_refunded");

            if (entry is { } refund)
            {
                var refundedAmount = Long(refund, "amount");
                if (refundedAmount is null or <= 0)
                {
                    return BillingWebhookResult.Ignored("refund without amount");
                }

                identity = Text(refund, "id") ?? eventId;
                credits = total is > 0
                    ? (int)Math.Clamp(
                        Math.Round(packCredits.Value * (double)refundedAmount.Value / total.Value, MidpointRounding.AwayFromZero),
                        0,
                        packCredits.Value)
                    : packCredits.Value;
                priceCents = refundedAmount.Value;
            }
            else
            {
                if (cumulative is null or <= 0)
                {
                    return BillingWebhookResult.Ignored("refund without amount");
                }

                if (total is not > 0)
                {
                    // The refunded share cannot be established without the original amount.
                    return BillingWebhookResult.Ignored("refund without the original amount");
                }

                if (cumulative < total)
                {
                    return BillingWebhookResult.Ignored(
                        "partial refund without a refund entry; charge.refunds is not included in webhook payloads");
                }

                identity = "refund-full:" + (Text(obj, "payment_intent") ?? Text(obj, "id") ?? eventId);
                credits = packCredits.Value;
                priceCents = cumulative.Value;
            }
        }

        return new BillingWebhookResult(kind, new BillingWebhookEvent
        {
            UserId = userId,
            NewTier = options.DefaultTier,
            Provider = "stripe",
            EventType = type,
            EventId = identity,
            ProviderObjectId = Text(obj, "id") ?? string.Empty,
            PaymentIntentId = Text(obj, "payment_intent") ?? Text(obj, "id") ?? string.Empty,
            EventTimestamp = timestamp,
            ProductId = Text(metadata, "price_id") ?? string.Empty,
            Store = "stripe",
            Currency = (Text(obj, "currency") ?? string.Empty).ToUpperInvariant(),
            PriceCents = priceCents,
            CancelAtPeriodEnd = null,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["resource"] = resource,
                ["credits"] = credits.ToString(CultureInfo.InvariantCulture),
                ["payment_intent_id"] = Text(obj, "payment_intent") ?? Text(obj, "id") ?? string.Empty
            }
        });
    }

    /// <summary>
    /// <c>checkout.session.completed</c> for a one-off, already-paid session. The parser only reads the
    /// event: it never writes an entitlement, because the consumer decides what the metadata means.
    /// </summary>
    private static BillingWebhookResult ParseCheckoutCompleted(
        string type,
        string eventId,
        DateTimeOffset timestamp,
        JsonElement obj)
    {
        var metadata = SessionMetadata(obj);

        // Metadata wins over client_reference_id (which a Payment Link may set to something else);
        // either is better than guessing, and dropping the event keeps the caller from granting twice.
        var userId = metadata.GetValueOrDefault("user_id") ?? Text(obj, "client_reference_id");
        if (string.IsNullOrWhiteSpace(userId))
        {
            return BillingWebhookResult.Ignored("missing user id");
        }

        if (Text(obj, "mode") == "subscription")
        {
            // customer.subscription.* and invoice.paid own the tier; granting on checkout as well
            // would apply the same purchase twice.
            return BillingWebhookResult.Ignored("subscription mode; subscription events carry the entitlement");
        }

        // Checkout completes a session before the money lands for delayed payment methods (SEPA,
        // Boleto): granting here would hand out the product for a payment that may still fail.
        if (Text(obj, "payment_status") != "paid")
        {
            return BillingWebhookResult.Ignored("checkout session not paid");
        }

        return new BillingWebhookResult(BillingWebhookKind.CheckoutCompleted, new BillingWebhookEvent
        {
            UserId = userId,

            // A checkout never changes a tier by itself; a consumer that sells tiers through a
            // subscription gets the tier from customer.subscription.*.
            NewTier = metadata.GetValueOrDefault("tier", string.Empty),
            Provider = "stripe",
            EventType = type,
            EventId = eventId,
            ProviderObjectId = Text(obj, "id") ?? string.Empty,
            PaymentIntentId = Text(obj, "payment_intent") ?? string.Empty,
            EventTimestamp = timestamp,
            ProductId = metadata.GetValueOrDefault("product_id", string.Empty),
            Store = "stripe",
            Currency = (Text(obj, "currency") ?? string.Empty).ToLowerInvariant(),
            PriceCents = Long(obj, "amount_total") ?? 0,
            CancelAtPeriodEnd = null,
            Metadata = metadata
        });
    }

    /// <summary>The session's metadata dictionary, copied verbatim (values only; keys are the caller's).</summary>
    private static Dictionary<string, string> SessionMetadata(JsonElement obj)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!obj.TryGetProperty("metadata", out var source) || source.ValueKind != JsonValueKind.Object)
        {
            return metadata;
        }

        foreach (var property in source.EnumerateObject())
        {
            if (Text(source, property.Name) is { } value)
            {
                metadata[property.Name] = value;
            }
        }

        return metadata;
    }

    private static (string Tier, bool Known) ResolveTier(string? priceId, JsonElement obj, StripeOptions options)
    {
        if (!string.IsNullOrWhiteSpace(priceId) && options.PriceToTier.TryGetValue(priceId, out var mapped))
        {
            return (mapped, true);
        }

        var metadataTier = NestedText(obj, "metadata", "tier");
        if (!string.IsNullOrWhiteSpace(metadataTier))
        {
            return (metadataTier, true);
        }

        return (options.DefaultTier, false);
    }

    /// <summary>
    /// The invoice line that describes the subscription's current price: the first mapped,
    /// non-proration line, then the first non-proration line, then the first line. Proration credits
    /// for the previous price are billed first, so <c>lines.data[0]</c> alone can downgrade a
    /// mid-cycle upgrade.
    /// </summary>
    private static JsonElement? InvoiceLine(JsonElement obj, StripeOptions options)
    {
        if (!obj.TryGetProperty("lines", out var lines) ||
            !lines.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array ||
            data.GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement? first = null;
        JsonElement? firstNonProration = null;
        foreach (var line in data.EnumerateArray())
        {
            first ??= line;
            if (Bool(line, "proration") == true)
            {
                continue;
            }

            firstNonProration ??= line;
            var priceId = NestedText(line, "price", "id");
            if (priceId is not null && options.PriceToTier.ContainsKey(priceId))
            {
                return line;
            }
        }

        return firstNonProration ?? first;
    }

    private static bool IsActiveStatus(string? status) =>
        status is "active" or "trialing" or "past_due";

    private static string? SubscriptionUserId(JsonElement obj) =>
        NestedText(obj, "metadata", "user_id")
        ?? Text(obj, "client_reference_id")
        ?? NestedText(obj, "subscription_details", "metadata", "user_id");

    private static (DateTimeOffset? Start, DateTimeOffset? End) SubscriptionPeriod(JsonElement obj)
    {
        var start = UnixSeconds(obj, "current_period_start")
                    ?? NestedUnixSeconds(obj, "items", "data", "0", "current_period_start");
        var end = UnixSeconds(obj, "current_period_end")
                  ?? NestedUnixSeconds(obj, "items", "data", "0", "current_period_end");
        return (start, end);
    }

    internal static string? Text(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    internal static long? Long(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(
                value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    internal static bool? Bool(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    internal static JsonElement? Nested(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind == JsonValueKind.Array)
            {
                if (!int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ||
                    index < 0 ||
                    index >= current.GetArrayLength())
                {
                    return null;
                }

                current = current[index];
                continue;
            }

            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current;
    }

    internal static string? NestedText(JsonElement element, params string[] path)
    {
        if (path.Length < 2)
        {
            return null;
        }

        var parent = Nested(element, path[..^1]);
        return parent is { } p ? Text(p, path[^1]) : null;
    }

    internal static DateTimeOffset? UnixSeconds(JsonElement element, string name)
    {
        var seconds = Long(element, name);
        return seconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;
    }

    internal static DateTimeOffset? NestedUnixSeconds(JsonElement element, params string[] path)
    {
        if (path.Length < 2)
        {
            return null;
        }

        var parent = Nested(element, path[..^1]);
        return parent is { } p ? UnixSeconds(p, path[^1]) : null;
    }

    internal static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}
