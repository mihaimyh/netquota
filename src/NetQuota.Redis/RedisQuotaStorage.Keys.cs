namespace NetQuota.Redis;

public sealed partial class RedisQuotaStorage
{
    // Flat keys: no {hash tags} because the scripts touch per-user usage keys and per-request
    // idempotency keys, which cannot share a cluster slot (see the class remarks).
    //
    // Every segment is percent-encoded so "a:b" + "c" cannot collide with "a" + "b:c".

    private string EntitlementKey(string userId) =>
        $"{_options.KeyPrefix}entitlement:{Segment(userId)}";

    private string UsageKey(string userId, string resource, PeriodWindow window) =>
        $"{_options.KeyPrefix}usage:{Segment(userId)}:{Segment(resource)}:{window.PeriodType}:{Segment(window.Key)}";

    private string DistinctKey(string userId, string resource, PeriodWindow window) =>
        $"{_options.KeyPrefix}distinct:{Segment(userId)}:{Segment(resource)}:{window.PeriodType}:{Segment(window.Key)}";

    private string ConsumptionKey(string userId, string idempotencyKey) =>
        RecordKey($"consume:{ScopedKey(userId, idempotencyKey)}");

    private string RefundKey(string userId, string idempotencyKey) =>
        RecordKey($"refund:{ScopedKey(userId, idempotencyKey)}");

    private string RecordKey(string logicalKey) =>
        $"{_options.KeyPrefix}record:{Segment(logicalKey)}";

    private string RateLimitKey(string resource, string subject) =>
        $"{_options.KeyPrefix}ratelimit:{Segment(resource)}:{Segment(subject)}";

    private string RateLimitGlobalKey(string resource) =>
        $"{_options.KeyPrefix}ratelimit_global:{Segment(resource)}";

    // Scoping must be injective: joining raw fields made ("a:b", "c") and ("a", "b:c") produce the
    // same record key, so one user's request replayed another user's charge. Escape each segment
    // exactly like UsageKey/DistinctKey; ids without reserved characters keep their legacy key.
    private static string ScopedKey(string userId, string idempotencyKey)
    {
        var prefix = $"{Segment(userId)}:";
        return idempotencyKey.StartsWith(prefix, StringComparison.Ordinal)
            ? idempotencyKey
            : prefix + Segment(idempotencyKey);
    }

    private static string Segment(string raw) => Uri.EscapeDataString(raw);
}
