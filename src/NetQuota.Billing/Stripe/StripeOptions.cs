using System.Globalization;
using System.Text;

namespace NetQuota.Billing.Stripe;

public sealed record StripeOptions
{
    public required string WebhookSecret { get; init; }
    public string ApiKey { get; init; } = string.Empty;
    public string ApiBaseUrl { get; init; } = "https://api.stripe.com";
    public string DefaultTier { get; init; } = "free";

    /// <summary>Stripe price id -> internal tier name.</summary>
    public IReadOnlyDictionary<string, string> PriceToTier { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Internal tier name -> Stripe price id, used by checkout.</summary>
    public IReadOnlyDictionary<string, string> TierToPriceId { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Highest tier first; used to pick one subscription during reconciliation.</summary>
    public IReadOnlyList<string> TierRanking { get; init; } = [];

    /// <summary>Reject webhooks older than this; prevents replays of captured payloads.</summary>
    public int SignatureToleranceSeconds { get; init; } = 300;

    /// <summary>Fails fast on a configuration that would silently downgrade or reject every webhook.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(WebhookSecret))
        {
            throw new ArgumentException("Stripe WebhookSecret is required.", nameof(WebhookSecret));
        }

        if (string.IsNullOrWhiteSpace(DefaultTier))
        {
            throw new ArgumentException("Stripe DefaultTier is required.", nameof(DefaultTier));
        }

        if (PriceToTier.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
        {
            throw new ArgumentException("Stripe PriceToTier contains an empty price or tier.", nameof(PriceToTier));
        }

        if (TierToPriceId.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
        {
            throw new ArgumentException("Stripe TierToPriceId contains an empty tier or price.", nameof(TierToPriceId));
        }
    }
}

public static class StripeWebhookSignature
{
    /// <summary>
    /// Verifies Stripe's <c>Stripe-Signature: t=...,v1=...</c> scheme: HMAC-SHA256 over
    /// <c>{timestamp}.{payload}</c>, compared in constant time, within the tolerance window.
    /// </summary>
    public static bool IsValid(
        string? signatureHeader,
        string secret,
        byte[] payload,
        DateTimeOffset nowUtc,
        int toleranceSeconds = 300)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        long? timestamp = null;
        var signatures = new List<string>();

        foreach (var part in signatureHeader.Split(','))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
            {
                continue;
            }

            var name = pair[0].Trim();
            var value = pair[1].Trim();
            if (name == "t" &&
                long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                timestamp = seconds;
            }
            else if (name == "v1")
            {
                signatures.Add(value);
            }
        }

        if (timestamp is null || signatures.Count == 0)
        {
            return false;
        }

        if (Math.Abs(nowUtc.ToUnixTimeSeconds() - timestamp.Value) > toleranceSeconds)
        {
            return false;
        }

        var timestampBytes = Encoding.UTF8.GetBytes($"{timestamp.Value}.");
        var buffer = new byte[timestampBytes.Length + payload.Length];
        timestampBytes.CopyTo(buffer, 0);
        payload.CopyTo(buffer, timestampBytes.Length);

        var expected = BillingSignatures.HmacSha256(secret, buffer);
        foreach (var candidate in signatures)
        {
            if (TryHexDecode(candidate, out var bytes) && BillingSignatures.FixedTimeEquals(expected, bytes))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryHexDecode(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromHexString(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
