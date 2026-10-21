# ASP.NET Core

## Register

```csharp
builder.Services.AddNetQuota(config, _ => new FirestoreQuotaStorage(firestoreDb));
```

`AddNetQuota` validates the config, registers `QuotaConfig`, `IQuotaStorage` and a singleton
`QuotaManager`. Pass a storage factory; without one it falls back to `InMemoryQuotaStorage`.

## Distributed rate limiter

`NetQuotaDistributedRateLimiter` bridges `QuotaManager.CheckRateLimitAsync` into
`System.Threading.RateLimiting`, so `RequireRateLimiting(policy)` can use distributed windows:

```csharp
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("refresh", context =>
    {
        var subject = $"{context.User.Identity?.Name ?? "anon"}:{context.Connection.RemoteIpAddress}";
        var manager = context.RequestServices.GetRequiredService<QuotaManager>();
        return RateLimitPartition.Get(subject, _ =>
            new NetQuotaDistributedRateLimiter(manager, "refresh", subject));
    });
});

app.UseRateLimiter();
```

The lease exposes `MetadataName.RetryAfter`; set `Retry-After` with
`Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds))` (sub-second delays must not become `0`).

By default the limiter resolves the subject's entitlement and charges against its effective tier
(promotions included), falling back to `QuotaConfig.DefaultTier`. Pass the optional fourth
constructor argument to pin a tier explicitly. `AcquireAsync(permitCount)` charges one window slot
per permit and grants the lease only when every permit fits; negative counts throw
`ArgumentOutOfRangeException`. A multi-permit denial is not rolled back: permits charged before the
denial stay consumed, so use permit counts above 1 only when over-charging on denial is acceptable.

## Endpoint filter

```csharp
app.MapPost("/v1/reports", Generate)
   .WithNetQuota(new NetQuotaEnforcementOptions
   {
       ResolveUserIdAsync = ctx => ValueTask.FromResult(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)),
       ResolveResourceAsync = _ => ValueTask.FromResult("reports"),
       ResolveAmountAsync = NetQuotaAmountExtractors.Fixed(1),
       ResolveIdempotencyKeyAsync = ctx => ValueTask.FromResult(ctx.Request.Headers["X-Request-ID"].FirstOrDefault()),
       PeriodType = PeriodType.Auto,
       QuotaExceededStatusCode = StatusCodes.Status429TooManyRequests,
       OnQuotaExceededAsync = null,
       OnRateLimitExceededAsync = null
   });
```

Amount extractors: `Fixed`, `FromHeader` (clamped at 1,000,000), `FromJsonBodyInt`,
`FromBodyByteLength`, and `FromBoundArgument<T>`.

Endpoint filters run **after** minimal-API argument binding. On an endpoint that declares a body
parameter (for example `MapPost("/reports", (ReportRequest body) => ...)`) the body has already been
consumed when the filter runs, so `FromJsonBodyInt` cannot re-read it and fails with a 500
`quota_amount_unresolved` instead of silently charging the fallback amount. The same fail-closed
response is returned for malformed JSON. Use the bound argument instead:

```csharp
app.MapPost("/reports", (ReportRequest body) => Generate(body))
   .WithNetQuota(new NetQuotaEnforcementOptions
   {
       ResolveUserIdAsync = ctx => ValueTask.FromResult(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)),
       ResolveResourceAsync = _ => ValueTask.FromResult("reports"),
       ResolveAmountFromArgumentsAsync =
           NetQuotaAmountExtractors.FromBoundArgument<ReportRequest>(body => body.Units)
   });
```

`FromJsonBodyInt` remains the right extractor for endpoints that only take `HttpContext` (and
therefore never read the body). Both extractors reject non-positive JSON amounts by falling back to
the configured fallback (at least 1) and clamp positive amounts at 1,000,000.

Headers:

- `X-RateLimit-Limit`, `X-RateLimit-Remaining`, `X-RateLimit-Reset` (unix seconds)
- `Retry-After` on rejections (≥ 1 second), including quota 429s derived from the window reset
- `X-Quota-Warning-Threshold`, `X-Quota-Warning-Used`, `X-Quota-Warning-Limit` when a threshold is
  crossed during the request

`X-Quota-Warning-*` is applied automatically when the filter consumes with a warning handler; the
thresholds come from `TierConfig.WarningThresholds`.

## Usage endpoint

```csharp
app.MapNetQuotaUsageEndpoint("/v1/me/usage", new NetQuotaUsageApiOptions(
    ResolveUserIdAsync: ctx => ValueTask.FromResult(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)),
    KnownResources: ["reports", "api_calls", "credits"],
    ResolveExplicitTierAsync: null,
    ResourceFilter: null,
    FilterInactiveResources: true));
```

Response shape:

```json
{
  "user_id": "u1",
  "tier": "pro",
  "status": "active",
  "promotion": { "tier": "premium", "expires_at": "...", "source": "support" },
  "resources": {
    "reports": {
      "limit": 10, "used": 3, "remaining": 7, "reset_at": "...",
      "breakdown": [
        { "source": "daily", "limit": 10, "used": 3, "balance": 7, "reset_at": "...", "distinct_keys": [] }
      ]
    }
  }
}
```

`status` is `active`, `expired` or `default`. If every quota read fails the endpoint returns 500
instead of an empty 200. `FilterInactiveResources` drops resources with no quota and no usage.

## Failure mapping

| Exception | Suggested status |
| --- | --- |
| `QuotaExceededException` | 429 or 402 |
| `RateLimitExceededException` | 429 + `Retry-After` |
| `UserSealedException` | 409/403 (identity merged) |
| `QuotaStorageUnavailableException` | 503 |
| `QuotaConfigValidationException` | 500 at startup, never at runtime |
