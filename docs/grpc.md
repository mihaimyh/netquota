# gRPC

`NetQuota.Grpc` turns NetQuota exceptions into the gRPC status the client can act on.

Without it, a service that rethrows a `QuotaExceededException` sends `Unknown`, so the client cannot
tell "50 requests/day reached" from a server fault and often retries, burning more quota.

| Exception | gRPC status |
| --- | --- |
| `QuotaExceededException` | `ResourceExhausted` |
| `RateLimitExceededException` | `ResourceExhausted` + `retry-after` trailer (seconds) |
| `QuotaConfigValidationException` | `InvalidArgument` |
| `UnsupportedQuotaOperationException` | `Unimplemented` |
| `SameUserMergeException`, `UserSealedException` | `FailedPrecondition` |
| `QuotaStorageUnavailableException`, `CircuitBreakerOpenException` | `Unavailable` |
| Any other `QuotaException` | `Internal` |
| Anything else | untouched |

## Register the interceptor

```csharp
builder.Services.AddGrpc(options => options.Interceptors.Add<QuotaExceptionInterceptor>());
```

The interceptor covers unary, client-streaming, server-streaming and duplex calls. Non-quota
exceptions keep their existing behaviour.

## Map an exception by hand

```csharp
try
{
    await manager.ConsumeAsync(userId, "api_calls", 1, PeriodType.Monthly);
}
catch (QuotaException ex) when (QuotaExceptionGrpc.TryMap(ex, out var rpcException))
{
    throw rpcException!;
}
```

`TryMap` returns `false` for anything that is not a `QuotaException`, so it is safe in a shared
error-mapping path. `ex.ToRpcException()` is the throwing variant.

For a rate-limit denial the mapped exception carries the wait time as trailing metadata:

```csharp
var entry = ex.Trailers.FirstOrDefault(e => e.Key == QuotaExceptionGrpc.RetryAfterMetadataKey);
var seconds = entry is null ? 1 : int.Parse(entry.Value, CultureInfo.InvariantCulture);
```

## Notes

- The package depends on `Grpc.Core.Api` only. The server package (`Grpc.AspNetCore.Server`)
  supplies the runtime.
- The interceptor maps the exception **type**, not the message, so callers do not parse strings.
- Because the namespace is `NetQuota.Grpc`, code already inside a `NetQuota.*` namespace must
  qualify `global::Grpc.Core` when it references gRPC types directly.
