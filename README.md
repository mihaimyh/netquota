# NetQuota

Transactional quotas, prepaid credits, tier promotions, cross-identity merges and hybrid rate
limiting for .NET — with interchangeable storage adapters.

NetQuota is the .NET port of [goquota](https://github.com/mihaimyh/goquota). It keeps the same
semantics (business quota vs. rate limiting, fail-closed by default, idempotent writes) and fixes
the production bugs found in the original library (see `docs/adapters.md`).

## Packages

| Package | Purpose |
| --- | --- |
| `NetQuota` | Core: `QuotaManager`, models, in-memory/tiered storage, circuit breaker, metrics |
| `NetQuota.Firestore` | Google Cloud Firestore adapter (transactions + TTL) |
| `NetQuota.Redis` | Redis adapter (atomic Lua scripts) |
| `NetQuota.Postgres` | PostgreSQL adapter (row locks + advisory locks, merges, audit log) |
| `NetQuota.AspNetCore` | Rate limiter bridge, endpoint filter, `/usage` endpoint, headers |
| `NetQuota.Billing` | Stripe and RevenueCat webhooks, checkout, transfers, reconciliation |
| `NetQuota.Grpc` | gRPC server interceptor: quota/rate-limit denials map to `ResourceExhausted` |

All packages target `net10.0` (LTS until November 2028).

## Quick start

```bash
dotnet add package NetQuota
```

```csharp
using NetQuota;

var config = new QuotaConfig
{
    DefaultTier = "free",
    PeriodTimeZone = TimeZoneInfo.Utc,          // or "Europe/Bucharest"
    Tiers = new Dictionary<string, TierConfig>
    {
        ["free"] = new TierConfig
        {
            DailyQuotas = new Dictionary<string, int> { ["reports"] = 5 },
            MonthlyQuotas = new Dictionary<string, int> { ["api_calls"] = 100 },
            InitialForeverCredits = new Dictionary<string, int> { ["credits"] = 3 },
            WarningThresholds = new Dictionary<string, IReadOnlyList<double>>
            {
                ["reports"] = [0.8, 1.0]
            },
            ConsumptionOrder = [PeriodType.Daily, PeriodType.Monthly, PeriodType.Forever]
        },
        ["pro"] = new TierConfig
        {
            DailyQuotas = new Dictionary<string, int> { ["reports"] = -1 }, // -1 = unlimited
            MonthlyQuotas = new Dictionary<string, int> { ["api_calls"] = 10_000 }
        }
    }
};

var manager = new QuotaManager(new InMemoryQuotaStorage(), config);

var result = await manager.ConsumeWithResultAsync("user-1", "reports", 1, PeriodType.Auto);
if (!result.Allowed)
{
    // 429 / 402 your choice; result carries Remaining, ResetAtUtc and the charged Period.
}
```

`PeriodType.Auto` cascades through `TierConfig.ConsumptionOrder` and reports which pool paid
(`result.Period`), so callers never have to guess.

## Storage adapters

```csharp
// Firestore
var storage = new NetQuota.Firestore.FirestoreQuotaStorage(FirestoreDb.Create("my-project"));

// Redis
var storage = new NetQuota.Redis.RedisQuotaStorage(await ConnectionMultiplexer.ConnectAsync("localhost:6379"));

// PostgreSQL (creates the schema unless disabled)
var dataSource = NpgsqlDataSource.Create(connectionString);
var storage = await NetQuota.Postgres.PostgresQuotaStorage.CreateAsync(dataSource);
```

Capabilities differ by adapter: merges are supported by in-memory, Firestore and PostgreSQL, but
not Redis (same as goquota). See `docs/adapters.md`.

## ASP.NET Core

```csharp
builder.Services.AddNetQuota(config, _ => new FirestoreQuotaStorage(firestoreDb));

app.UseRateLimiter();

app.MapGet("/v1/reports/{id}", Handler)
   .WithNetQuota(new NetQuotaEnforcementOptions
   {
       ResolveUserIdAsync = ctx => ValueTask.FromResult(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)),
       ResolveResourceAsync = _ => ValueTask.FromResult("reports"),
       ResolveAmountAsync = NetQuotaAmountExtractors.Fixed(1),
       PeriodType = PeriodType.Auto
   });
```

The filter consumes, maps quota/rate-limit rejections to 429 (configurable), emits
`X-RateLimit-*`, `Retry-After` and `X-Quota-Warning-*`, and honours `X-Request-ID` for
idempotency. See `docs/aspnetcore.md`.

## Billing

```csharp
var stripe = new StripeBillingProvider(
    new StripeOptions
    {
        WebhookSecret = configuration["Stripe:WebhookSecret"]!,
        PriceToTier = new Dictionary<string, string> { ["price_pro"] = "pro" },
        TierToPriceId = new Dictionary<string, string> { ["pro"] = "price_pro" },
        TierRanking = ["pro"]
    },
    new QuotaManagerBillingStore(manager),
    customerStore,
    httpClient,
    (evt, ct) => audit.RecordAsync(evt, ct));

var result = await stripe.HandleWebhookAsync(rawBody, request.Headers["Stripe-Signature"]);
```

Stripe retries and duplicate deliveries are safe: credit grants/refunds use the provider event id
as the idempotency key. RevenueCat `TRANSFER`, `NON_RENEWING_PURCHASE` and unknown event types are
handled without silently downgrading users. See `docs/billing.md`.

## Documentation

- [`docs/getting-started.md`](docs/getting-started.md) — configuration, periods, tiers, DI
- [`docs/adapters.md`](docs/adapters.md) — Firestore / Redis / PostgreSQL, retention, semantics
- [`docs/aspnetcore.md`](docs/aspnetcore.md) — middleware, usage endpoint, headers
- [`docs/billing.md`](docs/billing.md) — Stripe and RevenueCat integration
- [`docs/grpc.md`](docs/grpc.md) — gRPC interceptor and status mapping
- [`docs/operations.md`](docs/operations.md) — metrics, pruning, failure modes
- [`docs/publishing.md`](docs/publishing.md) — release tags, GitVersion, NuGet Trusted Publishing
- [`skills/netquota/SKILL.md`](skills/netquota/SKILL.md) — agent skill

## Build and test

```bash
dotnet build NetQuota.slnx -c Release
dotnet test NetQuota.slnx -c Release

# integration suites
docker run -d --rm --name netquota-redis -p 6379:6379 redis:7-alpine
docker run -d --rm --name netquota-postgres -e POSTGRES_USER=netquota -e POSTGRES_PASSWORD=netquota \
  -e POSTGRES_DB=netquota -p 5433:5432 postgres:16-alpine
export NETQUOTA_REDIS=localhost:6379
export NETQUOTA_POSTGRES='Host=127.0.0.1;Port=5433;Database=netquota;Username=netquota;Password=netquota'
# Firestore emulator: FIRESTORE_EMULATOR_HOST=127.0.0.1:8085
```

Integration suites skip (and report as skipped, never as passed) when their environment variable
is missing.

## License

MIT
