namespace NetQuota.Billing.Stripe;

/// <summary>
/// A hosted Stripe Checkout session built from an inline price, so a consumer can charge an amount
/// it computed itself (report, seat, one-off service) without pre-creating a Price in Stripe and
/// without teaching NetQuota anything about the product. <see cref="Metadata"/> is round-tripped
/// verbatim into the webhook event.
/// </summary>
public sealed record StripeCheckoutRequest
{
    /// <summary>Consumer user id; becomes <c>client_reference_id</c> and <c>metadata[user_id]</c>.</summary>
    public required string UserId { get; init; }

    /// <summary>Amount in the currency's minor unit (bani, cents). Must be positive.</summary>
    public required long AmountMinor { get; init; }

    /// <summary>Three-letter lowercase ISO code; Stripe rejects anything else.</summary>
    public string Currency { get; init; } = "ron";

    /// <summary>Shown on the checkout page and on the receipt.</summary>
    public required string ProductName { get; init; }

    /// <summary><c>payment</c> (default) or <c>subscription</c>.</summary>
    public string Mode { get; init; } = "payment";

    /// <summary><c>day</c>|<c>week</c>|<c>month</c>|<c>year</c> for <c>mode=subscription</c>; null for payment.</summary>
    public string? RecurringInterval { get; init; }

    public required string SuccessUrl { get; init; }

    public required string CancelUrl { get; init; }

    public string? CustomerEmail { get; init; }

    /// <summary>Consumer payload; copied to the session, the PaymentIntent and the Subscription.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Replayed create calls with the same key return the same session (max 255 chars).</summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>Identifiers of a created Checkout Session; redirect the browser to <see cref="Url"/>.</summary>
public sealed record StripeCheckoutSession(string SessionId, string Url);
