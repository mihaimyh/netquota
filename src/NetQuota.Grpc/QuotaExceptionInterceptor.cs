using Grpc.Core;
using Grpc.Core.Interceptors;

namespace NetQuota.Grpc;

/// <summary>
/// Server interceptor that converts NetQuota exceptions into their gRPC status.
/// <para>
/// Register it once per server (<c>AddGrpc(o =&gt; o.Interceptors.Add&lt;QuotaExceptionInterceptor&gt;())</c>)
/// so a quota or rate-limit denial reaches the client as <see cref="StatusCode.ResourceExhausted"/>
/// instead of <see cref="StatusCode.Unknown"/>. Any other exception is left untouched.
/// </para>
/// </summary>
public sealed class QuotaExceptionInterceptor : Interceptor
{
    /// <inheritdoc />
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(request, context).ConfigureAwait(false);
        }
        catch (QuotaException exception)
        {
            throw exception.ToRpcException();
        }
    }

    /// <inheritdoc />
    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(requestStream, context).ConfigureAwait(false);
        }
        catch (QuotaException exception)
        {
            throw exception.ToRpcException();
        }
    }

    /// <inheritdoc />
    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            await continuation(request, responseStream, context).ConfigureAwait(false);
        }
        catch (QuotaException exception)
        {
            throw exception.ToRpcException();
        }
    }

    /// <inheritdoc />
    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            await continuation(requestStream, responseStream, context).ConfigureAwait(false);
        }
        catch (QuotaException exception)
        {
            throw exception.ToRpcException();
        }
    }
}
