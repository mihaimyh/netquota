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

1. Update `<Version>` in `Directory.Build.props`.
2. `dotnet pack NetQuota.slnx -c Release -o artifacts/packages`
3. `dotnet nuget push artifacts/packages/*.nupkg --api-key <key> --source https://api.nuget.org/v3/index.json`
4. `dotnet nuget push artifacts/packages/*.snupkg --api-key <key> --source https://api.nuget.org/v3/index.json`
5. Tag: `git tag v<version> && git push --tags`

Consumers install `NetQuota`, `NetQuota.Firestore`, `NetQuota.Redis`, `NetQuota.Postgres`,
`NetQuota.AspNetCore`, `NetQuota.Billing`.

## Change policy

- Public API changes need a docs update in `docs/` and, when behavior changes, a note in the PR body.
- Do not reintroduce known goquota bugs: promotion loss on entitlement sync, refund replay returning
  the amount, tier casing sensitivity, per-resource forever credits sharing one idempotency key,
  webhook secret trusting `X-Forwarded-For`, non-idempotent credit grants.
