# NetQuota — Final Master Audit Report

**Repository:** `E:\devel\repos\netquota`  
**HEAD:** `147e2e9` (`chore: add FINAL_AUDIT_REPORT.md and update .gitignore`)  
**Audit date:** 2026-10-21  
**Audit state:** 18/18 modules completed · `COMPLETED`  
**Verdict:** **not release-ready** until the Phase 1 blockers are closed.

---

## 1. Executive Summary & Stack Overview

NetQuota is a compact, coherent .NET quota engine. The core design is sound: immutable contracts, a single explicit storage interface, atomic adapter primitives, provider-id idempotency on money paths, and a real xUnit regression suite. The architecture is not the problem. The verified defects cluster at the seams: quota accounting, tenant key construction, billing payload identity, async tiered persistence, transport input handling, and the release pipeline.

**The audit is complete.** 18 modules, 57 verified findings: **3 Critical, 18 High, 36 Medium**. Every counted finding has a standalone failing repro against HEAD `147e2e9`; three candidates were rejected as ambiguous or duplicate (see Section 4.6). No source or test file was changed; this report is the only tracked file modified by the synthesis pass.

### Headline verified risks

1. **The release pipeline cannot currently publish.** `GitVersion.yml` exits 1 under the workflow's GitVersion 6.x, and the quoted directory glob passed to `dotnet nuget push` is not expanded. No tag produces a package or GitHub release (`IB-01`, `IB-02`).
2. **Quota state can be bypassed or corrupted.** A JSON `int.MaxValue` overflows the in-memory usage counter (`A1`), anonymous monthly quotas reset daily (`QM-01`), explicit tier overrides discard the entitlement's monthly anchor (`QM-02`), and merge/drain rewrite usage incorrectly (`ISM-01`, `ISM-02`).
3. **Tenant isolation depends on a delimiter that user IDs can contain.** Redis, Firestore, and PostgreSQL all build idempotency/usage keys by concatenating `user:resource:key`; a `:` inside an id lets two identities share a record and replay another user's outcome (`REDIS-01`, `FIRE-05`, `PGC-3`).
4. **Billing money paths can over-revoke or duplicate credits.** Sequential partial refunds revoke the cumulative share (`STRIPE-01`: four $2 refunds revoke 200 credits from a 100-credit pack), expired PostgreSQL credit records are never refreshed (`PGE-1`), and a zero `IdempotencyTtl` silently disables replay protection (`CCD-01`).
5. **Async tiered storage has two authorities.** In `AsyncUsageSync`, a lagging cold read can overwrite hot and erase a consume (`TSM-01`), a refund can be applied to cold before the queued consume (`TSM-02`), and a full queue drops cold writes while `TryWrite` reports success (`TSM-03`).

### Stack overview

| Layer | Projects | Runtime / dependencies | Role |
|---|---|---|---|
| Core | `NetQuota` | C# `net10.0`, no external runtime packages | Contracts, `QuotaManager`, in-memory storage, local rate limiting, tiered storage, metrics |
| Storage adapters | `NetQuota.Firestore`, `NetQuota.Postgres`, `NetQuota.Redis` | Google.Cloud.Firestore 4.5.0; Npgsql 10.0.3; StackExchange.Redis 3.4.5 | Transactional quota, entitlement, credit, merge, and rate-limit persistence |
| Billing | `NetQuota.Billing` | Core only; provider HTTP via `HttpClient` | Stripe and RevenueCat webhook parsing, signature verification, checkout/portal/transfers, customer store |
| Transport | `NetQuota.AspNetCore` | `Microsoft.AspNetCore.App` framework reference | DI, endpoint filters, usage endpoint, `System.Threading.RateLimiting` bridge, headers |
| Tests | `NetQuota.Tests` | xUnit v3 4.0.2, Microsoft.Testing.Platform | 107 tests across core, billing, transport, and live-service adapter suites |
| Release | GitHub Actions | GitVersion 6.x, NuGet OIDC trusted publishing | CI, tag-driven versioning, six-package pack/push, GitHub release |

There is no frontend, GraphQL, gRPC, or protobuf surface. The wire contracts are Stripe/RevenueCat JSON webhooks, ASP.NET JSON request bodies, and Firestore/PostgreSQL row documents.

### Global baseline

| Check | Command | Result |
|---|---|---|
| Release build | `dotnet build NetQuota.slnx -c Release -warnaserror` | **0 warnings, 0 errors**, 1.80 s |
| Full test suite | `dotnet test NetQuota.slnx -c Release --no-build` | **107 total, 75 passed, 0 failed, 32 skipped**, 1.25 s |
| Adapter suites with services | PostgreSQL 16, Redis 7, Firestore emulator | PostgreSQL 12/12, Redis 14/14, Firestore 6/6 passed in dedicated runs |
| Analyzer gate | `dotnet format NetQuota.slnx analyzers --verify-no-changes --no-restore` | **exit 1**, 238 `xUnit1051` warnings, all in `tests/NetQuota.Tests` |

The 32 skipped tests are the adapter suites that gate on `NETQUOTA_POSTGRES`, `NETQUOTA_REDIS`, and `FIRESTORE_EMULATOR_HOST`. They were run separately against live services during the audit; the skips in the default global run are environmental, not coverage claims.

---

## 2. Subsystem & Feature Catalog

| Module | Domain | Files | Responsibility |
|---|---|---:|---|
| `core_domain_contracts` | core | 2 | Config/entitlement/consume/refund/merge DTOs, validation, storage contracts, exception hierarchy |
| `core_quota_manager` | core | 1 | Orchestration: consume cascade, entitlements, promotions, credits, refunds, tier changes, merges, rate limits, fallback, caches |
| `core_inmemory_storage` | core | 2 | Reference `IQuotaStorage`, lock-serialized state, local token-bucket/sliding-window limiter |
| `core_tiered_storage_metrics` | core | 1 | LRU cache, circuit breaker, hot/cold tiered storage, OpenTelemetry metrics |
| `storage_postgres_core` | backend | 2 | Schema/options, connection/transaction lifecycle, advisory locks, consume transaction |
| `storage_postgres_entitlements_credits` | backend | 3 | Entitlement upserts, credit records, usage I/O, forever-credit arithmetic |
| `storage_postgres_merge_audit` | backend | 2 | Cross-identity merge transaction, tombstones, audit write/query/prune |
| `storage_redis_atomicity` | backend | 3 | Redis keys/TTLs and Lua scripts for atomic quota, credit, promotion, rate-limit operations |
| `storage_firestore_transactions` | backend | 1 | Firestore transactions, server-clock probe, TTL fields, idempotency, merges |
| `billing_stripe` | backend | 3 | Stripe checkout/portal/transfer/customer operations and signed webhook parsing |
| `billing_revenuecat` | backend | 2 | RevenueCat auth/parsing, event normalization, entitlement application, transfers |
| `billing_webhook_security` | backend | 2 | Shared billing models, HMAC/constant-time helpers, body reader, rate limiter, metrics |
| `aspnetcore_integration` | backend | 2 | DI, endpoint filters, usage endpoint, amount/header extraction, rate-limiter bridge |
| `tests_core_engine` | infra | 3 | QuotaManager semantics, credit idempotency, entitlement preservation tests |
| `tests_billing` | infra | 2 | Stripe/RevenueCat normalization, signature/replay, refund/proration tests |
| `tests_storage_adapters` | infra | 3 | PostgreSQL/Redis/Firestore integration and concurrency suites |
| `tests_aspnetcore` | infra | 2 | Amount extraction and replay endpoint tests |
| `infra_build_release` | infra | 6 | CI, GitVersion, six-package pack/verify, OIDC push, GitHub release |

### Dependency graph

```text
                       +--------------------------+
                       |  NetQuota.AspNetCore     |
                       |  DI / filters / usage API|
                       +------------+-------------+
                                    |
+--------------------------+        |
|  NetQuota.Billing        |        |
|  Stripe / RevenueCat     |        |
+------------+-------------+        |
             |                      |
             +----------+-----------+
                        |
             +----------v-----------+
             |  NetQuota (core)     |
             |  contracts, manager, |
             |  in-memory, tiered   |
             +--+--------+--------+-+
                |        |        |
     +----------v-+  +---v------+ +v----------+
     | Firestore  |  | Postgres | | Redis     |
     +------------+  +----------+ +-----------+

     tests/NetQuota.Tests  --->  all projects
```

**Layering verdict:** no dependency-inversion violations. `NetQuota` has no project references. Every adapter references only `NetQuota` (plus its provider SDK). `NetQuota.Billing` and `NetQuota.AspNetCore` reference only `NetQuota`; neither imports the other. Tests reference all projects. The only deliberate core-to-core coupling is `QuotaManager` constructing `CircuitBreakerQuotaStorage`/`TieredQuotaStorage`, which are part of the same assembly.

---

## 3. Cross-Boundary Architectural Review

These are the seams that isolated module audits cannot see. Each is grounded in one or more verified findings; no new unverified defect is asserted.

### X1 — Composite keys are delimiter-concatenated, not injective (tenant boundary)

`QuotaManager.ScopedIdempotencyKey` builds `user:key` (`src/NetQuota/QuotaManager.cs:62-65`). Redis `ScopedKey` (`RedisQuotaStorage.Keys.cs:19-39`), Firestore document ids (`FirestoreQuotaStorage.cs:1196-1205`), and PostgreSQL `ScopedKey` (`PostgresQuotaStorage.Records.cs`) all use the same pattern. A user id containing `:` can therefore collide with a different `(user, key)` pair. The replay paths return the stored outcome without checking ownership.

**Evidence:** `REDIS-01`, `FIRE-05`, `PGC-3`; `QM` scoped keys. **Consequence:** cross-user idempotency replay, wrong `DistinctKeys`, suppressed or duplicated charges. **Direction:** length-prefix or SHA-256 each segment, or reject `:` in external ids; always verify `UserId`/`Resource` on replay.

### X2 — `explicitTier` conflates “override the tier” with “ignore the entitlement” (API boundary)

`GetQuotaAsync`, `PeekAsync`, `ConsumeWithResultAsync`, and `BuildQuotaViewAsync` set `ent = null` whenever `explicitTier` is non-empty (`QuotaManager.cs:301-304,386-389,519-527,1139-1143`). The override then selects limits but loses the subscription anniversary, timezone, promotion, and seal state. The dry-run path passes the resolved tier as an explicit override (`:615-617`), so it reads a different monthly window than the real consume. Unknown overrides silently fall back to the default tier via `GetTierConfig` (`:1262-1265`).

**Evidence:** `QM-02`, `A4`; ASP.NET `ResolveExplicitTierAsync` at `NetQuotaAspNetCoreExtensions.cs:239-255,328-330`. **Consequence:** monthly quota resets daily for explicit-tier users, dry-run disagrees with consume, paid users are throttled by the default tier. **Direction:** always resolve the entitlement; validate the override; use it only for tier selection.

### X3 — Billing parsers assume payload shapes the providers do not guarantee (provider boundary)

Stripe `charge.refunds` is an includable field not returned in webhook payloads; the parser falls back to cumulative `amount_refunded` and keys each revocation by event id (`StripeWebhookParser.cs:194-210`). Invoice tier selection uses `lines.data[0]`, which is a proration credit on an upgrade invoice (`:126-135`). The refund-proration tests use the synthetic expanded shape, so the production branch is untested (`TESTS-01`). RevenueCat transfer fan-out treats array membership as a ranking and downgrades the source when no eligible destination remains (`RC-01`).

**Evidence:** `STRIPE-01`, `STRIPE-02`, `TESTS-01`, `RC-01`. **Consequence:** paid credits are over-revoked; mid-cycle upgrades are written back as the old tier. **Direction:** key refunds by the individual refund id or a stable `payment_intent` identity; select the mapped, non-proration invoice line; require a destination before downgrading.

### X4 — Async tiered storage has two sources of truth (persistence boundary)

`TieredQuotaStorage` writes hot synchronously and cold through a bounded channel (`TieredStorageAndMetrics.cs:767-782`). In `AsyncUsageSync`, `GetUsageAsync` can read the lagging cold store and write that snapshot back into hot, erasing a just-applied consume (`TSM-01`). Refunds and other cold-direct mutations are applied to cold before queued consumes (`TSM-02`). `BoundedChannelFullMode.DropWrite` makes `TryWrite` return true for a discarded item, so the documented `AsyncErrorHandler` overflow branch is dead (`TSM-03`).

**Evidence:** `TSM-01`, `TSM-02`, `TSM-03`. **Consequence:** double charge, lost refund, silent cold-write loss after restart. **Direction:** make hot authoritative until the cold sequence catches up; sequence mutations per user/resource; use `Wait`/`DropOldest` with an explicit error signal.

### X5 — Entitlement rewrite semantics drift across adapters (contract boundary)

The core invariant says every entitlement rewrite preserves `Promotion`, `Timezone`, `Sealed`, `MigratedTo`, and `SealExpiresAtUtc`. The adapters implement that differently: InMemory drops the tombstone when the incoming record carries `Sealed = true` (`ISM-03`); Firestore cannot clear `expiresAtUtc` with `null` (`FIRE-02`); PostgreSQL `SetUsageAsync(distinctKeys: null)` clears dedupe keys (`PGE-2`); Redis consume without `EffectiveTier` overwrites the stored tier/prorated cap (`REDIS-05`).

**Evidence:** `ISM-03`, `FIRE-02`, `PGE-2`, `REDIS-05`. **Consequence:** lifetime users are downgraded, merge tombstones are lost, quota resets after an admin sync. **Direction:** one shared `BillingEntitlementTransition`-style merge in core; adapters persist all fields and distinguish “not supplied” from “clear”.

### X6 — Persistence schema and query contracts are incomplete (storage boundary)

PostgreSQL `TryConsumeAsync` writes `start/end` from the stored row instead of the window, leaving `period_end_utc` NULL and making `PruneExpiredAsync` a no-op for consumed rows (`PGC-2`). Audit pagination orders only by `timestamp_utc DESC`, so ties are not totally ordered (`PGM-3`). Expired idempotency rows still occupy the unique key, so a retry throws raw `23505` (`PGC-1`). Firestore document ids are not injective (`FIRE-01`).

**Evidence:** `PGC-1`, `PGC-2`, `PGM-3`, `FIRE-01`. **Consequence:** unbounded growth, wrong audit pages, unhandled provider exceptions. **Direction:** persist window bounds on every write; add a deterministic tie-breaker; replace or upsert expired idempotency records; hash document ids with length-prefixed segments.

### X7 — Transport filters run after model binding (transport/core boundary)

Minimal-API endpoint filters execute after the request body has been bound. `FromJsonBodyInt` tries to read the already-consumed stream, catches the parse failure, and returns the fallback amount of 1 (`A2`). There is no clamp on the JSON value, so `int.MaxValue` overflows the in-memory `Used` counter (`A1`). `FromHeader` clamps at 1,000,000, but the JSON path does not.

**Evidence:** `A1`, `A2`, `TS-01`. **Consequence:** endpoints undercharge, and a crafted body can invert the counter and bypass the quota. **Direction:** extract before binding or read a cached body feature; clamp at 1,000,000; use checked arithmetic or a maximum amount in `QuotaManager`.

### X8 — Release toolchain assumptions are not validated in CI (delivery boundary)

`GitVersion.yml` sets `next-version: 1.0.0`; GitVersion 6.8.2 rejects the unprefixed value under `tag-prefix: '[vV]'` and exits 1 (`IB-01`). The release workflow passes `"artifacts/packages/*.nupkg"` to `dotnet nuget push`; the CLI expands only filename-only globs in the current directory, so the step fails after OIDC login (`IB-02`).

**Evidence:** `IB-01`, `IB-02`; verified with `verify_gitversion.ps1` and `verify_push_glob.ps1`. **Consequence:** no release, no package publish. **Direction:** remove `next-version`, or pin GitVersion; push per-file or use `find`.

### Security and trust-boundary summary

- Billing signature verification is sound: HMAC-SHA256, constant-time compare, timestamp tolerance, body-size cap, proxy distrust by default.
- Client-supplied idempotency keys are not trusted by default; the ASP.NET filter returns `null` unless the host opts in (`NetQuotaAspNetCoreExtensions.cs:152-159`).
- `X-Forwarded-For` is only read when `TrustProxy` is explicitly enabled.
- The unresolved trust issues are X1 (tenant key collisions), X2 (explicit tier bypass), and X7 (amount input).
- `MapNetQuotaUsageEndpoint` has no built-in authentication/authorization; the host must place it behind its auth middleware. This is an integration responsibility, not a verified library defect.

---

## 4. Master Critical Bugs Table (Verified Only)

**Severity rule used in this master report:** Critical = release-blocking or direct data/tenant/paid-entitlement loss; High = deterministic money, quota, entitlement, crash, or cross-user correctness defect; Medium = retention, rate-limit configuration, reporting, robustness, and test coverage. The module reports retain their local labels; several High findings are business-critical and are listed first in the Phase 1 roadmap even though they are not marked Critical here.

### 4.1 Summary

| Severity | Count | Core | Backend | Infra/tests |
|---|---:|---:|---:|---:|
| Critical | 3 | 0 | 1 | 2 |
| High | 18 | 8 | 10 | 0 |
| Medium | 36 | 6 | 22 | 8 |
| **Total** | **57** | **14** | **33** | **10** |

### 4.2 Critical

| F | ID | Module | Defect | Root cause | Anchor | Repro |
|:--|:--|:--|:--|:--|:--|:--|
| **F1** | `A1` | `aspnetcore_integration` | `FromJsonBodyInt` has no clamp; `int.MaxValue` inverts the usage counter and bypasses the quota | The clamp was added to `FromHeader` and `FromBodyByteLength` but not to `FromJsonBodyInt`; the core validates only the sign of `amount`, and the reference adapter adds amounts wit… | `src/NetQuota/QuotaManager.cs:508-511`<br>`src/NetQuota/InMemoryQuotaStorage.cs:219` | `M13` |
| **F2** | `IB-01` | `infra_build_release` | `GitVersion.yml` cannot be evaluated: `next-version: 1.0.0` fails the version step | GitVersion 6.8.2 rejects the unprefixed `next-version` value and accepts `v1.0.0` (both verified above), i.e. the 6.8.2 parser applies the configured tag prefix to `next-version`.… | `GitVersion.yml:3`<br>`.github/workflows/release.yml:30-32` | `M18` |
| **F3** | `IB-02` | `infra_build_release` | The nuget.org push step passes a directory-qualified glob the CLI does not expand | The workflow assumes the CLI expands a path glob. It does not; the quoted pattern has a directory component, so the shell does not expand it either. The prior step's bash array wo… | `.github/workflows/release.yml:71,75` | `M18` |

### 4.3 High

