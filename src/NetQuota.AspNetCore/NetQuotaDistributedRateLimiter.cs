using System.Globalization;
using System.Threading.RateLimiting;
using NetQuota;

namespace NetQuota.AspNetCore;

/// <summary>
/// Bridges <see cref="QuotaManager.CheckRateLimitAsync"/> into ASP.NET Core's
/// <see cref="System.Threading.RateLimiting.RateLimiter"/> so <c>.RequireRateLimiting(policy)</c>
/// can evaluate distributed windows in Firestore (or in-memory) transparently.
/// </summary>
public sealed class NetQuotaDistributedRateLimiter(
    QuotaManager manager,
    string resource,
    string subjectKey) : RateLimiter
{
    public override TimeSpan? IdleDuration => TimeSpan.Zero;

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount) =>
        AcquireAsyncCore(permitCount, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount,
        CancellationToken cancellationToken)
    {
        var status = await manager.CheckRateLimitAsync(
            subjectKey,
            resource,
            explicitTier: "ANONYMOUS",
            cancellationToken);

        return new NetQuotaRateLimitLease(status);
    }

    private sealed class NetQuotaRateLimitLease(RateLimitStatus status) : RateLimitLease
    {
        public override bool IsAcquired => status.Allowed;

        public override IEnumerable<string> MetadataNames =>
            status.RetryAfter > TimeSpan.Zero ? [MetadataName.RetryAfter.Name] : [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (string.Equals(metadataName, MetadataName.RetryAfter.Name, StringComparison.Ordinal) &&
                status.RetryAfter > TimeSpan.Zero)
            {
                metadata = status.RetryAfter;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}
