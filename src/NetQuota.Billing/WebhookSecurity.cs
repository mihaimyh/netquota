using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;

namespace NetQuota.Billing;

/// <summary>
/// Fixed-window per-client limiter for webhook endpoints. Mirrors goquota's provider: the client IP
/// is taken from the connection unless <see cref="TrustProxy"/> is explicitly enabled, and expired
/// buckets are swept every 100 requests instead of on every call (BILLING-1 / BILLING-2).
/// </summary>
public sealed class WebhookRateLimiter(int limit, TimeSpan window, int cleanupEvery = 100)
{
    private sealed class Bucket
    {
        public int Count;
        public DateTimeOffset ResetAt;
    }

    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly int _cleanupEvery = Math.Max(1, cleanupEvery);
    private int _requests;

    public int Limit { get; } = limit;
    public TimeSpan Window { get; } = window;

    /// <summary>Honour <c>X-Forwarded-For</c>. Enable only behind a proxy that overwrites it.</summary>
    public bool TrustProxy { get; set; }

    public bool TryAcquire(string clientKey) => TryAcquire(clientKey, DateTimeOffset.UtcNow);

    public bool TryAcquire(string clientKey, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            if (++_requests % _cleanupEvery == 0)
            {
                CleanupCore(nowUtc);
                if (_requests >= _cleanupEvery * 10)
                {
                    _requests = 0;
                }
            }

            if (!_buckets.TryGetValue(clientKey, out var bucket) || nowUtc >= bucket.ResetAt)
            {
                _buckets[clientKey] = new Bucket { Count = 1, ResetAt = nowUtc.Add(Window) };
                return true;
            }

            if (bucket.Count >= Limit)
            {
                return false;
            }

            bucket.Count++;
            return true;
        }
    }

    public int Cleanup()
    {
        lock (_gate)
        {
            return CleanupCore(DateTimeOffset.UtcNow);
        }
    }

    private int CleanupCore(DateTimeOffset nowUtc)
    {
        var expired = _buckets
            .Where(pair => nowUtc >= pair.Value.ResetAt)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var key in expired)
        {
            _buckets.Remove(key);
        }

        return expired.Length;
    }

    /// <summary>
    /// Resolves the rate-limit key from the connection IP and (only when trusted) the first
    /// X-Forwarded-For entry. A spoofed header must never reset the bucket.
    /// </summary>
    public static string ResolveClientKey(string? remoteIp, string? forwardedFor, bool trustProxy)
    {
        if (trustProxy && !string.IsNullOrWhiteSpace(forwardedFor))
        {
            var first = forwardedFor.Split(',')[0].Trim();
            if (first.Length > 0)
            {
                return first;
            }
        }

        var remote = (remoteIp ?? string.Empty).Trim();
        if (remote.StartsWith('[') && remote.Contains("]:", StringComparison.Ordinal))
        {
            // Bracketed IPv6 with port: [::1]:5000 -> [::1]
            return remote[..(remote.LastIndexOf("]:", StringComparison.Ordinal) + 1)];
        }

        var portSeparator = remote.LastIndexOf(':');
        return portSeparator > 0 && remote.Count(c => c == ':') == 1
            ? remote[..portSeparator]
            : remote;
    }
}

public static class WebhookBodyReader
{
    public const int DefaultMaxBytes = 256 * 1024;

    /// <summary>Reads the raw body, rejecting empty or oversized payloads (DoS protection).</summary>
    public static async Task<byte[]> ReadAsync(
        Stream body,
        int maxBytes = DefaultMaxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var buffer = new MemoryStream(capacity: 8 * 1024);
        var chunk = new byte[8 * 1024];

        while (true)
        {
            var read = await body.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                throw new BillingPayloadException($"Webhook payload exceeds {maxBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            throw new BillingPayloadException("Webhook payload is empty.");
        }

        return buffer.ToArray();
    }
}

/// <summary>HMAC helpers shared by the Stripe and RevenueCat verifiers.</summary>
public static class BillingSignatures
{
    public static byte[] HmacSha256(byte[] key, byte[] payload)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(payload);
    }

    public static byte[] HmacSha256(string key, string payload) =>
        HmacSha256(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(payload));

    public static byte[] HmacSha256(string key, byte[] payload) =>
        HmacSha256(Encoding.UTF8.GetBytes(key), payload);

    public static bool FixedTimeEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        CryptographicOperations.FixedTimeEquals(left, right);

    public static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right));
}

/// <summary>
/// OpenTelemetry metrics for billing webhooks. Hybrid-billing usage is a counter, not a gauge
/// (OBS-3), so repeated events accumulate instead of overwriting a value.
/// </summary>
public sealed class BillingMetrics : IDisposable
{
    public const string DefaultMeterName = "NetQuota.Billing";

    private static readonly Lazy<BillingMetrics> LazyShared = new(() => new BillingMetrics());

    /// <summary>Process-wide instance; creating two Meters with one name duplicates instruments.</summary>
    public static BillingMetrics Shared => LazyShared.Value;

    private readonly Meter _meter;
    private readonly Counter<long> _webhookReceived;
    private readonly Counter<long> _webhookRejected;
    private readonly Counter<long> _webhookApplied;
    private readonly Counter<long> _transferApplied;
    private readonly Counter<long> _hybridBillingUsers;

    public BillingMetrics(string meterName = DefaultMeterName)
    {
        _meter = new Meter(meterName, "1.0.0");
        _webhookReceived = _meter.CreateCounter<long>("billing.webhook.received");
        _webhookRejected = _meter.CreateCounter<long>("billing.webhook.rejected");
        _webhookApplied = _meter.CreateCounter<long>("billing.webhook.applied");
        _transferApplied = _meter.CreateCounter<long>("billing.transfer.applied");
        // No "_total" suffix: OTel/Prometheus exporters append it for counters.
        _hybridBillingUsers = _meter.CreateCounter<long>("billing.hybrid_billing_users");
    }

    public void RecordReceived(string provider, string eventType) =>
        _webhookReceived.Add(1, Tags(provider, eventType));

    public void RecordRejected(string provider, string reason) =>
        _webhookRejected.Add(1,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("reason", reason));

    public void RecordApplied(string provider, string eventType, string tier) =>
        _webhookApplied.Add(1,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("event_type", eventType),
            new KeyValuePair<string, object?>("tier", tier));

    public void RecordTransfer(string provider, string role) =>
        _transferApplied.Add(1,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("role", role));

    public void RecordHybridBillingUser(string provider) =>
        _hybridBillingUsers.Add(1, new KeyValuePair<string, object?>("provider", provider));

    public void Dispose() => _meter.Dispose();

    private static KeyValuePair<string, object?>[] Tags(string provider, string eventType) =>
    [
        new("provider", provider),
        new("event_type", eventType)
    ];
}
