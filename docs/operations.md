# Operations

## Metrics (OpenTelemetry)

`OpenTelemetryQuotaMetrics` (meter `NetQuota`) emits:

| Instrument | Type | Tags |
| --- | --- | --- |
| `netquota.quota.consumption.total` | counter | `resource`, `tier`, `success` |
| `netquota.quota.consumption.amount` | histogram | `resource`, `tier` |
| `netquota.quota.check.duration` | histogram (s) | `resource` |
| `netquota.ratelimit.exceeded.total` | counter | `resource` |
| `netquota.ratelimit.check.duration` | histogram (s) | `resource` |
| `netquota.quota.warnings.total` | counter | `resource`, `tier`, `threshold` |
| `netquota.fallback.hits.total` | counter | `strategy` |
| `netquota.idempotency.hits.total` | counter | `operation_type` |

```csharp
builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter("NetQuota"));
builder.Services.AddSingleton<IQuotaMetrics, OpenTelemetryQuotaMetrics>();
```

Register the metrics instance once. `BusinessMetrics`-style signals (quota exhaustion, fallback use,
idempotency replays) are the ones worth alerting on.

## Retention

| Adapter | Retention |
| --- | --- |
| Firestore | TTL policy on `expiresAt` (usage, idempotency, audit) |
| Redis | `EXPIRE` at window end + `UsageRetentionPastWindowEnd`; forever credits never expire |
| PostgreSQL | `PruneExpiredAsync(now)` deletes recurring usage, distinct keys and expired records |
| In-memory | bounded LRU caches; process lifetime |

Schedule the PostgreSQL prune (daily is enough) and set the Firestore TTL policy in Terraform /
console. Without it, usage and idempotency collections grow forever.

## Failure modes

- **Storage outage**: quota reads/writes throw `QuotaStorageUnavailableException` unless a fallback
  is configured. Keep `FallbackConfig` disabled for money paths; if you enable `OptimisticAllowance`,
  cap it (default 10%) and monitor `netquota.fallback.hits.total`.
- **Rate-limit outage**: `FailOpenOnRateLimitError` (default `true`) allows traffic and records the
  fallback. Business quotas are unaffected.
- **Circuit breaker**: opens after `FailureThreshold` consecutive errors for `ResetTimeout`, then
  half-opens. Wire `OnStateChange` to a metric/log.
- **Cache**: `CacheConfig.Enabled` uses bounded LRU caches. Entitlement writes evict the user's
  usage entries; promotions are resolved on read, so no invalidation job is needed.

## Multi-instance notes

- Firestore and Redis rate limiting are shared; PostgreSQL and in-memory are per process.
- `QuotaManager.InvalidateEntitlement` only clears the local cache. When another instance changes an
  entitlement (grant/revoke, billing webhook), fan the user id out over pub/sub and call it on every
  instance, or keep `EntitlementTtl` short.
- Promotions are evaluated lazily against the current clock; no scheduler is required.

## Capacity

- Consume does one transaction/lock per call; batch only where the provider charges per call.
- `DistinctKey` dedupe keeps a set per `(user, resource, period)`; keep keys short (e.g. `cui:123`).
- Firestore: expect 1 read + 1 write per consume; the idempotency record adds 1 read/write when a key
  is supplied.
- Redis: one script round trip per consume.
- PostgreSQL: advisory lock + select + upsert in one transaction.
