using System.Globalization;
using System.Text.Json;

namespace NetQuota.Billing.RevenueCat;

public sealed record RevenueCatOptions
{
    public required string WebhookSecret { get; init; }
    public bool AcceptHmacSignature { get; init; }
    public string DefaultTier { get; init; } = "free";

    /// <summary>RevenueCat entitlement id -> internal tier name.</summary>
    public IReadOnlyDictionary<string, string> EntitlementToTier { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string ApiKey { get; init; } = string.Empty;
    public string ApiBaseUrl { get; init; } = "https://api.revenuecat.com";

    /// <summary>
    /// Fails fast instead of accepting every delivery (or, with an empty secret and HMAC enabled,
    /// accepting a signature anyone can compute).
    /// </summary>
    public void ValidateForWebhooks()
    {
        if (string.IsNullOrWhiteSpace(WebhookSecret))
        {
            throw new ArgumentException("RevenueCat WebhookSecret is required.", nameof(WebhookSecret));
        }

        if (string.IsNullOrWhiteSpace(DefaultTier))
        {
            throw new ArgumentException("RevenueCat DefaultTier is required.", nameof(DefaultTier));
        }

        if (EntitlementToTier.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
        {
            throw new ArgumentException(
                "RevenueCat EntitlementToTier contains an empty entitlement or tier.",
                nameof(EntitlementToTier));
        }
    }
}

public sealed record RevenueCatParseResult(
    BillingWebhookKind Kind,
    BillingWebhookEvent? Event = null,
    IReadOnlyList<string>? TransferredFrom = null,
    IReadOnlyList<string>? TransferredTo = null,
    string Reason = "")
{
    public static RevenueCatParseResult Ignored(string reason) => new(BillingWebhookKind.Ignored, Reason: reason);
}

/// <summary>
/// Pure parser for RevenueCat webhooks. Encodes the provider's quirks:
/// <c>price</c> is always USD while <c>currency</c> is the transaction currency; both
/// <c>timestamp_ms</c>/<c>event_timestamp_ms</c> and <c>purchase_date_ms</c>/<c>purchased_at_ms</c>
/// appear in the wild; tier resolution must not depend on map iteration order; and
/// <c>NON_RENEWING_PURCHASE</c> is a credit pack, not a subscription change.
/// </summary>
public static class RevenueCatWebhookParser
{
    private const string AnonymousIdPrefix = "$rcanonymousid:";

    /// <summary>
    /// Events that carry subscription state. Anything else (<c>SUBSCRIBER_ALIAS</c>, <c>TEST</c>,
    /// future types) must be ignored: parsing them as state changes rewrites the tier to the
    /// default and silently downgrades a paying customer.
    /// </summary>
    private static readonly HashSet<string> SubscriptionEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "INITIAL_PURCHASE",
        "RENEWAL",
        "CANCELLATION",
        "UNCANCELLATION",
        "PRODUCT_CHANGE",
        "SUBSCRIPTION_EXTENDED",
        "EXPIRATION",
        "BILLING_ISSUE",
        "SUBSCRIPTION_PAUSED",
        "REFUND_REVERSED"
    };

    public static RevenueCatParseResult Parse(byte[] body, RevenueCatOptions options)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return RevenueCatParseResult.Ignored("invalid json");
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("event", out var evt) || evt.ValueKind != JsonValueKind.Object)
            {
                return RevenueCatParseResult.Ignored("missing event");
            }

            var eventType = Text(evt, "type");
            var eventId = Text(evt, "id") ?? string.Empty;
            var eventTimestamp = UnixMilliseconds(evt, "event_timestamp_ms")
                                 ?? UnixMilliseconds(evt, "timestamp_ms")
                                 ?? DateTimeOffset.UtcNow;

