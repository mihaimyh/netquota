# NetQuota — Master Audit Report

**Repository:** `E:\devel\repos\netquota` · **Commit audited:** `4adf356` (main, 2026-10-21) · **Report date:** 2026-10-21
**Scope:** all 6 shipping packages + the test suite (31 C# files, ~14.5k LOC)
**Method:** 7 module audits in parallel, then a boundary pass over the seams; every finding below is anchored to source and, where possible, to a runnable repro in `.tmp/audit/repros/00_repro_index.md`.

---

## 1. Executive Summary & Stack Overview

NetQuota is a mature, unusually well-documented .NET quota engine, but it is **not shippable in its current state for any multi-tenant paid deployment**. Three classes of defect dominate:

1. **Trust-boundary bypasses in the ASP.NET layer** (`F1`, `F2`). The default endpoint filter accepts a client-supplied `X-Request-ID` as an idempotency key and accepts `amount = 0`. Together they let an unauthenticated-in-quota caller execute a protected endpoint indefinitely without consuming quota or hitting rate limits. These are one-line defaults, which makes them the cheapest high-impact fixes in the repo.
2. **Money-path non-idempotency and partial-refund over-revocation** (`F3`, `F4`). Forever credits are keyed by `user + idempotencyKey` — never by `resource` — in all four adapters, and Stripe partial refunds revoke the entire credit pack once per refund event. Both are explicitly called out in `AGENTS.md` as bugs that must not be reintroduced.
3. **Entitlement rewrite data loss** (`F5`, `F6`, `H1`). Every adapter's `SetEntitlementAsync` is a *union* merge except for `Timezone`, which is overwritten with `null`; Firestore additionally never deletes `expiresAtUtc` and preserves an *expired* seal forever. The billing layer masks this by going through `BillingEntitlementTransition.Merge`, so the bug only fires on the public storage/manager API — the path integrators are told to use in `docs/getting-started.md`.

Architecturally the picture is much healthier than the defect list suggests: the core has **zero project and zero package references**, adapters depend only on the core, and there is no layering inversion anywhere. The storage abstraction is genuinely capability-detected (`IUserMerger`, `IPromotionStore`, `IQuotaTimeSource`), the circuit breaker and tiered storage are real implementations rather than stubs, and the Postgres adapter's advisory-lock hierarchy is ordered correctly (`record → user → usage`) with no deadlock window found.

| Metric | Value |
|---|---|
| Build | **0 errors**, 404 warnings (all `xUnit1051`, test project only) |
| Test run (`dotnet test NetQuota.slnx -c Release --no-build`) | **75 total, 51 passed, 24 skipped, 0 failed**, 1.14 s |
| Test run (`dotnet run --project tests/NetQuota.Tests -c Release --no-build`) | 75 total, 51 passed, 24 skipped, 0 failed, 0.34 s |
| Test run with implicit build (`dotnet test` without `--no-build`) | **"Zero tests ran", exit code 5** — see `I1` |
| Analyzers | default Roslyn only; no `EnableNETAnalyzers` escalation, no `TreatWarningsAsErrors`, no third-party analyzers |
| Findings (module pass) | 65 raw → **54 consolidated** after de-duplication |
| Severity (consolidated) | **6 Critical · 16 High · 17 Medium · 15 Low** |
**Verdict:** fix `F1`–`F6` before the next release; they are all small, local changes. The package layout, adapter contracts and locking strategy should be preserved — see §7.

### Stack / runtime breakdown

| Layer | Project | TFM | Third-party | LOC |
|---|---|---|---|---|
| core | `src/NetQuota` | net10.0 | none | 4,394 |
| backend storage | `src/NetQuota.Postgres` | net10.0 | Npgsql 10.0.3 | 1,814 |
| backend storage | `src/NetQuota.Redis` | net10.0 | StackExchange.Redis 3.4.5 | 1,126 |
| backend storage | `src/NetQuota.Firestore` | net10.0 | Google.Cloud.Firestore 4.5.0 | 1,541 |
| backend billing | `src/NetQuota.Billing` | net10.0 | none (raw HTTP) | 2,288 |
| backend transport | `src/NetQuota.AspNetCore` | net10.0 | ASP.NET Core shared framework | 500 |
| infra | `tests/NetQuota.Tests` | net10.0 | xunit.v3 4.0.2, coverlet.MTP | 2,800 |

---

## 2. Subsystem & Feature Catalog

### 2.1 Consolidated directory breakdown

**`core` — `src/NetQuota`**
- `QuotaManager.cs` (1,672) — the public surface: consume/peek/refund, entitlement + promotion lifecycle, merge orchestration, quota views, cache + single-flight + fallback.
- `InMemoryQuotaStorage.cs` (771) — reference adapter; also the semantics oracle every other adapter is compared against.
- `TieredStorageAndMetrics.cs` (969) — `LruQuotaCache`, `CircuitBreakerQuotaStorage`, `OpenTelemetryQuotaMetrics`, `TieredQuotaStorage`.
- `QuotaModels.cs` (504) — `QuotaConfig`, `TierConfig`, `QuotaEntitlement`, `PeriodWindow` (DST/anniversary math), result records.
- `LocalRateLimitEngine.cs` (253) — token bucket + sliding window, per-instance.
- `QuotaAbstractions.cs` (225) — `IQuotaStorage`, `IUserMerger`, `IPromotionStore`, `IQuotaTimeSource`, exceptions.

**`backend/storage`**
- `src/NetQuota.Postgres` — schema DDL, consumption/refund/credit record tables, advisory-lock helpers, merge, audit, entitlements.
- `src/NetQuota.Redis` — key layout, six Lua scripts (consume, refund, top-up, refund-credits, drain, set-entitlement, tier-change, set-usage, rate-limit), server-time source.
- `src/NetQuota.Firestore` — transactional adapter, SHA-256 idempotency doc ids, server-timestamp probe, audit collection, rate limiting.

**`backend/billing`** — `StripeWebhookParser`/`StripeBillingProvider`, `RevenueCatWebhook`/`RevenueCatBillingProvider`, `WebhookSecurity` (HMAC + IP + rate limiter), `BillingModels` (`QuotaManagerBillingStore`, `BillingEntitlementTransition`, customer store).

**`backend/transport`** — `WithNetQuota` endpoint filter, `NetQuotaAmountExtractors`, `MapNetQuotaUsageEndpoint`, `NetQuotaDistributedRateLimiter` (`System.Threading.RateLimiting` bridge), header helpers.

**`infra`** — one xUnit v3 project; Redis/Postgres suites skip via `Assert.SkipWhen`; Firestore suite currently **returns early** instead.

### 2.2 Dependency graph (verified from `.csproj`)

```
                              ┌───────────────────────────────────────┐
                              │            NetQuota (core)            │
                              │  QuotaManager · models · InMemory     │
                              │  Tiered · CircuitBreaker · LRU        │
                              │  project refs: 0   package refs: 0    │
                              └───────────────────┬───────────────────┘
                                                  │ (one-way, no cycles)
        ┌──────────────┬──────────────┬───────────┴────────┬────────────────────┐
        ▼              ▼              ▼                    ▼                    ▼
  NetQuota.Redis  NetQuota.Postgres  NetQuota.Firestore  NetQuota.Billing  NetQuota.AspNetCore
  StackExchange    Npgsql 10.0.3      Google.Cloud         raw HttpClient    ASP.NET Core
  .Redis 3.4.5     (advisory locks)   .Firestore 4.5.0     (Stripe/RC API)   (filters, RateLimiter)
        │              │              │                    │                    │
        └──────────────┴──────────────┴─────────┬──────────┴────────────────────┘
                                                 ▼
                                   tests/NetQuota.Tests (xunit.v3)
```

**Layering verdict: PASS.** No adapter references another adapter; nothing above the core is imported by the core; `NetQuota.Billing` talks to quotas exclusively through `QuotaManagerBillingStore` (`BillingModels.cs:111`), not through a storage type. The only inversion-ish smell is behavioural, not structural: `QuotaManager` owns billing semantics (`InitialForeverCredits`) while `Billing` owns entitlement transitions, so the same invariant is enforced in two places with different results (`X3`, `X4`).

---

## 3. Cross-Boundary Architectural Review

These are seams that no single-module audit could see. They are ranked by blast radius.

### X1 — Casing split-brain: tiers are case-insensitive, resources and price maps are not
`QuotaConfig.Tiers` is built with `StringComparer.OrdinalIgnoreCase` (`QuotaModels.cs:75`), but every per-tier dictionary defaults to `StringComparer.Ordinal` (`QuotaModels.cs:42-47`) and `GetConfiguredLimit` does a plain `TryGetValue(resource, …)` (`QuotaManager.cs:1300-1304`). Billing repeats the pattern: `StripeOptions.PriceToTier`/`TierToPriceId` (`StripeOptions.cs:14-19`) and `RevenueCatOptions.EntitlementToTier` (`RevenueCatWebhook.cs:14`) are `Ordinal`, while `RevenueCatBillingProvider` and `QuotaConfig` compare tier names case-insensitively (`QuotaManager.cs` tier lookups, `RevenueCatBillingProvider.cs:290`).

Consequence: `SetEntitlementAsync(tier: "PRO")` succeeds, `GetConfiguredLimit(resource: "API")` silently returns `0` ("not configured") instead of the configured `api` quota, and `CheckoutUrlAsync("PRO")` misses a `"pro"` price mapping. `AGENTS.md` explicitly lists "tier casing sensitivity" as a bug not to reintroduce; it survives in the resource and price dimensions. **No test asserts any casing behaviour across adapters** (`TST-02`, `TST-03`).

### X2 — Adapter key-encoding divergence: the same identity is isolated in Redis, collides in Firestore, and is raw in Postgres
- Redis percent-encodes every segment: `Segment(raw) => Uri.EscapeDataString(raw)` (`RedisQuotaStorage.Keys.cs:39`) — deliberately collision-free ("`a:b` + `c` cannot collide with `a` + `b:c`", comment at `Keys.cs:8`).
- Firestore replaces only `/`, space and `:` with `_` (`FirestoreQuotaStorage.cs:1191-1200`, `SanitizeDocId` at `:1251`), so `("a_b","c")` and `("a","b_c")` map to the same usage document, as do `"user:1"` and `"user_1"` (`F/H9`).
- Postgres stores raw `text` primary keys — byte-exact, case-sensitive.

There is no shared `IQuotaKeyCodec` contract and no cross-adapter conformance test, so a tenant isolation guarantee holds on one backend and not on another. Any migration between adapters can also re-key documents (`%3A` vs `_`).

### X3 — Invariant 1 is enforced in the billing helper, not at the storage boundary
`BillingEntitlementTransition.Merge` correctly preserves `Promotion`, `Timezone`, `Sealed`, `MigratedTo`, `SealExpiresAtUtc` (`BillingModels.cs:248-268`). Every adapter's `SetEntitlementAsync` re-implements a *partial* version of the same merge: promotion is preserved, seal is preserved, **`Timezone` is not** (`InMemoryQuotaStorage.cs:65-86`, `PostgresQuotaStorage.Entitlements.cs:77`, `RedisQuotaStorage.Scripts.cs:218-219`, `FirestoreQuotaStorage.cs:91-108`). Because the billing path never calls the raw API, the defect is invisible to the billing tests and visible to every integrator following `docs/getting-started.md`. This is the single highest-leverage invariant to move *into* the storage contract (e.g. a `QuotaEntitlement.MergeFrom(existing)` helper the adapters must call).

### X4 — Entitlement write and credit grant are coupled by a tier-less idempotency key
`QuotaManager.SetEntitlementAsync` grants every `InitialForeverCredits` entry on **every** entitlement write, using `initial_bonus_{userId}_{resource}` (`QuotaManager.cs:117-133`). Consequences at the seam:
- upgrading to a tier with a *larger* initial allocation never grants the delta (same key, replay);
- downgrading to a tier with initial credits grants them even though the user is stepping down;
- a 3,650-day TTL means "once ever per user+resource", not "once per tier".

`BillingEntitlementTransition` has no notion of this side effect, so `StripeBillingProvider.SyncUserAsync` (which writes entitlements freely) can trigger it on a schedule. The idempotency domain should be `(user, resource, tier, grantVersion)`, and grant-on-transition should be explicit rather than a side effect of every write.

### X5 — `TieredQuotaStorage` inverts the cache on the read path and dual-writes without reconciliation
`GetUsageAsync` always reads **Cold** and then overwrites **Hot** (`TieredStorageAndMetrics.cs:774-778`): every read is a remote read *plus* a remote write, and Hot is never actually used as a read cache. Mutations (`TopUpForeverCreditsAsync`, `RefundForeverCreditsAsync`, `RefundAsync`, `DrainRemainingAsync`) are applied to Cold and then re-applied to Hot (`:834-899`) from Hot's own possibly-stale base, so the two stores can drift and only self-heal on the next Cold-first read. Worst case, `AsyncUsageSync` consumes from **Hot only** (`:802`) and enqueues the Cold mirror on a bounded channel with `BoundedChannelFullMode.DropWrite` (`:646-652`), whose failure handler only invokes `AsyncErrorHandler` (`:803-808`) — consumption is silently under-reported when the queue is full.

### X6 — Sealed-user outcome is unmapped at the transport boundary
`QuotaManager.ConsumeWithResultAsync` throws `UserSealedException` for a merged/sealed user (`QuotaManager.cs:529-532`); `WithNetQuota` catches nothing (`NetQuotaAspNetCoreExtensions.cs:238-277`), so ASP.NET turns it into a **500** instead of a 403/423 the caller can act on. Meanwhile `TieredQuotaStorage.IsUserSealedAsync` consults **Cold only** (`:937`) while reads prefer Hot, so the seal check and the entitlement read can disagree within one request. The core→transport error contract ("domain exception → status code") is implicit and untested (`TST-02`).

### X7 — `MergeUserResult.DroppedPromotion` means three different things
`QuotaManager` unconditionally overwrites the adapter's result with `sourceEnt.Promotion` (`QuotaManager.cs:1081-1085`) — even when the promotion is expired and even on an idempotent replay. Postgres filters active promotions when computing it but forgets it in the replay payload (`PostgresQuotaStorage.Merge.cs:10-12, 30-40`); Firestore never populates it and never clears the source promotion (`F/H7`); Redis does not support merges at all. A caller auditing "did we drop a paid promotion?" gets a different answer per backend and per retry.

### X8 — The quota view sums heterogeneous periods and leaks the `-1` sentinel
`BuildQuotaViewAsync` sums `usage.Limit` across Daily + Monthly + Forever into one `ResourceQuotaView.Limit/Used` (`QuotaManager.cs:1219-1253`). A tier of 10/day + 100/month + 50 forever is serialized as `limit: 160`, a number that corresponds to no real budget; unlimited is emitted as `-1`, which clients must special-case (`QuotaModels.cs:326`). The ASP.NET usage endpoint re-serializes the same object into hand-written `snake_case` (`NetQuotaAspNetCoreExtensions.cs:400-415`), so there is a *third* schema (record → anonymous object → JSON) with no contract test. `MapNetQuotaUsageEndpoint` additionally returns HTTP 200 with a partial `resources` map when only some reads fail (`:417-430`, `L7`).

---

## 4. Master Bug Table (verified only)

Consolidated from the 65 raw module findings. Duplicates across adapters are merged under one ID with all anchors listed. Severity follows the taxonomy in the brief; **TST-01 was re-ranked from Critical to High** (`H15`) because a silently-skipped test is a coverage defect, not data corruption or a security bypass.

### 4.1 Critical

| ID | Finding | Modules | Primary anchor |
|---|---|---|---|
| **F1** | Client-supplied `X-Request-ID` is the default consume idempotency key; a replay returns `Allowed=true, Replayed=true` and the filter still executes the endpoint | AspNetCore | `NetQuotaAspNetCoreExtensions.cs:149-153, 240-276` |
| **F2** | `amount = 0` is accepted by `FromHeader`/`FromJsonBodyInt` and short-circuits `ConsumeWithResultAsync` before quota **and** rate-limit checks | AspNetCore, core | `NetQuotaAspNetCoreExtensions.cs:85-141`; `QuotaManager.cs:515-518` |
| **F3** | Partial Stripe refunds revoke the **full** credit pack, and each partial refund carries a new `evt_` id so the full pack is revoked again | Billing | `StripeWebhookParser.cs:47-48, 162-204`; `StripeBillingProvider.cs:84-96` |
| **F4** | Forever credits: null/empty idempotency key means "no idempotency" (double grant on retry) while a shared key collapses **all resources** into one — the key never contains `resource` | core, Redis, Firestore, Billing | `QuotaManager.cs:910-984`; `InMemoryQuotaStorage.cs:414-422, 446-454`; `RedisQuotaStorage.cs:463-486`; `FirestoreQuotaStorage.cs:614, 662` |
| **F5** | Firestore `SetOptions.MergeAll` never emits `FieldValue.Delete` for `expiresAtUtc`, so a lifetime/non-expiring tier keeps the **old expiry** and is downgraded to `DefaultTier` on read | Firestore | `FirestoreQuotaStorage.cs:91-110, 1387-1431, 1438-1440` |
| **F6** | Expired seal is permanent: `SetEntitlementAsync` tests `existing.Sealed` instead of `existing.IsSealedAt(now)`, and sealing a source with no entitlement document writes no `tier`/`subscriptionStartDateUtc`, so `ParseEntitlement` fabricates a **new** `UtcNow` anchor on every read (drifting monthly cycle) | Firestore | `FirestoreQuotaStorage.cs:99-107, 888-897, 1436, 1456` |

### 4.2 High

| ID | Finding | Modules | Primary anchor |
|---|---|---|---|
| **H1** | `Timezone` (and, when already sealed, `MigratedTo`/`SealExpiresAtUtc`) dropped on entitlement rewrite — all four adapters | all storage | `InMemoryQuotaStorage.cs:65-86`; `PostgresQuotaStorage.Entitlements.cs:77`; `RedisQuotaStorage.Scripts.cs:218-219`; `FirestoreQuotaStorage.cs:91-108` |
| **H2** | Read-path limit resolution ignores Invariant 5: `-1` never overrides a stored cap, and a `null` stored `Tier` keeps a stale prorated limit after a tier change | core | `InMemoryQuotaStorage.cs:150-152`; `QuotaManager.cs:1496-1521` |
| **H3** | `TieredQuotaStorage` drops `Tier` when mirroring Cold→Hot (`SetUsageAsync` has no tier parameter), so prorated caps are discarded; sync-mode consumes never write a Hot idempotency record; seal check goes Cold-only | core | `TieredStorageAndMetrics.cs:767-800, 847-859, 937` |
| **H4** | Period windows misalign across DST and ignore the user's timezone for anniversary cycles (off-by-one-hour boundaries, 23h/25h days; `ForMonthly` always uses UTC midnight as the anchor) | core | `QuotaModels.cs:235-260`; `QuotaManager.cs:1256-1274` |
| **H5** | Circuit breaker trips on caller cancellation (5 aborted requests open it globally) and `HalfOpen` admits unlimited concurrent probes | core | `TieredStorageAndMetrics.cs:444-489` |
| **H6** | Postgres: an **expired** idempotency row is invisible to the read (`expires_at_utc > now`) but still unique, so the re-insert raises `23505` and is rethrown to the caller | Postgres | `PostgresQuotaStorage.cs:104-122, 298-307`; `Records.cs:60-68, 100-106` |
| **H7** | Firestore `MergeUserAsync` replay returns **empty** `TransferredUsage`/`TransferredForeverCredits`, ignores the merge record's `expiresAt`, and never clears the source promotion | Firestore | `FirestoreQuotaStorage.cs:777-789, 900-917` |
| **H8** | Firestore merge pins the target limit to `0` when the target doc has `limit == 0` even if the source is `> 0`, drops `tier` when writing usage, and mishandles duplicate `request.Items` (stale pre-loop snapshots) | Firestore | `FirestoreQuotaStorage.cs:805-813, 830-836, 867-880` |
| **H9** | Firestore doc ids collide across `(userId, resource)` splits and can be invalid/reserved (`.`, `..`, `__x__`), breaking tenant isolation or throwing from Firestore | Firestore | `FirestoreQuotaStorage.cs:1191-1200, 1251-1252` |
| **H10** | Global rate limit is one Firestore document rewritten on every request (unbounded `ticks` array, 1 write/s/document, 1 MiB cap); Redis enforces `GlobalLimit` for sliding-window only | Firestore, Redis | `FirestoreQuotaStorage.cs:947-998`; `RedisQuotaStorage.cs:541-553` |
| **H11** | `WebhookRateLimiter` trusts the **leftmost** `X-Forwarded-For` hop (spoofable), has no bucket cap (OOM), and admits one request when `limit <= 0` | Billing | `WebhookSecurity.cs:20, 33-60, 89-98` |
| **H12** | Incoming Stripe subscription/invoice webhooks parse `customer_id` but never persist it, so delete reconciliation and `PortalUrlAsync` fail after a restart or Payment-Link checkout | Billing | `StripeBillingProvider.cs:106-154, 338-354` |
| **H13** | `NetQuotaDistributedRateLimiter` hardcodes `explicitTier: "ANONYMOUS"` (entitled users throttled at anonymous rates) and ignores `permitCount` | AspNetCore | `NetQuotaDistributedRateLimiter.cs:21-35` |
| **H14** | Sync-over-async `.AsTask().GetAwaiter().GetResult()` in `AttemptAcquireCore` blocks a thread-pool thread on remote I/O with `CancellationToken.None` | AspNetCore | `NetQuotaDistributedRateLimiter.cs:21-22` |
| **H15** | All 5 Firestore integration tests `return` early instead of `Assert.SkipWhen`, so they report **passed with zero assertions** (Invariant 7) | infra | `NetQuotaFirestoreConcurrencyTests.cs:21-25, 98-102, 154-158, 196-200, 246-250` |
| **H16** | Zero tests reference `NetQuota.AspNetCore` at all, despite the project reference; every `F1`, `F2`, `H13`, `H14` regression would pass CI | infra | `NetQuota.Tests.csproj:23` |

### 4.3 Medium

| ID | Finding | Anchor |
|---|---|---|
| **M1** | Unbounded growth: `_idempotency`, `_sealedUsers`, `_auditLogs`, `_merges` never swept; `QuotaManager._optimisticUsage` keys by window and never evicts | `InMemoryQuotaStorage.cs:28-35`; `QuotaManager.cs:15, 1545-1565` |
| **M2** | Redis `distinct` sets are `SADD`-ed but never `EXPIRE`-d → permanent per-window memory growth | `RedisQuotaStorage.Scripts.cs:76-77, 270-277, 286-290` |
| **M3** | Redis passes `""` for absent `KEYS[i]` (hashes to slot 0) and uses no `{hash}` tags. `docs/adapters.md:50` correctly scopes the adapter to a single node/sentinel, so this is a portability limit rather than a bug — but the empty-key pattern still breaks any proxy/sharding layer, and a cluster deployment fails with `CROSSSLOT` even for single-key operations | `RedisQuotaStorage.cs:242-252, 361-371, 463-491, 556-563`; `Keys.cs:10-33` |
| **M4** | `SetUsageAsync(distinctKeys: null, used > 0)` wipes existing distinct keys in Postgres, Redis and Firestore, diverging from the InMemory reference semantics | `PostgresQuotaStorage.cs:275-276`; `RedisQuotaStorage.cs:318-349`; `FirestoreQuotaStorage.cs:393-412` |
| **M5** | Unchecked `int` arithmetic in consume/top-up/view allows overflow to negative, bypassing the limit check or producing a negative balance | `InMemoryQuotaStorage.cs:214, 430`; `QuotaManager.cs:663, 1224-1242, 1551` |
| **M6** | Circuit breaker hardcodes `UtcNow` (untestable deterministically) and runs `_onStateChange` inside the success `try`, so a throwing observer converts a **committed** operation into a failure and trips the breaker | `TieredStorageAndMetrics.cs:208, 447-463, 485-488, 520` |
| **M7** | Postgres merge overwrites a target `-1` (unlimited) limit with the source's positive limit because the guard is `> 0` instead of `!= 0` | `PostgresQuotaStorage.Merge.cs:131-136` |
| **M8** | Postgres merge reports **expired** promotions as dropped and loses `DroppedPromotion` on replay (payload omits it) | `PostgresQuotaStorage.Merge.cs:10-12, 30-40, 75, 265-291` |
| **M9** | Redis: promotion writes don't refresh `EntitlementTtl`; `DrainRemainingAsync` creates a recurring hash with no TTL and can never be expired once drained to `limit = 0` | `RedisQuotaStorage.cs:157-186, 494-507`; `Scripts.cs:188-200, 238-257` |
| **M10** | Redis ignores `RateLimitBackend.InMemory`, and `SlidingWindowScript` returns `Rate` (not `GlobalLimit`) on global denial and `RetryAfter = 0` on a 429 | `RedisQuotaStorage.cs:527-586`; `Scripts.cs:310-319` |
| **M11** | Firestore `GetAuditLogsAsync` filters `UserId`/`Resource` **in memory** over the newest ≤2,000 docs, so a busy tenant's audit history silently returns nothing | `FirestoreQuotaStorage.cs:1148-1189` |
| **M12** | Firestore merge does `2N` sequential `GetSnapshotAsync` calls inside one transaction (500-op / lock-timeout risk at ~240 items) and doesn't cap `request.Items` | `FirestoreQuotaStorage.cs:775-918` |
| **M13** | Billing: all paid tiers rank `1` on multi-source transfer (arbitrary winner); `Ordinal` tier/entitlement maps; downgrade to `DefaultTier` keeps a paid `ExpiresAtUtc` | `RevenueCatBillingProvider.cs:266-292`; `StripeOptions.cs:14-19`; `StripeBillingProvider.cs:225-239, 417-423` |
| **M14** | ASP.NET: unbounded `EnableBuffering` + full JSON DOM parse before quota; no `Retry-After` on 429; no `UserSealedException` mapping; no `userId` length check | `NetQuotaAspNetCoreExtensions.cs:48-67, 113-141, 221-295` |
| **M15** | `RedisQuotaStorage` writes `DateTimeOffset.UtcNow` in three mutation paths despite being an `IQuotaTimeSource`, so instances disagree under clock drift | `RedisQuotaStorage.cs:302-303, 339-341, 506` |
| **M16** | Billing customer link/invariance: `QuotaManagerBillingStore` surfaces `string?` keys upward but `IBillingEntitlementStore` documents them optional, so no caller is forced to supply one (the `F4` contract half) | `BillingModels.cs:93-107, 124-140` |
| **M17** (`I1`) | `dotnet test` **with** implicit build reports `Zero tests ran` and exits 5; only `--no-build` (CI) or `dotnet run` works. Any contributor running the documented command sees a red build and no tests | verified: `dotnet test NetQuota.slnx -c Release` (exit 5) vs `--no-build` (75 tests, exit 0); `global.json:6-8` |

### 4.4 Low (no patches supplied; see module reports)

`L1` `PeekAsync(Auto)` doesn't cascade and defaults to Daily on empty `ConsumptionOrder` (`QuotaManager.cs:393-395`). `L2` `ConcurrentDictionary`/`ConcurrentQueue` inside `lock`-serialized engines (`LocalRateLimitEngine.cs:17-26`). `L3` missing `netquota_credit_records(expires_at_utc)` index + orphan `distinct_keys` (`PostgresQuotaSchema.cs:108-116`). `L4` two extra `SELECT NOW()` round-trips under advisory lock (`PostgresQuotaStorage.cs:219, 267`). `L5` Redis replay returns live distinct members and `cjson.encode({})` → `"{}"` causing a swallowed `JsonException` on every consume (`Scripts.cs:28-36`; `RedisQuotaStorage.cs:673`). `L6` Redis `SetUsageAsync` distinct wipe + `UtcNow` (`REDIS-11/12`). `L7` `MapNetQuotaUsageEndpoint` swallows `OperationCanceledException` and returns partial 200 (`:387-430`). `L8` length-leaking `FixedTimeEquals(string,string)` and uninjected `UtcNow` in parsers (`WebhookSecurity.cs:171-175`). `L9` wall-clock `Task.Delay(80)` circuit-breaker test (`QuotaManagerTests.cs:407-453`). `L10` Firestore server-timestamp probe writes one global doc per operation (`FirestoreQuotaStorage.cs:36-62`). `L11` Redis/Postgres tests lack `try/finally` cleanup and dispose nothing (`TST-04`). `L12` `FakeStore` in billing tests discards `userId`/`resource`/`amount`, so refund behaviour is unasserted (`NetQuotaBillingTests.cs:43-57`). `L13` `InMemoryQuotaStorage.GetAuditLogsAsync` is `O(N log N)` over the full log. `L14` `QuotaManager.SupportsMerge` doesn't unwrap `TieredQuotaStorage`, producing `UnsupportedQuotaOperationException` on a supported configuration (`QuotaManager.cs:51-60`). `L15` `CircuitBreakerQuotaStorage` accepts no clock, making `L9` unavoidable.

### 4.5 Critical — root cause, repro, patch

> All diffs are minimal and assume the audited line numbers. `R<n>` refers to `.tmp/audit/repros/00_repro_index.md`.

#### F1 — Client-controlled idempotency key (ASP-01)
**Root cause.** `NetQuotaEnforcementOptions.ResolveIdempotencyKeyAsync` defaults to `X-Request-ID` (`:149-153`). `QuotaManager` checks the idempotency record before rate limiting and before debiting (`QuotaManager.cs:578-609` vs `:612`) and returns `Allowed=true, Replayed=true, ConsumedNewUnit=false`. `WithNetQuota` only branches on `!result.Allowed` (`:254`) and then calls `next(...)` (`:276`) — so a replayed request re-executes the endpoint for free, forever, until `IdempotencyTtl` (24 h default) elapses.
**Repro.** `R0`, then replay `curl -H 'X-Request-ID: fixed' …` against any `WithNetQuota` endpoint; or unit-test the filter with the same header twice and assert `next` ran twice. Static trace: `:240-276`.
**Patch.**
```diff
--- a/src/NetQuota.AspNetCore/NetQuotaAspNetCoreExtensions.cs
+++ b/src/NetQuota.AspNetCore/NetQuotaAspNetCoreExtensions.cs
@@
-    public Func<HttpContext, ValueTask<string?>> ResolveIdempotencyKeyAsync { get; init; } =
-        ctx => ValueTask.FromResult(
-            ctx.Request.Headers.TryGetValue(NetQuotaHeaderNames.RequestId, out var v) && !string.IsNullOrWhiteSpace(v)
-                ? (string?)v.ToString()
-                : null);
+    // A client-supplied header is not a trust boundary. Only derive an idempotency key when the
+    // host also enables response caching; otherwise a replay is a free endpoint execution.
+    public Func<HttpContext, ValueTask<string?>> ResolveIdempotencyKeyAsync { get; init; } =
+        _ => ValueTask.FromResult<string?>(null);
@@
-                return await next(invocationContext);
+                // Replayed consumptions must not re-run non-idempotent downstream work.
+                return result.Replayed
+                    ? Results.StatusCode(options.QuotaExceededStatusCode)
+                    : await next(invocationContext);
```

#### F2 — `amount = 0` is free (ASP-02)
**Root cause.** The extractors accept `parsed >= 0` / `val >= 0` (`:90, :123`), and `ConsumeWithResultAsync` only rejects negative amounts (`QuotaManager.cs:515-518`) then returns `Allowed` for `amount == 0` before the rate-limit and limit checks (`:538-574`). `FromBodyByteLength` also overflows `int` for `Content-Length > int.MaxValue` (`:109`).
**Repro.** `R1` — passes today; must fail.
**Patch.**
```diff
--- a/src/NetQuota.AspNetCore/NetQuotaAspNetCoreExtensions.cs
+++ b/src/NetQuota.AspNetCore/NetQuotaAspNetCoreExtensions.cs
@@
-                parsed >= 0)
+                parsed >= 1)
@@
-            var units = (int)Math.Ceiling((double)len / divisor);
-            return ValueTask.FromResult(Math.Max(minimumUnits, units));
+            var units = (int)Math.Min(1_000_000L, (long)Math.Ceiling((double)len / divisor));
+            return ValueTask.FromResult(Math.Max(Math.Max(1, minimumUnits), units));
@@
-                    val >= 0)
+                    val >= 1)
--- a/src/NetQuota/QuotaManager.cs
+++ b/src/NetQuota/QuotaManager.cs
@@
-        if (amount < 0)
+        if (amount <= 0)
         {
-            throw new QuotaConfigValidationException("Consume amount cannot be negative.");
+            throw new QuotaConfigValidationException("Consume amount must be positive.");
         }
```

#### F3 — Partial refund revokes the whole pack, repeatedly (BIL-01)
**Root cause.** `ParseCreditPurchase` reads `metadata["credits"]` — the original pack size — and never looks at `amount_refunded`/`amount` (`:171-203`). The refund is keyed by `refund.EventId` (`StripeBillingProvider.cs:92`), and Stripe emits a **new** event id for each partial refund, so the full pack is revoked again each time. Only `payment_intent.refunded` is handled (`:47-48`); the common `charge.refunded` is ignored.
**Repro.** `R6` — expects `credits == "20"` for a 20 % refund; gets `"100"`.
**Patch.**
```diff
--- a/src/NetQuota.Billing/Stripe/StripeWebhookParser.cs
+++ b/src/NetQuota.Billing/Stripe/StripeWebhookParser.cs
@@
-                "payment_intent.refunded" =>
+                "payment_intent.refunded" or "charge.refunded" =>
                     ParseCreditPurchase(BillingWebhookKind.Refund, "amount_refunded", type, eventId, timestamp, obj, options),
@@
-        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(resource) || credits is null or <= 0)
+        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(resource) || credits is null or <= 0)
         {
@@
         }
+
+        if (kind == BillingWebhookKind.Refund)
+        {
+            var total = Long(obj, "amount") ?? Long(obj, "amount_received") ?? 0;
+            var refunded = Long(obj, "amount_refunded") ?? 0;
+            if (refunded <= 0)
+            {
+                return BillingWebhookResult.Ignored("refund with zero amount_refunded");
+            }
+
+            if (total > 0 && refunded < total)
+            {
+                credits = (int)Math.Round(
+                    credits.Value * (double)refunded / total, MidpointRounding.AwayFromZero);
+            }
+        }
```
> Note: `credits` is currently a `int?` local; the patch mutates it after the null check, so `credits.Value` is safe. For full-refund idempotency across both event types, key the revocation by `pi_...` (already in `Metadata["payment_intent_id"]`) instead of `evt_...`.

#### F4 — Forever-credit idempotency is both missing and over-broad (CQE-01 / REDIS-07 / FS-M01 / BIL-02)
**Root cause.** Two opposite failures in one contract:
1. `QuotaManager.TopUpLimitAsync` / `RefundCreditsAsync` / `GrantOneTimeCreditAsync` accept `string? idempotencyKey = null` and forward it (`:914, :932, :977`). Adapters skip idempotency entirely for null/whitespace (`InMemoryQuotaStorage.cs:414`, `:446`) ⇒ a retried webhook double-grants.
2. When a key **is** supplied, adapters prefix only `userId`, never `resource` (`InMemoryQuotaStorage.cs:416, :448`; `RedisQuotaStorage.cs:463-465, 484-486`; `FirestoreQuotaStorage.cs:614, 662`; Postgres mirrors it) ⇒ one event id across two resources credits only the first.
**Repro.** `R2` — both halves.
**Patch (central + reference adapter; same two lines in the other three).**
```diff
--- a/src/NetQuota/QuotaManager.cs
+++ b/src/NetQuota/QuotaManager.cs
@@
     public async Task TopUpLimitAsync(
         string userId,
         string resource,
         int amount,
         string? idempotencyKey = null,
         CancellationToken cancellationToken = default)
     {
@@
-        var now = await GetNowUtcAsync(cancellationToken);
+        RequireIdempotencyKey(idempotencyKey);
+        var now = await GetNowUtcAsync(cancellationToken);
@@
     public async Task RefundCreditsAsync(
@@
-        var now = await GetNowUtcAsync(cancellationToken);
+        RequireIdempotencyKey(idempotencyKey);
+        var now = await GetNowUtcAsync(cancellationToken);
@@
+    private static void RequireIdempotencyKey(string? key)
+    {
+        if (string.IsNullOrWhiteSpace(key))
+        {
+            throw new QuotaConfigValidationException(
+                "An idempotency key derived from the provider event id is required on money paths.");
+        }
+    }
--- a/src/NetQuota/InMemoryQuotaStorage.cs
+++ b/src/NetQuota/InMemoryQuotaStorage.cs
@@
-                var idemKey = $"topup:{userId}:{idempotencyKey}";
+                var idemKey = $"topup:{userId}:{resource}:{idempotencyKey}";
@@
-                var idemKey = $"refund_credits:{userId}:{idempotencyKey}";
+                var idemKey = $"refund_credits:{userId}:{resource}:{idempotencyKey}";
```
> Compatibility note: changing the key shape invalidates in-flight idempotency records for up to `IdempotencyTtl` (24 h). Ship with a one-release dual-read (check old key first) or accept a 24 h replay window. Make the `IBillingEntitlementStore` parameters non-nullable in the same PR so callers cannot compile a keyless grant (`M16`).

#### F5 — Firestore never deletes a cleared expiry (FS-C01)
**Root cause.** `SerializeEntitlement` only adds `expiresAtUtc` when non-null (`:1402-1405`) and writes with `SetOptions.MergeAll` (`:110`), which leaves absent keys untouched. A user moving to a lifetime tier keeps the old timestamp; `ResolveEffectiveTier` then downgrades them (`QuotaModels.cs:212-215`) with no billing event to correct it. The same pattern (no `FieldValue.Delete`) affects `sealed`, `migratedTo`, `sealedUntilUtc`.
**Repro.** `R5` (emulator) — merge a lifetime entitlement over an expiring one, assert `ExpiresAtUtc is null` on read.
**Patch.**
```diff
--- a/src/NetQuota.Firestore/FirestoreQuotaStorage.cs
+++ b/src/NetQuota.Firestore/FirestoreQuotaStorage.cs
@@
-                if (!effective.Sealed && existing.Sealed)
+                if (string.IsNullOrWhiteSpace(effective.Timezone) && !string.IsNullOrWhiteSpace(existing.Timezone))
+                {
+                    effective = effective with { Timezone = existing.Timezone };
+                }
+
+                if (!effective.Sealed && existing.IsSealedAt(effective.UpdatedAtUtc))
                 {
@@
-        if (ent.ExpiresAtUtc is { } exp)
-        {
-            dict["expiresAtUtc"] = Timestamp.FromDateTimeOffset(exp);
-        }
+        dict["expiresAtUtc"] = ent.ExpiresAtUtc is { } exp
+            ? Timestamp.FromDateTimeOffset(exp)
+            : FieldValue.Delete;
@@
-        if (ent.Sealed)
+        if (ent.Sealed)
         {
@@
         }
+        else
+        {
+            dict["sealed"] = false;
+            dict["migratedTo"] = FieldValue.Delete;
+            dict["sealedUntilUtc"] = FieldValue.Delete;
+        }
```

#### F6 — Firestore expired seal is permanent, and sealed ghosts drift (FS-C02)
**Root cause.** `ParseEntitlement` sets `Sealed = true` whenever `sealedUntilUtc` exists, even if past (`:1456`); `SetEntitlementAsync` then only preserves when `!effective.Sealed && existing.Sealed` (no expiry check), so the seal is re-written forever. Separately, `MergeUserAsync` seals a source with no entitlement doc by writing `sealed/migratedTo/sealedUntilUtc` **without** `tier` or `subscriptionStartDateUtc` (`:888-897`); `ParseEntitlement` then substitutes `DateTimeOffset.UtcNow` for the anchor on **every read** (`:1436`), so the monthly window slides.
**Repro.** `R5`: seal with `SealExpiresAtUtc = now.AddMinutes(-1)`, rewrite the entitlement, assert the user is not sealed; then merge a user with no entitlement and assert the anchor is stable across two reads.
**Patch.**
```diff
--- a/src/NetQuota.Firestore/FirestoreQuotaStorage.cs
+++ b/src/NetQuota.Firestore/FirestoreQuotaStorage.cs
@@
-                if (!effective.Sealed && existing.Sealed)
+                if (!effective.Sealed && existing.IsSealedAt(effective.UpdatedAtUtc))
@@
             if (!srcEntSnap.Exists)
             {
                 tx.Set(srcEntRef, new Dictionary<string, object>
                 {
                     ["userId"] = request.SourceUserId,
+                    ["tier"] = "free",
+                    ["subscriptionStartDateUtc"] = Timestamp.FromDateTimeOffset(request.NowUtc),
                     ["sealed"] = true,
                     ["migratedTo"] = request.TargetUserId,
                     ["sealedUntilUtc"] = Timestamp.FromDateTimeOffset(request.SealExpiresAtUtc!.Value),
                     ["updatedAtUtc"] = Timestamp.FromDateTimeOffset(request.NowUtc)
                 }, SetOptions.MergeAll);
             }
```

---

## 5. Master Algorithmic & Scalability Bottlenecks

Measured baselines are the only numbers produced by execution; the per-operation costs are **derived from source**, not benchmarked.

### 5.1 Measured baseline

| Run | Wall clock | Notes |
|---|---|---|
| `dotnet build -c Release` (incremental) | 5.5 s | 0 errors, 404 `xUnit1051` warnings |
| `dotnet test -c Release --no-build` | 1.14 s | 75 tests, 51 executed |
| `dotnet run --project tests/NetQuota.Tests -c Release --no-build` | 0.34 s | same counts, no MTP bridge |
| `dotnet test` with implicit build | fails | "Zero tests ran", exit 5 (`I1`) |

### 5.2 Operation-count model

`N` = items in one merge, `k` = distinct keys per window, `U` = audit rows, `B` = buckets in the webhook limiter.

| Path | Round trips / OS ops | Complexity | Where it hurts |
|---|---|---|---|
| `InMemoryQuotaStorage.TryConsumeAsync` | 1 lock | O(k) (copies `DistinctKeys`) | `k` unbounded per window |
| `PostgresQuotaStorage.TryConsumeAsync` | 4–6 (lock, record read, usage update, record insert, `SELECT NOW()`) | O(log n) | advisory lock held across all of them |
| `RedisQuotaStorage.TryConsumeAsync` | 1 (Lua) | O(1) + O(k) `SADD` | `SMEMBERS` on replay is O(k) |
| `FirestoreQuotaStorage.TryConsumeAsync` | 1 tx (2–4 RPC) + retries | O(log n) | contention aborts under hot keys |
| `TieredQuotaStorage.GetUsageAsync` | **2** (Cold read + Hot write) | O(1) but 2× network | every read; Hot is not a cache |
| `TieredQuotaStorage.TryConsumeAsync` (async sync) | 1 Hot + 1 queued Cold | O(1) | `DropWrite` silently loses the Cold mirror |
| `FirestoreQuotaStorage.MergeUserAsync` | **2N + 4** sequential RPC | O(N) RPC | lock timeout at N ≳ 20; 500-op cap at N ≈ 240 |
| `FirestoreQuotaStorage.CheckRateLimitAsync` (global) | 1 tx on **one** doc | O(G) array rewrite per request | 1 write/s/doc ceiling; 1 MiB doc cap |
| `FirestoreQuotaStorage.GetAuditLogsAsync` | 1 query + in-memory filter | O(page log page) | misses older rows for busy tenants |
| `InMemoryQuotaStorage.GetAuditLogsAsync` | 1 lock + sort | O(U log U) | unbounded `U` |
| `WebhookRateLimiter.Acquire` | 1 lock + periodic O(B) sweep | O(B) under lock | B uncapped (`H11`) |
| `QuotaManager.GetEntitlementWithFallbackAsync` | single-flight + LRU | O(1) | correct and cheap — keep |

### 5.3 Recommended replacements (ranked by payoff)

1. **Firestore merge → batch read, then single-pass write.** Replace the `2N` `GetSnapshotAsync` loop with `tx.GetAllSnapshotsAsync(refs)` (one RPC) and dedupe `request.Items` before reading (`H8`, `M12`). Removes the dominant Firestore latency term.
2. **Global rate limit → sharded O(1) token bucket.** Neither the `ticks` array (Firestore) nor the single global key (Redis) scales. Store `(tokens, lastRefillTicks)` instead of a tick history; shard the global key across `R` buckets and sum. O(1) space, O(R) reads, no hot document.
3. **`TieredQuotaStorage` → read Hot first, fall back to Cold.** Today's Cold-first read makes Hot a write amplifier instead of a cache (`X5`). Flip the order and mirror on miss; add a per-user `Tier` column/parameter to `SetUsageAsync` so prorated caps survive the mirror (`H3`).
4. **Forever-credit idempotency → one record per (user, resource, key).** Already the fix for `F4`; also makes the money path O(1) instead of "first resource wins".
5. **Audit log → push filters down.** Firestore: `WhereEqualTo("userId", …)` with composite indexes; InMemory: ring buffer capped at ~10k (`M1`, `M11`).
6. **`InMemoryQuotaStorage` → periodic sweep.** A `SweepExpired(now)` (mirroring `LocalRateLimitEngine.SweepIdleKeys`) removes `_idempotency`, `_sealedUsers` and old `_merges` entries; cap `_auditLogs` (`M1`).

---

## 6. Global Phased Implementation Roadmap

### Phase 1 — Critical & High (fix immediately, ~2–3 days)

| Step | Work | Files | Exit criterion |
|---|---|---|---|
| 1.1 | `F1` + `F2`: stop trusting `X-Request-ID`; reject `amount <= 0` at extractor and manager; clamp `FromBodyByteLength` | `NetQuotaAspNetCoreExtensions.cs`, `QuotaManager.cs` | New `NetQuotaAspNetCoreTests` proves replay does not re-execute and `0` is rejected (also closes `H16`) |
| 1.2 | `F4` + `M16`: require non-empty keys on money paths; add `resource` to forever-credit keys in all 4 adapters; make `IBillingEntitlementStore` parameters non-nullable | `QuotaManager.cs`, `InMemoryQuotaStorage.cs`, `RedisQuotaStorage.cs`, `FirestoreQuotaStorage.cs`, `PostgresQuotaStorage*.cs`, `BillingModels.cs` | `R2` passes; replay test still grants exactly once |
| 1.3 | `F3`: prorate Stripe refunds; accept `charge.refunded`; ignore zero-amount refunds | `StripeWebhookParser.cs` | `R6` passes; duplicate partial refunds never exceed the pack |
| 1.4 | `F5` + `F6` + `H1`: one `MergeFrom` entitlement helper used by all 4 adapters; emit `FieldValue.Delete`; check `IsSealedAt`; write `tier`/anchor on sealed ghosts | core + 4 adapters | `R3` passes for all adapters; `R5` seal/anchor tests pass |
| 1.5 | `H15`: `Assert.SkipWhen` in the 5 Firestore tests; `I1`: make `dotnet test` (with build) report tests | `NetQuotaFirestoreConcurrencyTests.cs`, test project | `dotnet test` without `--no-build` exits 0 and reports 75 |
| 1.6 | `H6`, `H7`, `H8`, `H9`: Postgres upsert on expired keys; Firestore merge replay payload + limit/tier/dup handling; hashed doc ids | `PostgresQuotaStorage.Records.cs`, `FirestoreQuotaStorage.cs` | New concurrency tests with emulator + local Postgres |
| 1.7 | `H2`, `H3`, `H4`, `H5`: read-path `-1` override; carry `Tier` into Hot; safe DST anchor; breaker cancellation + single probe | core | `R4` passes; DST tests for `America/Havana` and `Europe/Bucharest`; breaker test no longer sleeps |
| 1.8 | `H10`–`H14`: shard/limit global rate windows; rightmost XFF + bucket cap; persist `customer_id`; drop `"ANONYMOUS"`; remove sync-over-async | Firestore, Redis, Billing, AspNetCore | Load test at 10× expected RPS shows no single-doc contention |

### Phase 2 — Medium (backlog, ~1 sprint)

| Step | Work | Exit criterion |
|---|---|---|
| 2.1 | `M1`, `M2`, `M9`: sweeps/TTLs for idempotency, seals, audit, Redis `distinct` and drained hashes | 24 h soak with stable RSS and stable Redis `DBSIZE` |
| 2.2 | `M4`, `M5`: one `SetUsageAsync` contract test across all adapters; widen arithmetic to `long`/saturating | Conformance suite green on 4 adapters |
| 2.3 | `M6`, `M14`, `M15`: injectable clock everywhere; `Retry-After`, sealed-user 403/423, bounded body reads; Redis server time in mutations | Deterministic clock tests; HTTP contract tests |
| 2.4 | `M7`, `M8`, `M13`: merge `-1` semantics, `DroppedPromotion` parity, tier ranking/casing/expiry | Merge replay matrix passes on InMemory + Postgres + Firestore |
| 2.5 | `M3`, `M10`, `M11`, `M12`: Redis cluster key strategy (hash tags) or an explicit "single-node only" guard; rate-limit parity; audited query pushdown; batched merge reads | `docs/adapters.md` matches behaviour; cluster smoke test |
| 2.6 | `L1`–`L15` + test hygiene (`L11`, `L12`): cleanup, disposal, deterministic waits, billing refund assertions | Flake-free 50× test runs |

---

## 7. What NOT to Change

1. **The one-way dependency graph and the capability interfaces.** `NetQuota` has zero package references and no adapter depends on another. `IUserMerger` / `IPromotionStore` / `IQuotaTimeSource` / `IPromotionCapabilityReporter` make adapter differences explicit instead of hiding them behind `NotSupportedException` at runtime. Do not collapse them into one fat `IQuotaStorage`.
2. **The Postgres advisory-lock hierarchy.** `record → user → usage` with ordinal ordering (`PostgresQuotaStorage.Merge.cs`) is correct, documented, and deadlock-free under the audited paths. Refactoring it "for clarity" is pure risk.
3. **`BillingEntitlementTransition.Merge` and `IsStale`.** This is the one place where Invariant 1 and "strictly older events lose" are implemented correctly. The fix for `H1` is to make the adapters *reuse* this logic, not to rewrite it.
4. **`LruQuotaCache` + single-flight entitlement fetch + fallback chain.** Bounded, correct, and cheap. The only change worth making is `_optimisticUsage` (`M1`); leave the LRU and `_entitlementFlights` alone.
5. **The Lua-script-per-mutation design in Redis and the `INSERT … ON CONFLICT` + `NpgsqlDbType.Jsonb` discipline in Postgres.** Both are atomic, injection-free, and auditable. Fix the *keys* and *TTLs* (`F4`, `M2`, `M9`); do not move logic into C# and lose atomicity.

---

## 8. Teardown

Audit state is `"status": "COMPLETED"` in `.tmp/audit/audit_state.json` (all 7 modules `COMPLETED`, `findings_count` = 13/7/12/13/8/7/5).

Artifacts to keep until the fixes land:

```
.tmp/audit/audit_state.json
.tmp/audit/findings/00_core_quota_engine.md … 06_infra_test_suite.md
.tmp/audit/repros/00_repro_index.md
FINAL_AUDIT_REPORT.md
```

Remove the scratch directory when done:

**Bash**
```bash
rm -rf .tmp/audit
# optional: keep only the deliverables
# mkdir -p docs/audit && mv .tmp/audit/findings .tmp/audit/repros docs/audit/ && rm -rf .tmp/audit
```

**PowerShell**
```powershell
Remove-Item -Recurse -Force .tmp/audit
# optional: keep only the deliverables
# New-Item -ItemType Directory -Force docs/audit | Out-Null
# Move-Item .tmp/audit/findings, .tmp/audit/repros docs/audit/
# Remove-Item -Recurse -Force .tmp/audit
```

> `.tmp/` is already in `.gitignore` (added in the working tree), so none of these files are tracked. `FINAL_AUDIT_REPORT.md` at the repo root **is** untracked and will show in `git status` — move it under `docs/audit/` if you prefer to commit it.
