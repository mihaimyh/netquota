# AGENTS

## Repository

`NetQuota` — transactional quotas, prepaid credits, promotions, merges and rate limiting for .NET.
Port of goquota (`E:\devel\repos\goquota`) with the production bugs fixed. Published as NuGet
packages under `mihaimyh/netquota`.

## Layout

- `src/NetQuota` — core (`QuotaManager`, models, in-memory/tiered storage, circuit breaker)
- `src/NetQuota.Firestore` — Firestore adapter
- `src/NetQuota.Redis` — Redis adapter (Lua)
- `src/NetQuota.Postgres` — PostgreSQL adapter (schema, locks, merges, audit, prune)
- `src/NetQuota.AspNetCore` — rate limiter bridge, endpoint filter, usage endpoint
- `src/NetQuota.Billing` — Stripe + RevenueCat webhooks, checkout, transfers, metrics
- `tests/NetQuota.Tests` — unit + integration tests (adapters skip without env vars)
- `docs/` — user documentation; `skills/netquota/SKILL.md` — agent skill

## Build and test

```bash
dotnet build NetQuota.slnx -c Release
dotnet test NetQuota.slnx -c Release
```

Integration tests skip (reported as skipped, never passed) unless configured:

```bash
docker run -d --rm --name netquota-redis -p 6379:6379 redis:7-alpine
docker run -d --rm --name netquota-postgres -e POSTGRES_USER=netquota -e POSTGRES_PASSWORD=netquota \
  -e POSTGRES_DB=netquota -p 5433:5432 postgres:16-alpine
NETQUOTA_REDIS=localhost:6379
NETQUOTA_POSTGRES='Host=127.0.0.1;Port=5433;Database=netquota;Username=netquota;Password=netquota'
FIRESTORE_EMULATOR_HOST=127.0.0.1:8085   # optional, Firestore emulator
```

## Invariants

1. `SetEntitlementAsync` never clears `Promotion`; only `ClearPromotionAsync` does. Preserve
   `Promotion`, `Timezone`, `Sealed`, `MigratedTo`, `SealExpiresAtUtc` on every rewrite.
2. Promotions and seals are evaluated at read time; no background jobs.
3. `-1` is unlimited. `0` means "not configured" for recurring periods; forever credits use the
   stored limit.
4. Money paths take an idempotency key derived from the provider event id. Never grant/refund without
   one.
5. Unlimited quotas override stored prorated caps; prorated caps survive consumes while the tier
   matches.
6. Adapters must be atomic: Firestore transaction, Redis Lua, PostgreSQL transaction + advisory locks.
7. Integration tests must skip explicitly (`Assert.SkipWhen`), never pass with zero assertions.

## Release

Tag-driven, versioned by GitVersion (`GitVersion.yml`, `GitHubFlow/v1`, tag prefix `v`).
There is no `<Version>` in the source.

1. `git tag v1.2.3 && git push origin v1.2.3`
2. `.github/workflows/release.yml` builds, tests, packs one package per project, exchanges a GitHub
   OIDC token for a short-lived NuGet key (`NuGet/login@v1`) and pushes to nuget.org.
3. The workflow also creates the GitHub release with the `.nupkg`/`.snupkg` artifacts.

One-time setup: a nuget.org Trusted Publishing policy (owner `mihaimyh`, repo `netquota`, workflow
file `release.yml`, no environment) and the `NUGET_USER` GitHub secret with the nuget.org profile
name. See `docs/publishing.md`.

Manual fallback (API key) and local packing instructions are in the same document.

## Change policy

- Public API changes need a docs update in `docs/` and, when behavior changes, a note in the PR body.
- Do not reintroduce known goquota bugs: promotion loss on entitlement sync, refund replay returning
  the amount, tier casing sensitivity, per-resource forever credits sharing one idempotency key,
  webhook secret trusting `X-Forwarded-For`, non-idempotent credit grants.