            if (string.Equals(eventType, "TRANSFER", StringComparison.OrdinalIgnoreCase))
            {
                var from = FilterAppUserIds(ReadStringArray(evt, "transferred_from"));
                var to = FilterAppUserIds(ReadStringArray(evt, "transferred_to"));
                return new RevenueCatParseResult(
                    BillingWebhookKind.Transfer,
                    new BillingWebhookEvent
                    {
                        UserId = to.FirstOrDefault() ?? string.Empty,
                        NewTier = options.DefaultTier,
                        Provider = "revenuecat",
                        EventType = eventType ?? "TRANSFER",
                        EventId = eventId,
                        EventTimestamp = eventTimestamp
                    },
                    from,
                    to);
            }

            var userId = Text(evt, "app_user_id");
            if (string.IsNullOrWhiteSpace(userId))
            {
                return RevenueCatParseResult.Ignored("missing app_user_id");
            }

            var productId = Text(evt, "product_id") ?? string.Empty;
            var (priceCents, currency) = PurchasedPrice(evt);
            var purchasedAt = UnixMilliseconds(evt, "purchased_at_ms")
                              ?? UnixMilliseconds(evt, "purchase_date_ms");
            var expiresAt = UnixMilliseconds(evt, "expiration_at_ms");

            if (string.Equals(eventType, "NON_RENEWING_PURCHASE", StringComparison.OrdinalIgnoreCase))
            {
                // Credit pack: must never rewrite the subscription entitlement (goquota 1958efa).
                return new RevenueCatParseResult(BillingWebhookKind.OneTimePurchase, new BillingWebhookEvent
                {
                    UserId = userId,
                    NewTier = options.DefaultTier,
                    PreviousTier = options.DefaultTier,
                    Provider = "revenuecat",
                    EventType = eventType!,
                    EventId = eventId,
                    EventTimestamp = eventTimestamp,
                    ProductId = productId,
                    Store = (Text(evt, "store") ?? string.Empty).ToUpperInvariant(),
                    Currency = currency,
                    PriceCents = priceCents,
                    PeriodType = Text(evt, "period_type") ?? string.Empty,
                    PurchasedAt = purchasedAt,
                    ExpiresAt = expiresAt,
                    CancelAtPeriodEnd = null,
                    Metadata = Metadata(evt)
                });
            }

            var tier = ResolveTier(document.RootElement, evt, options);
            if (!SubscriptionEventTypes.Contains(eventType ?? string.Empty))
            {
                return RevenueCatParseResult.Ignored($"unhandled event type {eventType}");
            }