| F | ID | Module | Defect | Root cause | Anchor | Repro |
|:--|:--|:--|:--|:--|:--|:--|
| **F4** | `CCD-01` | `core_domain_contracts` | `IdempotencyTtl <= 0` silently disables money-path replay protection | `IdempotencyTtl` is declared at `src/NetQuota/QuotaModels.cs:77`, but `Validate()` has no positivity guard. The storage contract assumes a positive TTL when it writes `expiresAt`. | `src/NetQuota/QuotaModels.cs:77`<br>`src/NetQuota/QuotaModels.cs:84-179` | `M1` |
| **F5** | `CCD-02` | `core_domain_contracts` | Case-sensitive `Tiers` dictionary is accepted and breaks case-insensitive tier resolution | `Tiers` is an auto-property (`src/NetQuota/QuotaModels.cs:75`) whose default initializer is bypassed by deserialization or by an explicitly supplied dictionary. The comparer is pa… | `src/NetQuota/QuotaModels.cs:75`<br>`src/NetQuota/QuotaManager.cs:108-110` | `M1` |
| **F6** | `QM-01` | `core_quota_manager` | Anonymous monthly quota resets every day | `BuildWindow` overrides the nullable anchor that `PeriodWindow.ForMonthly` already supports for calendar-month cycles. The null-anchor branch at `QuotaModels.cs:246-257` is unreac… | `src/NetQuota/QuotaManager.cs:1238-1244`<br>`src/NetQuota/QuotaModels.cs:246-257` | `M2` |
| **F7** | `QM-02` | `core_quota_manager` | `explicitTier` bypasses entitlement state and accepts unknown tier names | The code conflates “override the tier used for limits” with “do not resolve the entitlement”. The entitlement is the source of the period anchor, timezone, promotion, and seal sta… | `src/NetQuota/QuotaManager.cs:301-304,519-527,1139-1143`<br>`src/NetQuota/QuotaManager.cs:1262-1265` | `M2` |
| **F8** | `ISM-01` | `core_inmemory_storage` | Merge rewrites the target's `Used` to the distinct-key count and erases usage | The merge treats "one distinct key" as "one used unit". That holds only while every consume charged `amount == 1`. `TryConsumeAsync` allows any positive amount with a distinct key… | `src/NetQuota/InMemoryQuotaStorage.cs:580-595`<br>`src/NetQuota.Postgres/PostgresQuotaStorage.Merge.cs:120-129` | `M3` |
| **F9** | `ISM-02` | `core_inmemory_storage` | `DrainRemainingAsync` silently does nothing when `Used == 0` | Draining is expressed as `Limit = Used`, but `Limit == 0` is overloaded as "no stored limit". A drain of a zero-usage window therefore writes a value every reader interprets as "u… | `src/NetQuota/InMemoryQuotaStorage.cs:482-488` | `M3` |
| **F10** | `TSM-01` | `core_tiered_storage_metrics` | `GetUsageAsync` lets lagging cold storage overwrite hot and erase a consume | The tiered wrapper has two conflicting notions of authority. In `AsyncUsageSync` mode it writes hot first and cold asynchronously, which makes hot authoritative for consumption; b… | `src/NetQuota/TieredStorageAndMetrics.cs:767-782`<br>`src/NetQuota/TieredStorageAndMetrics.cs` | `M4` |
| **F11** | `TSM-03` | `core_tiered_storage_metrics` | A full async queue silently drops the cold write; the documented error signal never fires | `BoundedChannelFullMode.DropWrite` is documented as dropping the incoming item and reporting success to the writer; the channel never returns `false` for a full buffer in this mod… | `src/NetQuota/TieredStorageAndMetrics.cs:646-652`<br>`src/NetQuota/TieredStorageAndMetrics.cs` | `M4` |
| **F12** | `PGC-1` | `storage_postgres_core` | Reusing a consume/refund idempotency key after its TTL throws `23505` | can only replay *unexpired* records, so it converts the constraint violation into a caller-visible `PostgresException`. | `src/NetQuota.Postgres/PostgresQuotaStorage.Records.cs:60-68,100-120`<br>`src/NetQuota.Postgres/PostgresQuotaStorage.cs:104-122` | `M5` |
| **F13** | `PGE-1` | `storage_postgres_entitlements_credits` | An expired credit record is never refreshed, so every retry with the same key grants or refunds again | window never restarts. `InMemoryQuotaStorage` overwrites the idempotency entry on a post-expiry call (`InMemoryQuotaStorage.cs:419-427`, `:451-459`), which is why its behaviour is… | `src/NetQuota.Postgres/PostgresQuotaStorage.Records.cs:226`<br>`src/NetQuota.Postgres/PostgresQuotaStorage.cs:433-436` | `M6` |
| **F14** | `REDIS-01` | `storage_redis_atomicity` | Idempotency record keys collide across users/resources when IDs contain `:` | ScopedKey concatenates user/resource/key with ':'; a ':' inside an id can produce the same key for a different pair, and replay never verifies ownership. | `src/NetQuota.Redis/RedisQuotaStorage.Keys.cs:19-23`<br>`src/NetQuota.Redis/RedisQuotaStorage.Keys.cs:34-39` | `M8` |
| **F15** | `REDIS-02` | `storage_redis_atomicity` | Distinct-key sets never receive a TTL; Redis memory grows without bound | ConsumeScript creates a distinctKey set but never sets EXPIRE; usage hashes get a TTL, the sets do not. | `src/NetQuota.Redis/RedisQuotaStorage.Scripts.cs:76-77`<br>`src/NetQuota.Redis/RedisQuotaStorage.Scripts.cs:270-275` | `M8` |
| **F16** | `FIRE-01` | `storage_firestore_transactions` | Document ids are not injective; usage and entitlement documents collide across users/resources | Firestore document IDs concatenate/sanitize user, resource, period, and window segments without escaping; different tuples can collapse to the same document ID, so one tenant can overwrite another's. | `src/NetQuota.Firestore/FirestoreQuotaStorage.cs:1196-1197`<br>`src/NetQuota.Firestore/FirestoreQuotaStorage.cs:1199-1201` | `M9` |
| **F17** | `FIRE-02` | `storage_firestore_transactions` | `SetEntitlementAsync` cannot clear `expiresAtUtc`; an old expiry silently downgrades a non-expiring user | SetEntitlementAsync always writes expiresAtUtc; an incoming null does not clear the existing field, so a stale expiry silently downgrades a lifetime user. | `src/NetQuota.Firestore/FirestoreQuotaStorage.cs:115`<br>`src/NetQuota.Firestore/FirestoreQuotaStorage.cs:1402-1410` | `M9` |
| **F18** | `FIRE-05` | `storage_firestore_transactions` | Consume/refund idempotency keys collide across users when the user id or key contains `:` | Idempotency document ids are a hash of 'user:key' without length-prefixing; ':' in user or key lets two pairs collide, and replay does not verify the owner. | `src/NetQuota.Firestore/FirestoreQuotaStorage.cs:1259-1265`<br>`src/NetQuota.Firestore/FirestoreQuotaStorage.cs:1267-1273` | `M9` |
| **F19** | `STRIPE-01` | `billing_stripe` | Sequential partial refunds revoke the cumulative share (credit over-revocation) | `ParseCreditPurchase` treats the cumulative `amount_refunded` as "this refund's amount" whenever the payload does not expand `charge.refunds`, and keys the revocation by the event… | `src/NetQuota.Billing/Stripe/StripeWebhookParser.cs:194-210`<br>`src/NetQuota.Billing/Stripe/StripeBillingProvider.cs:84-96` | `M10` |
| **F20** | `A2` | `aspnetcore_integration` | `FromJsonBodyInt` charges the fallback on every endpoint that binds a body (filters run after binding) | Minimal-API filters run after model binding; the JSON extractor sees an exhausted request body, swallows the parse failure and returns the fallback amount of 1. | `src/NetQuota.AspNetCore/NetQuotaAspNetCoreExtensions.cs:116-144,234`<br>`src/NetQuota.AspNetCore/NetQuotaAspNetCoreExtensions.cs:282` | `M13` |
| **F21** | `A4` | `aspnetcore_integration` | The rate-limiter bridge hardcodes `explicitTier: "ANONYMOUS"`, so entitlements are never consulted | The tier is a constructor-time literal; the class has no tier parameter and the doc comment promises transparent distributed windows for `RequireRateLimiting(policy)` without ment… | `src/NetQuota.AspNetCore/NetQuotaDistributedRateLimiter.cs:24-34`<br>`src/NetQuota/QuotaManager.cs:332-337` | `M13` |

### 4.4 Medium

