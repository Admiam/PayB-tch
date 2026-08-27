# Paybitch Backend

.NET 10 · ASP.NET Core Minimal APIs · PostgreSQL 16 · EF Core 10 (writes) + Dapper (shaped reads).

The server implementation of the [Paybitch](../README.md) expense-sharing app. Design of record:
[`docs/BACKEND_DESIGN.md`](../docs/BACKEND_DESIGN.md) (v1, decisions **D1–D9**) and
[`docs/BACKEND_EXTENSIONS.md`](../docs/BACKEND_EXTENSIONS.md) (extensions, rules **X1–X6**).

## What's implemented

**v1** (all of `BACKEND_DESIGN.md`): Sign in with Apple + refresh rotation + `token_epoch`,
groups/members/roles/leave/remove, ghost-claim invites, categories, expenses with
server-authoritative largest-remainder splits (equal/exact/shares/percentage), settlements,
on-demand per-currency balances + greedy debt simplification, optimistic concurrency
(`If-Match`/`412`), `(group_id, client_id)` idempotency, delta sync (`change_log`), activity feed,
GDPR anonymize + data export.

**Extensions**: **E0** platform primitives (Postgres `SKIP LOCKED` job runner, `IBlobStore`,
`IEmailSender`), **E3** recurring expenses, **E4** exports (CSV/PDF/stats/GDPR), **E5**
notifications + invite landing, **E6** email/OTP auth, **E7** avatars, **E8** scale/ops +
Hetzner exit runbook, **E10a** comments. _Excluded by request:_ E1 receipts/attachments, E2 live FX.

Money is **integer minor units** everywhere; on the wire it is a JSON **string** + a sibling
`currency` field (never a float). The "splits sum to the total" invariant is enforced by a
`DEFERRABLE` Postgres trigger (`SchemaHardening` migration) and validated at the boundary.

- **~26k LOC**, **82 endpoints**, **26 tables**, **7 background job handlers**, **4 hosted services**.

## Layout

```
backend/
├── src/
│   ├── Paybitch.Domain/          # pure C# — Money, Currency, Splitter, BalanceCalculator, DebtSimplifier (zero framework deps)
│   ├── Paybitch.Infrastructure/  # EF Core: AppDbContext, entities, configs, migrations, abstractions
│   └── Paybitch.Api/             # Minimal APIs; Common/ (conventions) + Features/<slice>/ (vertical slices)
└── tests/
    ├── Paybitch.Domain.Tests/    # 71 money/split/settlement unit + FsCheck property tests
    └── Paybitch.Api.Tests/       # 8 end-to-end HTTP tests on real Postgres (Testcontainers)
```

Vertical-slice conventions (see `Features/Currencies/CurrenciesModule.cs` for the reference):
each slice implements `IEndpointModule` (auto-discovered, mapped under `/v1`, secure-by-default),
uses `Problems.*` + `ProblemCodes` (RFC 9457), `.RequireGroupMembership()`/`.RequireGroupAdmin()`
(D6 — non-members get `404`), `.WithValidation()` (FluentValidation), and `IJobHandler`
implementations are auto-registered by an assembly scan.

## Run it

Prereqs: .NET 10 SDK, Docker.

```bash
cd backend

# 1. Postgres (host port 5544 — 5432/5433 are often taken locally)
docker compose up -d postgres

# 2. Apply the schema (discrete step — the app never migrates on startup)
PAYBITCH_DB="Host=localhost;Port=5544;Database=paybitch;Username=paybitch;Password=paybitch" \
  dotnet ef database update --project src/Paybitch.Infrastructure --startup-project src/Paybitch.Api

# 3. Run the API (reads appsettings.Development.json → localhost:5544)
dotnet run --project src/Paybitch.Api
#   → http://localhost:5234  ·  Scalar API docs at /scalar/v1 (Development only)

# health / a real endpoint
curl localhost:5234/health/ready
curl localhost:5234/v1/currencies
```

Full stack in containers (multi-stage `Dockerfile`, EF bundle as a discrete `migrate` step):

```bash
docker compose up -d postgres
docker compose run --rm migrate     # applies migrations
docker compose up -d api            # → http://localhost:8080
```

## Test

```bash
dotnet test                         # Domain (71) + Api integration (8)
```

The API tests spin their own `postgres:16` Testcontainer, run migrations against it, and mint
JWTs via DI (Apple isn't needed for tests). They assert the ledger invariant (Σ net == 0 per
currency), idempotent replay vs. `client_id_conflict`, settlement balance movement, `If-Match`
`428`/`412`/`200`, `split_sum_mismatch`, and delta-sync embedding.

## Configuration

All tunables come from config (`Operational` section = Appendix A) and secrets from env — never
source. Notable keys: `ConnectionStrings:Postgres` (or `PAYBITCH_DB`), `Jwt:*`, `Apple:*`
(Sign in with Apple; a dev ephemeral ES256 key is generated when unset so the app boots without
secrets), `Blob:LocalRoot` (filesystem blob store; S3/Hetzner is the prod target behind
`IBlobStore`), `EmailAuth:Pepper`, `Notifications:*`. CORS is deny-all (native iOS client only).

## Not yet wired (intentional follow-ups)

- Per-endpoint rate-limit **policies** for the extension routes (the limiter infra + the
  Postgres distributed store exist; policies are registered but not attached to every new route).
- The Postgres distributed rate limiter (E8) is provided but only needed at >1 instance.
- Real APNs / SES / S3 transports (dev impls log; interfaces are prod-ready seams).

See [`ops/HETZNER_EXIT_RUNBOOK.md`](ops/HETZNER_EXIT_RUNBOOK.md) and
[`ops/MULTI_INSTANCE_AUDIT.md`](ops/MULTI_INSTANCE_AUDIT.md).