            return new RevenueCatParseResult(BillingWebhookKind.Subscription, new BillingWebhookEvent
            {
                UserId = userId,
                NewTier = tier,
                Provider = "revenuecat",
                EventType = eventType ?? string.Empty,
                EventId = eventId,
                EventTimestamp = eventTimestamp,
                ProductId = productId,
                Store = (Text(evt, "store") ?? string.Empty).ToUpperInvariant(),
                Currency = currency,
                PriceCents = priceCents,
                PeriodType = Text(evt, "period_type") ?? string.Empty,
                PurchasedAt = purchasedAt,
                ExpiresAt = expiresAt,
                CancelAtPeriodEnd = CancellationState(eventType, Text(evt, "cancel_reason")),
                CancelReason = Text(evt, "cancel_reason") ?? string.Empty,
                Metadata = Metadata(evt)
            });
        }
    }

    /// <summary>
    /// Dollars to minor units. <c>price</c> may only be paired with USD; the local amount comes from
    /// <c>price_in_purchased_currency</c> with the event currency (goquota d7946d2).
    /// </summary>
    public static (long PriceCents, string Currency) PurchasedPrice(JsonElement evt)
    {
        var currency = (Text(evt, "currency") ?? string.Empty).Trim().ToUpperInvariant();
        var local = Number(evt, "price_in_purchased_currency");
        if (local is > 0)
        {
            return ((long)Math.Round(local.Value * 100, MidpointRounding.AwayFromZero), currency);
        }

        var price = Number(evt, "price");
        return price is > 0
            ? ((long)Math.Round(price.Value * 100, MidpointRounding.AwayFromZero), "USD")
            : (0, currency);
    }

    /// <summary>
    /// Auto-renew tri-state. <c>CANCELLATION</c> also fires for refunds, where auto-renew can stay
    /// on, so customer-support / billing-error reasons stay unspecified instead of forcing "off".
    /// </summary>
    public static bool? CancellationState(string? eventType, string? cancelReason)
    {
        switch ((eventType ?? string.Empty).Trim().ToUpperInvariant())
        {
            case "UNCANCELLATION":
            case "INITIAL_PURCHASE":
            case "RENEWAL":
            case "PRODUCT_CHANGE":
            case "SUBSCRIPTION_EXTENDED":
            case "REFUND_REVERSED":
                return false;
            case "CANCELLATION":
                return (cancelReason ?? string.Empty).Trim().ToUpperInvariant() switch
                {
                    "CUSTOMER_SUPPORT" or "BILLING_ERROR" => null,
                    _ => true
                };
            default:
                return null;
        }
    }

    public static IReadOnlyList<string> FilterAppUserIds(IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        foreach (var raw in ids)
        {
            var id = (raw ?? string.Empty).Trim();
            if (id.Length == 0 ||
                id.StartsWith(AnonymousIdPrefix, StringComparison.OrdinalIgnoreCase) ||
                !seen.Add(id))
            {
                continue;
            }

            result.Add(id);
        }

        return result;
    }

    private static string ResolveTier(JsonElement root, JsonElement evt, RevenueCatOptions options)
    {
        var eventType = (Text(evt, "type") ?? string.Empty).Trim().ToUpperInvariant();
        if (eventType is "EXPIRATION" or "CANCELLATION" or "BILLING_ISSUE" or "SUBSCRIPTION_PAUSED")
        {
            var expiration = UnixMilliseconds(evt, "expiration_at_ms");
            if (expiration is null || expiration <= DateTimeOffset.UtcNow)
            {
                return options.DefaultTier;
            }
        }

        foreach (var entitlementId in ReadStringArray(evt, "entitlement_ids"))
        {
            if (options.EntitlementToTier.TryGetValue(entitlementId, out var tier))
            {
                return tier;
            }
        }

        var eventEntitlementId = Text(evt, "entitlement_id");
        if (!string.IsNullOrWhiteSpace(eventEntitlementId) &&
            options.EntitlementToTier.TryGetValue(eventEntitlementId, out var eventTier))
        {
            return eventTier;
        }

        // Deterministic: sorted keys, never map iteration order (goquota 3253fad).
        foreach (var candidate in options.EntitlementToTier.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (TryGetActiveEntitlement(root, candidate))
            {
                return options.EntitlementToTier[candidate];
            }
        }

        return options.DefaultTier;
    }

    private static bool TryGetActiveEntitlement(JsonElement root, string entitlementId)
    {
        foreach (var containerName in new[] { "subscriber", "customer_info" })
        {
            if (!root.TryGetProperty(containerName, out var container) ||
                !container.TryGetProperty("entitlements", out var entitlements) ||
                entitlements.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var property in entitlements.EnumerateObject())
            {
                if (!string.Equals(property.Name, entitlementId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var isActive = property.Value.TryGetProperty("is_active", out var active) &&
                               active.ValueKind == JsonValueKind.True;
                if (isActive)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static Dictionary<string, string> Metadata(JsonElement evt) =>
        new(StringComparer.Ordinal)
        {
            ["product_id"] = Text(evt, "product_id") ?? string.Empty,
            ["entitlement_id"] = Text(evt, "entitlement_id") ?? string.Empty,
            ["period_type"] = Text(evt, "period_type") ?? string.Empty,
            ["store"] = Text(evt, "store") ?? string.Empty
        };

    private static string? Text(JsonElement element, string name)
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

    private static double? Number(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static DateTimeOffset? UnixMilliseconds(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        long? milliseconds = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };

        return milliseconds is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds.Value) : null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(id => id.Length > 0)
            .ToArray();
    }
}
