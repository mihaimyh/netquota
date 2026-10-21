using System.Globalization;
using System.Text.Json;

namespace NetQuota.Billing.Stripe;

/// <summary>
/// Pure parser for Stripe webhooks. Tolerates both the legacy and the 2025 API shapes
/// (<c>current_period_*</c> at the subscription level and under <c>items.data[0]</c>,
/// <c>subscription_details.metadata</c> and <c>parent.subscription_details.metadata</c>).
/// An unmapped price is never treated as the default tier: the event is ignored so the customer
/// keeps their current entitlement instead of being silently downgraded.
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
                "payment_intent.refunded" =>
                    ParseCreditPurchase(BillingWebhookKind.Refund, "amount_refunded", type, eventId, timestamp, obj, options),
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

        var priceId = NestedText(obj, "lines", "data", "0", "price", "id");
        var (tier, known) = ResolveTier(priceId, obj, options);
        if (!known)
        {
            return BillingWebhookResult.Ignored($"unknown price '{priceId}'");
        }

        var start = NestedUnixSeconds(obj, "lines", "data", "0", "period", "start");
        var end = NestedUnixSeconds(obj, "lines", "data", "0", "period", "end");
        var currency = NestedText(obj, "lines", "data", "0", "price", "currency") ?? Text(obj, "currency");

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
        var credits = ParseInt(Text(metadata, "credits")) ?? ParseInt(Text(metadata, "amount"));

        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(resource) || credits is null or <= 0)
        {
            // Refunds of subscription invoices must not touch prepaid credits.
            return BillingWebhookResult.Ignored(kind == BillingWebhookKind.Refund
                ? "refund without credit-pack metadata"
                : "one-time payment without user_id/resource/credits metadata");
        }

        return new BillingWebhookResult(kind, new BillingWebhookEvent
        {
            UserId = userId,
            NewTier = options.DefaultTier,
            Provider = "stripe",
            EventType = type,
            EventId = eventId,
            EventTimestamp = timestamp,
            ProductId = Text(metadata, "price_id") ?? string.Empty,
            Store = "stripe",
            Currency = (Text(obj, "currency") ?? string.Empty).ToUpperInvariant(),
            PriceCents = Long(obj, amountField) ?? Long(obj, "amount") ?? 0,
            CancelAtPeriodEnd = null,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["resource"] = resource,
                ["credits"] = credits.Value.ToString(CultureInfo.InvariantCulture),
                ["payment_intent_id"] = Text(obj, "id") ?? string.Empty
            }
        });
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
