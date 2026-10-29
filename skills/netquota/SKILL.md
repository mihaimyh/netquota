---
name: netquota
description: Use when adding or changing quotas, rate limits, tiered entitlements, prepaid credits, promotions, identity merges, or Stripe/RevenueCat billing webhooks in a .NET service that depends on the NetQuota packages. Covers initialization, adapter choice, invariants, and the failure modes the library already fixed.
---

# NetQuota

Transactional quota and rate-limiting library for .NET (`net10.0`). Packages:
`NetQuota`, `NetQuota.Firestore`, `NetQuota.Redis`, `NetQuota.Postgres`,
`NetQuota.AspNetCore`, `NetQuota.Billing`, `NetQuota.Grpc`.

## When to use

Use this skill when the task mentions: daily/monthly quotas, usage caps, 429/402 responses, fair
use, prepaid credits, lifetime credits, tier upgrades/downgrades, promotions or trials, anonymous →
account migration, Stripe/RevenueCat webhooks updating entitlements.

Do **not** hand-roll counters in Firestore/SQL. Do **not** add a second quota mechanism next to
NetQuota.

## Initialize

```csharp
var config = new QuotaConfig
{
    DefaultTier = "free",
    PeriodTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest"),
    Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
    {
        ["free"] = new TierConfig
        {
            DailyQuotas = new Dictionary<string, int> { ["reports"] = 5 },
            MonthlyQuotas = new Dictionary<string, int> { ["api_calls"] = 1000 },
            ConsumptionOrder = [PeriodType.Daily, PeriodType.Monthly]
        },
        ["pro"] = new TierConfig
        {
            DailyQuotas = new Dictionary<string, int> { ["reports"] = -1 },
            MonthlyQuotas = new Dictionary<string, int> { ["api_calls"] = 50_000 }
        }
    }
};
config.Validate();

var storage = new FirestoreQuotaStorage(firestoreDb);   // or Redis / Postgres
var manager = new QuotaManager(storage, config);
```

ASP.NET Core: `services.AddNetQuota(config, _ => storage);`

## Choose the adapter

| Need | Adapter |
| --- | --- |
| Cloud Run + Firestore stack | `NetQuota.Firestore` |
| Low latency, shared across instances | `NetQuota.Redis` |
| Relational stack, merges + audit in SQL | `NetQuota.Postgres` |
| Unit tests | `InMemoryQuotaStorage` |
| Hot cache + durable store | `TieredQuotaStorage` |

Redis cannot merge identities. PostgreSQL and in-memory rate limiting are per process.

## Invariants — do not break these

1. Resolve tiers through `GetEffectiveTierAsync` or `QuotaEntitlement.ResolveEffectiveTier`.
   Reading `Tier` directly ignores an active promotion.
2. `SetEntitlementAsync` must never clear a promotion; only `RevokePromotionAsync` does. Do not
   rebuild entitlements without copying `Promotion`, `Timezone`, `Sealed`, `MigratedTo`,
   `SealExpiresAtUtc`.
3. Always pass an `IdempotencyKey` for anything that touches money or a provider retry. Credit
   grants/refunds must use the provider event id.
4. `-1` means unlimited. Never use `0` as "unlimited"; `0` means "not configured" and falls back to
   the tier default for recurring periods.
5. Use `PeriodType.Auto` for user-facing consumption so the cascade in `ConsumptionOrder` decides
   which pool is charged; report `result.Period` and `result.ResetAtUtc`.
6. Use `DistinctKey` for "same entity must not be charged twice in a period" (e.g. `cui:123`).
7. Fail closed on storage errors. Do not enable `FallbackConfig.OptimisticAllowance` for quotas
   without a cap and a metric.
8. Prune retention: Firestore TTL policy on `expiresAt`; PostgreSQL `PruneExpiredAsync`.
9. Webhook endpoints read the raw body before JSON middleware and verify the signature.
10. Never trust `X-Forwarded-For` for webhook rate limiting unless a trusted proxy overwrites it.

## Common mistakes

- Calling `ConsumeAsync` and ignoring `ConsumeResult.Period` with `Auto` — the log/metric then lies
  about which pool was charged.
- Using `SetUsageAsync` for refunds — it overwrites counters. Use `RefundWithResultAsync` or
  `RefundFromConsumeAsync`.
- Treating an idempotent replay as a new charge (`result.Replayed` is true, nothing moved).
- Adding a new Stripe price without updating `StripeOptions.PriceToTier` — events for it are
  ignored (fail closed), so the user keeps their current tier until you map it.
- Assuming RevenueCat sends `app_user_id` on `TRANSFER` — it does not.
- Using the same `IdempotencyKey` for two different operations (grant vs refund).
- Granting credits in a callback *and* in the webhook handler — double credit.

## Testing

- Unit: `new QuotaManager(new InMemoryQuotaStorage(() => now), config)` with a controllable clock.
- Redis: set `NETQUOTA_REDIS=localhost:6379` (tests skip otherwise).
- PostgreSQL: set `NETQUOTA_POSTGRES='Host=...;Port=5433;...'`.
- Firestore: set `FIRESTORE_EMULATOR_HOST=127.0.0.1:8085`.
- Billing: pure parsers/verifiers, no network; use a fake `HttpMessageHandler` for checkout/sync.

## Reference

Repository: https://github.com/mihaimyh/netquota — `docs/` has getting-started, adapters,
aspnetcore, billing and operations guides.
