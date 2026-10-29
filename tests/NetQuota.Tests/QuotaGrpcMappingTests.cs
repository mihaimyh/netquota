using Grpc.Core;
using NetQuota.Grpc;

namespace NetQuota.Tests;

/// <summary>
/// Covers the gRPC surface: quota and rate-limit denials must reach the client as
/// <c>ResourceExhausted</c> with a stable message, and non-quota exceptions must pass through
/// unchanged. See <see cref="QuotaExceptionGrpc"/> and <see cref="QuotaExceptionInterceptor"/>.
/// </summary>
public class QuotaGrpcMappingTests
{
    // ---------- doubles ----------

    private sealed class StubServerCallContext : ServerCallContext
    {
        protected override string MethodCore => "/test.Service/Method";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "ipv4:127.0.0.1:12345";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => [];
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore { get; } = [];
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore { get; } =
            new(string.Empty, new Dictionary<string, List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) =>
            Task.CompletedTask;
    }

    private static QuotaExceededException QuotaExceeded() =>
        new("user-1", "api_calls", PeriodType.Monthly, used: 50, limit: 50);

    private static RateLimitExceededException RateLimited(TimeSpan retryAfter) =>
        new(
            "user-1",
            "api_calls",
            new RateLimitStatus(
                Allowed: false,
                Limit: 10,
                Remaining: 0,
                ResetAtUtc: DateTimeOffset.UtcNow.Add(retryAfter),
                RetryAfter: retryAfter));

    // ---------- mapping ----------

    [Fact]
    public void QuotaExceeded_MapsToResourceExhausted()
    {
        var exception = QuotaExceeded();

        Assert.True(QuotaExceptionGrpc.TryMap(exception, out var rpcException));
        Assert.NotNull(rpcException);
        Assert.Equal(StatusCode.ResourceExhausted, rpcException!.StatusCode);
        Assert.Contains("Quota exceeded", rpcException.Status.Detail, StringComparison.Ordinal);
        Assert.Contains("user-1", rpcException.Status.Detail, StringComparison.Ordinal);
        Assert.Empty(rpcException.Trailers);
    }

    [Fact]
    public void RateLimitExceeded_MapsToResourceExhausted_WithRetryAfter()
    {
        var exception = RateLimited(TimeSpan.FromSeconds(42.4));

        Assert.True(QuotaExceptionGrpc.TryMap(exception, out var rpcException));
        Assert.NotNull(rpcException);
        Assert.Equal(StatusCode.ResourceExhausted, rpcException!.StatusCode);

        var entry = Assert.Single(
            rpcException.Trailers,
            e => e.Key == QuotaExceptionGrpc.RetryAfterMetadataKey);
        Assert.Equal("43", entry.Value); // ceil, never the truncated seconds
    }

    [Fact]
    public void RateLimitExceeded_SubSecondRetryAfter_IsAtLeastOne()
    {
        Assert.True(QuotaExceptionGrpc.TryMap(RateLimited(TimeSpan.FromMilliseconds(200)), out var rpcException));

        var entry = Assert.Single(
            rpcException!.Trailers,
            e => e.Key == QuotaExceptionGrpc.RetryAfterMetadataKey);
        Assert.Equal("1", entry.Value);
    }

    [Fact]
    public void OtherQuotaExceptions_MapToTheirOwnCode()
    {
        (QuotaException Exception, StatusCode Expected)[] cases =
        [
            (new QuotaConfigValidationException("bad config"), StatusCode.InvalidArgument),
            (new UnsupportedQuotaOperationException("merge"), StatusCode.Unimplemented),
            (new SameUserMergeException("user-1"), StatusCode.FailedPrecondition),
            (new UserSealedException("user-1"), StatusCode.FailedPrecondition),
            (new QuotaStorageUnavailableException("firestore down"), StatusCode.Unavailable),
            (new CircuitBreakerOpenException(), StatusCode.Unavailable),
            (new QuotaException("unexpected"), StatusCode.Internal),
        ];

        foreach (var (exception, expected) in cases)
        {
            Assert.True(QuotaExceptionGrpc.TryMap(exception, out var rpcException));
            Assert.Equal(expected, rpcException!.StatusCode);
            Assert.Equal(exception.Message, rpcException.Status.Detail);
        }
    }

    [Fact]
    public void NonQuotaException_IsNotMapped()
    {
        Assert.False(QuotaExceptionGrpc.TryMap(new InvalidOperationException("boom"), out var rpcException));
        Assert.Null(rpcException);

        Assert.False(QuotaExceptionGrpc.TryMap(null, out rpcException));
        Assert.Null(rpcException);
    }

    [Fact]
    public void ToRpcException_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => QuotaExceptionGrpc.ToRpcException(null!));
    }

    // ---------- interceptor ----------

    [Fact]
    public async Task UnaryInterceptor_ConvertsQuotaDenial()
    {
        var interceptor = new QuotaExceptionInterceptor();

        var rpcException = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            await interceptor.UnaryServerHandler<string, string>(
                "request",
                new StubServerCallContext(),
                (_, _) => throw QuotaExceeded());
        });

        Assert.Equal(StatusCode.ResourceExhausted, rpcException.StatusCode);
    }

    [Fact]
    public async Task UnaryInterceptor_PassesThroughOtherExceptions()
    {
        var interceptor = new QuotaExceptionInterceptor();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await interceptor.UnaryServerHandler<string, string>(
                "request",
                new StubServerCallContext(),
                (_, _) => throw new InvalidOperationException("boom"));
        });
    }

    [Fact]
    public async Task UnaryInterceptor_ReturnsTheUnaryResult()
    {
        var interceptor = new QuotaExceptionInterceptor();

        var result = await interceptor.UnaryServerHandler<string, string>(
            "request",
            new StubServerCallContext(),
            (request, _) => Task.FromResult(request.Length.ToString()));

        Assert.Equal("7", result);
    }
}
