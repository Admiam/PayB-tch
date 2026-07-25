# Multi-Instance Audit

> Every stateful component enumerated **assuming one instance today**, with its multi-instance status and the
> **trigger** that forces the remedy. Condensed from BACKEND_EXTENSIONS §8.5 + the EXT-D8 rulings (§8.2).
>
> **EXT-D8m — this audit is a gate on every new mutating component**, v1 and extension alike. Anything that
> holds state in-process (a `Channel<T>`, an in-memory cache, a lease) MUST declare its multi-instance story
> here **before** a second instance ships. The honest default at indie scale is **one instance + one
> Postgres**, and most triggers below never fire (EXT-D8a) — scale work is **trigger-gated, not roadmap-gated**.

## Component audit (§8.5)

| Component | v1 state | Status | Remedy |
|---|---|---|---|
| `RateLimiter` (§4.3) | in-memory, per-instance | **BROKEN** — 2× every Appendix A cap; independently poisonable partitions | EXT-D8b: the `rate_limit_counters` store — `Features/Platform/RateLimiting/PostgresRateLimitStore.cs` (this repo) |
| `token_epoch` cache (§4.1) | per-instance, TTL ≤ 60 s | **Tolerable** — bounded by TTL + access-token ceiling | EXT-D8b: shared keyed `SELECT users.token_epoch`, **same change** as the limiter (§4.1: "with the limiter, not before") |
| `PushNotificationWorker` `Channel<T>` (§4.5) | in-process queue | **BROKEN** — a worker sees only its own instance's writes; other instances' mutations never fan out | Replace with **E5's durable outbox** (`FOR UPDATE SKIP LOCKED` poll). E5 owns it; E8 records the dependency |
| E0 background jobs (prune, purge, counter GC) | scheduled | **Already safe** — at-least-once + idempotent (X5); jobs claimed via `FOR UPDATE SKIP LOCKED` | Add a single-flight **leader `pg_try_advisory_lock`** so N instances don't all fire one prune. E0-owned |
| `change_log` visibility lock (§3.5.3) | `pg_advisory_xact_lock` | **Already safe** — DB-level lock, cross-instance by construction | none |
| EF migrations (§6) | discrete bundle, pre-traffic | **Already safe** — never migrate-on-startup | none |

The two **BROKEN** rows are exactly the two shortcuts §4.3 and §4.5 *declared* single-instance-only. E8 does
not fix them speculatively — it names the trigger (EXT-D8b) and the owner (E5 for push) so they are fixed **in
the same change** that adds the second instance, never after it is already dropping pushes / halving caps.

## Trigger thresholds (EXT-D8 — measured off the §6 observability stack, never a calendar date)

| Trigger | Threshold (read off observability) | Remedy | Ref |
|---|---|---|---|
| **Second app instance** | any deployment topology with > 1 app instance | Distributed limiter (`rate_limit_counters`) + shared `token_epoch` read, **before** the 2nd instance serves traffic. Push `Channel<T>` → E5 durable outbox in the same change. Add the E0 leader lock. | EXT-D8b |
| **Distributed store = Redis?** | **two** independent needs, both paid for: sustained limiter/cache write contention Postgres can't serve at p95 (measured) **and** a second in-memory need (e.g. E5 fan-out beyond `LISTEN/NOTIFY`) | Adopt Redis. One need does not justify a second stateful service to back up / secure / carry across the exit | EXT-D8c/d |
| **Connection pool pressure** | DB connections sustained **> 70 %** of `max_connections` | **Try Npgsql multiplexing + a right-sized pool first**; reach for **PgBouncer (transaction mode)** only if that doesn't hold | EXT-D8e |
| **PgBouncer landed (transaction mode)** | — (consequence of the above) | Disable server-side prepared statements: Npgsql `Max Auto Prepare=0`, `No Reset On Close=true`; SCRAM to the bouncer | EXT-D8f |
| **Read replicas** | a **sustained read p95 regression** that survives indexing + query fixes (deliberately near-unreachable — D4 on-demand balance math is sub-ms, no materialized read models) | Read replica(s). Adds replication lag as a correctness variable for a money app → **very likely never** | EXT-D8g |

## The distributed limiter (EXT-D8b) — how it's wired

Shipped in this repo but **not registered** (v1 is single-instance and correct with the in-memory limiter):

- `Features/Platform/RateLimiting/PostgresRateLimitStore.cs` — atomic `UPSERT … RETURNING count`; partition
  keys HMAC-hashed at rest (X6/§8.6); trusted-proxy-resolved caller key; fail-open on store error.
- `PostgresPartitionRateLimiter.cs` — `System.Threading.RateLimiting` adapter so the named policies swap in
  one expression.
- `RateLimitGcHandler.cs` (`Kind = "ratelimit.gc"`) + `RateLimitGcScheduler.cs` — prune expired windows past
  the widest configured window; a stale row is harmless (X5).
- `PostgresRateLimitingRegistration.AddPostgresRateLimiting(config)` — the **opt-in** one-liner. Its XML-doc
  carries the exact two-line swap-in (Program.cs registration + the `RateLimitPolicies` partition-factory
  change) and the required `RateLimiting__PartitionHmacKey` secret (identical across all instances).

`rate_limit_counters` is **not** in the `/sync` `entity_type` set (§3.5.1) and must never be added — it is
disposable operational state (X2), excluded from the exit dump (EXT-D8n).
