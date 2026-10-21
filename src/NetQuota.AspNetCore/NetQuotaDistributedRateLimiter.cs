using System.Globalization;
using System.Threading.RateLimiting;
using NetQuota;

namespace NetQuota.AspNetCore;

/// <summary>
/// Bridges <see cref="QuotaManager.CheckRateLimitAsync"/> into ASP.NET Core's
/// <see cref="System.Threading.RateLimiting.RateLimiter"/> so <c>.RequireRateLimiting(policy)</c>
/// can evaluate distributed windows in Firestore (or in-memory) transparently.
/// </summary>
/// <remarks>
/// <c>explicitTier</c> is an optional tier override. When it is <c>null</c> (the default) the
/// subject's entitlement is resolved and its effective tier is used, falling back to
/// <see cref="QuotaConfig.DefaultTier"/>. Weighted acquisitions charge one window slot per permit;
/// the charge is not transactional, so a multi-permit acquisition denied part way through keeps the
/// permits it already charged consumed.
/// </remarks>
public sealed class NetQuotaDistributedRateLimiter(
    QuotaManager manager,
    string resource,
    string subjectKey,
    string? explicitTier = null) : RateLimiter
{
    public override TimeSpan? IdleDuration => TimeSpan.Zero;

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount) =>
        AcquireAsyncCore(permitCount, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(permitCount);

        if (permitCount == 0)
        {
            return new NetQuotaRateLimitLease(
                new RateLimitStatus(true, int.MaxValue, int.MaxValue, DateTimeOffset.UtcNow, TimeSpan.Zero));
        }

        RateLimitStatus? status = null;
        for (var permit = 0; permit < permitCount; permit++)
        {
            status = await manager.CheckRateLimitAsync(
                subjectKey,
                resource,
                explicitTier,
                cancellationToken);

            if (!status.Allowed)
            {
                return new NetQuotaRateLimitLease(status);
            }

            if (status.Limit == int.MaxValue)
            {
                // No window is configured for the subject's tier, so every remaining permit fits.
                return new NetQuotaRateLimitLease(status);
            }
        }

        return new NetQuotaRateLimitLease(status!);
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