| F | ID | Module | Defect | Root cause | Anchor | Repro |
|:--|:--|:--|:--|:--|:--|:--|
| **F22** | `CCD-03` | `core_domain_contracts` | NaN warning threshold passes validation and silently disables warnings | The relational pattern does not exclude NaN. The validation message says the value must be in `(0, 1]`, but the predicate is not equivalent to that interval for non-finite values. | `src/NetQuota/QuotaModels.cs:47`<br>`src/NetQuota/QuotaModels.cs:168-177` | `M1` |
| **F23** | `QM-03` | `core_quota_manager` | Caller cancellation is swallowed or rewritten as a storage failure | The generic catches treat cancellation as an infrastructure failure. Cancellation is not a storage outage; it must propagate so the caller and ASP.NET Core request pipeline can ob… | `src/NetQuota/QuotaManager.cs:67-82`<br>`src/NetQuota/QuotaManager.cs:352-368` | `M2` |
| **F24** | `ISM-03` | `core_inmemory_storage` | A rewrite carrying `Sealed = true` drops the merge tombstone | The preservation branch is conditioned on the incoming entitlement being *unsealed*. A rewrite that echoes the current sealed flag but does not know the tombstone fields falls thr… | `src/NetQuota/InMemoryQuotaStorage.cs:81-89` | `M3` |
| **F25** | `LRL-01` | `core_inmemory_storage` | Token-bucket idle sweep resets a partially refilled bucket | The sweep TTL is derived only from the window (`2 * Window` floored at 10 min). It assumes a bucket is full after at most two windows, which is true only when `Burst <= 2 * Rate`.… | `src/NetQuota/LocalRateLimitEngine.cs:7-13,222-232` | `M3` |
| **F26** | `LRL-02` | `core_inmemory_storage` | One global sliding window per resource is shared across incompatible tier windows | The global queue is keyed by `resource` only, while the window and the limit come from the per-call `RateLimitConfig`. `WindowTicks` is a single mutable field, so the most recent… | `src/NetQuota/LocalRateLimitEngine.cs:15-20,47-65,73-79` | `M3` |
| **F27** | `TSM-02` | `core_tiered_storage_metrics` | Refund is applied to cold before the queued consume; the refund is lost and the stores diverge | Consumes are serialized through the single pump, but refunds, drains, tier changes, usage seeds, credit top-ups and merges call cold directly. A refund that arrives while an earli… | `src/NetQuota/TieredStorageAndMetrics.cs:834-845,693-700` | `M4` |
| **F28** | `PGC-2` | `storage_postgres_core` | `TryConsumeAsync` never stores the window bounds, so `PruneExpiredAsync` can never delete consumed rows | ConsumeCoreAsync reads start/end from the stored row (null on first write) and upserts those nulls instead of spec.Window.StartUtc/EndUtc; PruneExpiredAsync requires a non-null pe… | `src/NetQuota.Postgres/PostgresQuotaStorage.cs:186-189`<br>`src/NetQuota.Postgres/PostgresQuotaStorage.cs:542-571` | `M5` |
| **F29** | `PGC-3` | `storage_postgres_core` | `ScopedKey` collides across users, and the replay path never checks record ownership | ScopedKey concatenates the user id and idempotency key with ':'; a ':' inside either can alias another pair, and the replay path never compares the stored UserId/Resource. | `src/NetQuota.Postgres/PostgresQuotaStorage.Records.cs:1-40`<br>`src/NetQuota/QuotaManager.cs:62-65` | `M5` |
| **F30** | `PGE-2` | `storage_postgres_entitlements_credits` | `SetUsageAsync(distinctKeys: null)` deletes the user's dedupe keys (diverges from `InMemoryQuotaStorage`) | Postgres SetUsageAsync treats distinctKeys: null as 'clear the set'; InMemory preserves the existing set when used > 0. | `src/NetQuota.Postgres/PostgresQuotaStorage.cs:280-300` | `M6` |
| **F31** | `PGM-1` | `storage_postgres_merge_audit` | A merge replay does not verify the user pair, so a reused key silently skips the merge and reports success | The merge idempotency lookup keys only on the caller-provided string and never compares source_user_id/target_user_id; a reused key returns AlreadyMerged for a different pair. | `src/NetQuota.Postgres/PostgresQuotaStorage.Merge.cs:30-40,293-307` | `M7` |
| **F32** | `PGM-2` | `storage_postgres_merge_audit` | `DroppedPromotion` is reported for promotions that were not dropped | ReadPromotionAsync does not check promotion_expires_at_utc; expired promotions are returned as DroppedPromotion, and with SealSource:false nothing was cleared. | `src/NetQuota.Postgres/PostgresQuotaStorage.Merge.cs:265-291` | `M7` |
| **F33** | `PGM-3` | `storage_postgres_merge_audit` | Audit pagination has no total order, so `LIMIT` can return the oldest entries of a timestamp tie | Audit query orders only by timestamp_utc DESC; ties have no deterministic secondary key, so LIMIT can return older rows. | `src/NetQuota.Postgres/PostgresQuotaStorage.Audit.cs:30-60` | `M7` |
| **F34** | `REDIS-03` | `storage_redis_atomicity` | Token-bucket TTL is shorter than the refill time; an idle bucket resets to a full burst | Token-bucket TTL is derived from the window, but a partially empty bucket can need Burst/Rate windows to refill; the sweep deletes it and the next call starts full. | `src/NetQuota.Redis/RedisQuotaStorage.cs:536-538`<br>`src/NetQuota.Redis/RedisQuotaStorage.Scripts.cs:351-383` | `M8` |
| **F35** | `REDIS-04` | `storage_redis_atomicity` | Token-bucket rate limiting ignores `RateLimitConfig.GlobalLimit` | The Redis token-bucket Lua ignores RateLimitConfig.GlobalLimit; only the local engine and the sliding-window script enforce it. | `src/NetQuota.Redis/RedisQuotaStorage.cs:541-553`<br>`src/NetQuota.Redis/RedisQuotaStorage.Scripts.cs:343-384` | `M8` |
| **F36** | `REDIS-05` | `storage_redis_atomicity` | A consume without `EffectiveTier` destroys the stored tier and the prorated cap | ConsumeScript writes effectiveTier unconditionally; a null EffectiveTier overwrites the stored tier/limit, so the prorated cap is lost. | `src/NetQuota.Redis/RedisQuotaStorage.Scripts.cs:42-51`<br>`src/NetQuota.Redis/RedisQuotaStorage.Scripts.cs:71-75` | `M8` |
| **F37** | `REDIS-06` | `storage_redis_atomicity` | `DrainRemainingAsync` creates a permanent recurring usage hash | DrainScript writes a recurring usage hash without EXPIRE and without period fields; a later consume is denied before reaching the EXPIRE path. | `src/NetQuota.Redis/RedisQuotaStorage.cs:503-506`<br>`src/NetQuota.Redis/RedisQuotaStorage.Scripts.cs:188-200` | `M8` |
| **F38** | `REDIS-07` | `storage_redis_atomicity` | `IdempotencyTtl <= 0` means “remember forever” for consume/refund and “remember nothing” for credit grants | Redis interprets IdempotencyTtl <= 0 as no expiry for consume/refund and as no record for credit grants; other adapters expire immediately. | `src/NetQuota.Redis/RedisQuotaStorage.cs:259`<br>`src/NetQuota.Redis/RedisQuotaStorage.cs:376` | `M8` |
| **F39** | `FIRE-03` | `storage_firestore_transactions` | Merge idempotency replay ignores the record's `expiresAt` | Merge idempotency lookup only checks that the document exists; it ignores the record's expiresAt and replays expired merges. | `src/NetQuota.Firestore/FirestoreQuotaStorage.cs:782-793`<br>`src/NetQuota.Firestore/FirestoreQuotaStorage.cs:912` | `M9` |
| **F40** | `FIRE-04` | `storage_firestore_transactions` | Merge replay returns empty transfer dictionaries instead of the original counts | Merge replay returns new empty dictionaries; transfer counts are never persisted in the idempotency document. | `src/NetQuota.Firestore/FirestoreQuotaStorage.cs:787-792`<br>`src/NetQuota.Firestore/FirestoreQuotaStorage.cs:905-914` | `M9` |
| **F41** | `FIRE-06` | `storage_firestore_transactions` | Sealing a user without an entitlement fabricates a new `subscriptionStartDateUtc` on every read | Sealing a user without an entitlement writes a synthetic entitlement with SubscriptionStartDateUtc derived from now on every read, so the anchor moves. | `src/NetQuota.Firestore/FirestoreQuotaStorage.cs:889-903`<br>`src/NetQuota.Firestore/FirestoreQuotaStorage.cs:1438-1445` | `M9` |
| **F42** | `STRIPE-02` | `billing_stripe` | The invoice tier is taken from `lines.data[0]`, whatever that line is | Invoice lines are a list of independent charges/credits, not "the subscription's price". Picking index 0 assumes an ordering Stripe does not define and that proration invoices vio… | `src/NetQuota.Billing/Stripe/StripeWebhookParser.cs:126-135`<br>`src/NetQuota.Billing/Stripe/StripeWebhookParser.cs:235-249` | `M10` |
| **F43** | `STRIPE-03` | `billing_stripe` | The parsed `customer_id` is never used: delete reconciliation and the portal need a pre-existing link | The provider treats the customer link as write-only at checkout time and ignores the customer id that every subscription/invoice webhook already contains, so the link is missing e… | `src/NetQuota.Billing/Stripe/StripeBillingProvider.cs:102-120,343-344,381`<br>`src/NetQuota.Billing/Stripe/StripeWebhookParser.cs:102` | `M10` |
| **F44** | `STRIPE-04` | `billing_stripe` | A refund that rounds to zero credits is sent to the store as `amount = 0` and fails the webhook | The parser's proration legitimately produces zero, but the money path calls the store unconditionally and the store's contract requires a positive amount. | `src/NetQuota.Billing/Stripe/StripeBillingProvider.cs:84-93`<br>`src/NetQuota/QuotaManager.cs:891-894` | `M10` |
| **F45** | `RC-01` | `billing_revenuecat` | A TRANSFER with no eligible destination still downgrades the source | The guard counts `from` and `to` together instead of requiring a destination to move the entitlement to. Filtering anonymous destinations is correct (the app cannot resolve them),… | `src/NetQuota.Billing/RevenueCat/RevenueCatBillingProvider.cs:169-237` | `M11` |
| **F46** | `SEC-01` | `billing_webhook_security` | `WebhookRateLimiter` does not validate its configuration and a zero limit fails open | No `ArgumentOutOfRangeException` guard on `limit`/`window`, combined with a bucket-insert-then-check ordering that treats "0 allowed" as "1 allowed". | `src/NetQuota.Billing/WebhookSecurity.cs:12-26,46-55` | `M12` |
| **F47** | `A3` | `aspnetcore_integration` | `NetQuotaDistributedRateLimiter` ignores `permitCount` | The bridge has no way to pass a permit count into `QuotaManager.CheckRateLimitAsync`; the parameter is dropped. `permitCount < 0` is also not validated (the base `RateLimiter` con… | `src/NetQuota.AspNetCore/NetQuotaDistributedRateLimiter.cs:24-34` | `M13` |
| **F48** | `A5` | `aspnetcore_integration` | A 429 quota denial omits `Retry-After`, although the docs promise it | The two rejection paths are asymmetric: the exception path uses `RateLimitStatus.RetryAfter`, the quota path only has `ResetAtUtc` and was never wired to derive a delay. | `src/NetQuota.AspNetCore/NetQuotaAspNetCoreExtensions.cs:284-301` | `M13` |
| **F49** | `A6` | `aspnetcore_integration` | The usage endpoint swallows cancellation and keeps reading storage | A blanket `catch (Exception)` with no `cancellationToken.ThrowIfCancellationRequested()` guard. The same pattern exists around the entitlement read (`:338-344`). | `src/NetQuota.AspNetCore/NetQuotaAspNetCoreExtensions.cs:387-430` | `M13` |
| **F50** | `TCE-01` | `tests_core_engine` | `EntitlementPreservationTests` claims Invariant 1 but omits the sealed tombstone | The test class generalizes the invariant in its comment but implements only the field that was easiest to observe (`Timezone`) plus the pre-existing promotion test. The sealed tom… | `tests/NetQuota.Tests/EntitlementPreservationTests.cs:5-9`<br>`src/NetQuota/InMemoryQuotaStorage.cs:81-89` | `M14` |
| **F51** | `TCE-02` | `tests_core_engine` | The merge regression test never tests idempotency and never asserts usage monotonicity | The test was written against the happy-path merge (one unit per document) and mirrors the implementation's assumption instead of the invariant. Because `ConsumeOptions` permits `A… | `tests/NetQuota.Tests/QuotaManagerTests.cs:153-189`<br>`src/NetQuota/InMemoryQuotaStorage.cs:586-595` | `M14` |
| **F52** | `TESTS-01` | `tests_billing` | The refund-proration suite only exercises a payload shape Stripe cannot send | The fixtures encode the parser's preferred input rather than the provider's actual output, so the fallback branch — the one that runs in production — has no coverage at all. The s… | `tests/NetQuota.Tests/StripeRefundProrationTests.cs:42-187` | `M15` |
| **F53** | `PGA-1` | `tests_storage_adapters` | No adapter test covers `PruneExpiredAsync`, so the retention contract is unverified (and broken) | The adapter suite has no prune/retention test; the missing assertion fails against HEAD because consumed windows are never pruned. | `tests/NetQuota.Tests/NetQuotaPostgresTests.cs` | `M16` |
| **F54** | `PGA-2` | `tests_storage_adapters` | The expired-idempotency-record path is untested, so a double credit is invisible | The suite never exercises an expired idempotency record followed by a retry; the missing assertion fails against HEAD because the retry grants again. | `tests/NetQuota.Tests/NetQuotaPostgresTests.cs` | `M16` |
| **F55** | `PGA-3` | `tests_storage_adapters` | Merge idempotency is only tested for the same pair, so a cross-pair replay is invisible | Merge idempotency is tested only for the same user pair; the suite cannot see a cross-pair replay returning AlreadyMerged. | `tests/NetQuota.Tests/NetQuotaPostgresTests.cs` | `M16` |
| **F56** | `TS-01` | `tests_aspnetcore` | The amount suite never exercises `FromJsonBodyInt` through a real pipeline, so post-binding extraction and the missing… | The amount tests call FromJsonBodyInt on a hand-built DefaultHttpContext with a seekable body, not through a body-bound endpoint, so post-binding fallback and the missing clamp ar… | `tests/NetQuota.Tests/NetQuotaAspNetCoreAmountTests.cs:51-71,120-128` | `M17` |
| **F57** | `TS-02` | `tests_aspnetcore` | `NetQuotaDistributedRateLimiter` and `MapNetQuotaUsageEndpoint` have zero test coverage | No test constructs NetQuotaDistributedRateLimiter or MapNetQuotaUsageEndpoint; permitCount and entitlement-tier behavior are unverified. | `src/NetQuota.AspNetCore/NetQuotaDistributedRateLimiter.cs:24-34`<br>`src/NetQuota.AspNetCore/NetQuotaAspNetCoreExtensions.cs:307-448` | `M17` |

### 4.5 Detailed Critical and High Findings

### F1 — A1 — `FromJsonBodyInt` has no clamp; `int.MaxValue` inverts the usage counter and bypasses the quota

**Severity:** Critical · **Module:** `aspnetcore_integration`  
**Root cause:** The clamp was added to `FromHeader` and `FromBodyByteLength` but not to `FromJsonBodyInt`; the core validates only the sign of `amount`, and the reference adapter adds amounts with unchecked `int` arithmetic.  
**Anchor:** `src/NetQuota/QuotaManager.cs:508-511`<br>`src/NetQuota/InMemoryQuotaStorage.cs:219`  
**Repro:** `dotnet test --project .tmp/audit/repros/aspnetcore_integration/AspNetCoreIntegrationRepro.csproj -c Release`

```diff
--- a/src/NetQuota.AspNetCore/NetQuotaAspNetCoreExtensions.cs
+++ b/src/NetQuota.AspNetCore/NetQuotaAspNetCoreExtensions.cs
@@ -125,7 +125,7 @@ public static class NetQuotaAmountExtractors
                     prop.TryGetInt32(out var val) &&
                     val >= 1)
                 {
-                    return val;
+                    return Math.Min(val, 1_000_000);
                 }
```

### F2 — IB-01 — `GitVersion.yml` cannot be evaluated: `next-version: 1.0.0` fails the version step

**Severity:** Critical · **Module:** `infra_build_release`  
**Root cause:** GitVersion 6.8.2 rejects the unprefixed `next-version` value and accepts `v1.0.0` (both verified above), i.e. the 6.8.2 parser applies the configured tag prefix to `next-version`. The workflow floats on `6.x`, so it picks up the affected build; the repo cannot make progress on a release until either the config or the tool pin changes. Note that `docs/publishing.md:4` already states that versioning is derived from the tag, so the `next-version` seed is redundant for a repository that has tags.  
**Anchor:** `GitVersion.yml:3`<br>`.github/workflows/release.yml:30-32`  
**Repro:** `pwsh -NoProfile -File .tmp/audit/repros/infra_build_release/verify_gitversion.ps1`

```diff
--- a/GitVersion.yml
+++ b/GitVersion.yml
@@ -1,6 +1,5 @@
 workflow: GitHubFlow/v1
 tag-prefix: '[vV]'
-next-version: 1.0.0
 semantic-version-format: Strict
```

### F3 — IB-02 — The nuget.org push step passes a directory-qualified glob the CLI does not expand

**Severity:** Critical · **Module:** `infra_build_release`  
**Root cause:** The workflow assumes the CLI expands a path glob. It does not; the quoted pattern has a directory component, so the shell does not expand it either. The prior step's bash array works because it is an unquoted assignment.  
**Anchor:** `.github/workflows/release.yml:71,75`  
**Repro:** `pwsh -NoProfile -File .tmp/audit/repros/infra_build_release/verify_push_glob.ps1`

```diff
--- a/.github/workflows/release.yml
+++ b/.github/workflows/release.yml
@@ -69,11 +69,12 @@
       - name: Push packages to nuget.org
+        working-directory: artifacts/packages
         run: |
-          dotnet nuget push "artifacts/packages/*.nupkg" \
+          dotnet nuget push "*.nupkg" \
             --api-key "${{ steps.login.outputs.NUGET_API_KEY }}" \
             --source https://api.nuget.org/v3/index.json \
             --skip-duplicate
-          dotnet nuget push "artifacts/packages/*.snupkg" \
+          dotnet nuget push "*.snupkg" \
             --api-key "${{ steps.login.outputs.NUGET_API_KEY }}" \
             --source https://api.nuget.org/v3/index.json \
             --skip-duplicate
```

### F4 — CCD-01 — `IdempotencyTtl <= 0` silently disables money-path replay protection

**Severity:** High · **Module:** `core_domain_contracts`  
**Root cause:** `IdempotencyTtl` is declared at `src/NetQuota/QuotaModels.cs:77`, but `Validate()` has no positivity guard. The storage contract assumes a positive TTL when it writes `expiresAt`.  
**Anchor:** `src/NetQuota/QuotaModels.cs:77`<br>`src/NetQuota/QuotaModels.cs:84-179`  
**Repro:** `dotnet test --project .tmp/audit/repros/core_domain_contracts/CoreDomainContractsRepro.csproj -c Release -- --filter-class CoreDomainContractsRepro.ContractDefectTests`

```diff
--- a/src/NetQuota/QuotaModels.cs
+++ b/src/NetQuota/QuotaModels.cs
@@ -96,6 +96,11 @@ public sealed record QuotaConfig
         if (!Tiers.ContainsKey(DefaultTier))
         {
             throw new QuotaConfigValidationException($"Default tier '{DefaultTier}' was not found in Tiers.");
         }
 
+        if (IdempotencyTtl <= TimeSpan.Zero)
+        {
+            throw new QuotaConfigValidationException("IdempotencyTtl must be greater than zero.");
+        }
+
         if (CircuitBreakerConfig is { Enabled: true, FailureThreshold: <= 0 })
```

### F5 — CCD-02 — Case-sensitive `Tiers` dictionary is accepted and breaks case-insensitive tier resolution

**Severity:** High · **Module:** `core_domain_contracts`  
**Root cause:** `Tiers` is an auto-property (`src/NetQuota/QuotaModels.cs:75`) whose default initializer is bypassed by deserialization or by an explicitly supplied dictionary. The comparer is part of the dictionary instance, not the `QuotaConfig` contract, so the “tier names are case-insensitive” invariant cannot be enforced by the current declaration.  
**Anchor:** `src/NetQuota/QuotaModels.cs:75`<br>`src/NetQuota/QuotaManager.cs:108-110`  
**Repro:** `dotnet test --project .tmp/audit/repros/core_domain_contracts/CoreDomainContractsRepro.csproj -c Release -- --filter-class CoreDomainContractsRepro.ContractDefectTests`

```diff
--- a/src/NetQuota/QuotaModels.cs
+++ b/src/NetQuota/QuotaModels.cs
@@ -72,8 +72,24 @@ public sealed record FallbackConfig(
 public sealed record QuotaConfig
 {
     public string DefaultTier { get; init; } = "free";
-    public IReadOnlyDictionary<string, TierConfig> Tiers { get; init; } = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase);
+    private IReadOnlyDictionary<string, TierConfig> _tiers =
+        new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase);
+
+    public IReadOnlyDictionary<string, TierConfig> Tiers
+    {
+        get => _tiers;
+        init => _tiers = value is Dictionary<string, TierConfig> d &&
+                        ReferenceEquals(d.Comparer, StringComparer.OrdinalIgnoreCase)
+            ? d
+            : new Dictionary<string, TierConfig>(
+                value ?? new Dictionary<string, TierConfig>(),
+                StringComparer.OrdinalIgnoreCase);
+    }
+
     public TimeZoneInfo PeriodTimeZone { get; init; } = TimeZoneInfo.Utc;
```

### F6 — QM-01 — Anonymous monthly quota resets every day

**Severity:** High · **Module:** `core_quota_manager`  
**Root cause:** `BuildWindow` overrides the nullable anchor that `PeriodWindow.ForMonthly` already supports for calendar-month cycles. The null-anchor branch at `QuotaModels.cs:246-257` is unreachable from `QuotaManager`, so anonymous monthly quotas are daily-rolling.  
**Anchor:** `src/NetQuota/QuotaManager.cs:1238-1244`<br>`src/NetQuota/QuotaModels.cs:246-257`  
**Repro:** `dotnet test --project .tmp/audit/repros/core_quota_manager/CoreQuotaManagerRepro.csproj -c Release -- --filter-class CoreQuotaManagerRepro.ManagerDefectTests --filter-class CoreQuotaManagerRepro.CancellationTests`

```diff
--- a/src/NetQuota/QuotaManager.cs
+++ b/src/NetQuota/QuotaManager.cs
@@ -1236,8 +1236,9 @@ public sealed class QuotaManager
-        // Align monthly cycles with startOfDayUTC(now) when SubscriptionStartDateUtc is absent (goquota d25f469)
-        var monthlyAnchor = ent?.SubscriptionStartDateUtc ?? StartOfDayUtc(nowUtc);
+        // Without a subscription anchor, use the calendar month. Anchoring on "today" changes the
+        // period key every day and resets monthly quotas daily.
+        var monthlyAnchor = ent?.SubscriptionStartDateUtc;
 
         return periodType switch
         {
```

### F7 — QM-02 — `explicitTier` bypasses entitlement state and accepts unknown tier names

**Severity:** High · **Module:** `core_quota_manager`  
**Root cause:** The code conflates “override the tier used for limits” with “do not resolve the entitlement”. The entitlement is the source of the period anchor, timezone, promotion, and seal state; only the tier name should be overridden. `GetTierConfig` is intentionally fallback-based for unknown stored entitlement tiers, so it cannot validate caller-supplied names.  
**Anchor:** `src/NetQuota/QuotaManager.cs:301-304,519-527,1139-1143`<br>`src/NetQuota/QuotaManager.cs:1262-1265`  
**Repro:** `dotnet test --project .tmp/audit/repros/core_quota_manager/CoreQuotaManagerRepro.csproj -c Release -- --filter-class CoreQuotaManagerRepro.ManagerDefectTests --filter-class CoreQuotaManagerRepro.CancellationTests`

```diff
--- a/src/NetQuota/QuotaManager.cs
+++ b/src/NetQuota/QuotaManager.cs
@@ -1256,6 +1256,22 @@ public sealed class QuotaManager
     private string ResolveTierName(QuotaEntitlement? ent, DateTimeOffset nowUtc)
     {
         var candidate = ent?.ResolveEffectiveTier(nowUtc, _config.DefaultTier) ?? _config.DefaultTier;
         return _config.Tiers.ContainsKey(candidate) ? candidate : _config.DefaultTier;
     }
 
+    private string? ValidateExplicitTier(string? explicitTier)
+    {
+        if (string.IsNullOrWhiteSpace(explicitTier))
+        {
+            return null;
+        }
+
+        if (!_config.Tiers.ContainsKey(explicitTier))
+        {
+            throw new QuotaConfigValidationException($"Unknown tier '{explicitTier}'.");
+        }
+
+        return explicitTier;
+    }
+
     private TierConfig GetTierConfig(string tierName) =>
```

### F8 — ISM-01 — Merge rewrites the target's `Used` to the distinct-key count and erases usage

**Severity:** High · **Module:** `core_inmemory_storage`  
**Root cause:** The merge treats "one distinct key" as "one used unit". That holds only while every consume charged `amount == 1`. `TryConsumeAsync` allows any positive amount with a distinct key and stores `Used += amount`, so `Used` can exceed `DistinctKeys.Count`. The merge then overwrites the target's `Used` with the smaller key count. The same formula is duplicated in the Postgres and Firestore merge paths, so this is a cross-adapter accounting bug, not an in-memory quirk.  
**Anchor:** `src/NetQuota/InMemoryQuotaStorage.cs:580-595`<br>`src/NetQuota.Postgres/PostgresQuotaStorage.Merge.cs:120-129`  
**Repro:** `dotnet test --project .tmp/audit/repros/core_inmemory_storage/CoreInMemoryStorageRepro.csproj -c Release -- --filter-class NetQuota.Audit.CoreInMemoryStorage.InMemoryDefectTests`

```diff
--- a/src/NetQuota/InMemoryQuotaStorage.cs
+++ b/src/NetQuota/InMemoryQuotaStorage.cs
@@ -583,11 +583,15 @@ public sealed class InMemoryQuotaStorage(Func<DateTimeOffset>? clock = null)
                         .OrderBy(x => x, StringComparer.Ordinal)
                         .ToList();
 
-                    var newTargetUsed = mergedDistinct.Count > 0
-                        ? mergedDistinct.Count
-                        : tgtState.Used + srcState.Used;
+                    // A distinct key represents one unit only when every recorded consume charged one
+                    // unit. If either side charged amount > 1, additive accounting is the only safe
+                    // merge; never replace real usage with the key count.
+                    var countBased = tgtState.Used == tgtState.DistinctKeys.Count &&
+                                     srcState.Used == srcState.DistinctKeys.Count;
+                    var newTargetUsed = countBased
+                        ? Math.Max(tgtState.Used, mergedDistinct.Count)
+                        : tgtState.Used + srcState.Used;
 
                     var added = Math.Max(0, newTargetUsed - tgtState.Used);
```

### F9 — ISM-02 — `DrainRemainingAsync` silently does nothing when `Used == 0`

**Severity:** High · **Module:** `core_inmemory_storage`  
**Root cause:** Draining is expressed as `Limit = Used`, but `Limit == 0` is overloaded as "no stored limit". A drain of a zero-usage window therefore writes a value every reader interprets as "use the tier default". The consume path has the same overload and additionally ignores the stored limit when the stored tier is `null`.  
**Anchor:** `src/NetQuota/InMemoryQuotaStorage.cs:482-488`  
**Repro:** `dotnet test --project .tmp/audit/repros/core_inmemory_storage/CoreInMemoryStorageRepro.csproj -c Release -- --filter-class NetQuota.Audit.CoreInMemoryStorage.InMemoryDefectTests`

```diff
--- a/src/NetQuota/InMemoryQuotaStorage.cs
+++ b/src/NetQuota/InMemoryQuotaStorage.cs
@@ -14,7 +14,8 @@ public sealed class InMemoryQuotaStorage(Func<DateTimeOffset>? clock = null)
     private sealed record UsageState(
         int Used,
         int Limit,
         List<string> DistinctKeys,
-        string? Tier = null);
+        string? Tier = null,
+        bool Drained = false);
@@ -152,7 +153,7 @@ public sealed class InMemoryQuotaStorage(Func<DateTimeOffset>? clock = null)
-            var effectiveLimit = window.PeriodType == PeriodType.Forever
+            var effectiveLimit = window.PeriodType == PeriodType.Forever || state.Drained
                 ? state.Limit
                 : (state.Limit != 0 ? state.Limit : defaultLimit);
@@ -484,7 +485,7 @@ public sealed class InMemoryQuotaStorage(Func<DateTimeOffset>? clock = null)
-            _usages[key] = existing with { Limit = Math.Max(0, existing.Used) };
+            _usages[key] = existing with { Limit = Math.Max(0, existing.Used), Drained = true };
@@ -706,7 +707,7 @@ public sealed class InMemoryQuotaStorage(Func<DateTimeOffset>? clock = null)
     private static int ResolveEffectiveLimitForConsume(StorageConsumeSpec spec, UsageState state)
     {
-        if (spec.Window.PeriodType == PeriodType.Forever)
+        if (spec.Window.PeriodType == PeriodType.Forever || state.Drained)
         {
             return state.Limit;
         }
```

### F10 — TSM-01 — `GetUsageAsync` lets lagging cold storage overwrite hot and erase a consume

**Severity:** High · **Module:** `core_tiered_storage_metrics`  
**Root cause:** The tiered wrapper has two conflicting notions of authority. In `AsyncUsageSync` mode it writes hot first and cold asynchronously, which makes hot authoritative for consumption; but `GetUsageAsync` always reads cold first and seeds hot from it, which treats cold as authoritative. The two rules meet in the window where the queue has not drained, and the seed overwrites the newer hot value. `GetEntitlementAsync` (`:722-737`) has the same cold-fill shape but entitlements are written cold + hot sync  
**Anchor:** `src/NetQuota/TieredStorageAndMetrics.cs:767-782`<br>`src/NetQuota/TieredStorageAndMetrics.cs`  
**Repro:** `dotnet test --project .tmp/audit/repros/core_tiered_storage_metrics/CoreTieredStorageMetricsRepro.csproj -c Release -- --filter-class NetQuota.Audit.CoreTieredStorageMetrics.TieredDefectTests`

```diff
--- a/src/NetQuota/TieredStorageAndMetrics.cs
+++ b/src/NetQuota/TieredStorageAndMetrics.cs
@@ -628,6 +628,8 @@ public sealed class TieredQuotaStorage :
     private readonly Channel<StorageConsumeSpec>? _coldSyncQueue;
     private readonly Task? _coldSyncPump;
     private readonly CancellationTokenSource? _coldSyncCts;
+    private int _pendingColdWrites;
+    private TaskCompletionSource _coldIdlePulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
@@ -685,6 +687,14 @@ public sealed class TieredQuotaStorage :
             catch (Exception ex)
             {
                 _config.AsyncErrorHandler?.Invoke(ex);
             }
+            finally
+            {
+                if (Interlocked.Decrement(ref _pendingColdWrites) == 0)
+                {
+                    Interlocked.Exchange(
+                            ref _coldIdlePulse,
+                            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
+                        .TrySetResult();
+                }
+            }
         }
@@ -767,6 +777,7 @@ public sealed class TieredQuotaStorage :
     public async Task<QuotaUsage?> GetUsageAsync(
         string userId, string resource, PeriodWindow window, int defaultLimit,
         CancellationToken cancellationToken = default)
     {
+        await WaitForColdSyncIdleAsync(cancellationToken);
         var cold = await _config.Cold.GetUsageAsync(userId, resource, window, defaultLimit, cancellationToken);
         if (cold is not null)
         {
@@ -800,13 +811,13 @@ public sealed class TieredQuotaStorage :
 
         var hotOutcome = await _config.Hot.TryConsumeAsync(spec, cancellationToken);
-        if (_coldSyncQueue is not null &&
-            !_coldSyncQueue.Writer.TryWrite(spec))
+        if (_coldSyncQueue is not null && !EnqueueColdSync(spec))
         {
             _config.AsyncErrorHandler?.Invoke(
                 new QuotaException("Cold usage sync queue is full; the consumption was not mirrored to cold storage."));
         }
 
         return hotOutcome;
     }
+
+    private bool EnqueueColdSync(StorageConsumeSpec spec)
+    {
+        Interlocked.Increment(ref _pendingColdWrites);
+        if (_coldSyncQueue!.Writer.TryWrite(spec))
+        {
+            return true;
+        }
+
+        DropPendingColdWrite();
+        return false;
+    }
+
+    private void DropPendingColdWrite()
+    {
+        if (Interlocked.Decrement(ref _pendingColdWrites) == 0)
+        {
+            Interlocked.Exchange(
+                    ref _coldIdlePulse,
+                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
+                .TrySetResult();
+        }
+    }
+
+    private async Task WaitForColdSyncIdleAsync(CancellationToken cancellationToken)
+    {
+        if (_coldSyncQueue is null)
+        {
+            return;
+        }
+
+        while (Volatile.Read(ref _pendingColdWrites) > 0)
+        {
+            var pulse = Volatile.Read(ref _coldIdlePulse);
+            if (Volatile.Read(ref _pendingColdWrites) == 0)
+            {
+                return;
+            }
+
+            await pulse.Task.WaitAsync(cancellationToken);
+        }
+    }
```

### F11 — TSM-03 — A full async queue silently drops the cold write; the documented error signal never fires

**Severity:** High · **Module:** `core_tiered_storage_metrics`  
**Root cause:** `BoundedChannelFullMode.DropWrite` is documented as dropping the incoming item and reporting success to the writer; the channel never returns `false` for a full buffer in this mode. The code assumes `TryWrite == false` means "full", which is only true for `BoundedChannelFullMode.Wait`. Therefore the documented overflow notification is dead code. (The queue-full exception text at `:807` is never constructed in this path.)  
**Anchor:** `src/NetQuota/TieredStorageAndMetrics.cs:646-652`<br>`src/NetQuota/TieredStorageAndMetrics.cs`  
**Repro:** `dotnet test --project .tmp/audit/repros/core_tiered_storage_metrics/CoreTieredStorageMetricsRepro.csproj -c Release -- --filter-class NetQuota.Audit.CoreTieredStorageMetrics.TieredDefectTests`

```diff
--- a/src/NetQuota/TieredStorageAndMetrics.cs
+++ b/src/NetQuota/TieredStorageAndMetrics.cs
@@ -648,7 +648,7 @@ public sealed class TieredQuotaStorage :
             {
                 SingleReader = true,
                 SingleWriter = false,
-                FullMode = BoundedChannelFullMode.DropWrite
+                FullMode = BoundedChannelFullMode.Wait
             });
```

### F12 — PGC-1 — Reusing a consume/refund idempotency key after its TTL throws `23505`

**Severity:** High · **Module:** `storage_postgres_core`  
**Root cause:** can only replay *unexpired* records, so it converts the constraint violation into a caller-visible `PostgresException`.  
**Anchor:** `src/NetQuota.Postgres/PostgresQuotaStorage.Records.cs:60-68,100-120`<br>`src/NetQuota.Postgres/PostgresQuotaStorage.cs:104-122`  
**Repro:** `$env:NETQUOTA_POSTGRES='Host=127.0.0.1;Port=55433;Database=netquota;Username=netquota;Password=netquota'; dotnet test --project .tmp/audit/repros/storage_postgres_core/StoragePostgresCoreRepro.csproj -c Release`

```diff
@@ -58,6 +58,9 @@ private static async Task InsertConsumptionRecordAsync(
         CancellationToken cancellationToken)
     {
         await using var command = Command(connection, tx, """
+            DELETE FROM netquota_consumption_records
+            WHERE idempotency_key = @key AND expires_at_utc <= @now;
+
             INSERT INTO netquota_consumption_records (
                 idempotency_key, user_id, resource, amount, period_type, period_key,
                 period_start_utc, period_end_utc, new_used, effective_limit, allowed,
@@ -98,6 +101,9 @@ private static async Task InsertRefundRecordAsync(
         CancellationToken cancellationToken)
     {
         await using var command = Command(connection, tx, """
+            DELETE FROM netquota_refund_records
+            WHERE idempotency_key = @key AND expires_at_utc <= @now;
+
             INSERT INTO netquota_refund_records (
                 idempotency_key, user_id, resource, amount, period_type, period_key,
                 period_start_utc, period_end_utc, reason, timestamp_utc, expires_at_utc)
```

### F13 — PGE-1 — An expired credit record is never refreshed, so every retry with the same key grants or refunds again

**Severity:** High · **Module:** `storage_postgres_entitlements_credits`  
**Root cause:** window never restarts. `InMemoryQuotaStorage` overwrites the idempotency entry on a post-expiry call (`InMemoryQuotaStorage.cs:419-427`, `:451-459`), which is why its behaviour is correct.  
**Anchor:** `src/NetQuota.Postgres/PostgresQuotaStorage.Records.cs:226`<br>`src/NetQuota.Postgres/PostgresQuotaStorage.cs:433-436`  
**Repro:** `$env:NETQUOTA_POSTGRES='Host=127.0.0.1;Port=55433;Database=netquota;Username=netquota;Password=netquota'; dotnet test --project .tmp/audit/repros/storage_postgres_entitlements_credits/StoragePostgresCreditsRepro.csproj -c Release`

```diff
@@ -222,7 +222,9 @@ private static async Task InsertCreditRecordAsync(
             INSERT INTO netquota_credit_records (
                 idempotency_key, kind, user_id, resource, amount, timestamp_utc, expires_at_utc)
             VALUES (@key, @kind, @user, @resource, @amount, @now, @expires)
-            ON CONFLICT (idempotency_key) DO NOTHING
+            ON CONFLICT (idempotency_key) DO UPDATE SET
+                expires_at_utc = EXCLUDED.expires_at_utc,
+                timestamp_utc = EXCLUDED.timestamp_utc
             """,
```

### F14 — REDIS-01 — Idempotency record keys collide across users/resources when IDs contain `:`

**Severity:** High · **Module:** `storage_redis_atomicity`  
**Root cause:** ScopedKey concatenates user/resource/key with ':'; a ':' inside an id can produce the same key for a different pair, and replay never verifies ownership.  
**Anchor:** `src/NetQuota.Redis/RedisQuotaStorage.Keys.cs:19-23`<br>`src/NetQuota.Redis/RedisQuotaStorage.Keys.cs:34-39`  
**Repro:** `$env:NETQUOTA_REDIS='127.0.0.1:6391'; dotnet test --project .tmp/audit/repros/storage_redis_atomicity/StorageRedisAtomicityRepro.csproj -c Release -- --filter-class StorageRedisAtomicityRepro.RedisAtomicityTests`

```diff
--- a/src/NetQuota/QuotaManager.cs
+++ b/src/NetQuota/QuotaManager.cs
@@
-    public static string ScopedIdempotencyKey(string userId, string? key) =>
-        string.IsNullOrWhiteSpace(key)
-            ? string.Empty
-            : (key.StartsWith($"{userId}:", StringComparison.Ordinal) ? key : $"{userId}:{key}");
+    public static string ScopedIdempotencyKey(string userId, string? key) =>
+        string.IsNullOrWhiteSpace(key)
+            ? string.Empty
+            : $"{userId.Length}:{userId}:{key}";
--- a/src/NetQuota.Redis/RedisQuotaStorage.Keys.cs
+++ b/src/NetQuota.Redis/RedisQuotaStorage.Keys.cs
@@
-    private static string ScopedKey(string userId, string idempotencyKey) =>
-        idempotencyKey.StartsWith($"{userId}:", StringComparison.Ordinal)
-            ? idempotencyKey
-            : $"{userId}:{idempotencyKey}";
+    private static string ScopedKey(string userId, string idempotencyKey)
+    {
+        var prefix = $"{userId.Length}:{userId}:";
+        return idempotencyKey.StartsWith(prefix, StringComparison.Ordinal)
+            ? idempotencyKey
+            : prefix + idempotencyKey;
+    }
--- a/src/NetQuota.Redis/RedisQuotaStorage.cs
+++ b/src/NetQuota.Redis/RedisQuotaStorage.cs
@@
-            : RecordKey($"topup:{userId}:{resource}:{idempotencyKey}");
+            : RecordKey($"topup:{Segment(userId)}:{Segment(resource)}:{Segment(idempotencyKey)}");
@@
-            : RecordKey($"refund_credits:{userId}:{resource}:{idempotencyKey}");
+            : RecordKey($"refund_credits:{Segment(userId)}:{Segment(resource)}:{Segment(idempotencyKey)}");
```

### F15 — REDIS-02 — Distinct-key sets never receive a TTL; Redis memory grows without bound

**Severity:** High · **Module:** `storage_redis_atomicity`  
**Root cause:** ConsumeScript creates a distinctKey set but never sets EXPIRE; usage hashes get a TTL, the sets do not.  
**Anchor:** `src/NetQuota.Redis/RedisQuotaStorage.Scripts.cs:76-77`<br>`src/NetQuota.Redis/RedisQuotaStorage.Scripts.cs:270-275`  
**Repro:** `$env:NETQUOTA_REDIS='127.0.0.1:6391'; dotnet test --project .tmp/audit/repros/storage_redis_atomicity/StorageRedisAtomicityRepro.csproj -c Release -- --filter-class StorageRedisAtomicityRepro.RedisAtomicityTests`

```diff
--- a/src/NetQuota.Redis/RedisQuotaStorage.Scripts.cs
+++ b/src/NetQuota.Redis/RedisQuotaStorage.Scripts.cs
@@
-        if distinctMember ~= '' then redis.call('SADD', distinctKey, distinctMember) end
+        if distinctMember ~= '' then
+            redis.call('SADD', distinctKey, distinctMember)
+            if usageTtl > 0 then redis.call('EXPIRE', distinctKey, usageTtl) end
+        end
@@
         if ARGV[7] == '1' then
             redis.call('DEL', distinctKey)
             for i = 8, #ARGV do
                 redis.call('SADD', distinctKey, ARGV[i])
             end
+            if tonumber(ARGV[5]) > 0 then redis.call('EXPIRE', distinctKey, tonumber(ARGV[5])) end
         end
@@
         redis.call('DEL', distinctKey)
         for i = 6, #ARGV do
             redis.call('SADD', distinctKey, ARGV[i])
         end
+        if tonumber(ARGV[5]) > 0 then redis.call('EXPIRE', distinctKey, tonumber(ARGV[5])) end
```

### F16 — FIRE-01 — Document ids are not injective; usage and entitlement documents collide across users/resources

**Severity:** High · **Module:** `storage_firestore_transactions`  
**Root cause:** Firestore document IDs concatenate/sanitize user, resource, period, and window segments without escaping; different tuples can collapse to the same document ID, so one tenant's usage or entitlement can overwrite another's.  
**Anchor:** `src/NetQuota.Firestore/FirestoreQuotaStorage.cs:1196-1197`<br>`src/NetQuota.Firestore/FirestoreQuotaStorage.cs:1199-1201`  
**Repro:** `$env:FIRESTORE_EMULATOR_HOST='127.0.0.1:8091'; dotnet test --project .tmp/audit/repros/storage_firestore_transactions/StorageFirestoreTransactionsRepro.csproj -c Release -- --filter-class StorageFirestoreTransactionsRepro.FirestoreTransactionTests`

```diff
--- a/src/NetQuota.Firestore/FirestoreQuotaStorage.cs
+++ b/src/NetQuota.Firestore/FirestoreQuotaStorage.cs
@@
     private DocumentReference EntitlementDoc(string userId) =>
-        firestoreDb.Collection(_options.EntitlementsCollection).Document(SanitizeDocId(userId));
+        firestoreDb.Collection(_options.EntitlementsCollection)
+            .Document(HashDocId($"ent\u001f{userId}"));
 
     private DocumentReference UsageDoc(string userId, string resource, PeriodWindow window) =>
         firestoreDb.Collection(_options.UsageCollection)
-            .Document(SanitizeDocId($"{userId}_{resource}_{window.PeriodType}_{window.Key}"));
+            .Document(HashDocId($"usage\u001f{userId}\u001f{resource}\u001f{window.PeriodType}\u001f{window.Key}"));
 
     private DocumentReference RateLimitDoc(string resource, string userId) =>
         firestoreDb.Collection(_options.RateLimitsCollection)
-            .Document(SanitizeDocId($"{resource}_{userId}"));
+            .Document(HashDocId($"rate\u001f{resource}\u001f{userId}"));
```

### F17 — FIRE-02 — `SetEntitlementAsync` cannot clear `expiresAtUtc`; an old expiry silently downgrades a non-expiring user

**Severity:** High · **Module:** `storage_firestore_transactions`  
**Root cause:** SetEntitlementAsync always writes expiresAtUtc; an incoming null does not clear the existing field, so a stale expiry silently downgrades a lifetime user.  
**Anchor:** `src/NetQuota.Firestore/FirestoreQuotaStorage.cs:115`<br>`src/NetQuota.Firestore/FirestoreQuotaStorage.cs:1402-1410`  
**Repro:** `$env:FIRESTORE_EMULATOR_HOST='127.0.0.1:8091'; dotnet test --project .tmp/audit/repros/storage_firestore_transactions/StorageFirestoreTransactionsRepro.csproj -c Release -- --filter-class StorageFirestoreTransactionsRepro.FirestoreTransactionTests`

```diff
--- a/src/NetQuota.Firestore/FirestoreQuotaStorage.cs
+++ b/src/NetQuota.Firestore/FirestoreQuotaStorage.cs
@@
-        if (ent.ExpiresAtUtc is { } exp)
-        {
-            dict["expiresAtUtc"] = Timestamp.FromDateTimeOffset(exp);
-        }
+        dict["expiresAtUtc"] = ent.ExpiresAtUtc is { } exp
+            ? Timestamp.FromDateTimeOffset(exp)
+            : FieldValue.Delete;
```

### F18 — FIRE-05 — Consume/refund idempotency keys collide across users when the user id or key contains `:`

**Severity:** High · **Module:** `storage_firestore_transactions`  
**Root cause:** Idempotency document ids are a hash of 'user:key' without length-prefixing; ':' in user or key lets two pairs collide, and replay does not verify the owner.  
**Anchor:** `src/NetQuota.Firestore/FirestoreQuotaStorage.cs:1259-1265`<br>`src/NetQuota.Firestore/FirestoreQuotaStorage.cs:1267-1273`  
**Repro:** `$env:FIRESTORE_EMULATOR_HOST='127.0.0.1:8091'; dotnet test --project .tmp/audit/repros/storage_firestore_transactions/StorageFirestoreTransactionsRepro.csproj -c Release -- --filter-class StorageFirestoreTransactionsRepro.FirestoreTransactionTests`

```diff
--- a/src/NetQuota/QuotaManager.cs
+++ b/src/NetQuota/QuotaManager.cs
@@
-    public static string ScopedIdempotencyKey(string userId, string? key) =>
-        string.IsNullOrWhiteSpace(key)
-            ? string.Empty
-            : (key.StartsWith($"{userId}:", StringComparison.Ordinal) ? key : $"{userId}:{key}");
+    public static string ScopedIdempotencyKey(string userId, string? key) =>
+        string.IsNullOrWhiteSpace(key)
+            ? string.Empty
+            : $"{userId.Length}:{userId}:{key}";
--- a/src/NetQuota.Firestore/FirestoreQuotaStorage.cs
+++ b/src/NetQuota.Firestore/FirestoreQuotaStorage.cs
@@
     private static string NormalizeConsumeDocKey(string userId, string idempotencyKey)
-    {
-        var scoped = idempotencyKey.StartsWith($"{userId}:", StringComparison.Ordinal)
-            ? idempotencyKey
-            : $"{userId}:{idempotencyKey}";
-        return $"consume_{scoped}";
-    }
+        => $"consume_{userId.Length}:{userId}:{idempotencyKey}";
 
     private static string NormalizeRefundDocKey(string userId, string idempotencyKey)
-    {
-        var scoped = idempotencyKey.StartsWith($"{userId}:", StringComparison.Ordinal)
-            ? idempotencyKey
-            : $"{userId}:{idempotencyKey}";
-        return $"refund_{scoped}";
-    }
+        => $"refund_{userId.Length}:{userId}:{idempotencyKey}";
```

### F19 — STRIPE-01 — Sequential partial refunds revoke the cumulative share (credit over-revocation)

**Severity:** High · **Module:** `billing_stripe`  
**Root cause:** `ParseCreditPurchase` treats the cumulative `amount_refunded` as "this refund's amount" whenever the payload does not expand `charge.refunds`, and keys the revocation by the event id, so repeated partial refunds (and the two event types that can describe one refund) each revoke another cumulative slice. The parser's own comment (`StripeWebhookParser.cs:190-193`) states the intent — "Revoke only this refund's share … so the events cannot revoke the whole pack once per event or twice for the same   
**Anchor:** `src/NetQuota.Billing/Stripe/StripeWebhookParser.cs:194-210`<br>`src/NetQuota.Billing/Stripe/StripeBillingProvider.cs:84-96`  
**Repro:** `dotnet test --project .tmp/audit/repros/billing_stripe/StripeBillingRepro.csproj -c Release`

```diff
--- a/src/NetQuota.Billing/Stripe/StripeWebhookParser.cs
+++ b/src/NetQuota.Billing/Stripe/StripeWebhookParser.cs
@@ -188,27 +188,43 @@
         if (kind == BillingWebhookKind.Refund)
         {
-            // A pack can be refunded in several partial refunds, and Stripe emits both
-            // payment_intent.refunded and charge.refunded for the same refund. Revoke only this
-            // refund's share of the pack and key it by the refund id, so the events cannot revoke
-            // the whole pack once per event or twice for the same refund.
+            // `amount_refunded` on the charge/payment intent is CUMULATIVE, and `charge.refunds` is an
+            // includable field that Stripe does not return in webhook payloads. Using the cumulative
+            // amount as this refund's share revokes 20 + 40 + 60 ... credits for three 20% refunds.
+            // Revoke a share only when the payload names the individual refund; a full refund revokes
+            // the whole pack under a payment-intent key so redeliveries cannot revoke it twice.
             var entry = Nested(obj, "refunds", "data", "0");
-            var refundedAmount = (entry is { } e ? Long(e, "amount") : null) ?? Long(obj, "amount_refunded");
-            identity = (entry is { } e2 ? Text(e2, "id") : null) ?? eventId;
+            var total = Long(obj, "amount_received") ?? Long(obj, "amount");
+            var cumulative = Long(obj, "amount_refunded");
 
-            if (refundedAmount is null or <= 0)
+            if (entry is { } refund)
             {
-                return BillingWebhookResult.Ignored("refund without amount");
-            }
+                var refundedAmount = Long(refund, "amount");
+                if (refundedAmount is null or <= 0)
+                {
+                    return BillingWebhookResult.Ignored("refund without amount");
+                }
 
-            var total = Long(obj, "amount_received") ?? Long(obj, "amount");
-            credits = total is > 0
-                ? (int)Math.Clamp(
-                    Math.Round(packCredits.Value * (double)refundedAmount.Value / total.Value, MidpointRounding.AwayFromZero),
-                    0,
-                    packCredits.Value)
-                : packCredits.Value;
-            priceCents = refundedAmount.Value;
+                identity = Text(refund, "id") ?? eventId;
+                credits = total is > 0
+                    ? (int)Math.Clamp(
+                        Math.Round(packCredits.Value * (double)refundedAmount.Value / total.Value, MidpointRounding.AwayFromZero),
+                        0,
+                        packCredits.Value)
+                    : packCredits.Value;
+                priceCents = refundedAmount.Value;
+            }
+            else
+            {
+                if (total is > 0 && cumulative is { } refunded && refunded < total)
+                {
+                    return BillingWebhookResult.Ignored(
+                        "partial refund without a refund entry; charge.refunds is not included in webhook payloads");
+                }
+
+                identity = "refund-full:" + (Text(obj, "payment_intent") ?? Text(obj, "id") ?? eventId);
+                credits = packCredits.Value;
+                priceCents = cumulative ?? total ?? 0;
+            }
         }
```

### F20 — A2 — `FromJsonBodyInt` charges the fallback on every endpoint that binds a body (filters run after binding)

**Severity:** High · **Module:** `aspnetcore_integration`  
**Root cause:** Minimal-API filters run after model binding; the JSON extractor sees an exhausted request body, swallows the parse failure and returns the fallback amount of 1.  
**Anchor:** `src/NetQuota.AspNetCore/NetQuotaAspNetCoreExtensions.cs:116-144,234`<br>`src/NetQuota.AspNetCore/NetQuotaAspNetCoreExtensions.cs:282`  
**Repro:** `dotnet test --project .tmp/audit/repros/aspnetcore_integration/AspNetCoreIntegrationRepro.csproj -c Release`

```diff
--- a/src/NetQuota.AspNetCore/NetQuotaAspNetCoreExtensions.cs
+++ b/src/NetQuota.AspNetCore/NetQuotaAspNetCoreExtensions.cs
@@ -141,7 +141,16 @@ public static class NetQuotaAmountExtractors
             return fallback;
         };
+
+    public static Func<EndpointFilterInvocationContext, ValueTask<int>> FromBoundArgument<T>(
+        Func<T, int> selector, int fallback = 1) =>
+        ctx =>
+        {
+            var value = ctx.Arguments.OfType<T>().Select(selector).FirstOrDefault();
+            return ValueTask.FromResult(value >= 1 ? Math.Min(value, 1_000_000) : Math.Max(1, fallback));
+        };
 }
@@ -149,6 +158,13 @@ public sealed record NetQuotaEnforcementOptions
     public required Func<HttpContext, ValueTask<string?>> ResolveUserIdAsync { get; init; }
     public required Func<HttpContext, ValueTask<string>> ResolveResourceAsync { get; init; }
     public Func<HttpContext, ValueTask<int>> ResolveAmountAsync { get; init; } = NetQuotaAmountExtractors.Fixed(1);
+    /// <summary>
+    /// Amount resolver that can read arguments minimal APIs already bound. Use this when the
+    /// endpoint binds a JSON body: endpoint filters run after binding, so
+    /// <see cref="NetQuotaAmountExtractors.FromJsonBodyInt"/> cannot see the request body.
+    /// </summary>
+    public Func<EndpointFilterInvocationContext, ValueTask<int>>? ResolveAmountFromArgumentsAsync { get; init; }
     /// <summary>
     /// Optional idempotency key for the consume call. The default is <c>null</c>: a header supplied
     /// by the caller is not a trust boundary, because a replayed consumption is reported as allowed
             }
 
             var resource = await options.ResolveResourceAsync(httpContext);
-            var amount = await options.ResolveAmountAsync(httpContext);
+            var amount = options.ResolveAmountFromArgumentsAsync is not null
+                ? await options.ResolveAmountFromArgumentsAsync(invocationContext)
+                : await options.ResolveAmountAsync(httpContext);
             var idempotencyKey = await options.ResolveIdempotencyKeyAsync(httpContext);
```

### F21 — A4 — The rate-limiter bridge hardcodes `explicitTier: "ANONYMOUS"`, so entitlements are never consulted

**Severity:** High · **Module:** `aspnetcore_integration`  
**Root cause:** The tier is a constructor-time literal; the class has no tier parameter and the doc comment promises transparent distributed windows for `RequireRateLimiting(policy)` without mentioning a fixed tier.  
**Anchor:** `src/NetQuota.AspNetCore/NetQuotaDistributedRateLimiter.cs:24-34`<br>`src/NetQuota/QuotaManager.cs:332-337`  
**Repro:** `dotnet test --project .tmp/audit/repros/aspnetcore_integration/AspNetCoreIntegrationRepro.csproj -c Release`

```diff
--- a/src/NetQuota.AspNetCore/NetQuotaDistributedRateLimiter.cs
+++ b/src/NetQuota.AspNetCore/NetQuotaDistributedRateLimiter.cs
@@ -12,9 +12,10 @@ public sealed class NetQuotaDistributedRateLimiter(
 public sealed class NetQuotaDistributedRateLimiter(
     QuotaManager manager,
     string resource,
-    string subjectKey) : RateLimiter
+    string subjectKey,
+    string? explicitTier = null) : RateLimiter
 {
@@ -28,7 +29,7 @@ public sealed class NetQuotaDistributedRateLimiter(
         var status = await manager.CheckRateLimitAsync(
             subjectKey,
             resource,
-            explicitTier: "ANONYMOUS",
+            explicitTier: explicitTier,
             cancellationToken);
```


### 4.6 Rejected candidates (not counted)

| Candidate | Module | Why rejected |
|---|---|---|
| `RC-02` | `billing_revenuecat` | Payload order decides which paid tier wins, but no documented ranking rule exists; the repro asserts one defensible tie-break, not a specified contract. |
| `SEC-02` | `billing_webhook_security` | Non-JSON 2xx body escapes as `JsonReaderException`; this is an error-contract robustness gap, not a security defect, and providers return JSON today. |
| `TESTS-02` | `tests_billing` | The test pins the event id as the refund key; this is the same money-path defect as `STRIPE-01`, so counting it separately would double-report. |

---

## 5. Master Algorithmic & Scalability Bottlenecks

### 5.1 Benchmarks

| Workload | N=100 | N=1,000 | N=10,000 | Verdict |
|---|---:|---:|---:|---|
| `QuotaManager.ConsumeWithResultAsync` (µs/op) | 2.23 | 2.29 | 1.74 | O(1); flat |
| `QuotaManager.MergeUserAsync` (µs/op, N resources) | 231 | 1,673 | 46,092 | O(resources × periods); ~46 ms at 10k |
| InMemory distinct-key consumes (total ms) | 1.4 | 34.6 | 2,376 | Copy+sort each consume; quadratic aggregate; not counted as a separate finding because the full list is part of the public result |
| `TieredQuotaStorage` sync (ms) | 3.6 | 1.3 | 21.0 | Linear |
| `TieredQuotaStorage` async (ms) | 0.2 | 0.7 | 19.2 | Linear; pump keeps up without contention |
| Redis distinct-key sets | 100 sets | 1,000 sets | 10,000 sets | **All retained without TTL** (`REDIS-02`); linear unbounded memory |
| Webhook rate limiter insert (µs/op) | 36.9¹ | 0.97 | 3.15 | O(1) dictionary; ~76–97 B/key; sweep every 100 requests |
| Webhook rate limiter hit (µs/op) | 0.81 | 5.35 | 2.36 | O(1) under one lock |

¹ first-pass JIT/noise.

### 5.2 Complexity findings

- **InMemory distinct keys:** each consume copies and sorts the full `DistinctKeys` list; the aggregate over K keys is O(K² log K). At 10k keys, one consume costs ~238 µs and the full run 2.4 s. The public `StorageConsumeOutcome` returns the full list, so a full copy is inherent; the re-sort is avoidable. Recommendation: maintain a sorted list and binary-insert (or return an immutable snapshot only on demand).
- **Redis distinct keys:** one set per `(user, resource, window)` with no `EXPIRE`; memory grows linearly and never self-heals. Recommendation: set a TTL at/after the window end, matching the usage hash.
- **PostgreSQL consumed windows:** `period_end_utc` is NULL on the consume path, so prune never deletes those rows; retention grows without bound. Recommendation: write `spec.Window.StartUtc/EndUtc` and add a retention test.
- **Rate limiter sweep:** the fixed `2 × Window` floor assumes a bucket refills in two windows; with `Burst >> Rate`, the sweep can reset a partially refilled bucket. Recommendation: track `FullRefillTicks`.
- **Webhook rate limiter:** expired buckets are retained until the 100-request sweep, not on time; with `TrustProxy` enabled and an appending proxy, the key space is attacker-controlled. Recommendation: time-based sweep or bounded cache.
- **Merge:** O(resources × periods) by construction; 10k resources complete in ~46 ms. No algorithmic fix needed, but batch/admin callers should bound `Resources`.

---

## 6. Global Phased Implementation Roadmap

### Phase 1 — Critical & High (fix immediately)

**A. Unblock releases (do first; no runtime change).**
1. `IB-01`: remove `next-version: 1.0.0` from `GitVersion.yml` (or pin GitVersion 6.7 / use `v1.0.0`).
2. `IB-02`: replace the quoted directory glob with per-file pushes or `find artifacts/packages -name '*.nupkg' -exec dotnet nuget push {} ...`.

**B. Close the quota bypass and accounting corruption.**
3. `A1`: clamp `FromJsonBodyInt` to 1,000,000 and add a `MaxConsumeAmount`/checked-arithmetic guard in `QuotaManager` and `InMemoryQuotaStorage`.
4. `A2`: extract the amount before model binding, or read from a cached body feature; add an end-to-end body-bound test.
5. `QM-01`: pass `ent?.SubscriptionStartDateUtc` directly to `ForMonthly`; never anchor anonymous monthly cycles to “today”.
6. `QM-02` / `A4`: always resolve the entitlement; validate `explicitTier`; use the override only for tier selection. Fix the rate-limiter bridge's hardcoded `"ANONYMOUS"`.
7. `ISM-01`: preserve `Used` when merging distinct keys; use distinct-key union only for the dedupe set, not as the used count.
8. `ISM-02`: encode drain as an explicit zero-limit state instead of overloading `0`; or write `Used` and a drained flag.

**C. Stop money and entitlement loss.**
9. `STRIPE-01`: key refunds by individual refund id or stable payment-intent identity; never treat cumulative `amount_refunded` as this refund's share. Update `TESTS-01`.
10. `PGE-1`: refresh/replace expired credit records before insert; make the retry path idempotent after TTL.
11. `PGC-1`: on unique-constraint conflict, delete/replace the expired row and re-read, or use an upsert with the expiry predicate.
12. `CCD-01`: reject `IdempotencyTtl <= 0` in `QuotaConfig.Validate`.
13. `CCD-02`: normalize `Tiers` to `OrdinalIgnoreCase` in the `init` accessor.
14. `FIRE-02`: distinguish “null means clear” from “not supplied”; write `FieldValue.Delete` for a null expiry.
15. `ISM-03`: preserve `MigratedTo`/`SealExpiresAtUtc` whenever the existing record is sealed, regardless of the incoming flag.
16. `RC-01`: require a non-empty eligible destination before downgrading transfer sources.

**D. Fix tenant key isolation systemically.**
17. `REDIS-01`, `FIRE-05`, `PGC-3`: length-prefix or hash each key segment; verify `UserId`/`Resource` on replay. This is one cross-cutting fix, not three independent ones.
18. `REDIS-02`: set a TTL on distinct-key sets; add a retention benchmark assertion.
19. `REDIS-05`: never overwrite a stored tier/limit with a null `EffectiveTier`.

**E. Fix async tiered durability.**
20. `TSM-01`: never write a cold read back into hot; hot is authoritative until the cold sequence catches up.
21. `TSM-03`: use `BoundedChannelFullMode.Wait` or `DropOldest` with an explicit error signal; assert `TryWrite` semantics in a test.

### Phase 2 — Medium (backlog, after Phase 1 verification)

**Retention/pruning:** `PGC-2`, `REDIS-06`, `PGM-3`, `TSM-02`.
**Rate limiting/quota edges:** `LRL-01`, `LRL-02`, `REDIS-03`, `REDIS-04`, `REDIS-07`, `A3`, `A5`.
**Billing robustness:** `STRIPE-02`, `STRIPE-03`, `STRIPE-04`, `SEC-01`, `PGM-1`, `PGM-2`, `FIRE-03`, `FIRE-04`, `FIRE-06`.
**Transport/cancellation:** `QM-03`, `A6`.
**Test blind spots:** `TCE-01`, `TCE-02`, `TESTS-01` (regression after fix), `PGA-1`, `PGA-2`, `PGA-3`, `TS-01`, `TS-02`.
**Analyzer cleanup:** 238 `xUnit1051` warnings in the test project; switch to `TestContext.Current.CancellationToken` or suppress with a documented justification.

### Verification gate after each phase

```bash
dotnet build NetQuota.slnx -c Release -warnaserror
dotnet test NetQuota.slnx -c Release --no-build
dotnet format NetQuota.slnx analyzers --verify-no-changes --no-restore
```

With services:

```bash
NETQUOTA_POSTGRES='Host=127.0.0.1;Port=5433;Database=netquota;Username=netquota;Password=netquota' \
NETQUOTA_REDIS='localhost:6379' \
FIRESTORE_EMULATOR_HOST='127.0.0.1:8085' \
dotnet test NetQuota.slnx -c Release
```

---

## 7. What NOT to Change

1. **The immutable record contracts and explicit `IQuotaStorage` interface.** `QuotaEntitlement`, `QuotaUsage`, `ConsumeResult`, `StorageConsumeSpec`, and `StorageConsumeOutcome` are the reason adapters can be swapped and audited. Do not replace them with mutable classes or tuples.
2. **Storage-level atomicity and group-scoped invalidation.** Redis Lua scripts, PostgreSQL advisory locks/transactions, and Firestore read-write transactions are the correct enforcement layer; LruQuotaCache.RemoveGroup is O(entries for one user), not a full-cache scan. Preserve both when fixing the tiered-storage desync.
3. **Provider-event idempotency on money paths.** The manager's early `GetConsumptionRecordAsync` check, the per-resource credit keys, and the refusal to grant/refund without a key are correct. Fix the TTL and key-collision defects; do not remove the requirement.
4. **Billing signature and transport hardening.** HMAC-SHA256 with constant-time compare, ±300 s tolerance, the 256 KB body cap, and `X-Forwarded-For` distrust by default are all sound. Keep them.
5. **Half-open UTC windows and month-end snap-back.** `PeriodWindow.ForDaily`/`ForMonthly` are continuous across all 141 installed time zones for 2025–2027; the Jan 31 → Feb 28 → Mar 31 clamp is intentional. Fix the anchor selection, not the window math.

---

## 8. Reproduction Matrix & Teardown

### 8.1 Reproduction matrix

| Ref | Module | Findings | Exact reproduction command |
|:--|:--|--:|:--|
| `M1` | `core_domain_contracts` | 3 | `dotnet test --project .tmp/audit/repros/core_domain_contracts/CoreDomainContractsRepro.csproj -c Release -- --filter-class CoreDomainContractsRepro.ContractDefectTests` |
| `M2` | `core_quota_manager` | 3 | `dotnet test --project .tmp/audit/repros/core_quota_manager/CoreQuotaManagerRepro.csproj -c Release -- --filter-class CoreQuotaManagerRepro.ManagerDefectTests --filter-class CoreQuotaManagerRepro.CancellationTests` |
| `M3` | `core_inmemory_storage` | 5 | `dotnet test --project .tmp/audit/repros/core_inmemory_storage/CoreInMemoryStorageRepro.csproj -c Release -- --filter-class NetQuota.Audit.CoreInMemoryStorage.InMemoryDefectTests` |
| `M4` | `core_tiered_storage_metrics` | 3 | `dotnet test --project .tmp/audit/repros/core_tiered_storage_metrics/CoreTieredStorageMetricsRepro.csproj -c Release -- --filter-class NetQuota.Audit.CoreTieredStorageMetrics.TieredDefectTests` |
| `M5` | `storage_postgres_core` | 3 | `$env:NETQUOTA_POSTGRES='Host=127.0.0.1;Port=55433;Database=netquota;Username=netquota;Password=netquota'; dotnet test --project .tmp/audit/repros/storage_postgres_core/StoragePostgresCoreRepro.csproj -c Release` |
| `M6` | `storage_postgres_entitlements_credits` | 2 | `$env:NETQUOTA_POSTGRES='Host=127.0.0.1;Port=55433;Database=netquota;Username=netquota;Password=netquota'; dotnet test --project .tmp/audit/repros/storage_postgres_entitlements_credits/StoragePostgresCreditsRepro.csproj -c Release` |
| `M7` | `storage_postgres_merge_audit` | 3 | `$env:NETQUOTA_POSTGRES='Host=127.0.0.1;Port=55433;Database=netquota;Username=netquota;Password=netquota'; dotnet test --project .tmp/audit/repros/storage_postgres_merge_audit/StoragePostgresMergeRepro.csproj -c Release` |
| `M8` | `storage_redis_atomicity` | 7 | `$env:NETQUOTA_REDIS='127.0.0.1:6391'; dotnet test --project .tmp/audit/repros/storage_redis_atomicity/StorageRedisAtomicityRepro.csproj -c Release -- --filter-class StorageRedisAtomicityRepro.RedisAtomicityTests` |
| `M9` | `storage_firestore_transactions` | 6 | `$env:FIRESTORE_EMULATOR_HOST='127.0.0.1:8091'; dotnet test --project .tmp/audit/repros/storage_firestore_transactions/StorageFirestoreTransactionsRepro.csproj -c Release -- --filter-class StorageFirestoreTransactionsRepro.FirestoreTransactionTests` |
| `M10` | `billing_stripe` | 4 | `dotnet test --project .tmp/audit/repros/billing_stripe/StripeBillingRepro.csproj -c Release` |
| `M11` | `billing_revenuecat` | 1 | `dotnet test --project .tmp/audit/repros/billing_revenuecat/RevenueCatBillingRepro.csproj -c Release` |
| `M12` | `billing_webhook_security` | 1 | `dotnet test --project .tmp/audit/repros/billing_webhook_security/WebhookSecurityRepro.csproj -c Release` |
| `M13` | `aspnetcore_integration` | 6 | `dotnet test --project .tmp/audit/repros/aspnetcore_integration/AspNetCoreIntegrationRepro.csproj -c Release` |
| `M14` | `tests_core_engine` | 2 | `dotnet test --project .tmp/audit/repros/tests_core_engine/TestsCoreEngineRepro.csproj -c Release -- --filter-class NetQuota.Audit.TestsCoreEngine.TestCoverageDefectTests` |
| `M15` | `tests_billing` | 1 | `dotnet test --project .tmp/audit/repros/tests_billing/TestsBillingRepro.csproj -c Release` |
| `M16` | `tests_storage_adapters` | 3 | `$env:NETQUOTA_POSTGRES='Host=127.0.0.1;Port=55433;Database=netquota;Username=netquota;Password=netquota'; $env:NETQUOTA_REDIS='127.0.0.1:6391'; dotnet test --project .tmp/audit/repros/tests_storage_adapters/StorageAdaptersTestsRepro.csproj -c Release` |
| `M17` | `tests_aspnetcore` | 2 | `dotnet test --project .tmp/audit/repros/tests_aspnetcore/TestsAspNetCoreRepro.csproj -c Release -- --filter-class TestsAspNetCoreRepro.CoverageGapTests` |
| `M18` | `infra_build_release` | 2 | `pwsh -NoProfile -File .tmp/audit/repros/infra_build_release/verify_gitversion.ps1; pwsh -NoProfile -File .tmp/audit/repros/infra_build_release/verify_push_glob.ps1` |

PostgreSQL commands expect a server matching the report (`127.0.0.1:55433`, `netquota/netquota`); Redis commands expect `127.0.0.1:6391`; Firestore commands expect the emulator on `127.0.0.1:8091`. The module reports contain the exact container/emulator setup and the observed output for each finding.

Commands above use PowerShell syntax. In Bash, replace `$env:NAME='value';` with `NAME='value'` before the command.

### 8.2 Teardown (only when you no longer need the evidence)

Run from the repository root. The audit scratch tree is disposable; archive it first if you want to keep the reports.

**Bash**

```bash
tar -czf netquota-audit-2026-10-21.tgz .tmp/audit 2>/dev/null || true
rm -rf .tmp/audit
docker rm -f netquota-audit-pg netquota-audit-redis 2>/dev/null || true
```

**PowerShell**

```powershell
Compress-Archive -Path .tmp\audit -DestinationPath netquota-audit-2026-10-21.zip -Force
Remove-Item -Recurse -Force .tmp\audit
docker rm -f netquota-audit-pg netquota-audit-redis 2>$null
```

Stop the Firestore emulator in its own terminal (Ctrl+C); it is not a container. If you used different container names or ports, remove those instead.

---

## Appendix A — Minimal Unified Diffs for Medium Findings (F22–F57)

Critical and High diffs are included in Section 4.5. This appendix carries the remaining 36 minimal diffs so every counted finding has an actionable patch sketch in one document.

### F22 — CCD-03 — NaN warning threshold passes validation and silently disables warnings

**Severity:** Medium · **Module:** `core_domain_contracts`  
**Root cause:** The relational pattern does not exclude NaN. The validation message says the value must be in `(0, 1]`, but the predicate is not equivalent to that interval for non-finite values.  
**Anchor:** `src/NetQuota/QuotaModels.cs:47`<br>`src/NetQuota/QuotaModels.cs:168-177`  
**Repro:** `dotnet test --project .tmp/audit/repros/core_domain_contracts/CoreDomainContractsRepro.csproj -c Release -- --filter-class CoreDomainContractsRepro.ContractDefectTests`

```diff
--- a/src/NetQuota/QuotaModels.cs
+++ b/src/NetQuota/QuotaModels.cs
@@ -169,7 +169,7 @@ public sealed record QuotaConfig
                 foreach (var threshold in thresholds)
                 {
-                    if (threshold is <= 0.0 or > 1.0)
+                    if (!double.IsFinite(threshold) || threshold is <= 0.0 or > 1.0)
                     {
                         throw new QuotaConfigValidationException($"Tier '{tierName}' warning threshold for '{res}' must be in (0, 1].");
                     }
```

### F23 — QM-03 — Caller cancellation is swallowed or rewritten as a storage failure

**Severity:** Medium · **Module:** `core_quota_manager`  
**Root cause:** The generic catches treat cancellation as an infrastructure failure. Cancellation is not a storage outage; it must propagate so the caller and ASP.NET Core request pipeline can observe the abort.  
**Anchor:** `src/NetQuota/QuotaManager.cs:67-82`<br>`src/NetQuota/QuotaManager.cs:352-368`  
**Repro:** `dotnet test --project .tmp/audit/repros/core_quota_manager/CoreQuotaManagerRepro.csproj -c Release -- --filter-class CoreQuotaManagerRepro.ManagerDefectTests --filter-class CoreQuotaManagerRepro.CancellationTests`

```diff
--- a/src/NetQuota/QuotaManager.cs
+++ b/src/NetQuota/QuotaManager.cs
@@ -1285,6 +1285,10 @@ public sealed class QuotaManager
         catch (UserSealedException)
         {
             throw;
         }
+        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
+        {
+            throw;
+        }
         catch (Exception ex)
         {
             if (_config.FallbackConfig is { Enabled: true })
```

### F24 — ISM-03 — A rewrite carrying `Sealed = true` drops the merge tombstone

**Severity:** Medium · **Module:** `core_inmemory_storage`  
**Root cause:** The preservation branch is conditioned on the incoming entitlement being *unsealed*. A rewrite that echoes the current sealed flag but does not know the tombstone fields falls through and clears them.  
**Anchor:** `src/NetQuota/InMemoryQuotaStorage.cs:81-89`  
**Repro:** `dotnet test --project .tmp/audit/repros/core_inmemory_storage/CoreInMemoryStorageRepro.csproj -c Release -- --filter-class NetQuota.Audit.CoreInMemoryStorage.InMemoryDefectTests`

```diff
--- a/src/NetQuota/InMemoryQuotaStorage.cs
+++ b/src/NetQuota/InMemoryQuotaStorage.cs
@@ -78,15 +78,15 @@ public sealed class InMemoryQuotaStorage(Func<DateTimeOffset>? clock = null)
                     entitlement = entitlement with { Timezone = existing.Timezone };
                 }
 
-                if (!entitlement.Sealed && existing.Sealed)
+                if (existing.Sealed)
                 {
                     entitlement = entitlement with
                     {
                         Sealed = existing.Sealed,
-                        MigratedTo = existing.MigratedTo,
-                        SealExpiresAtUtc = existing.SealExpiresAtUtc
+                        MigratedTo = entitlement.MigratedTo ?? existing.MigratedTo,
+                        SealExpiresAtUtc = entitlement.SealExpiresAtUtc ?? existing.SealExpiresAtUtc
                     };
                 }
             }
```

### F25 — LRL-01 — Token-bucket idle sweep resets a partially refilled bucket

**Severity:** Medium · **Module:** `core_inmemory_storage`  
**Root cause:** The sweep TTL is derived only from the window (`2 * Window` floored at 10 min). It assumes a bucket is full after at most two windows, which is true only when `Burst <= 2 * Rate`. With `Burst > 2 * Rate` (especially `Burst >> Rate`), full refill takes `Burst / Rate * Window`; the sweep discards the state before that and grants a free burst. `Burst` is not validated against `Rate` (`QuotaModels.cs:154-157` only rejects negative values).  
**Anchor:** `src/NetQuota/LocalRateLimitEngine.cs:7-13,222-232`  
**Repro:** `dotnet test --project .tmp/audit/repros/core_inmemory_storage/CoreInMemoryStorageRepro.csproj -c Release -- --filter-class NetQuota.Audit.CoreInMemoryStorage.InMemoryDefectTests`

```diff
--- a/src/NetQuota/LocalRateLimitEngine.cs
+++ b/src/NetQuota/LocalRateLimitEngine.cs
@@ -9,6 +9,7 @@ public sealed class LocalRateLimitEngine
         public long LastRefillTicks;
         public long LastSeenTicks;
         public long WindowTicks;
+        public long FullRefillTicks;
     }
@@ -115,7 +116,8 @@ public sealed class LocalRateLimitEngine
                 Tokens = capacity,
                 LastRefillTicks = nowTicks,
                 LastSeenTicks = nowTicks,
-                WindowTicks = windowTicks
+                WindowTicks = windowTicks,
+                FullRefillTicks = (long)(capacity / refillPerSecond * TimeSpan.TicksPerSecond)
             };
@@ -127,6 +129,7 @@ public sealed class LocalRateLimitEngine
             state.LastRefillTicks = nowTicks;
             state.LastSeenTicks = nowTicks;
             state.WindowTicks = windowTicks;
+            state.FullRefillTicks = (long)(capacity / refillPerSecond * TimeSpan.TicksPerSecond);
         }
@@ -225,7 +228,7 @@ public sealed class LocalRateLimitEngine
         foreach (var (k, b) in _buckets)
         {
-            var idleWindow = Math.Max(b.WindowTicks * 2, minTtlTicks);
+            var idleWindow = Math.Max(Math.Max(b.WindowTicks * 2, b.FullRefillTicks), minTtlTicks);
             if (nowTicks - b.LastSeenTicks > idleWindow)
```

### F26 — LRL-02 — One global sliding window per resource is shared across incompatible tier windows

**Severity:** Medium · **Module:** `core_inmemory_storage`  
**Root cause:** The global queue is keyed by `resource` only, while the window and the limit come from the per-call `RateLimitConfig`. `WindowTicks` is a single mutable field, so the most recent caller redefines the retention window for every other caller of that resource. The per-user sliding window does not have this problem because its key includes the user, and the user's config changes rarely; the global queue is explicitly cross-user.  
**Anchor:** `src/NetQuota/LocalRateLimitEngine.cs:15-20,47-65,73-79`  
**Repro:** `dotnet test --project .tmp/audit/repros/core_inmemory_storage/CoreInMemoryStorageRepro.csproj -c Release -- --filter-class NetQuota.Audit.CoreInMemoryStorage.InMemoryDefectTests`

```diff
--- a/src/NetQuota/LocalRateLimitEngine.cs
+++ b/src/NetQuota/LocalRateLimitEngine.cs
@@ -47,7 +47,8 @@ public sealed class LocalRateLimitEngine
             if (config.GlobalLimit > 0)
             {
-                var globalState = _globalSlidingWindows.GetOrAdd(resource, _ => new SlidingWindowState());
+                var globalKey = $"{resource}|{windowTicks}|{config.GlobalLimit}";
+                var globalState = _globalSlidingWindows.GetOrAdd(globalKey, _ => new SlidingWindowState());
                 globalState.WindowTicks = windowTicks;
@@ -73,7 +74,8 @@ public sealed class LocalRateLimitEngine
             if (result.Allowed && config.GlobalLimit > 0)
             {
-                var globalState = _globalSlidingWindows.GetOrAdd(resource, _ => new SlidingWindowState());
+                var globalKey = $"{resource}|{windowTicks}|{config.GlobalLimit}";
+                var globalState = _globalSlidingWindows.GetOrAdd(globalKey, _ => new SlidingWindowState());
                 globalState.WindowTicks = windowTicks;
```

### F27 — TSM-02 — Refund is applied to cold before the queued consume; the refund is lost and the stores diverge

**Severity:** Medium · **Module:** `core_tiered_storage_metrics`  
**Root cause:** Consumes are serialized through the single pump, but refunds, drains, tier changes, usage seeds, credit top-ups and merges call cold directly. A refund that arrives while an earlier consume is still queued is therefore applied to cold *before* that consume. Cold's `Math.Max(0, used - amount)` clamps the refund to zero, and the later consume lands on top, so the cold copy permanently overstates usage by the refunded amount. This is not a lost hot update: it is an out-of-order durable history.  
**Anchor:** `src/NetQuota/TieredStorageAndMetrics.cs:834-845,693-700`  
**Repro:** `dotnet test --project .tmp/audit/repros/core_tiered_storage_metrics/CoreTieredStorageMetricsRepro.csproj -c Release -- --filter-class NetQuota.Audit.CoreTieredStorageMetrics.TieredDefectTests`

```diff
--- a/src/NetQuota/TieredStorageAndMetrics.cs
+++ b/src/NetQuota/TieredStorageAndMetrics.cs
@@ -834,6 +834,9 @@ public sealed class TieredQuotaStorage :
     public async Task<StorageRefundOutcome> RefundAsync(
         RefundRequest request, PeriodWindow window, int defaultLimit,
         TimeSpan idempotencyTtl, DateTimeOffset nowUtc,
         CancellationToken cancellationToken = default)
     {
+        // Cold must not see this refund before the consumes that created the usage.
+        await WaitForColdSyncIdleAsync(cancellationToken);
         var outcome = await _config.Cold.RefundAsync(request, window, defaultLimit, idempotencyTtl, nowUtc, cancellationToken);
         await _config.Hot.RefundAsync(request, window, defaultLimit, idempotencyTtl, nowUtc, cancellationToken);
         return outcome;
     }
```

### F28 — PGC-2 — `TryConsumeAsync` never stores the window bounds, so `PruneExpiredAsync` can never delete consumed rows

**Severity:** Medium · **Module:** `storage_postgres_core`  
**Root cause:** ConsumeCoreAsync reads start/end from the stored row (null on first write) and upserts those nulls instead of spec.Window.StartUtc/EndUtc; PruneExpiredAsync requires a non-null period_end_utc.  
**Anchor:** `src/NetQuota.Postgres/PostgresQuotaStorage.cs:186-189`<br>`src/NetQuota.Postgres/PostgresQuotaStorage.cs:542-571`  
**Repro:** `$env:NETQUOTA_POSTGRES='Host=127.0.0.1;Port=55433;Database=netquota;Username=netquota;Password=netquota'; dotnet test --project .tmp/audit/repros/storage_postgres_core/StoragePostgresCoreRepro.csproj -c Release`

```diff
@@ -151,7 +151,7 @@ private async Task<StorageConsumeOutcome> ConsumeCoreAsync(
-        var (used, storedLimit, storedTier, start, end, distinct) = await ReadUsageAsync(
+        var (used, storedLimit, storedTier, storedStart, storedEnd, distinct) = await ReadUsageAsync(
             connection, tx, spec.UserId, spec.Resource, spec.Window, cancellationToken);
@@ -186,7 +186,8 @@ private async Task<StorageConsumeOutcome> ConsumeCoreAsync(
         await UpsertUsageAsync(
             connection, tx, spec.UserId, spec.Resource, spec.Window,
             newUsed, effectiveLimit, spec.EffectiveTier ?? storedTier,
-            start, end, spec.NowUtc, cancellationToken);
+            storedStart ?? spec.Window.StartUtc, storedEnd ?? spec.Window.EndUtc,
+            spec.NowUtc, cancellationToken);
```

### F29 — PGC-3 — `ScopedKey` collides across users, and the replay path never checks record ownership

**Severity:** Medium · **Module:** `storage_postgres_core`  
**Root cause:** ScopedKey concatenates the user id and idempotency key with ':'; a ':' inside either can alias another pair, and the replay path never compares the stored UserId/Resource.  
**Anchor:** `src/NetQuota.Postgres/PostgresQuotaStorage.Records.cs:1-40`<br>`src/NetQuota/QuotaManager.cs:62-65`  
**Repro:** `$env:NETQUOTA_POSTGRES='Host=127.0.0.1;Port=55433;Database=netquota;Username=netquota;Password=netquota'; dotnet test --project .tmp/audit/repros/storage_postgres_core/StoragePostgresCoreRepro.csproj -c Release`

```diff
-    private static string ScopedKey(string userId, string idempotencyKey) =>
-        idempotencyKey.StartsWith($"{userId}{RecordKeyPrefixSeparator}", StringComparison.Ordinal)
-            ? idempotencyKey
-            : $"{userId}{RecordKeyPrefixSeparator}{idempotencyKey}";
+    // A user id may contain ':', so the prefix must be unambiguous: (user="a:b", key="c") and
+    // (user="a", key="b:c") must not share a record.
+    private static string ScopedKey(string userId, string idempotencyKey) =>
+        $"{userId.Length}:{userId}{RecordKeyPrefixSeparator}{idempotencyKey}";
```

### F30 — PGE-2 — `SetUsageAsync(distinctKeys: null)` deletes the user's dedupe keys (diverges from `InMemoryQuotaStorage`)

**Severity:** Medium · **Module:** `storage_postgres_entitlements_credits`  
**Root cause:** Postgres SetUsageAsync treats distinctKeys: null as 'clear the set'; InMemory preserves the existing set when used > 0.  
**Anchor:** `src/NetQuota.Postgres/PostgresQuotaStorage.cs:280-300`  
**Repro:** `$env:NETQUOTA_POSTGRES='Host=127.0.0.1;Port=55433;Database=netquota;Username=netquota;Password=netquota'; dotnet test --project .tmp/audit/repros/storage_postgres_entitlements_credits/StoragePostgresCreditsRepro.csproj -c Release`

```diff
@@ -273,8 +273,11 @@ public async Task SetUsageAsync(
             connection, tx, userId, resource, window, Math.Max(0, used), limit, tier,
             start ?? window.StartUtc, end ?? window.EndUtc, now, cancellationToken);
 
-        await ReplaceDistinctAsync(
-            connection, tx, userId, resource, window, distinctKeys ?? [], cancellationToken);
+        if (distinctKeys is not null || used <= 0)
+        {
+            await ReplaceDistinctAsync(
+                connection, tx, userId, resource, window, distinctKeys ?? [], cancellationToken);
+        }
 
         await tx.CommitAsync(cancellationToken);
```

### F31 — PGM-1 — A merge replay does not verify the user pair, so a reused key silently skips the merge and reports success

**Severity:** Medium · **Module:** `storage_postgres_merge_audit`  
**Root cause:** The merge idempotency lookup keys only on the caller-provided string and never compares source_user_id/target_user_id; a reused key returns AlreadyMerged for a different pair.  
**Anchor:** `src/NetQuota.Postgres/PostgresQuotaStorage.Merge.cs:30-40,293-307`  
**Repro:** `$env:NETQUOTA_POSTGRES='Host=127.0.0.1;Port=55433;Database=netquota;Username=netquota;Password=netquota'; dotnet test --project .tmp/audit/repros/storage_postgres_merge_audit/StoragePostgresMergeRepro.csproj -c Release`

```diff
@@ -27,15 +27,23 @@ public async Task<MergeUserResult> MergeUserAsync(
         await AcquireAdvisoryLockAsync(
             connection, tx, $"merge-record:{request.IdempotencyKey}", MergeRecordLockSalt, cancellationToken);
 
         var existing = await ReadMergeRecordAsync(connection, tx, request.IdempotencyKey, cancellationToken);
         if (existing is not null)
         {
+            if (!string.Equals(existing.Value.Source, request.SourceUserId, StringComparison.Ordinal) ||
+                !string.Equals(existing.Value.Target, request.TargetUserId, StringComparison.Ordinal))
+            {
+                throw new QuotaException(
+                    $"Merge idempotency key '{request.IdempotencyKey}' was already used for a different user pair.");
+            }
+
             await tx.CommitAsync(cancellationToken);
             return new MergeUserResult(
                 request.SourceUserId,
                 request.TargetUserId,
                 AlreadyMerged: true,
-                existing.TransferredUsage,
-                existing.TransferredForever);
+                existing.Value.Payload.TransferredUsage,
+                existing.Value.Payload.TransferredForever);
         }
@@ -293,14 +301,20 @@ public async Task<MergeUserResult> MergeUserAsync(
-    private static async Task<MergeRecordPayload?> ReadMergeRecordAsync(
+    private static async Task<(string Source, string Target, MergeRecordPayload Payload)?> ReadMergeRecordAsync(
         NpgsqlConnection connection,
         NpgsqlTransaction tx,
         string idempotencyKey,
         CancellationToken cancellationToken)
     {
         await using var command = Command(connection, tx,
-            "SELECT result FROM netquota_merge_records WHERE idempotency_key = @key",
+            "SELECT source_user_id, target_user_id, result FROM netquota_merge_records WHERE idempotency_key = @key",
             ("key", idempotencyKey));
 
-        var value = await command.ExecuteScalarAsync(cancellationToken);
-        return value is string json
-            ? JsonSerializer.Deserialize<MergeRecordPayload>(json)
-            : null;
+        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
+        if (!await reader.ReadAsync(cancellationToken))
+        {
+            return null;
+        }
+
+        return (reader.GetString(0), reader.GetString(1),
+            JsonSerializer.Deserialize<MergeRecordPayload>(reader.GetString(2))!);
     }
```

### F32 — PGM-2 — `DroppedPromotion` is reported for promotions that were not dropped

**Severity:** Medium · **Module:** `storage_postgres_merge_audit`  
**Root cause:** ReadPromotionAsync does not check promotion_expires_at_utc; expired promotions are returned as DroppedPromotion, and with SealSource:false nothing was cleared.  
**Anchor:** `src/NetQuota.Postgres/PostgresQuotaStorage.Merge.cs:265-291`  
**Repro:** `$env:NETQUOTA_POSTGRES='Host=127.0.0.1;Port=55433;Database=netquota;Username=netquota;Password=netquota'; dotnet test --project .tmp/audit/repros/storage_postgres_merge_audit/StoragePostgresMergeRepro.csproj -c Release`

```diff
@@ -73,7 +73,9 @@ public async Task<MergeUserResult> MergeUserAsync(
-        var droppedPromotion = await ReadPromotionAsync(connection, tx, request.SourceUserId, cancellationToken);
+        var droppedPromotion = request.SealSource
+            ? await ReadPromotionAsync(connection, tx, request.SourceUserId, request.NowUtc, cancellationToken)
+            : null;
@@ -265,16 +267,18 @@ public async Task<MergeUserResult> MergeUserAsync(
     private static async Task<TierPromotion?> ReadPromotionAsync(
         NpgsqlConnection connection,
         NpgsqlTransaction tx,
         string userId,
+        DateTimeOffset nowUtc,
         CancellationToken cancellationToken)
     {
         await using var command = Command(connection, tx, """
             SELECT promotion_tier, promotion_granted_at_utc, promotion_expires_at_utc,
                    promotion_source, promotion_reason, promotion_idempotency_key
-            FROM netquota_entitlements WHERE user_id = @user
+            FROM netquota_entitlements
+            WHERE user_id = @user
+              AND (promotion_expires_at_utc IS NULL OR promotion_expires_at_utc > @now)
             """,
-            ("user", userId));
+            ("user", userId),
+            ("now", nowUtc));
```

### F33 — PGM-3 — Audit pagination has no total order, so `LIMIT` can return the oldest entries of a timestamp tie

**Severity:** Medium · **Module:** `storage_postgres_merge_audit`  
**Root cause:** Audit query orders only by timestamp_utc DESC; ties have no deterministic secondary key, so LIMIT can return older rows.  
**Anchor:** `src/NetQuota.Postgres/PostgresQuotaStorage.Audit.cs:30-60`  
**Repro:** `$env:NETQUOTA_POSTGRES='Host=127.0.0.1;Port=55433;Database=netquota;Username=netquota;Password=netquota'; dotnet test --project .tmp/audit/repros/storage_postgres_merge_audit/StoragePostgresMergeRepro.csproj -c Release`

```diff
@@ -54,7 +54,7 @@ public async Task<IReadOnlyList<AuditLogEntry>> GetAuditLogsAsync(
             SELECT user_id, resource, action, amount, actor, reason, timestamp_utc
             FROM netquota_audit_logs
             {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty)}
-            ORDER BY timestamp_utc DESC
+            ORDER BY timestamp_utc DESC, id DESC
             LIMIT @limit
             """;
```

### F34 — REDIS-03 — Token-bucket TTL is shorter than the refill time; an idle bucket resets to a full burst

**Severity:** Medium · **Module:** `storage_redis_atomicity`  
**Root cause:** Token-bucket TTL is derived from the window, but a partially empty bucket can need Burst/Rate windows to refill; the sweep deletes it and the next call starts full.  
**Anchor:** `src/NetQuota.Redis/RedisQuotaStorage.cs:536-538`<br>`src/NetQuota.Redis/RedisQuotaStorage.Scripts.cs:351-383`  
**Repro:** `$env:NETQUOTA_REDIS='127.0.0.1:6391'; dotnet test --project .tmp/audit/repros/storage_redis_atomicity/StorageRedisAtomicityRepro.csproj -c Release -- --filter-class StorageRedisAtomicityRepro.RedisAtomicityTests`

```diff
--- a/src/NetQuota.Redis/RedisQuotaStorage.cs
+++ b/src/NetQuota.Redis/RedisQuotaStorage.cs
@@
-        var ttlSeconds = (long)Math.Ceiling((config.Window + TimeSpan.FromMinutes(5)).TotalSeconds);
+        var burst = config.Burst > 0 ? config.Burst : config.Rate;
+        var refillWindow = TimeSpan.FromMilliseconds(
+            Math.Ceiling((double)windowMs * Math.Max(1, burst) / Math.Max(1, config.Rate)));
+        var retention = config.Algorithm == RateLimitAlgorithm.TokenBucket && refillWindow > config.Window
+            ? refillWindow
+            : config.Window;
+        var ttlSeconds = (long)Math.Ceiling((retention + TimeSpan.FromMinutes(5)).TotalSeconds);
```

### F35 — REDIS-04 — Token-bucket rate limiting ignores `RateLimitConfig.GlobalLimit`

**Severity:** Medium · **Module:** `storage_redis_atomicity`  
**Root cause:** The Redis token-bucket Lua ignores RateLimitConfig.GlobalLimit; only the local engine and the sliding-window script enforce it.  
**Anchor:** `src/NetQuota.Redis/RedisQuotaStorage.cs:541-553`<br>`src/NetQuota.Redis/RedisQuotaStorage.Scripts.cs:343-384`  
**Repro:** `$env:NETQUOTA_REDIS='127.0.0.1:6391'; dotnet test --project .tmp/audit/repros/storage_redis_atomicity/StorageRedisAtomicityRepro.csproj -c Release -- --filter-class StorageRedisAtomicityRepro.RedisAtomicityTests`

```diff
--- a/src/NetQuota.Redis/RedisQuotaStorage.cs
+++ b/src/NetQuota.Redis/RedisQuotaStorage.cs
@@
         if (config.Algorithm == RateLimitAlgorithm.TokenBucket)
         {
+            var globalKey = config.GlobalLimit > 0 ? RateLimitGlobalKey(resource) : string.Empty;
             result = AsArray(await _db.ScriptEvaluateAsync(
                 TokenBucketScript,
-                [RateLimitKey(resource, userId)],
+                [RateLimitKey(resource, userId), globalKey],
                 [
                     nowMs,
                     config.Rate,
                     windowMs,
                     config.Burst > 0 ? config.Burst : config.Rate,
-                    ttlSeconds
+                    ttlSeconds,
+                    config.GlobalLimit,
+                    $"{nowMs}:{Guid.NewGuid():N}"
                 ]));
         }
--- a/src/NetQuota.Redis/RedisQuotaStorage.Scripts.cs
+++ b/src/NetQuota.Redis/RedisQuotaStorage.Scripts.cs
@@
     private static readonly string TokenBucketScript = """
         local key = KEYS[1]
+        local globalKey = KEYS[2]
         local nowMs = tonumber(ARGV[1])
         local rate = tonumber(ARGV[2])
         local windowMs = tonumber(ARGV[3])
         local burst = tonumber(ARGV[4])
         local ttl = tonumber(ARGV[5])
+        local globalLimit = tonumber(ARGV[6])
+        local member = ARGV[7]
 
         if rate <= 0 or windowMs <= 0 or burst <= 0 then
             return { 0, 0, nowMs + windowMs }
         end
 
+        if globalKey ~= '' and globalLimit > 0 then
+            redis.call('ZREMRANGEBYSCORE', globalKey, '-inf', nowMs - windowMs)
+            if redis.call('ZCARD', globalKey) >= globalLimit then
+                local oldest = redis.call('ZRANGE', globalKey, 0, 0, 'WITHSCORES')
+                local reset = nowMs + windowMs
+                if oldest and #oldest >= 2 then reset = tonumber(oldest[2]) + windowMs end
+                if ttl > 0 then redis.call('EXPIRE', globalKey, ttl) end
+                return { 0, 0, reset }
+            end
+        end
+
         local state = redis.call('HMGET', key, 'tokens', 'lastRefillMs')
@@
         redis.call('HSET', key, 'tokens', tostring(tokens), 'lastRefillMs', lastRefill)
         if ttl > 0 then redis.call('EXPIRE', key, ttl) end
+        if allowed == 1 and globalKey ~= '' and globalLimit > 0 then
+            redis.call('ZADD', globalKey, nowMs, member)
+            if ttl > 0 then redis.call('EXPIRE', globalKey, ttl) end
+        end
         return { allowed, remaining, reset }
```

### F36 — REDIS-05 — A consume without `EffectiveTier` destroys the stored tier and the prorated cap

**Severity:** Medium · **Module:** `storage_redis_atomicity`  
**Root cause:** ConsumeScript writes effectiveTier unconditionally; a null EffectiveTier overwrites the stored tier/limit, so the prorated cap is lost.  
**Anchor:** `src/NetQuota.Redis/RedisQuotaStorage.Scripts.cs:42-51`<br>`src/NetQuota.Redis/RedisQuotaStorage.Scripts.cs:71-75`  
**Repro:** `$env:NETQUOTA_REDIS='127.0.0.1:6391'; dotnet test --project .tmp/audit/repros/storage_redis_atomicity/StorageRedisAtomicityRepro.csproj -c Release -- --filter-class StorageRedisAtomicityRepro.RedisAtomicityTests`

```diff
--- a/src/NetQuota.Redis/RedisQuotaStorage.Scripts.cs
+++ b/src/NetQuota.Redis/RedisQuotaStorage.Scripts.cs
@@
         local used = tonumber(redis.call('HGET', usageKey, 'used') or '0')
         local storedLimit = redis.call('HGET', usageKey, 'limit')
         local storedTier = redis.call('HGET', usageKey, 'tier')
+        local effectiveTier = tier ~= '' and tier or (storedTier or '')
 
         local limit = configuredLimit
@@
-        elseif storedLimit and storedTier and tier ~= '' and string.lower(storedTier) == string.lower(tier) and tonumber(storedLimit) ~= 0 then
+        elseif storedLimit and storedTier and effectiveTier ~= '' and string.lower(storedTier) == string.lower(effectiveTier) and tonumber(storedLimit) ~= 0 then
             -- Preserve prorated limits written by ApplyTierChange for the current tier.
             limit = tonumber(storedLimit)
@@
         redis.call('HSET', usageKey,
-            'used', newUsed, 'limit', limit, 'tier', tier,
+            'used', newUsed, 'limit', limit, 'tier', effectiveTier,
             'periodType', periodType, 'periodKey', periodKey,
```

### F37 — REDIS-06 — `DrainRemainingAsync` creates a permanent recurring usage hash

**Severity:** Medium · **Module:** `storage_redis_atomicity`  
**Root cause:** DrainScript writes a recurring usage hash without EXPIRE and without period fields; a later consume is denied before reaching the EXPIRE path.  
**Anchor:** `src/NetQuota.Redis/RedisQuotaStorage.cs:503-506`<br>`src/NetQuota.Redis/RedisQuotaStorage.Scripts.cs:188-200`  
**Repro:** `$env:NETQUOTA_REDIS='127.0.0.1:6391'; dotnet test --project .tmp/audit/repros/storage_redis_atomicity/StorageRedisAtomicityRepro.csproj -c Release -- --filter-class StorageRedisAtomicityRepro.RedisAtomicityTests`

```diff
--- a/src/NetQuota.Redis/RedisQuotaStorage.cs
+++ b/src/NetQuota.Redis/RedisQuotaStorage.cs
@@
         await _db.ScriptEvaluateAsync(
             DrainScript,
             [UsageKey(userId, resource, window)],
-            [FormatDate(DateTimeOffset.UtcNow)]);
+            [FormatDate(DateTimeOffset.UtcNow), UsageTtlSeconds(window, DateTimeOffset.UtcNow)]);
--- a/src/NetQuota.Redis/RedisQuotaStorage.Scripts.cs
+++ b/src/NetQuota.Redis/RedisQuotaStorage.Scripts.cs
@@
         local usageKey = KEYS[1]
         local nowIso = ARGV[1]
+        local usageTtl = tonumber(ARGV[2])
@@
         redis.call('HSET', usageKey, 'used', used, 'limit', used, 'updatedAtUtc', nowIso)
+        if usageTtl and usageTtl > 0 then redis.call('EXPIRE', usageKey, usageTtl) end
         return 1
```

### F38 — REDIS-07 — `IdempotencyTtl <= 0` means “remember forever” for consume/refund and “remember nothing” for credit grants

**Severity:** Medium · **Module:** `storage_redis_atomicity`  
**Root cause:** Redis interprets IdempotencyTtl <= 0 as no expiry for consume/refund and as no record for credit grants; other adapters expire immediately.  
**Anchor:** `src/NetQuota.Redis/RedisQuotaStorage.cs:259`<br>`src/NetQuota.Redis/RedisQuotaStorage.cs:376`  
**Repro:** `$env:NETQUOTA_REDIS='127.0.0.1:6391'; dotnet test --project .tmp/audit/repros/storage_redis_atomicity/StorageRedisAtomicityRepro.csproj -c Release -- --filter-class StorageRedisAtomicityRepro.RedisAtomicityTests`

```diff
--- a/src/NetQuota.Redis/RedisQuotaStorage.cs
+++ b/src/NetQuota.Redis/RedisQuotaStorage.cs
@@
-        var consumptionKey = string.IsNullOrWhiteSpace(spec.IdempotencyKey)
+        var consumptionKey = string.IsNullOrWhiteSpace(spec.IdempotencyKey) || spec.IdempotencyTtl <= TimeSpan.Zero
             ? string.Empty
             : ConsumptionKey(spec.UserId, spec.IdempotencyKey);
@@
-        var refundKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
+        var refundKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) || idempotencyTtl <= TimeSpan.Zero
             ? string.Empty
             : RefundKey(request.UserId, request.IdempotencyKey);
```

### F39 — FIRE-03 — Merge idempotency replay ignores the record's `expiresAt`

**Severity:** Medium · **Module:** `storage_firestore_transactions`  
**Root cause:** Merge idempotency lookup only checks that the document exists; it ignores the record's expiresAt and replays expired merges.  
**Anchor:** `src/NetQuota.Firestore/FirestoreQuotaStorage.cs:782-793`<br>`src/NetQuota.Firestore/FirestoreQuotaStorage.cs:912`  
**Repro:** `$env:FIRESTORE_EMULATOR_HOST='127.0.0.1:8091'; dotnet test --project .tmp/audit/repros/storage_firestore_transactions/StorageFirestoreTransactionsRepro.csproj -c Release -- --filter-class StorageFirestoreTransactionsRepro.FirestoreTransactionTests`

```diff
--- a/src/NetQuota.Firestore/FirestoreQuotaStorage.cs
+++ b/src/NetQuota.Firestore/FirestoreQuotaStorage.cs
@@
             if (idemRef is not null)
             {
                 var idemSnap = await ReadIdempotencyAsync(tx, idemRef, legacyIdemRef!, cancellationToken);
-                if (idemSnap is not null)
+                if (idemSnap is not null &&
+                    ReadTimestamp(idemSnap.ToDictionary(), "expiresAt", request.NowUtc) > request.NowUtc)
                 {
                     return new MergeUserResult(
```

### F40 — FIRE-04 — Merge replay returns empty transfer dictionaries instead of the original counts

**Severity:** Medium · **Module:** `storage_firestore_transactions`  
**Root cause:** Merge replay returns new empty dictionaries; transfer counts are never persisted in the idempotency document.  
**Anchor:** `src/NetQuota.Firestore/FirestoreQuotaStorage.cs:787-792`<br>`src/NetQuota.Firestore/FirestoreQuotaStorage.cs:905-914`  
**Repro:** `$env:FIRESTORE_EMULATOR_HOST='127.0.0.1:8091'; dotnet test --project .tmp/audit/repros/storage_firestore_transactions/StorageFirestoreTransactionsRepro.csproj -c Release -- --filter-class StorageFirestoreTransactionsRepro.FirestoreTransactionTests`

```diff
--- a/src/NetQuota.Firestore/FirestoreQuotaStorage.cs
+++ b/src/NetQuota.Firestore/FirestoreQuotaStorage.cs
@@
                 if (idemSnap is not null &&
                     ReadTimestamp(idemSnap.ToDictionary(), "expiresAt", request.NowUtc) > request.NowUtc)
                 {
+                    var idemData = idemSnap.ToDictionary();
                     return new MergeUserResult(
                         request.SourceUserId,
                         request.TargetUserId,
                         AlreadyMerged: true,
-                        new Dictionary<string, int>(),
-                        new Dictionary<string, int>());
+                        ReadIntMap(idemData, "transferredUsage"),
+                        ReadIntMap(idemData, "transferredForever"));
                 }
@@
                 tx.Set(idemRef, new Dictionary<string, object>
                 {
                     ["operation"] = "merge_user",
                     ["sourceUserId"] = request.SourceUserId,
                     ["targetUserId"] = request.TargetUserId,
+                    ["transferredUsage"] = transferredUsage.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
+                    ["transferredForever"] = transferredForever.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
                     ["expiresAt"] = Timestamp.FromDateTimeOffset(request.NowUtc.AddDays(365)),
                     ["updatedAtUtc"] = Timestamp.FromDateTimeOffset(request.NowUtc)
                 });
@@
     private static IReadOnlyList<long> ReadLongList(IDictionary<string, object> data, string key)
     {
@@
     }
+
+    private static IReadOnlyDictionary<string, int> ReadIntMap(
+        IDictionary<string, object> data,
+        string key)
+    {
+        if (!data.TryGetValue(key, out var raw) || raw is not IDictionary<string, object> map)
+        {
+            return new Dictionary<string, int>(StringComparer.Ordinal);
+        }
+
+        return map.ToDictionary(
+            kv => kv.Key,
+            kv => Convert.ToInt32(kv.Value, CultureInfo.InvariantCulture),
+            StringComparer.Ordinal);
+    }
```

### F41 — FIRE-06 — Sealing a user without an entitlement fabricates a new `subscriptionStartDateUtc` on every read

**Severity:** Medium · **Module:** `storage_firestore_transactions`  
**Root cause:** Sealing a user without an entitlement writes a synthetic entitlement with SubscriptionStartDateUtc derived from now on every read, so the anchor moves.  
**Anchor:** `src/NetQuota.Firestore/FirestoreQuotaStorage.cs:889-903`<br>`src/NetQuota.Firestore/FirestoreQuotaStorage.cs:1438-1445`  
**Repro:** `$env:FIRESTORE_EMULATOR_HOST='127.0.0.1:8091'; dotnet test --project .tmp/audit/repros/storage_firestore_transactions/StorageFirestoreTransactionsRepro.csproj -c Release -- --filter-class StorageFirestoreTransactionsRepro.FirestoreTransactionTests`

```diff
--- a/src/NetQuota.Firestore/FirestoreQuotaStorage.cs
+++ b/src/NetQuota.Firestore/FirestoreQuotaStorage.cs
@@
             if (request.SealSource)
             {
                 var sealUntil = request.SealExpiresAtUtc ?? request.NowUtc.AddDays(30);
+                var sealPayload = new Dictionary<string, object>
+                {
+                    ["userId"] = request.SourceUserId,
+                    ["sealed"] = true,
+                    ["migratedTo"] = request.TargetUserId,
+                    ["sealedUntilUtc"] = Timestamp.FromDateTimeOffset(sealUntil),
+                    ["updatedAtUtc"] = Timestamp.FromDateTimeOffset(request.NowUtc)
+                };
+                if (!srcEntSnap.Exists)
+                {
+                    sealPayload["tier"] = "free";
+                    sealPayload["subscriptionStartDateUtc"] = Timestamp.FromDateTimeOffset(request.NowUtc);
+                }
                 tx.Set(
                     srcEntRef,
-                    new Dictionary<string, object>
-                    {
-                        ["userId"] = request.SourceUserId,
-                        ["sealed"] = true,
-                        ["migratedTo"] = request.TargetUserId,
-                        ["sealedUntilUtc"] = Timestamp.FromDateTimeOffset(sealUntil),
-                        ["updatedAtUtc"] = Timestamp.FromDateTimeOffset(request.NowUtc)
-                    },
+                    sealPayload,
                     SetOptions.MergeAll);
             }
```

### F42 — STRIPE-02 — The invoice tier is taken from `lines.data[0]`, whatever that line is

**Severity:** Medium · **Module:** `billing_stripe`  
**Root cause:** Invoice lines are a list of independent charges/credits, not "the subscription's price". Picking index 0 assumes an ordering Stripe does not define and that proration invoices violate. The period/currency fields inherit the same assumption.  
**Anchor:** `src/NetQuota.Billing/Stripe/StripeWebhookParser.cs:126-135`<br>`src/NetQuota.Billing/Stripe/StripeWebhookParser.cs:235-249`  
**Repro:** `dotnet test --project .tmp/audit/repros/billing_stripe/StripeBillingRepro.csproj -c Release`

```diff
--- a/src/NetQuota.Billing/Stripe/StripeWebhookParser.cs
+++ b/src/NetQuota.Billing/Stripe/StripeWebhookParser.cs
@@ -126,10 +126,13 @@
-        var priceId = NestedText(obj, "lines", "data", "0", "price", "id");
+        var line = InvoiceLine(obj, options);
+        var priceId = line is { } l ? NestedText(l, "price", "id") : null;
         var (tier, known) = ResolveTier(priceId, obj, options);
         if (!known)
         {
             return BillingWebhookResult.Ignored($"unknown price '{priceId}'");
         }
 
-        var start = NestedUnixSeconds(obj, "lines", "data", "0", "period", "start");
-        var end = NestedUnixSeconds(obj, "lines", "data", "0", "period", "end");
-        var currency = NestedText(obj, "lines", "data", "0", "price", "currency") ?? Text(obj, "currency");
+        var start = line is { } s ? NestedUnixSeconds(s, "period", "start") : null;
+        var end = line is { } e ? NestedUnixSeconds(e, "period", "end") : null;
+        var currency = (line is { } c ? NestedText(c, "price", "currency") : null) ?? Text(obj, "currency");
@@ -235,6 +238,36 @@
     private static (string Tier, bool Known) ResolveTier(string? priceId, JsonElement obj, StripeOptions options)
     {
         if (!string.IsNullOrWhiteSpace(priceId) && options.PriceToTier.TryGetValue(priceId, out var mapped))
         {
             return (mapped, true);
         }
 
         var metadataTier = NestedText(obj, "metadata", "tier");
         if (!string.IsNullOrWhiteSpace(metadataTier))
         {
             return (metadataTier, true);
         }
 
         return (options.DefaultTier, false);
     }
+
+    /// <summary>
+    /// The invoice line that describes the subscription's current price: the first mapped,
+    /// non-proration line, falling back to the first line. Proration credits for the previous price
+    /// are billed first, so `lines.data[0]` alone downgrades a mid-cycle upgrade.
+    /// </summary>
+    private static JsonElement? InvoiceLine(JsonElement obj, StripeOptions options)
+    {
+        if (!obj.TryGetProperty("lines", out var lines) ||
+            !lines.TryGetProperty("data", out var data) ||
+            data.ValueKind != JsonValueKind.Array ||
+            data.GetArrayLength() == 0)
+        {
+            return null;
+        }
+
+        JsonElement? fallback = null;
+        foreach (var line in data.EnumerateArray())
+        {
+            var priceId = NestedText(line, "price", "id");
+            if (priceId is null || !options.PriceToTier.ContainsKey(priceId) || Bool(line, "proration") == true)
+            {
+                fallback ??= line;
+                continue;
+            }
+
+            return line;
+        }
+
+        return fallback;
+    }
```

### F43 — STRIPE-03 — The parsed `customer_id` is never used: delete reconciliation and the portal need a pre-existing link

**Severity:** Medium · **Module:** `billing_stripe`  
**Root cause:** The provider treats the customer link as write-only at checkout time and ignores the customer id that every subscription/invoice webhook already contains, so the link is missing exactly when reconciliation matters.  
**Anchor:** `src/NetQuota.Billing/Stripe/StripeBillingProvider.cs:102-120,343-344,381`<br>`src/NetQuota.Billing/Stripe/StripeWebhookParser.cs:102`  
**Repro:** `dotnet test --project .tmp/audit/repros/billing_stripe/StripeBillingRepro.csproj -c Release`

```diff
--- a/src/NetQuota.Billing/Stripe/StripeBillingProvider.cs
+++ b/src/NetQuota.Billing/Stripe/StripeBillingProvider.cs
@@ -100,6 +100,18 @@
 
         // A cancelled subscription must not drop the user if another one is still active
         // (goquota 48c14ee): reconcile against Stripe before downgrading, and use the event's own
         // timestamp so the reconciliation does not swallow the audit callback.
+        // The event itself names the customer: learning it here keeps delete reconciliation and the
+        // billing portal working for customers created outside CheckoutUrlAsync (Payment Links,
+        // imports) or after the customer store was rebuilt.
+        if (subscription.Metadata.TryGetValue("customer_id", out var eventCustomerId) &&
+            !string.IsNullOrWhiteSpace(eventCustomerId) &&
+            string.IsNullOrWhiteSpace(await _customers.GetCustomerIdAsync(Name, subscription.UserId, cancellationToken)))
+        {
+            await _customers.SaveCustomerIdAsync(Name, subscription.UserId, eventCustomerId, cancellationToken);
+        }
+
         if (subscription.EventType == "customer.subscription.deleted")
         {
-            var customerId = await _customers.GetCustomerIdAsync(Name, subscription.UserId, cancellationToken);
+            var customerId = await _customers.GetCustomerIdAsync(Name, subscription.UserId, cancellationToken)
+                             ?? subscription.Metadata.GetValueOrDefault("customer_id");
             if (string.IsNullOrWhiteSpace(customerId))
             {
```

### F44 — STRIPE-04 — A refund that rounds to zero credits is sent to the store as `amount = 0` and fails the webhook

**Severity:** Medium · **Module:** `billing_stripe`  
**Root cause:** The parser's proration legitimately produces zero, but the money path calls the store unconditionally and the store's contract requires a positive amount.  
**Anchor:** `src/NetQuota.Billing/Stripe/StripeBillingProvider.cs:84-93`<br>`src/NetQuota/QuotaManager.cs:891-894`  
**Repro:** `dotnet test --project .tmp/audit/repros/billing_stripe/StripeBillingRepro.csproj -c Release`

```diff
--- a/src/NetQuota.Billing/Stripe/StripeBillingProvider.cs
+++ b/src/NetQuota.Billing/Stripe/StripeBillingProvider.cs
@@ -84,14 +84,20 @@
         if (result.Kind == BillingWebhookKind.Refund && result.Event is { } refund)
         {
             var credits = StripeWebhookParser.ParseInt(refund.Metadata.GetValueOrDefault("credits")) ?? 0;
-            await _store.RefundCreditsAsync(
-                refund.UserId,
-                refund.Metadata["resource"],
-                credits,
-                refund.EventType,
-                idempotencyKey: refund.EventId,
-                cancellationToken);
+            // A refund can prorate to fewer than half a credit; the store requires a positive amount.
+            if (credits > 0)
+            {
+                await _store.RefundCreditsAsync(
+                    refund.UserId,
+                    refund.Metadata["resource"],
+                    credits,
+                    refund.EventType,
+                    idempotencyKey: refund.EventId,
+                    cancellationToken);
+            }
+
             await InvokeAsync(refund, cancellationToken);
             return result;
         }
```

### F45 — RC-01 — A TRANSFER with no eligible destination still downgrades the source

**Severity:** Medium · **Module:** `billing_revenuecat`  
**Root cause:** The guard counts `from` and `to` together instead of requiring a destination to move the entitlement to. Filtering anonymous destinations is correct (the app cannot resolve them), but it must make the transfer a no-op, not a downgrade.  
**Anchor:** `src/NetQuota.Billing/RevenueCat/RevenueCatBillingProvider.cs:169-237`  
**Repro:** `dotnet test --project .tmp/audit/repros/billing_revenuecat/RevenueCatBillingRepro.csproj -c Release`

```diff
--- a/src/NetQuota.Billing/RevenueCat/RevenueCatBillingProvider.cs
+++ b/src/NetQuota.Billing/RevenueCat/RevenueCatBillingProvider.cs
@@ -166,9 +166,12 @@
         var fromIds = result.TransferredFrom ?? [];
         var toIds = result.TransferredTo ?? [];
-        if (fromIds.Count == 0 && toIds.Count == 0)
+        if (fromIds.Count == 0 || toIds.Count == 0)
         {
+            // No eligible destination (anonymous aliases are filtered): downgrading the sources would
+            // revoke the paying subscriber's tier without entitling anyone.
             return;
         }
```

### F46 — SEC-01 — `WebhookRateLimiter` does not validate its configuration and a zero limit fails open

**Severity:** Medium · **Module:** `billing_webhook_security`  
**Root cause:** No `ArgumentOutOfRangeException` guard on `limit`/`window`, combined with a bucket-insert-then-check ordering that treats "0 allowed" as "1 allowed".  
**Anchor:** `src/NetQuota.Billing/WebhookSecurity.cs:12-26,46-55`  
**Repro:** `dotnet test --project .tmp/audit/repros/billing_webhook_security/WebhookSecurityRepro.csproj -c Release`

```diff
--- a/src/NetQuota.Billing/WebhookSecurity.cs
+++ b/src/NetQuota.Billing/WebhookSecurity.cs
@@ -14,6 +14,15 @@
     private sealed class Bucket
     {
         public int Count;
         public DateTimeOffset ResetAt;
     }
 
+    private readonly int _limit = limit > 0
+        ? limit
+        : throw new ArgumentOutOfRangeException(nameof(limit), limit, "Webhook rate limit must be positive.");
+
+    private readonly TimeSpan _window = window > TimeSpan.Zero
+        ? window
+        : throw new ArgumentOutOfRangeException(nameof(window), window, "Webhook rate window must be positive.");
+
     private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
     private readonly Lock _gate = new();
     private readonly int _cleanupEvery = Math.Max(1, cleanupEvery);
     private int _requests;
 
-    public int Limit { get; } = limit;
-    public TimeSpan Window { get; } = window;
+    public int Limit => _limit;
+    public TimeSpan Window => _window;
@@ -46,7 +55,7 @@
             if (!_buckets.TryGetValue(clientKey, out var bucket) || nowUtc >= bucket.ResetAt)
             {
-                _buckets[clientKey] = new Bucket { Count = 1, ResetAt = nowUtc.Add(Window) };
+                _buckets[clientKey] = new Bucket { Count = 1, ResetAt = nowUtc.Add(_window) };
                 return true;
             }
 
-            if (bucket.Count >= Limit)
+            if (bucket.Count >= _limit)
             {
                 return false;
             }
```

### F47 — A3 — `NetQuotaDistributedRateLimiter` ignores `permitCount`

**Severity:** Medium · **Module:** `aspnetcore_integration`  
**Root cause:** The bridge has no way to pass a permit count into `QuotaManager.CheckRateLimitAsync`; the parameter is dropped. `permitCount < 0` is also not validated (the base `RateLimiter` contract requires `ArgumentOutOfRangeException`).  
**Anchor:** `src/NetQuota.AspNetCore/NetQuotaDistributedRateLimiter.cs:24-34`  
**Repro:** `dotnet test --project .tmp/audit/repros/aspnetcore_integration/AspNetCoreIntegrationRepro.csproj -c Release`

```diff
--- a/src/NetQuota.AspNetCore/NetQuotaDistributedRateLimiter.cs
+++ b/src/NetQuota.AspNetCore/NetQuotaDistributedRateLimiter.cs
@@ -24,12 +24,37 @@ public sealed class NetQuotaDistributedRateLimiter(
     protected override async ValueTask<RateLimitLease> AcquireAsyncCore(
         int permitCount,
         CancellationToken cancellationToken)
     {
+        ArgumentOutOfRangeException.ThrowIfNegative(permitCount);
+        if (permitCount == 0)
+        {
+            return new NetQuotaRateLimitLease(
+                new RateLimitStatus(true, int.MaxValue, int.MaxValue, DateTimeOffset.UtcNow, TimeSpan.Zero));
+        }
+
         var status = await manager.CheckRateLimitAsync(
             subjectKey,
             resource,
             explicitTier: "ANONYMOUS",
             cancellationToken);
 
-        return new NetQuotaRateLimitLease(status);
+        if (!status.Allowed)
+        {
+            return new NetQuotaRateLimitLease(status);
+        }
+
+        for (var i = 1; i < permitCount; i++)
+        {
+            status = await manager.CheckRateLimitAsync(subjectKey, resource, "ANONYMOUS", cancellationToken);
+            if (!status.Allowed)
+            {
+                return new NetQuotaRateLimitLease(status);
+            }
+        }
+
+        return new NetQuotaRateLimitLease(status with
+        {
+            Remaining = Math.Max(0, status.Remaining - (permitCount - 1))
+        });
     }
```

### F48 — A5 — A 429 quota denial omits `Retry-After`, although the docs promise it

**Severity:** Medium · **Module:** `aspnetcore_integration`  
**Root cause:** The two rejection paths are asymmetric: the exception path uses `RateLimitStatus.RetryAfter`, the quota path only has `ResetAtUtc` and was never wired to derive a delay.  
**Anchor:** `src/NetQuota.AspNetCore/NetQuotaAspNetCoreExtensions.cs:284-301`  
**Repro:** `dotnet test --project .tmp/audit/repros/aspnetcore_integration/AspNetCoreIntegrationRepro.csproj -c Release`

```diff
--- a/src/NetQuota.AspNetCore/NetQuotaAspNetCoreExtensions.cs
+++ b/src/NetQuota.AspNetCore/NetQuotaAspNetCoreExtensions.cs
@@ -62,6 +62,13 @@ public static class NetQuotaHttpHeaders
         if (result.ResetAtUtc is { } reset)
         {
             context.Response.Headers[NetQuotaHeaderNames.RateLimitReset] =
                 reset.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
         }
+
+        if (!result.Allowed && result.ResetAtUtc is { } retryReset)
+        {
+            var retrySeconds = Math.Max(1, (int)Math.Ceiling((retryReset - DateTimeOffset.UtcNow).TotalSeconds));
+            context.Response.Headers[NetQuotaHeaderNames.RetryAfter] =
+                retrySeconds.ToString(CultureInfo.InvariantCulture);
+        }
     }
```

### F49 — A6 — The usage endpoint swallows cancellation and keeps reading storage

**Severity:** Medium · **Module:** `aspnetcore_integration`  
**Root cause:** A blanket `catch (Exception)` with no `cancellationToken.ThrowIfCancellationRequested()` guard. The same pattern exists around the entitlement read (`:338-344`).  
**Anchor:** `src/NetQuota.AspNetCore/NetQuotaAspNetCoreExtensions.cs:387-430`  
**Repro:** `dotnet test --project .tmp/audit/repros/aspnetcore_integration/AspNetCoreIntegrationRepro.csproj -c Release`

```diff
--- a/src/NetQuota.AspNetCore/NetQuotaAspNetCoreExtensions.cs
+++ b/src/NetQuota.AspNetCore/NetQuotaAspNetCoreExtensions.cs
@@ -421,8 +421,9 @@ public static RouteHandlerBuilder MapNetQuotaUsageEndpoint(
                 }
                 catch (Exception ex)
                 {
+                    cancellationToken.ThrowIfCancellationRequested();
                     firstError ??= ex;
                 }
             }
```

### F50 — TCE-01 — `EntitlementPreservationTests` claims Invariant 1 but omits the sealed tombstone

**Severity:** Medium · **Module:** `tests_core_engine`  
**Root cause:** The test class generalizes the invariant in its comment but implements only the field that was easiest to observe (`Timezone`) plus the pre-existing promotion test. The sealed tombstone is the field most likely to be dropped by a rewrite because callers construct a fresh `QuotaEntitlement` from a billing model and usually only know the `Sealed` bool. Because no test asserts it, the incomplete `8a716a7` fix looked complete.  
**Anchor:** `tests/NetQuota.Tests/EntitlementPreservationTests.cs:5-9`<br>`src/NetQuota/InMemoryQuotaStorage.cs:81-89`  
**Repro:** `dotnet test --project .tmp/audit/repros/tests_core_engine/TestsCoreEngineRepro.csproj -c Release -- --filter-class NetQuota.Audit.TestsCoreEngine.TestCoverageDefectTests`

```diff
--- a/tests/NetQuota.Tests/EntitlementPreservationTests.cs
+++ b/tests/NetQuota.Tests/EntitlementPreservationTests.cs
@@ -73,4 +73,27 @@ public class EntitlementPreservationTests
         Assert.Equal("pro", stored!.Promotion?.Tier);
         Assert.Equal("Europe/Bucharest", stored.Timezone);
     }
+
+    [Fact]
+    public async Task Rewrite_WithSealedFlag_PreservesSealTombstone()
+    {
+        var storage = new InMemoryQuotaStorage(() => Now);
+        var sealUntil = Now.AddDays(30);
+
+        await storage.SetEntitlementAsync(new QuotaEntitlement(
+            "u1", "free", Now, Now,
+            Sealed: true,
+            MigratedTo: "target-user",
+            SealExpiresAtUtc: sealUntil));
+
+        // A billing sync that only knows the boolean flag.
+        await storage.SetEntitlementAsync(new QuotaEntitlement(
+            "u1", "pro", Now, Now.AddMinutes(1), Sealed: true));
+
+        var stored = (await storage.GetEntitlementAsync("u1"))!;
+        Assert.Equal("target-user", stored.MigratedTo);
+        Assert.Equal(sealUntil, stored.SealExpiresAtUtc);
+        Assert.False(stored.IsSealedAt(Now.AddDays(60)));
+    }
 }
```

### F51 — TCE-02 — The merge regression test never tests idempotency and never asserts usage monotonicity

**Severity:** Medium · **Module:** `tests_core_engine`  
**Root cause:** The test was written against the happy-path merge (one unit per document) and mirrors the implementation's assumption instead of the invariant. Because `ConsumeOptions` permits `Amount > 1` together with a `DistinctKey`, and `StorageConsumeSpec` carries both to every adapter, the test does not cover a reachable input. The name also overstates what is checked: there is no replay assertion to protect the merge idempotency record.  
**Anchor:** `tests/NetQuota.Tests/QuotaManagerTests.cs:153-189`<br>`src/NetQuota/InMemoryQuotaStorage.cs:586-595`  
**Repro:** `dotnet test --project .tmp/audit/repros/tests_core_engine/TestsCoreEngineRepro.csproj -c Release -- --filter-class NetQuota.Audit.TestsCoreEngine.TestCoverageDefectTests`

```diff
--- a/tests/NetQuota.Tests/QuotaManagerTests.cs
+++ b/tests/NetQuota.Tests/QuotaManagerTests.cs
@@ -183,9 +183,34 @@ public class QuotaManagerTests
         Assert.False(merge.AlreadyMerged);
+        Assert.Equal(1, merge.TransferredUsage[$"{ConsultareResource}:{PeriodType.Daily}"]);
         var peek1 = await manager.PeekAsync("account:acc_1", ConsultareResource, PeriodType.Daily, "cui:35703208", "FREE");
         var peek2 = await manager.PeekAsync("account:acc_1", ConsultareResource, PeriodType.Daily, "cui:38183029", "FREE");
         Assert.True(peek1.AlreadySeenDistinctKey);
         Assert.True(peek2.AlreadySeenDistinctKey);
         Assert.Equal(2, peek1.Used);
+
+        // A replay must be reported and must not move usage again.
+        var replay = await manager.MergeUserAsync(new MergeUserRequest(
+            SourceUserId: "anon:1",
+            TargetUserId: "account:acc_1",
+            Resources: [ConsultareResource],
+            Periods: [PeriodType.Daily],
+            IdempotencyKey: "merge_day_1",
+            SealSource: false));
+        Assert.True(replay.AlreadyMerged);
+        Assert.Equal(2, (await manager.GetQuotaAsync("account:acc_1", ConsultareResource, PeriodType.Daily)).Used);
+
+        // A consume that charged more than one unit per distinct key must not be erased by a merge.
+        await manager.ConsumeWithResultAsync(
+            "account:acc_2",
+            ConsultareResource,
+            5,
+            PeriodType.Daily,
+            new ConsumeOptions(DistinctKey: "cui:40000000"),
+            explicitTier: "FREE");
+        await manager.ConsumeWithResultAsync(
+            "anon:2",
+            ConsultareResource,
+            1,
+            PeriodType.Daily,
+            new ConsumeOptions(DistinctKey: "cui:40000001"));
+        await manager.MergeUserAsync(new MergeUserRequest(
+            SourceUserId: "anon:2",
+            TargetUserId: "account:acc_2",
+            Resources: [ConsultareResource],
+            Periods: [PeriodType.Daily],
+            IdempotencyKey: "merge_day_2",
+            SealSource: false));
+        Assert.Equal(
+            6,
+            (await manager.GetQuotaAsync("account:acc_2", ConsultareResource, PeriodType.Daily)).Used);
     }
```

### F52 — TESTS-01 — The refund-proration suite only exercises a payload shape Stripe cannot send

**Severity:** Medium · **Module:** `tests_billing`  
**Root cause:** The fixtures encode the parser's preferred input rather than the provider's actual output, so the fallback branch — the one that runs in production — has no coverage at all. The suite therefore certifies a behaviour that the shipped code does not have.  
**Anchor:** `tests/NetQuota.Tests/StripeRefundProrationTests.cs:42-187`  
**Repro:** `dotnet test --project .tmp/audit/repros/tests_billing/TestsBillingRepro.csproj -c Release`

```diff
--- a/tests/NetQuota.Tests/StripeRefundProrationTests.cs
+++ b/tests/NetQuota.Tests/StripeRefundProrationTests.cs
@@ -74,6 +74,30 @@
         Assert.Equal(20, RefundedCredits(result));
         Assert.Equal("re_first", result.Event!.EventId);
     }
 
+    [Fact]
+    public void PartialRefund_WithoutExpandedRefunds_RevokesOnlyItsShare()
+    {
+        // `charge.refunds` is an includable field that Stripe does not return in webhook payloads;
+        // this is the shape the endpoint actually receives.
+        var result = Parse($$"""
+            {
+              "id": "evt_refund_first", "type": "charge.refunded", "created": 1760000000,
+              "data": { "object": {
+                "id": "ch_pack100", "object": "charge", "payment_intent": "pi_pack100",
+                "amount": 1000, "amount_refunded": 200,
+                "currency": "usd", "status": "succeeded",
+                {{PackMetadata}} } }
+            }
+            """);
+
+        Assert.Equal(20, RefundedCredits(result));
+    }
+
     [Fact]
     public void SequentialPartialRefunds_RevokeTheirOwnShares_NeverTheWholePackTwice()
     {
         // Stripe reports amount_refunded cumulatively and lists the newest refund first.
```

### F53 — PGA-1 — No adapter test covers `PruneExpiredAsync`, so the retention contract is unverified (and broken)

**Severity:** Medium · **Module:** `tests_storage_adapters`  
**Root cause:** The adapter suite has no prune/retention test; the missing assertion fails against HEAD because consumed windows are never pruned.  
**Anchor:** `tests/NetQuota.Tests/NetQuotaPostgresTests.cs`  
**Repro:** `$env:NETQUOTA_POSTGRES='Host=127.0.0.1;Port=55433;Database=netquota;Username=netquota;Password=netquota'; $env:NETQUOTA_REDIS='127.0.0.1:6391'; dotnet test --project .tmp/audit/repros/tests_storage_adapters/StorageAdaptersTestsRepro.csproj -c Release`

```diff
@@ -358,6 +358,27 @@ public class NetQuotaPostgresTests
         Assert.Equal("admin_set", filtered[0].Action);
     }
 
+    [Fact]
+    public async Task Prune_DeletesExpiredRecurringUsage_ButNeverForeverCredits()
+    {
+        var maybeStorage = await CreateAsync();
+        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
+        var storage = maybeStorage!;
+
+        var userId = NewUser();
+        var old = new PeriodWindow(
+            PeriodType.Daily, "2020-01-01",
+            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
+            new DateTimeOffset(2020, 1, 2, 0, 0, 0, TimeSpan.Zero));
+
+        await storage.TryConsumeAsync(new StorageConsumeSpec(
+            userId, "api_calls", 1, old, 10, null, "k1", TimeSpan.FromHours(1), DateTimeOffset.UtcNow, "free"));
+        await storage.TopUpForeverCreditsAsync(userId, "credits", 5, null, TimeSpan.FromHours(1), DateTimeOffset.UtcNow);
+
+        await storage.PruneExpiredAsync(DateTimeOffset.UtcNow.AddDays(30));
+
+        Assert.Equal(0, (await storage.GetUsageAsync(userId, "api_calls", old, 10))!.Used);
+        Assert.Equal(5, (await storage.GetUsageAsync(userId, "credits", PeriodWindow.Forever, 0))!.Limit);
+    }
+
     [Fact]
     public async Task ConcurrentConsume_RespectsLimitExactly()
```

### F54 — PGA-2 — The expired-idempotency-record path is untested, so a double credit is invisible

**Severity:** Medium · **Module:** `tests_storage_adapters`  
**Root cause:** The suite never exercises an expired idempotency record followed by a retry; the missing assertion fails against HEAD because the retry grants again.  
**Anchor:** `tests/NetQuota.Tests/NetQuotaPostgresTests.cs`  
**Repro:** `$env:NETQUOTA_POSTGRES='Host=127.0.0.1;Port=55433;Database=netquota;Username=netquota;Password=netquota'; $env:NETQUOTA_REDIS='127.0.0.1:6391'; dotnet test --project .tmp/audit/repros/tests_storage_adapters/StorageAdaptersTestsRepro.csproj -c Release`

```diff
@@ -198,6 +198,29 @@ public class NetQuotaPostgresTests
         Assert.Equal(10, usage!.Limit);
     }
 
+    [Fact]
+    public async Task TopUp_SameKeyAfterTtlExpiry_DoesNotGrantOnEveryRetry()
+    {
+        var maybeStorage = await CreateAsync();
+        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
+        var storage = maybeStorage!;
+
+        var userId = NewUser();
+        var ttl = TimeSpan.FromMilliseconds(150);
+
+        await storage.TopUpForeverCreditsAsync(userId, "credits", 10, "topup_1", ttl, DateTimeOffset.UtcNow);
+
+        await Task.Delay(400);
+        await storage.TopUpForeverCreditsAsync(userId, "credits", 10, "topup_1", ttl, DateTimeOffset.UtcNow);
+
+        // The immediately following retry must replay the second grant.
+        await storage.TopUpForeverCreditsAsync(userId, "credits", 10, "topup_1", ttl, DateTimeOffset.UtcNow);
+
+        Assert.Equal(20, (await storage.GetUsageAsync(userId, "credits", PeriodWindow.Forever, 0))!.Limit);
+    }
+
     [Fact]
     public async Task ConcurrentRefund_SameIdempotencyKey_ReplaysWithoutThrowing()
```

### F55 — PGA-3 — Merge idempotency is only tested for the same pair, so a cross-pair replay is invisible

**Severity:** Medium · **Module:** `tests_storage_adapters`  
**Root cause:** Merge idempotency is tested only for the same user pair; the suite cannot see a cross-pair replay returning AlreadyMerged.  
**Anchor:** `tests/NetQuota.Tests/NetQuotaPostgresTests.cs`  
**Repro:** `$env:NETQUOTA_POSTGRES='Host=127.0.0.1;Port=55433;Database=netquota;Username=netquota;Password=netquota'; $env:NETQUOTA_REDIS='127.0.0.1:6391'; dotnet test --project .tmp/audit/repros/tests_storage_adapters/StorageAdaptersTestsRepro.csproj -c Release`

```diff
@@ -335,6 +335,31 @@ public class NetQuotaPostgresTests
         await Assert.ThrowsAsync<SameUserMergeException>(() =>
             storage.MergeUserAsync(request with { TargetUserId = source, IdempotencyKey = $"{source}:same" }));
     }
 
+    [Fact]
+    public async Task Merge_SameKeyForAnotherPair_IsRejectedOrMerges()
+    {
+        var maybeStorage = await CreateAsync();
+        Assert.SkipWhen(maybeStorage is null, PostgresNotConfigured);
+        var storage = maybeStorage!;
+
+        var now = DateTimeOffset.UtcNow;
+        var daily = PeriodWindow.ForDaily(now, TimeZoneInfo.Utc);
+        var (sourceA, targetA, sourceB, targetB) = (NewUser(), NewUser(), NewUser(), NewUser());
+
+        StorageMergeRequest Req(string s, string t) => new(
+            s, t, [new MergeItem("api_calls", PeriodType.Daily, daily, daily)],
+            "shared_key", SealSource: true, NowUtc: now);
+
+        await storage.TryConsumeAsync(new StorageConsumeSpec(
+            sourceB, "api_calls", 1, daily, 10, null, null, TimeSpan.FromHours(1), now, "free"));
+
+        await storage.MergeUserAsync(Req(sourceA, targetA));
+        var second = await storage.MergeUserAsync(Req(sourceB, targetB));
+
+        Assert.False(second.AlreadyMerged);
+        Assert.Equal(1, (await storage.GetUsageAsync(targetB, "api_calls", daily, 10))!.Used);
+    }
+
     [Fact]
     public async Task AuditLog_FiltersAndOrdersNewestFirst()
```

### F56 — TS-01 — The amount suite never exercises `FromJsonBodyInt` through a real pipeline, so post-binding extraction and the missing clamp are invisible

**Severity:** Medium · **Module:** `tests_aspnetcore`  
**Root cause:** The amount tests call FromJsonBodyInt on a hand-built DefaultHttpContext with a seekable body, not through a body-bound endpoint, so post-binding fallback and the missing clamp are invisible.  
**Anchor:** `tests/NetQuota.Tests/NetQuotaAspNetCoreAmountTests.cs:51-71,120-128`  
**Repro:** `dotnet test --project .tmp/audit/repros/tests_aspnetcore/TestsAspNetCoreRepro.csproj -c Release -- --filter-class TestsAspNetCoreRepro.CoverageGapTests`

```diff
--- a/tests/NetQuota.Tests/NetQuotaAspNetCoreAmountTests.cs
+++ b/tests/NetQuota.Tests/NetQuotaAspNetCoreAmountTests.cs
@@ -79,6 +79,32 @@ public class NetQuotaAspNetCoreAmountTests
         var amount = await NetQuotaAmountExtractors.FromBodyByteLength()(context);
 
         Assert.InRange(amount, 1, 1_000_000);
     }
+
+    [Theory]
+    [InlineData("1000001", 1_000_000)]
+    [InlineData("2147483647", 1_000_000)]
+    public async Task FromHeader_ClampsAtOneMillion(string raw, int expected)
+    {
+        var context = new DefaultHttpContext();
+        context.Request.Headers["X-Units"] = raw;
+        Assert.Equal(expected, await NetQuotaAmountExtractors.FromHeader("X-Units")(context));
+    }
+
+    [Fact]
+    public async Task Endpoint_JsonBodyAmount_IsChargedOnABodyBoundEndpoint()
+    {
+        // Same StartAppAsync shape, but MapPost("/work", (WorkItem item) => ...) and
+        // ResolveAmountAsync = NetQuotaAmountExtractors.FromJsonBodyInt("units").
+        // POST {"units":4} must leave Used == 4 (quota 10).
+        var (app, client, manager) = await StartJsonAppAsync(quotaPerDay: 10);
+        await using var _ = app;
+        using var __ = client;
+
+        var response = await client.PostAsync("/work",
+            new StringContent("""{"units":4}""", Encoding.UTF8, "application/json"));
+
+        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
+        Assert.Equal(4, (await manager.GetQuotaAsync("user-1", "api", PeriodType.Daily)).Used);
+    }
 
     private static DefaultHttpContext JsonContext(string json)
```

### F57 — TS-02 — `NetQuotaDistributedRateLimiter` and `MapNetQuotaUsageEndpoint` have zero test coverage

**Severity:** Medium · **Module:** `tests_aspnetcore`  
**Root cause:** No test constructs NetQuotaDistributedRateLimiter or MapNetQuotaUsageEndpoint; permitCount and entitlement-tier behavior are unverified.  
**Anchor:** `src/NetQuota.AspNetCore/NetQuotaDistributedRateLimiter.cs:24-34`<br>`src/NetQuota.AspNetCore/NetQuotaAspNetCoreExtensions.cs:307-448`  
**Repro:** `dotnet test --project .tmp/audit/repros/tests_aspnetcore/TestsAspNetCoreRepro.csproj -c Release -- --filter-class TestsAspNetCoreRepro.CoverageGapTests`

```diff
--- /dev/null
+++ b/tests/NetQuota.Tests/NetQuotaAspNetCoreRateLimiterTests.cs
@@ -0,0 +1,33 @@
+using NetQuota.AspNetCore;
+
+public class NetQuotaAspNetCoreRateLimiterTests
+{
+    private static QuotaManager Manager(int rate, int proRate = 0) => new(
+        new InMemoryQuotaStorage(),
+        new QuotaConfig
+        {
+            DefaultTier = "free",
+            Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
+            {
+                ["free"] = new() { RateLimits = new Dictionary<string, RateLimitConfig>(StringComparer.Ordinal)
+                    { ["api"] = new RateLimitConfig(RateLimitAlgorithm.SlidingWindow, rate, TimeSpan.FromMinutes(1)) } },
+                ["pro"] = new() { RateLimits = new Dictionary<string, RateLimitConfig>(StringComparer.Ordinal)
+                    { ["api"] = new RateLimitConfig(RateLimitAlgorithm.SlidingWindow, proRate, TimeSpan.FromMinutes(1)) } }
+            }
+        });
+
+    [Fact]
+    public async Task AcquireAsync_ChargesEveryPermit()
+    {
+        using var limiter = new NetQuotaDistributedRateLimiter(Manager(3), "api", "s1");
+        using var first = await limiter.AcquireAsync(3, TestContext.Current.CancellationToken);
+        using var second = await limiter.AcquireAsync(3, TestContext.Current.CancellationToken);
+        Assert.True(first.IsAcquired);
+        Assert.False(second.IsAcquired);
+    }
+}
```


---

## Appendix B — Evidence Map

- Per-module reports: `.tmp/audit/findings/<module_id>.md` (18 files).
- Machine-readable audit state: `.tmp/audit/audit_state.json` (18/18 completed, 57 findings).
- Repro projects: `.tmp/audit/repros/<module_id>/`.
- Benchmarks: `.tmp/audit/repros/*_bench/` and the benchmark sections of the module reports.
- Legacy prior-audit reports: `.tmp/audit/findings/00_–06_*.md`; these are historical and were excluded from the 57-count to avoid double-counting.

*Prepared 2026-10-21. All findings are verified against HEAD `147e2e9`; no proposed diff was applied.*
