# Getting started

## Install

```bash
dotnet add package NetQuota
dotnet add package NetQuota.Firestore   # or NetQuota.Redis / NetQuota.Postgres
dotnet add package NetQuota.AspNetCore  # optional
dotnet add package NetQuota.Billing     # optional
```

## Configure tiers

```csharp
var config = new QuotaConfig
{
    DefaultTier = "free",
    PeriodTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest"),
    IdempotencyTtl = TimeSpan.FromHours(24),
    Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
    {
        ["free"] = new TierConfig
        {
            DailyQuotas = new Dictionary<string, int> { ["reports"] = 5 },
            MonthlyQuotas = new Dictionary<string, int> { ["api_calls"] = 100 },
            ConsumptionOrder = [PeriodType.Daily, PeriodType.Monthly]
        }
    }
};
config.Validate(); // throws QuotaConfigValidationException
```

Rules:

- `-1` means unlimited. Any other negative value fails validation.
- `ConsumptionOrder` must not contain `Auto`; it defines the cascade for `PeriodType.Auto`.
- `InitialForeverCredits` is granted once per `(user, resource)` with a deterministic key.
- `WarningThresholds` are fractions in `(0, 1]`.

## Create the manager

```csharp
var manager = new QuotaManager(storage, config);
```

`QuotaManager` wraps the storage in a circuit breaker when `CircuitBreakerConfig.Enabled` is true,
and validates the config in the constructor.

## Consume

```csharp
var result = await manager.ConsumeWithResultAsync(
    userId: "user-1",
    resource: "reports",
    amount: 1,
    periodType: PeriodType.Auto,
    options: new ConsumeOptions(
        IdempotencyKey: requestId,       // retries are free
        DistinctKey: $"cui:{cui}",       // same key never charges twice
        WarningHandler: handler),
    explicitTier: null);

result.Allowed;        // false => quota exhausted
result.Period;         // concrete pool charged: Daily, Monthly or Forever
result.ConsumedNewUnit;// false on a distinct-key replay
result.Replayed;       // true on an idempotency replay
result.ResetAtUtc;     // window end for the charged period
```

Use `TryConsumeAsync` when a denial is a normal outcome (no exception), and `ConsumeAsync` when you
prefer `QuotaExceededException`.

## Entitlements and promotions

```csharp
await manager.SetEntitlementAsync(new QuotaEntitlement(
    UserId: userId,
    Tier: "pro",
    SubscriptionStartDateUtc: DateTimeOffset.UtcNow,
    UpdatedAtUtc: DateTimeOffset.UtcNow,
    ExpiresAtUtc: null,
    Timezone: "Europe/Bucharest"));

await manager.GrantPromotionAsync(new PromotionRequest(
    UserId: userId,
    Tier: "premium",
    Duration: TimeSpan.FromDays(14),
    Source: "support",
    Reason: "incident compensation",
    IdempotencyKey: $"promo:{ticketId}"));

await manager.GetEffectiveTierAsync(userId); // "premium" until it expires, then "pro"
```

Promotions are an overlay: `SetEntitlementAsync` never clears them, only `RevokePromotionAsync`
does. Resolve the tier through `QuotaEntitlement.ResolveEffectiveTier` (or
`GetEffectiveTierAsync`) — reading `Tier` directly ignores the overlay.

## Prepaid credits

```csharp
await manager.TopUpLimitAsync(userId, "credits", 10, idempotencyKey: $"topup:{paymentId}");
await manager.ConsumeWithResultAsync(userId, "credits", 2, PeriodType.Forever);
await manager.RefundCreditsAsync(userId, "credits", 2, reason: "refund", idempotencyKey: $"refund:{paymentId}");
```

`GetMeterQuotaAsync` is the UI view (spent bonus drops off the bar); `GetEffectiveQuotaAsync` is the
ledger view (granted total stays). `GetQuotaAsync(..., PeriodType.Forever)` returns the current
balance for a single pool.

## Tier changes

`ApplyTierChangeAsync(userId, newTier, resource)` prorates the current monthly cycle:
`used + round(newLimit * remainingFraction)`. The prorated limit is stored and preserved by later
consumes while the stored tier matches the current tier.

## Merging identities

```csharp
var merged = await manager.MergeUserAsync(new MergeUserRequest(
    SourceUserId: "anon:123",
    TargetUserId: "account:456",
    Resources: ["reports"],
    Periods: [PeriodType.Daily],
    IdempotencyKey: $"merge:{dayKey}:123:456",
    SealSource: true));

merged.DroppedPromotion; // non-null when the source had an overlay that was not moved
```

Daily/monthly usage moves as distinct keys (or as a sum when neither side has them); forever credits
move as the remaining balance. `SealSource` writes a tombstone and blocks further consumption for
the source. Redis does not support merges.

## Time

Daily boundaries use `PeriodTimeZone` unless the entitlement carries a `Timezone`; set it with
`UpdateTimezoneAsync`. Monthly cycles are anniversaries of `SubscriptionStartDateUtc`, with
month-end snap-back (Jan 31 → Feb 28 → Mar 31).
