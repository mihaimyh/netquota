# Storage adapters

| Adapter | Consume | Promotions | Drain | Merge | Rate limiting | Audit log |
| --- | --- | --- | --- | --- | --- | --- |
| In-memory | yes | yes | yes | yes | per process | yes |
| Firestore | yes | yes | yes | yes | distributed | yes |
| Redis | yes | yes | yes | **no** | distributed (Lua) | no |
| PostgreSQL | yes | yes | yes | yes | per process | yes |
| Tiered (hot/cold) | yes | yes | yes | yes* | per config | cold only |

\* the tiered wrapper delegates only when both inner adapters support merges.

## Firestore

```csharp
var storage = new FirestoreQuotaStorage(
    FirestoreDb.Create("my-project"),
    new FirestoreQuotaOptions
    {
        EntitlementsCollection = "quota_entitlements",
        UsageCollection = "quota_usage",
        RateLimitsCollection = "quota_rate_limits",
        IdempotencyCollection = "quota_idempotency",
        AuditLogsCollection = "quota_audit_logs",
        UsageRetentionPastWindowEnd = TimeSpan.FromDays(7),
        UseServerTimestampProbe = false
    });
```

- Every consume/refund/top-up runs in a Firestore transaction.
- Idempotency document ids are SHA-256 hashes of the logical key, so caller-supplied
  `X-Request-ID` values are bounded and cannot collide. Legacy sanitized ids are still read for one
  TTL window during upgrades.
- Enable a Firestore TTL policy on `expiresAt` for the usage, idempotency and audit collections.
- `UseServerTimestampProbe` adds a write+read per `GetNowUtcAsync`; leave it off on Cloud Run.

## Redis

```csharp
var storage = new RedisQuotaStorage(
    await ConnectionMultiplexer.ConnectAsync(connectionString),
    new RedisQuotaOptions { KeyPrefix = "myapp:", UsageRetentionPastWindowEnd = TimeSpan.FromDays(7) });
```

- Every mutation is one Lua script; consume, refund, top-up, credit refund, drain, tier change and
  rate limiting are atomic.
- Entitlements never expire by default: expiring them silently downgrades paying users if the
  billing sync is late.
- Usage hashes and their distinct-key sets expire at window end + retention; forever credits never
  expire. Idempotency records live for the configured `IdempotencyTtl`; a non-positive TTL means the
  request is not remembered. Distinct-key sets written before this version get their TTL on the next
  write; expire any leftovers once during the upgrade.
- Idempotency record keys escape the user id and the request id separately, so `("a:b", "c")` and
  `("a", "b:c")` cannot share a record. Ids made only of unreserved characters keep their existing
  keys; ids that contain `:` change shape, so a request issued just before the upgrade may be
  replayed once. Existing `netquota:record:*` entries expire on their own TTL.
- Targets a single Redis instance / sentinel. Redis Cluster needs every key of a script in one slot,
  which per-request idempotency keys cannot guarantee.
- Merges are not supported (`UnsupportedQuotaOperationException`).

## PostgreSQL

```csharp
var dataSource = NpgsqlDataSource.Create(connectionString);
var storage = await PostgresQuotaStorage.CreateAsync(dataSource, new PostgresQuotaOptions
{
    EnsureSchema = true,                                 // idempotent DDL under an advisory lock
    UsageRetentionPastWindowEnd = TimeSpan.FromDays(7)
});
```

- Consume/refund/top-up/drain take a transaction-scoped advisory lock on the usage key, plus one on
  the idempotency record for money paths, so concurrent retries cannot double-credit.
- Merges lock both identities and every involved usage key in a deterministic order, then reject
  same-user and sealed accounts.
- `PruneExpiredAsync(now)` deletes expired recurring usage, distinct keys and idempotency records;
  schedule it (cron, worker, or a database job). Forever credits and entitlements are never pruned.
- Rate limiting uses in-process windows; use Redis or Firestore if you need it shared across
  instances.
- Build the data source with pooling, `Max Auto Prepare` and a bounded `Command Timeout`.

## Tiered (hot/cold)

```csharp
var storage = new TieredQuotaStorage(new TieredQuotaStorageConfig(
    Hot: memoryStorage,
    Cold: firestoreStorage,
    AsyncUsageSync: true,
    AsyncErrorHandler: ex => logger.LogWarning(ex, "cold sync failed")));
```

With `AsyncUsageSync` the cold write goes through a bounded channel drained by one background pump
and flushed on `DisposeAsync`; queue overflows are reported through `AsyncErrorHandler`. The storage
implements `IAsyncDisposable`, so register it as a singleton and let the host dispose it.

## Resilience

- `CircuitBreakerConfig` opens after N consecutive storage errors and fails fast for `ResetTimeout`.
- `FallbackConfig` can read stale cache (`FallbackToCache` + `MaxStaleness`) or use a secondary
  storage. Leave it disabled for fail-closed billing/quota paths.
- `FailOpenOnRateLimitError` (default true) degrades to allowing traffic when only the rate-limit
  check fails; quota reads/writes still fail closed.
