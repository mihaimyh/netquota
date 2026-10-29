using System.Globalization;
using Grpc.Core;

namespace NetQuota.Grpc;

/// <summary>
/// Maps NetQuota exceptions onto gRPC statuses.
/// <para>
/// A gRPC server that lets a <see cref="QuotaExceededException"/> escape returns <c>Unknown</c>,
/// so a client cannot tell "50 requests/day reached" from a server fault and often retries,
/// burning more quota. These helpers give the denial a stable code and keep the message.
/// </para>
/// </summary>
public static class QuotaExceptionGrpc
{
    /// <summary>
    /// Trailing metadata key carrying the number of seconds a client should wait, set on a mapped
    /// <see cref="RateLimitExceededException"/>. Mirrors the HTTP <c>Retry-After</c> header.
    /// </summary>
    public const string RetryAfterMetadataKey = "retry-after";

    /// <summary>
    /// Maps a NetQuota exception onto an <see cref="RpcException"/>, or returns <see langword="false"/>
    /// for anything that is not a <see cref="QuotaException"/> (leave those to the server).
    /// </summary>
    /// <param name="exception">The exception thrown by the service implementation.</param>
    /// <param name="rpcException">The mapped exception when the method returns true.</param>
    public static bool TryMap(Exception? exception, out RpcException? rpcException)
    {
        rpcException = exception switch
        {
            // Denials: the caller exhausted a budget, not a server fault.
            RateLimitExceededException rateLimit => Build(
                StatusCode.ResourceExhausted,
                rateLimit.Message,
                rateLimit.RetryAfter),
            QuotaExceededException quota => Build(StatusCode.ResourceExhausted, quota.Message),

            // Client mistakes and unsupported operations are not retryable.
            QuotaConfigValidationException validation => Build(StatusCode.InvalidArgument, validation.Message),
            UnsupportedQuotaOperationException unsupported => Build(StatusCode.Unimplemented, unsupported.Message),
            SameUserMergeException sameUser => Build(StatusCode.FailedPrecondition, sameUser.Message),
            UserSealedException sealedUser => Build(StatusCode.FailedPrecondition, sealedUser.Message),

            // Storage outages (including an open circuit breaker) are transient.
            QuotaStorageUnavailableException unavailable => Build(StatusCode.Unavailable, unavailable.Message),

            // Any other QuotaException is a library-side failure.
            QuotaException quotaException => Build(StatusCode.Internal, quotaException.Message),
            _ => null,
        };

        return rpcException is not null;
    }

    /// <summary>
    /// Converts a NetQuota exception into the <see cref="RpcException"/> a gRPC service should throw.
    /// </summary>
    /// <exception cref="ArgumentException">The argument is not a <see cref="QuotaException"/>.</exception>
    public static RpcException ToRpcException(this QuotaException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!TryMap(exception, out var rpcException))
        {
            throw new ArgumentException("Only NetQuota exceptions can be mapped.", nameof(exception));
        }

        return rpcException!;
    }

    private static RpcException Build(StatusCode statusCode, string message, TimeSpan? retryAfter = null)
    {
        var status = new Status(statusCode, message);
        var trailers = new Metadata();

        if (retryAfter is { } delay)
        {
            // A sub-second reset still needs a positive value so a client does not hot-loop.
            var seconds = Math.Max(1, (long)Math.Ceiling(delay.TotalSeconds));
            trailers.Add(RetryAfterMetadataKey, seconds.ToString(CultureInfo.InvariantCulture));
        }

        return new RpcException(status, trailers);
    }
}
