# Paybitch — Backend Extensions (.NET 9 + PostgreSQL)

> The forward plan (v2.5 → v4) layered on the reconciled v1 backend
> ([`BACKEND_DESIGN.md`](./BACKEND_DESIGN.md)). Produced from a per‑extension design pass
> (jobs · blobs · email · recurring · FX · exports · receipts · invites · comments · budgets ·
> digests · ops) followed by adversarial reviews scoped per extension
> (money‑correctness · security · GDPR · reliability). Where a slice contradicted the v1
> invariants, **v1 wins** — this document never re‑opens a §0 decision. Every "🔧 fix" note
> marks a defect a review caught and how it's resolved here.

**Target stack (unchanged):** .NET 9 / ASP.NET Core · EF Core 9 (writes) + Dapper (shaped reads) ·
PostgreSQL 16+ · Npgsql · Railway (EU) → Hetzner later. **New in v2.5:** a Postgres‑backed jobs
substrate, an S3‑compatible blob store, and a transactional email sender — *no new stateful
dependency* (Postgres stays the only stateful service, preserving the Hetzner exit of §6).

---

## 0. Ground rules (read this first — everything below obeys them)

Extensions do not get to relitigate v1. The nine reconciled decisions are inherited verbatim;
six generalization rules (X1–X6) plus one meta‑rule govern how *any* new surface is added.

### 0.1 Inherited decisions D1–D9 (authoritative in [`BACKEND_DESIGN.md`](./BACKEND_DESIGN.md) §0)

| # | One‑line restatement | Extensions must |
|---|----------------------|-----------------|
| **D1** | Money = integer minor units (`bigint`/`long`); wire = JSON **string** of minor units + sibling `currency`. | Never introduce float/`NUMERIC` money; converted money is quarantined (X3), never an `amount`. |
| **D2** | `currencies` is the scale authority (CZK=0, EUR/USD/GBP=2). | Read scale from `currencies`; never hard‑code 2 decimals. |
| **D3** | Splits use largest‑remainder (Hamilton); `expense_splits` is the single participant source. | Recurring/templated expenses reuse `ProportionalAllocate` (§2.3) — no second split path. |
| **D4** | Balances computed **on‑demand** per currency; **no materialized balance table.** | No extension adds a second authoritative store for anything ledger‑derivable (X2). |
| **D5** | Per‑currency settle only; a settlement is in the **debt's** currency; FX convergence is deferred + snapshot‑backed. | FX (E3) is display‑only + snapshot‑provenanced; never nets across currencies. |
| **D6** | One authz path: indexed `(group_id, user_id)` membership check **first**; non‑members get **404** (no leak). | Every new group‑scoped route runs the membership check as its first statement. |
| **D7** | GDPR delete = **anonymize**: soft‑delete user, null member `user_id`, scrub PII, **retain ledger**. | New PII columns join the erase scrub; blobs get two‑phase deletion (X4). |
| **D8** | Optimistic concurrency via `version int` + `If-Match`/`412`; money entities reject‑and‑merge. | Every new editable entity carries `version` + honours `If-Match` (X1). |
| **D9** | One idempotency mechanism: permanent `(group_id, client_id)` unique on create. | Background jobs that create rows derive a **deterministic** `client_id` (X5). |

### 0.2 Extension generalization rules X1–X6

These are to extensions what D1–D9 are to v1: binding, cross‑cutting, cited by number in every
chapter. Mirrors the §0 D‑table style.

| # | Rule | Why / what it fixes |
|---|------|---------------------|
| **X1** | **Every client‑cached, client‑editable entity joins the FULL sync contract.** `client_id` + `UNIQUE(group_id, client_id)` create‑idempotency (D9); `version int` + `If-Match`/`412` (D8); soft‑delete tombstones surfaced in `/sync` (§3.5); indexed membership check first, 404 to non‑members (D6). *Applies to:* recurring rules, comments, attachment metadata, budgets. | One contract, not four bespoke ones. Any entity a client can edit offline **must** dedup, version, and tombstone the same way expenses do — or the outbox/delta‑pull engine (§7) silently corrupts it. |
| **X2** | **No extension introduces a SECOND authoritative store for anything derivable from the ledger.** Stats, budget progress, FX‑converted displays, and digest content are **computed on demand**; any cache is keyed on entity `version` and disposable (D4). | Kills drift bugs — the worst class in money apps. A stored stat is a stored balance by another name. |
| **X3** | **Converted / approximate money is STRUCTURALLY QUARANTINED on the wire.** It travels in a distinct `display`/`approx` block (still string minor units of the **target** currency + explicit rate provenance), **never** in an `amount` field, and **never** enters balance math, settlements, or an export's authoritative columns (D1, D5). | A converted number that can be mistaken for a ledger amount *will* be, eventually, by some client or export consumer. Type‑level separation makes the mistake unrepresentable. |
| **X4** | **Blobs are REFERENCED, never owned by Postgres.** DB rows store metadata + an opaque storage key; blob deletion is **two‑phase** (tombstone row → async hard‑delete job → verify), so GDPR‑anonymize (D7) and ledger retention can diverge per blob class. | Postgres is the only stateful service (§6) — it must not become a BLOB heap. Two‑phase delete lets a receipt vanish on erase while its expense row is retained. |
| **X5** | **All background work is at‑least‑once and IDEMPOTENT.** Any job that **creates money rows** (recurring materialization) derives a **deterministic** `client_id`, so the D9 unique constraint absorbs retries. The scheduler may double‑fire; the ledger may **not** double‑charge. | At‑least‑once is the only honest delivery guarantee for a polled queue. Determinism moves correctness from the scheduler (unreliable) to the DB constraint (reliable). |
| **X6** | **Every new UNAUTHENTICATED surface** (magic‑link verify, unsubscribe link, export download URL, invite email landing) gets its **own rate‑limit partition**, **enumeration‑safe uniform responses**, and **hashed‑at‑rest tokens.** The invite‑token pattern of v1 §3.8 / §4.3 is the template. | Each new public endpoint is a new attack surface. Reusing the proven invite pattern (128‑bit single‑use token, stored hashed, IP‑partitioned, uniform 200/404) means we don't reinvent — or re‑bug — it. |

**Meta‑rule — additive‑only /v1 (§3.11).** API growth is **ADDITIVE within `/v1`**. No extension
changes the meaning of an existing field, removes one, or forces a `/v2`. New fields, endpoints,
`entity_type`s, and enum members are added; clients ignore unknowns.

### 0.3 API evolution policy (restates §3.11)

- **Additive within `/v1`.** New endpoints, new response fields, new `split_type`/`entity_type`/
  `verb`/`method` enum members, new query params with safe defaults. Never a breaking change.
- **No field ever changes meaning.** `amount` stays authoritative minor‑units of `currency`,
  forever. Converted money arrives in a *new sibling block* (X3), never by overloading `amount`.
- **Forward‑compatible clients.** Clients ignore unknown JSON fields and tolerate unknown enum
  values (render‑as‑unknown, never crash) — the contract that lets the server ship features
  ahead of an app‑store review cycle.
- **`/sync` is where additivity is load‑bearing.** New `change_log.entity_type`s (§3.5) are added,
  never renumbered; an old client that doesn't know `'comment'` skips those rows harmlessly
  (it never had local state to reconcile).
- A `/v2` is a last resort reserved for a change no additive path can express. Nothing in this
  document requires one.

---

### 0.4 Platform primitives (E0) — the load‑bearing chapter

Three primitives every later extension imports. E0 ships in **v2.5** and is a hard prerequisite
for E1–E10. Design principle: **add capability, not a stateful dependency** — Postgres remains
the only stateful service, so the Hetzner exit (§6) stays "point DNS, restore dump, compose up."

#### 0.4.1 E0 decisions

| # | Ruling | Why / what it fixes |
|---|--------|---------------------|
| **EXT‑D0a** | **Jobs live in a `jobs` table drained by a .NET `BackgroundService` using `SELECT … FOR UPDATE SKIP LOCKED`.** No Redis, no broker. | Keeps Postgres the only stateful service (§6). `SKIP LOCKED` makes N app instances safe workers with zero coordination. |
| **EXT‑D0b** | **At‑least‑once delivery (X5).** Exponential backoff + jitter on failure; `attempts ≥ max_attempts` → `status='dead'` (poison/dead‑letter); a lease stranded by a mid‑run worker kill is reclaimed on expiry (EXT‑D0h). | Honest guarantee for a polled queue. Poison rows stop retry storms and become the alert signal, not a silent loss. |
| **EXT‑D0c** | **Money‑creating jobs derive a deterministic `client_id`** so the D9 unique constraint absorbs a double‑fire. | Moves correctness from scheduler timing to a DB constraint. A retried recurring materialization no‑ops instead of double‑charging. |
| **EXT‑D0d** | **One `oldest‑queued‑age` gauge, alerted like `Σ balances == 0`.** A rising floor = the drain is wedged. | The queue's canary. Mirrors v1's money‑invariant alert (§6): one number that says "something is silently broken." |
| **EXT‑D0e** | **`IBlobStore` is the INTERSECTION of Hetzner Object Storage and Cloudflare R2** — presign PUT/GET, HEAD, DELETE only. | The exit path (provider swap) can never silently break on a feature only one provider has. |
| **EXT‑D0f** | **`IEmailSender` renders templates SERVER‑SIDE (cs/en), checks suppression before every send.** | Provider swap is config‑only; no provider‑hosted templates to re‑author. Suppression survives the swap (Postgres‑mirrored). |
| **EXT‑D0g** | **Hand‑rolled jobs, not Hangfire/Quartz.** ~1 table + ~120 lines. | We need the deterministic‑`client_id` idempotency contract (X5) and the exact claim SQL either library would make us fight. See §0.4.2. |
| **EXT‑D0h** | **A stale lease is RECLAIMABLE.** The claim also picks up `status='running'` rows whose `locked_at` is older than the **lease timeout**, re‑incrementing `attempts`. Timeout is set comfortably above the longest handler runtime (export builds); tuned in Appendix A. | A worker killed mid‑run (a Railway rolling deploy is multi‑instance and SIGTERMs the old instance while it holds a claimed job, §6) otherwise strands that job in `'running'` forever — at‑*most*‑once, contradicting EXT‑D0b/X5. The reaper restores at‑least‑once across a deploy overlap; `attempts` still climbs so a genuinely poisonous (repeatedly‑crashing) job terminates into `'dead'` at `max_attempts`, not an infinite reclaim loop. |

#### 0.4.2 (a) Jobs substrate — Postgres‑backed, multi‑instance‑safe

Covers both **time‑based** work (recurring materialization, `change_log` prune §3.5.5) and
**demand** work (export builds, blob hard‑deletes, email sends) through one `run_at` column.

```sql
CREATE TABLE jobs (
    id           uuid PRIMARY KEY DEFAULT uuid_generate_v7(),
    kind         text NOT NULL,                        -- 'recurring.materialize'|'export.build'|
                                                       --   'blob.hard_delete'|'email.send'|'change_log.prune'
    payload      jsonb NOT NULL DEFAULT '{}'::jsonb,    -- ids only — never money/PII beyond ids (§4.3 logging hygiene)
    status       text NOT NULL DEFAULT 'queued'
                    CHECK (status IN ('queued','running','succeeded','failed','dead')),
    run_at       timestamptz NOT NULL DEFAULT now(),    -- time-based AND demand work share this
    priority     smallint NOT NULL DEFAULT 100,         -- lower = sooner
    attempts     int NOT NULL DEFAULT 0,
    max_attempts int NOT NULL DEFAULT 8,                -- → 'dead' on exhaustion (EXT-D0b; Appendix A)
    locked_by    text,                                  -- worker instance id holding the lease
    locked_at    timestamptz,                           -- lease start; a lease older than @leaseTimeout is reclaimable (EXT-D0h)
    last_error   text,                                  -- truncated; never full stack + no PII
    dedupe_key   text,                                  -- optional: collapse duplicate enqueues
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_jobs_claim  ON jobs (priority, run_at) WHERE status = 'queued';
CREATE INDEX ix_jobs_reap   ON jobs (locked_at)         WHERE status = 'running';  -- reaper: stale-lease lookup (EXT-D0h)
-- At most one live job per dedupe_key (e.g. one prune, one export per (user,params) in flight):
CREATE UNIQUE INDEX uq_jobs_dedupe ON jobs (dedupe_key)
    WHERE dedupe_key IS NOT NULL AND status IN ('queued','running');
```

**Claim (atomic lease + reaper).** One `UPDATE … RETURNING` per worker tick — no read‑then‑write
race. The second predicate folds lease recovery (EXT‑D0h) into the same query, so there is no
separate sweep to schedule: an expired lease is just another claimable row.

```sql
UPDATE jobs SET status='running', locked_by=@instance, locked_at=now(),
                attempts=attempts+1, updated_at=now()   -- reclaim re-increments attempts → poison still dies at max_attempts
WHERE id = (
    SELECT id FROM jobs
    WHERE (status='queued'  AND run_at <= now())
       OR (status='running' AND locked_at < now() - @leaseTimeout)   -- reclaim a stranded lease (EXT-D0h)
    ORDER BY priority, run_at
    FOR UPDATE SKIP LOCKED           -- N instances, zero coordination, no double-claim
    LIMIT 1
)
RETURNING *;
```

**Drain loop** — a single `BackgroundService`, safe to run on every app instance:

```csharp
while (!ct.IsCancellationRequested)
{
    var job = await _jobs.TryClaimAsync(_instanceId, ct);
    if (job is null) { await Task.Delay(Jitter(_idleDelay), ct); continue; }   // back off when empty
    try
    {
        await _dispatch[job.Kind].RunAsync(job, ct);          // handler is itself idempotent
        await _jobs.MarkSucceededAsync(job.Id, ct);
    }
    catch (Exception ex)
    {
        await _jobs.MarkFailedAsync(job.Id, ex, backoff: Backoff(job.Attempts), ct);
        // Backoff → run_at = now() + min(2^attempts · base, cap) ± jitter; status back to 'queued'.
        // attempts ≥ max_attempts → status='dead' (poison queue), no requeue (EXT-D0b).
    }
}
```

- **Polling cadence:** ~1 s busy‑poll, backing off to ~5 s idle, each delay ± jitter to
  de‑sync instances. `run_at <= now()` means a scheduled job fires within one idle interval;
  low‑latency demand work can optionally wake the loop via `LISTEN/NOTIFY` (nice‑to‑have, not
  required for correctness).
- **Reliability contract (X5):** at‑least‑once. Handlers **must** be idempotent. The canonical
  case — recurring materialization creating an expense — derives
  `client_id = $"recur:{ruleId}:{occurrenceDate:yyyy-MM-dd}"`, so a scheduler double‑fire hits
  `UNIQUE(group_id, client_id)` (D9) and the second insert no‑ops. The scheduler may double‑fire;
  the ledger may not double‑charge (EXT‑D0c).
- **Stale‑lease reclaim (EXT‑D0h) — what makes at‑least‑once true *across a deploy*.** SKIP LOCKED
  alone is at‑least‑once only while workers stay up. A worker can die mid‑run: a Railway rolling
  deploy is multi‑instance and non‑cancellable (§6), so it SIGTERMs the old instance while it is
  still running a claimed job. That job then sits in `status='running'` with a `locked_at` that
  stops advancing. The claim's second predicate re‑claims it once `locked_at < now() − @leaseTimeout`
  and re‑increments `attempts` (so a job that crashes its worker every run still terminates at
  `max_attempts` → `'dead'`, not an infinite reclaim loop). Without this, an orphaned
  `recurring.materialize` is a **missed** charge (silently never created), and an orphaned
  dedupe‑keyed job (`change_log.prune`, an export) permanently blocks `uq_jobs_dedupe` for its key —
  prune never runs again and `pruned_through_seq` never advances — because that partial index covers
  `status IN ('queued','running')`; driving the reclaimed job to `'succeeded'`/`'dead'` frees the
  key. `@leaseTimeout` is set comfortably above the longest expected handler runtime (export builds
  are the long pole) and tuned in Appendix A.
- **Observability (EXT‑D0d):** export a gauge
  `jobs_oldest_queued_age_seconds = now() − min(run_at) WHERE status='queued' AND run_at<=now()`
  and a counter of `status='dead'` rows. Alert when the age exceeds the Appendix‑A threshold —
  same spirit as v1's "alert on the `Σ balances == 0` invariant above all" (§6). Because that gauge
  filters `status='queued'`, it is **blind to a stranded lease**; pair it with
  `jobs_running_over_lease = count(*) WHERE status='running' AND locked_at < now() − @leaseTimeout`,
  which should sit at 0 — a sustained non‑zero value means the reaper isn't draining (e.g. every
  instance crashes on the same poison job before it reaches `max_attempts`).

**🔧 Why hand‑rolled over Hangfire / Quartz.NET.**

| Option | Verdict |
|--------|---------|
| **Hangfire** | Ships its own storage schema + dashboard, but its Postgres provider still polls, and adopting it means we don't own the claim SQL or the money‑idempotency contract (X5) — we'd bolt determinism on top anyway. Net: a dependency that duplicates what we need and hides what we care about. |
| **Quartz.NET** | A capable *scheduler*, but clustered execution needs its own table set and lock semantics; it's a cron engine where we mostly need a durable, idempotent work queue. Overkill. |
| **Hand‑rolled (chosen)** | One table, ~120 lines, `FOR UPDATE SKIP LOCKED`. Zero new stateful dependency (keeps the Hetzner exit clean, §6). We own the deterministic‑`client_id` idempotency (X5) and the claim query outright. The scheduling we need — `run_at`, backoff, poison queue, one gauge — is exactly what's above. |

#### 0.4.3 (b) `IBlobStore` — S3‑compatible, intersection contract

```csharp
public interface IBlobStore
{
    Task<PresignedUrl> PresignPutAsync(string key, string contentType, long maxBytes,
                                       TimeSpan ttl, CancellationToken ct);
    Task<PresignedUrl> PresignGetAsync(string key, TimeSpan ttl, CancellationToken ct);
    Task<BlobHead?>    HeadAsync(string key, CancellationToken ct);   // exists? size? etag? — post-upload verify
    Task              DeleteAsync(string key, CancellationToken ct);  // idempotent: absent == success (X4)
}
```

The contract is deliberately the **intersection** of Hetzner Object Storage (Ceph RGW) and
Cloudflare R2 — both S3‑compatible, but their dialects diverge. The interface exposes only what
**both** guarantee, so the provider swap (part of the Railway→Hetzner exit, or Hetzner↔R2) never
silently breaks. Dialect quirks abstracted away:

| Quirk | How the interface neutralizes it |
|-------|----------------------------------|
| **ACLs** — R2 rejects object ACLs; RGW honors them. | Never set `x-amz-acl`. Buckets are private by policy; access is only ever via a short‑TTL presigned URL. |
| **Storage classes / egress billing** — R2 has no egress fee and a flat class; Hetzner bills egress. | No storage‑class headers on PUT; cost is an ops concern, not a code path. |
| **Checksums** — providers differ on `x-amz-checksum-*` trailers. | Verify via `HeadAsync` (size + our own content‑hash stored in DB metadata), not a provider checksum header. |
| **Multipart minimums** — differing min part sizes. | v2.5 receipts are small → single PUT only. If multipart is ever needed, use the S3‑floor 5 MiB min part. |
| **LIST pagination markers** — differ. | We **never** enumerate: every blob's opaque key lives in a Postgres row (X4). No `List` in the interface at all. |
| **CORS** — configured per provider, out of band. | Presigned PUT from the client needs bucket CORS; that's deploy config, not application code. |

Deletion is **two‑phase** (X4): a metadata row is tombstoned synchronously, then a
`blob.hard_delete` job (`DeleteAsync` + `HeadAsync` verify) reclaims the object asynchronously and
idempotently. This is what lets a receipt be erased on GDPR‑anonymize (D7) while its expense row is
retained (E5).

#### 0.4.4 (c) `IEmailSender` — templated transactional send + suppression

```csharp
public interface IEmailSender
{
    Task<EmailSendResult> SendAsync(EmailMessage msg, CancellationToken ct);
    Task<bool>            IsSuppressedAsync(string email, CancellationToken ct);  // hard suppression only: bounce/complaint/manual
}

public sealed record EmailMessage(
    string To,
    string TemplateId,                                   // 'invite' | 'magic_link' | 'digest' | 'export_ready'
    string Locale,                                       // 'cs' | 'en'
    IReadOnlyDictionary<string,string> Model,            // rendered server-side into HTML + text
    string? IdempotencyKey);                             // dedupes re-sends across job retries (X5)
```

- **Server‑side rendering (EXT‑D0f).** Templates live in‑repo (localized cs/en) and render to a
  final MIME body on **our** side; the provider only ever receives finished HTML+text, never a
  provider‑hosted template id. Switching provider (Resend / Postmark / SES‑EU) is therefore
  **config‑only** — no templates to re‑author, no lock‑in.
- **Suppression before every send (X6, GDPR).** `IsSuppressedAsync` consults a Postgres‑mirrored
  suppression list (hard bounces, complaints) so it survives a provider swap. Every
  marketing‑adjacent send (E5 digests) carries a one‑click unsubscribe whose token follows the
  X6 pattern (hashed at rest, IP‑partitioned, uniform response).
- **All sends go through the jobs substrate** (`kind='email.send'`) so a transient provider outage
  retries with backoff and `IdempotencyKey` prevents a retry from double‑sending.

```sql
CREATE TABLE email_suppressions (
    email       citext PRIMARY KEY,
    -- Hard-deliverability signals only. Digest opt-out is NOT a suppression — it lives in
    -- users.digest_opt_in (owned by E5, §5), so unsubscribing never drops transactional mail.
    reason      text NOT NULL CHECK (reason IN ('bounce','complaint','manual')),
    created_at  timestamptz NOT NULL DEFAULT now()
);
```

---

### 0.5 Tier map, dependency graph & review gates

#### 0.5.1 Extension index & tier map

Every extension imports ≥1 E0 primitive and obeys X1–X6. IDs are stable; the version column is the
authoritative ship‑order anchor (names are descriptive).

| Ext | Name | Governed by | Phasing → ships |
|-----|------|-------------|-----------------|
| **E0** | Platform primitives (jobs · blob · email) | X4 · X5 · X6 | whole → **v2.5** |
| **E1** | Receipts & attachments | X4 · X1 | whole → **v3** |
| **E2** | Live FX (ČNB feed) | X3 · X2 (· D5) | whole → **v3** |
| **E3** | Recurring expenses | X1 · X5 | whole → **v3** |
| **E4** | Exports & reporting | X3 · X6 | E4a CSV/stats → **v2.5** · E4b PDF/GDPR → **v3** |
| **E5** | Notifications deepening | X5 · X6 · X2 | E5a invite email + push prefs → **v2.5** · E5b prefs matrix/Live Activity, E5c digests → **v4+** |
| **E6** | Email auth (magic‑link) | X6 · X1 | whole → **v3** |
| **E7** | Avatars | X4 · X1 | whole → **v3** (joint with E1) |
| **E8** | Scale & ops | (ops) | E8a checklist/dashboards → **v3** · E8b admin tooling → **v4+** |
| **E9** | Web companion / invite landing | X6 · X1 | E9a landing → **v2.5** · E9b/E9c web/analytics → **v4+** |
| **E10** | Comments / reactions / search / budgets | X1 · X2 | E10a comments → **v3** · E10b/c/d reactions/search/budgets → **v4+** |

**By release:**
- **v2.5** (foundation): E0 · E4a‑CSV · E5a · E9a
- **v3** (feature wave): E1 · E7 · E2 · E3 · E4b · E6 · E10a · E8a
- **v4+** (trigger‑based, built on telemetry demand): E5b/c · E8b · E9b · E9c · E10b/c/d

#### 0.5.2 Design‑order dependency graph

```
                        ┌────────────────────────────────────────┐
                        │  E0  Platform primitives      v2.5      │
                        │  jobs · blob · email                    │
                        │  RELIABILITY gate — blocks everything   │
                        └───────────────────┬────────────────────┘
                                            │  every extension imports ≥1 primitive
      ┌──────────┬──────────┬──────────┬────┴─────┬──────────┬───────────────┐
      ▼          ▼          ▼          ▼          ▼          ▼               ▼
  E2 Live FX E3 Recur.  E4 Export  E5 Notif.  E6 Email   E9a Landing   (all parallel,
  money‑corr money‑corr sec+money$ security   auth        (v2.5)         independent —
  (X3)       (X5)       (X6)       (X6)       security    no gate        depend only on E0)
                                              (X6)

      E1 Receipts ──────── joint ──────── E7 Avatars
      security (X4)        one blob        no gate
                           pipeline,       (rides E1's review)
                           built together

      E10a Comments   (v3, GDPR‑ruling gate — body scrub on erase)
      E8a Ops & observability   (v3, ops‑checklist gate — dashboards/alerts/runbook)

      ── v4+ trigger‑based (built when usage/telemetry demands, not on a schedule) ──
      E5b prefs matrix/Live Activity · E5c digests · E8b admin · E9b/E9c web/analytics · E10b/c/d reactions/search/budgets
```

Order rules: **E0 first** (hard prerequisite). **E1 + E7 are built jointly** as one blob slice
(receipts and avatars share the presigned‑upload / server‑side re‑encode pipeline, so they are
designed together). **E2 / E3 / E4 / E5 / E6 are mutually independent** and parallelizable once E0 lands. **v4+**
extensions are demand‑triggered — no fixed slot; they ship when their v2.5/v3 base shows the usage
that justifies them.

#### 0.5.3 Adversarial‑review gate

Each extension names the **one** review it must pass before merge — the analogue of v1's
money‑correctness + security/architecture passes, scoped per feature.

| Ext | Mandatory review gate | Focus |
|-----|----------------------|-------|
| **E0** | **Reliability** | At‑least‑once semantics **including stale‑lease reclaim across a deploy overlap** (EXT‑D0h); `SKIP LOCKED` claim correctness; poison‑queue termination (reclaim re‑increments `attempts`); the `oldest‑queued‑age` alert **and the stale‑lease signal** actually fire. |
| **E1** (+**E7**) | **Security** | Presigned‑URL scope/TTL, blob authz, MIME/size enforcement, server‑side re‑encode boundary (EXIF/polyglot/decompression bomb), two‑phase delete (X4); avatars ride this same review. |
| **E2** | **Money‑correctness** | Converted money never enters the ledger, settlements, or export authoritative columns (X3); snapshot provenance (D5). |
| **E3** | **Money‑correctness** | Materialization cannot double‑charge (X5 deterministic `client_id`); `Σ net == 0` per currency intact; split reuse (D3). |
| **E4** | **Security + money‑light** | Download‑URL is authz‑scoped & X6‑hardened; export authoritative columns carry only D1 minor‑units; converted columns quarantined (X3). |
| **E5** | **Security** | Unauthenticated surfaces (invite landing, unsubscribe, webhook) X6‑hardened; no names/amounts on APNs (§4.5); digest consent vs hard‑suppression split (no account‑recovery DoS). |
| **E6** | **Security** | Token hashing at rest, enumeration‑safe uniform responses, per‑surface rate‑limit partition (X6). |
| **E7** | *(none — rides E1's security review)* | Low‑risk; no money, no new unauthenticated surface. |
| **E8a** | **Ops checklist** | Dashboards, alert thresholds (Appendix A), runbook, on‑call signal quality. |
| **E9a** | *(none — invite landing rides E5's security review, §5)* | Public landing is X6‑hardened; strictly less exposed than the JSON invite preview. |
| **E10a** | **GDPR ruling** | Comment body is free‑text PII: scrub‑in‑place on erase, retain the shell (D7); membership authz (D6); money‑inert (nothing to quarantine, X3). |

---


## 1. E1 Receipts & attachments + E7 Avatars (shared blob pipeline)

> Joint chapter: E1 (receipts on expenses) and E7 (user / member / group avatars) share
> **one** blob pipeline behind `IBlobStore` (owned by **E0**). They diverge on exactly one
> axis — GDPR class (§1.6) — and that divergence is the reason to design them together rather
> than let two teams invent two upload dances. Everything here obeys D1–D9 (v1 §0) and the
> extension rules X1–X6 (§0.2); API growth is additive within `/v1` (§3.11 meta-rule) — new
> tables, columns, a new `change_log` entity type, new problem codes, new Appendix A constants,
> **nothing** existing renamed or retyped.

### 1.1 Scope & user value

E1 lets a member attach a **receipt** (photo or PDF) to an expense — the single most-requested
"is this real?" affordance in expense-sharing, and the audit trail for who-paid-what.
E7 lets a user set a **user avatar**, a **member image** (including a ghost's, set by an admin),
and a **group icon image** — turning the SF-Symbol-only placeholders of v1 (§3.12) into real
pictures. Both are **tier v3**. Both were unreachable in v1 by deliberate cut: `users.avatar_url`
/ `group_members.image_url` existed as *reserved* columns no endpoint could write (§3.12). This
chapter makes them writable **without** letting a single user-supplied URL into the system.

### 1.2 Decisions

Load-bearing section. Numbered `EXT-D1x`, mirroring the v1 §0 D-table (one-line ruling + why).

| # | Decision | Why / what it fixes |
|---|----------|---------------------|
| **EXT-D1a** | **Provider = Hetzner Object Storage** (S3-compatible, EU region), all access behind E0's `IBlobStore`; **Cloudflare R2** is the pre-approved fallback if egress cost ever dominates. | Hetzner Object Storage **is** the §6 Railway→Hetzner exit destination — blobs land in their final home on day one, so the cutover is "point DNS, restore the DB dump," with **zero blob migration**. The S3 API keeps R2 / MinIO a config swap, not a rewrite. 🔧 A managed provider's proprietary storage (Firebase/S3-only features) would have re-created the exact lock-in §6 spent its budget escaping. |
| **EXT-D1b** | **Blobs are referenced, never owned by Postgres** (X4). Rows hold metadata + an opaque `uuid` storage key; **zero `bytea`** payload in the DB. | Keeps the DB small and the managed backups cheap; lets blob lifetime (GDPR erase vs ledger retention) diverge per blob **class** (§1.6) instead of being chained to a row's `deleted_at`. |
| **EXT-D1c** | **Upload = presigned direct PUT, a 3-step dance:** `intent` → server writes a `pending` row + mints a presigned PUT to a **write-only staging key** (size + content-type baked into the signature) → client PUTs bytes straight to storage → `complete` → server verifies. **Client bytes never transit the API.** | The API never buffers an untrusted 10 MB body (Kestrel stays at the §5 256 KB JSON cap); storage does the heavy transfer. The staging key is the quarantine (EXT-D1d). |
| **EXT-D1d** | **The raw client upload lands in a `staging/` key that is NEVER served** to anyone; only the server **re-encoded clean object** (EXT-D1e) ever gets a presigned GET. | A malicious or polyglot upload is structurally unable to reach another user's device — there is no readable path to the bytes the client actually sent. |
| **EXT-D1e** | **Server-side decode → re-encode is the mandatory image security boundary** (E1 *and* E7): cap input pixels from the header **before** full decode (decompression-bomb guard) → sniff magic bytes (polyglot killer) → **strip all EXIF/GPS** → resize (avatars **512×512**, receipts bounded long-edge) → emit canonical **JPEG** → generate the thumbnail here. **Server input = JPEG + PNG only** (exactly what the §1.9 decoder handles natively); the default iOS capture format **HEIC is converted to JPEG on-device before upload** (Image I/O, trivial), so no HEIF decoder ever ships in the Linux container. **Provider image-transform features are rejected.** | Receipt photos geolocate — **EXIF GPS stripping is not optional**. Re-encoding is the one operation that simultaneously kills EXIF, polyglots, and appended payloads. 🔧 Provider transforms would re-introduce the EXT-D1a lock-in and break the exit path. 🔧 Constraining input to JPEG/PNG keeps the boundary dependency-free — the §1.9 decoder (ImageSharp) has no HEIF path, and pulling **libheif** into the container to accept the one format iOS converts for free would only add native attack surface. |
| **EXT-D1f** | **PDFs bypass re-encode** (no raster in v3): `%PDF-` magic-byte sniff + size cap + allowlist, **never rendered or executed**, no thumbnail (v3-optional: a *sandboxed* page-1 raster). **SVG is rejected outright.** | A PDF has no safe in-process decode boundary → treat it as an opaque, never-executed blob. SVG is an XSS/script vector and is not on the allowlist (EXT-D1o). |
| **EXT-D1g** | **Read = per-request presigned GET, short TTL (minutes), minted ONLY after the §4.2 membership check;** unguessable `uuid` keys; the bucket is **private** (no public objects, ever). | D6's "404 to non-members, no existence leak" **extends to storage** — a presigned GET is never minted for a non-member, and an unguessable key defeats enumeration even if a URL leaks after expiry. |
| **EXT-D1h** | **Blob deletion is two-phase** (X4): a `blob_deletions` row written **in the mutation's own transaction** → E0's deletion worker `DELETE`s the object → `HEAD`-404 verify → stamps `confirmed_at`. **At-least-once + idempotent** (X5; object `DELETE` of an already-gone key = success). | An object `DELETE` cannot be transactional with Postgres; a crash between "row tombstoned" and "object gone" must never orphan a blob. The deletion worker closes the gap and is safe to double-fire. |
| **EXT-D1i** | **Orphan sweep (E0 job):** `pending` attachment rows past the completion TTL, and abandoned staging objects, are reclaimed (bucket lifecycle rule for `staging/`; a `blob_deletions` row for any final key already written). A `pending` row emits **no** `change_log`. | A never-completed upload must not leak storage **or** half-appear on another device. Emitting sync state only at `ready` (EXT-D1k) keeps unverified bytes invisible. |
| **EXT-D1j** | **Attachments join the FULL X1 sync contract as their OWN entity** — new `change_log.entity_type = 'attachment'`, own `version`, own tombstone — **not** embedded in the §3.2 expense representation. | 🔧 If a receipt rode the expense aggregate, every upload would bump `expenses.version` and spuriously `412` a concurrent expense edit (D8). Decoupling keeps the money aggregate's version pure; the §3.2 wire shape is **unchanged** (additive, §3.11). |
| **EXT-D1k** | **Only the `pending → ready` transition emits the `attachment` upsert;** delete emits the tombstone. `pending` is silent. | A pending, unverified upload is not shareable state — surfacing it would show peers a broken thumbnail for a receipt that may never materialize. |
| **EXT-D1l** | **Avatars are mutable singletons on the reserved `*_url` columns** (§3.12), now **writable**, holding a **versioned** key `avatars/{ownerType}/{id}/{n}`; replace writes `n+1` and enqueues the old key (EXT-D1h); the client cache-busts on `n`. **No new sync entity** — member image rides the existing `member` upsert (§3.5.2), group icon rides `group`, the user avatar is `/me`-only. | Singleton overwrite (E7) vs append-only immutable receipts (E1) — the core E1↔E7 shape divergence. Riding the existing `member`/`group` sync entities means E7 adds **zero** `change_log` types. |
| **EXT-D1m** | **The reserved `*_url` columns store a SERVER-MINTED opaque storage key, never a client-supplied URL;** no image endpoint binds a URL or key from the request body. | 🔧 Preserves the §3.12 / §4.3 SSRF & content-injection guard ("no user-supplied URLs enter the system") **even though the columns became writable** — the client uploads bytes, the server owns the key. |
| **EXT-D1n** | **GDPR class divergence — the load-bearing E1↔E7 split.** **RECEIPT = group content:** survives the uploader's D7 anonymize (`uploaded_by → NULL`, blob intact), two-phase hard-deleted only when its parent expense stays soft-deleted **past retention** (Appendix A, 90 d — the **EXT-D1q receipt-reaper** owns this trigger) or by an admin on demand (the `DELETE` endpoint). **AVATAR = PII:** D7 anonymize **hard-deletes the blob** (two-phase, `reason='gdpr_erase'`) and nulls the column, inside the §4.4 transaction. | A receipt is the *other* members' financial record (D7 "retain the ledger," legitimate interest) and EXIF is already stripped (EXT-D1e), so no residual geo-PII. An avatar is plausibly the data subject's **face** → the strongest right (D7 "scrub PII") reaches it. |
| **EXT-D1o** | **Set-image authz extends the §3.1 self-only rule additively;** limits register in Appendix A. Self sets own user avatar + own member image; **admin/owner** sets a **ghost** member's image and the **group icon**; setting another **linked** member's image → `403`. Delete: **uploader or admin** (mirrors §3.6 void rights). Blob limits: **≤ 10 MB**, **≤ 5 attachments/expense**, allowlist `{image/jpeg, image/png, application/pdf}` (**no `image/heic`** — converted client-side before upload, EXT-D1e / §1.9), presigned PUT/GET TTL in minutes, completion/orphan TTL. Enforced at **both** presign (Content-Length range + Content-Type) **and** complete (`HEAD` + sniff). | Ghosts can't authenticate to set their own image, so an admin must; otherwise the §3.1 self-only floor holds and no user-supplied URL is trusted (EXT-D1m). Belt-and-suspenders limits close the gap between what the signature promises and what actually landed. |
| **EXT-D1q** | **Receipt-reaper (E1-owned `BackgroundService`) — the component that implements the EXT-D1n past-retention hard-delete.** Low-cadence scan of `attachments` (`status='ready'`) joined to `expenses` where `expenses.deleted_at < now() − <90 d retention>`; per row, in **one tx**: two-phase-enqueue `blob_deletions` (`reason='attachment_purged'`) for `storage_key` **and** `thumb_key` (when non-null), flip `status='deleted'` + stamp `deleted_at` + bump `version` (the `attachment` tombstone then rides `/sync`, EXT-D1j/k). Keys off the **durable** `expenses.deleted_at`, **not** `change_log` (pruned at 90 d). Idempotent (X5): the status flip drops the row from the scan set; `blob_deletions`'s PK makes a re-enqueue a no-op. | Without it, EXT-D1n's "receipt dies past retention" rule has **no owner** — v1 never hard-purges expenses (§1.3), so a receipt on a soft-deleted expense would live in storage **forever** (X4: unbounded growth, the exact hazard X4 exists to kill). 🔧 The FK `attachments.expense_id ON DELETE CASCADE` is **not** this mechanism: a raw cascade deletes the attachment **row** without a `blob_deletions` row, orphaning the object — so any **future** expense hard-purge path (§1.3 reserves one) MUST enqueue the attachment `blob_deletions` rows in the same tx **before** the cascade fires (an FK cascade cannot honor EXT-D1h's in-tx enqueue). |

### 1.3 Schema (DDL delta)

All additive (§3.11). No backfill — the reserved `*_url` columns were empty in v1.

```sql
-- (1) Extend the sync entity enum — additive; only NEW change_log rows use it (§3.5.1).
-- NOTE: Adds 'attachment' to the change_log.entity_type CHECK (v1 §3.5) — the CHECK's
--       authoritative full value set is consolidated in Appendix A.

-- (2) Receipt metadata. Bytes live in object storage (EXT-D1b); this row owns only the
--     opaque storage key + SERVER-VERIFIED metadata. Full X1 sync contract: client_id +
--     UNIQUE(group_id, client_id) idempotency (D9), version + If-Match (D8), soft-delete
--     tombstone surfaced in /sync (§3.5), membership check first (D6).
CREATE TABLE attachments (
    id           uuid PRIMARY KEY DEFAULT uuid_generate_v7(),
    group_id     uuid NOT NULL REFERENCES groups(id)   ON DELETE CASCADE,
    expense_id   uuid NOT NULL REFERENCES expenses(id) ON DELETE CASCADE,  -- receipts hang off an expense.
    -- ⚠ CASCADE is INERT in v1 (expenses are never hard-purged — §1.3). A FUTURE expense hard-purge MUST
    --   enqueue this attachment's blob_deletions row(s) in the same tx BEFORE the cascade fires (EXT-D1q):
    --   a raw FK cascade drops the attachment ROW without a blob_deletions row and orphans the object
    --   (EXT-D1h's in-tx enqueue cannot ride an FK cascade). The EXT-D1q reaper handles the v1 trigger.
    uploaded_by  uuid REFERENCES users(id) ON DELETE SET NULL,            -- D7: NULL on uploader anonymize; blob survives (EXT-D1n)
    client_id    text,                                     -- offline id → idempotency (D9 / X1)
    kind         text NOT NULL DEFAULT 'receipt'
                     CHECK (kind IN ('receipt')),          -- reserved for future classes (comment attachments, …) — additive
    storage_key  uuid NOT NULL UNIQUE,                     -- unguessable clean-object key (D6 extends to storage, EXT-D1g)
    thumb_key    uuid,                                     -- server-generated thumbnail; NULL for PDFs (EXT-D1f)
    mime         text NOT NULL
                     CHECK (mime IN ('image/jpeg','image/png','application/pdf')),  -- no image/heic: converted client-side (EXT-D1e/o, §1.9)
    size_bytes   bigint NOT NULL CHECK (size_bytes > 0),   -- CLEAN object size (server truth), not the client's claim
    sha256       bytea NOT NULL,                           -- hash of the CLEAN object, computed at complete
    width        int,                                      -- images only
    height       int,
    status       text NOT NULL DEFAULT 'pending'
                     CHECK (status IN ('pending','ready','deleted')),
    version      int NOT NULL DEFAULT 1,                   -- optimistic concurrency (D8 / X1)
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),       -- /sync cursor needs it (§3.5)
    deleted_at   timestamptz,                              -- soft-delete tombstone (X1)
    UNIQUE (group_id, client_id)                           -- per-group create idempotency (D9)
);
CREATE INDEX ix_attachments_expense ON attachments(expense_id)               WHERE deleted_at IS NULL;
CREATE INDEX ix_attachments_group   ON attachments(group_id, updated_at DESC);
CREATE INDEX ix_attachments_pending ON attachments(created_at)               WHERE status = 'pending';  -- orphan sweep target

-- Receipt-reaper (EXT-D1q) drives off soft-deleted PARENT expenses; bound its scan. Additive on the
-- existing expenses table (§3.11) — coordinate with the money / E-core cluster.
CREATE INDEX ix_expenses_deleted ON expenses(deleted_at) WHERE deleted_at IS NOT NULL;

-- (3) Two-phase blob-deletion ledger (X4). A row = "the object at storage_key must die."
--     Written in the SAME tx as the metadata tombstone / avatar overwrite / §4.4 anonymize;
--     E0's worker performs the object DELETE, HEAD-404 verifies, then stamps confirmed_at.
CREATE TABLE blob_deletions (
    storage_key  uuid PRIMARY KEY,                         -- the object to hard-delete
    reason       text NOT NULL
                     CHECK (reason IN ('attachment_purged','avatar_replaced','gdpr_erase','orphan')),
    requested_at timestamptz NOT NULL DEFAULT now(),
    confirmed_at timestamptz,                              -- set on DELETE + HEAD-404 verify (EXT-D1h)
    attempts     int NOT NULL DEFAULT 0
);
CREATE INDEX ix_blob_deletions_pending ON blob_deletions(requested_at) WHERE confirmed_at IS NULL;

-- (4) Avatars reuse the RESERVED v1 columns (§3.12), now writable and holding a versioned
--     storage KEY (EXT-D1l/m), NOT a URL:
--       users.avatar_url          -- already exists (§1.3) — un-reserved, no DDL
--       group_members.image_url   -- already exists (§1.3) — un-reserved, no DDL
--     groups had only icon_symbol, so add the parallel image column (additive — §3.11):
ALTER TABLE groups ADD COLUMN image_url text;
```

### 1.4 API surface

E0 provides the primitives: `IBlobStore.PresignPut(key, contentType, maxBytes, ttl)`,
`PresignGet(key, ttl)`, `Head(key)`, `Get(key)`, `Put(key, stream, contentType)`, `Delete(key)`,
plus the orphan-sweeper and blob-deletion `BackgroundService`s. E1/E7 own the tables, the
endpoints, the validators, the **re-encode/verify** logic (EXT-D1e), and the E1-owned **receipt-reaper**
`BackgroundService` (EXT-D1q — past-retention receipt hard-delete: expense-attachment business logic,
not generic blob infra, so it lives with E1, not E0).

**E1 — receipts** (all under an existing expense; `{g}`/`{e}` gated by the §4.2 membership check → non-member `404`, D6):

| Method | Path | Purpose | Auth |
|---|---|---|---|
| `POST` | `/groups/{g}/expenses/{e}/attachments` | Upload **intent** → `pending` row + presigned PUT to a staging key | member |
| `POST` | `/groups/{g}/expenses/{e}/attachments/{a}/complete` | **Finalize**: `HEAD` + sniff + re-encode + thumbnail → `ready`; `If-Match` | member |
| `GET` | `/groups/{g}/expenses/{e}/attachments` | List an expense's attachments (metadata only; no URLs) | member |
| `GET` | `/groups/{g}/expenses/{e}/attachments/{a}/url` | Mint short-TTL presigned GET(s) — full + thumb | member |
| `DELETE` | `/groups/{g}/expenses/{e}/attachments/{a}` | Soft-delete (tombstone) + enqueue two-phase blob delete; `If-Match` | uploader **or** admin |

**E7 — avatars** (singletons on `*_url`; reads 302-redirect to a freshly minted presigned GET, so an `<img>` tag can hit them directly):

| Method | Path | Purpose | Auth |
|---|---|---|---|
| `POST` / `POST` | `/me/avatar` · `/me/avatar/complete` | Intent / finalize own **user avatar** | self |
| `GET` / `DELETE` | `/me/avatar` | `302` → presigned GET / remove (null column + two-phase delete) | self |
| `POST` / `POST` | `/groups/{g}/members/{m}/image` · `…/complete` | Intent / finalize a **member image** — self, or a **ghost's** by an admin (EXT-D1o) | self or admin-for-ghost |
| `GET` / `DELETE` | `/groups/{g}/members/{m}/image` | `302` → presigned GET / remove | member (read) · self-or-admin (delete) |
| `POST` / `POST` | `/groups/{g}/icon` · `…/complete` | Intent / finalize the **group icon** image | admin |
| `GET` / `DELETE` | `/groups/{g}/icon` | `302` → presigned GET / remove | member (read) · admin (delete) |

**Worked example — receipt (the 3-step dance).** Money note: attachments carry **no** money, so
D1's string-minor-units rule and X3's converted-money quarantine **do not engage here**; byte
counts and pixel dimensions travel as plain JSON integers (the `basisPoints` precedent, §3.2).

```jsonc
// (1) POST /groups/grp_7Hk2/expenses/exp_9Qm4/attachments
{
  "clientId": "att_local_3F1A",     // → (groupId, clientId) idempotency (D9 / X1)
  "mime": "image/jpeg",            // iOS converted the camera HEIC → JPEG on-device (EXT-D1e); the allowlist has no image/heic
  "sizeBytes": 4193021,             // claimed; the presigned PUT pins Content-Length to it
  "sha256": "e3b0c44298fc1c149afbf4c8996fb924…"   // claimed hash of the RAW bytes (verified in transit, EXT-D1o)
}
```
```jsonc
// 201 Created   ETag: "1"
{
  "id": "att_5Kp2", "clientId": "att_local_3F1A", "expenseId": "exp_9Qm4",
  "status": "pending", "version": 1,
  "upload": {                        // presigned PUT straight to storage — bytes never touch the API (EXT-D1c)
    "method": "PUT",
    "url": "https://fsn1.your-objectstorage.com/staging/att_5Kp2?X-Amz-Signature=…",
    "expiresAt": "2026-07-07T09:20:00Z",
    "headers": { "Content-Type": "image/jpeg", "Content-Length": "4193021" }
  }
}
```
```jsonc
// (2) client PUTs the bytes to upload.url, then:
// (3) POST /groups/grp_7Hk2/expenses/exp_9Qm4/attachments/att_5Kp2/complete   If-Match: "1"   (empty body)
// 200 OK   ETag: "2"
{
  "id": "att_5Kp2", "expenseId": "exp_9Qm4", "kind": "receipt", "status": "ready",
  "mime": "image/jpeg",             // canonicalized JPEG — EXIF/GPS stripped, resized, re-encoded server-side (EXT-D1e)
  "sizeBytes": 1880422,             // CLEAN object size (server truth), not the claimed 4193021
  "sha256": "9f2cb1e4…",            // hash of the CLEAN object
  "width": 1600, "height": 2131,
  "version": 2, "deleted": false
}
```
```jsonc
// GET /groups/grp_7Hk2/expenses/exp_9Qm4/attachments/att_5Kp2/url   → 200
{
  "full":  { "url": "https://fsn1.your-objectstorage.com/att_5Kp2?X-Amz-Signature=…",       "expiresAt": "2026-07-07T09:25:00Z" },
  "thumb": { "url": "https://fsn1.your-objectstorage.com/att_5Kp2_thumb?X-Amz-Signature=…", "expiresAt": "2026-07-07T09:25:00Z" }
}
```

### 1.5 Sync & offline impact (X1 checklist)

- **Flows through `/sync`?** **Yes** — receipt thumbnails are rendered offline, so attachment
  metadata is client-cached and joins the full sync contract (X1).
- **`change_log` entity_type:** new **`attachment`** (§1.3 DDL (1); additive, §3.11). Append rules
  extend the §3.5.2 table:

  | Mutation | Rows appended |
  |---|---|
  | attachment `complete` (`pending → ready`) | `attachment` **upsert** |
  | attachment intent (`pending`) | **none** — silent until verified (EXT-D1k) |
  | attachment soft-delete (`DELETE`, admin/uploader on demand) | `attachment` **delete** (tombstone) |
  | receipt-reaper purge (parent expense soft-deleted past retention — EXT-D1q) | `attachment` **delete** (tombstone), one per reaped receipt |
  | member image set / clear | `member` **upsert** (rides the existing entity — EXT-D1l) |
  | group icon set / clear | `group` **upsert** |

- **Wire payload (`type: "attachment"`)** carries metadata only — **never a presigned URL**.
  🔧 A signed URL is credential-bearing and expires in minutes; it is not durable sync state.
  The client stores the metadata and mints a fresh GET via `…/attachments/{a}/url` on demand,
  caching by `id`+`version`.

  ```jsonc
  { "type": "attachment", "id": "att_5Kp2", "groupId": "grp_7Hk2", "deleted": false,
    "data": { "expenseId": "exp_9Qm4", "kind": "receipt", "mime": "image/jpeg",
              "sizeBytes": 1880422, "sha256": "9f2cb1e4…",
              "width": 1600, "height": 2131, "status": "ready", "version": 2 } }
  ```
  Tombstone: `deleted: true`, `data: null` — the client soft-deletes locally (§3.5.4 apply rules),
  removing the thumbnail. Avatars carry **no** attachment entity; the current key/version appears
  inside the `member`/`group` `data` block (an added optional field — additive), and the read
  endpoints 302 to a presigned GET.
- **Bootstrap (§3.5.5):** add attachments to the per-group REST fetch (step 2) — `GET …/expenses/{e}/attachments`
  during re-bootstrap and on the join/claim per-group fetch. Avatars need no extra call — the
  key rides the member/group rows already fetched.
- **Offline outbox ordering (EXT-D1p):** the expense create syncs **first** (D9-idempotent); the
  attachment is a **dependent** outbox entry keyed on the expense's server `id`, itself a 3-step
  (intent → PUT → complete) sub-sequence.
  - Expense create returned **`409 client_id_conflict`** → the outbox pulls `existingId` (§3.4)
    and re-parents the attachment to it — the expense exists either way.
  - Expense `PUT` `412` later → irrelevant; the attachment references a stable `expense_id`.
  - Expense **soft-deleted before the attachment reaches `ready`** → `complete` returns
    **`409 parent_gone`**; the outbox **drops** the attachment (parent-gone = success, mirroring
    §3.2b's "`DELETE`+`404` = success"), and the orphan sweeper reclaims the staging/pending blob.
  - Retried intent with the same `(group_id, client_id)` → the existing `pending`/`ready` row
    (D9), plus a **fresh** presigned PUT if still `pending`.

### 1.6 GDPR / privacy impact (D7 / X4 checklist)

**Two blob classes, deliberately divergent (EXT-D1n) — this is why E1 and E7 share a pipeline
but not a policy:**

| | **Receipt (E1)** | **Avatar (E7)** |
|---|---|---|
| Class | group content (other members' financial record) | data-subject PII (plausibly a face) |
| On uploader **anonymize** (D7, §4.4) | **retained** — `uploaded_by → NULL`, blob intact; EXIF already stripped (EXT-D1e), no residual geo-PII | **hard-deleted** — blob enqueued (`reason='gdpr_erase'`), column nulled |
| Hard-delete trigger | parent expense soft-deleted **past retention** (Appendix A, 90 d) → **EXT-D1q receipt-reaper** *or* admin `DELETE` on demand | the D7 anonymize itself |
| On voluntary **leave** (§3.8.3) | n/a | **retained** — leave keeps `image_url` (§3.8.3 ruling); the blob survives, dies only on erase |

**§4.4 anonymize-transaction hooks (additive, coordinate with the auth/GDPR cluster).** Each PII
image write in the existing transaction also enqueues a `blob_deletions` row (worker deletes
post-commit — network I/O outside the tx, the same two-phase shape as the §4.4 step-9 Apple-revoke
outbox):

- **step 2** (user row scrub): `avatar_url` already nulled → enqueue its key if non-null.
- **step 5** (unlinked member rows, `WHERE user_id = @uid`): `image_url` already nulled → enqueue.
- **step 6** (previously-left rows, `WHERE former_user_id = @uid`): `image_url` already nulled → enqueue.

Receipts are untouched by all three (their `uploaded_by → NULL` is the §4.4 step-8 shape:
`ON DELETE SET NULL` made explicit).

**`GET /me/export` (§4.4).** Lists attachment **metadata** for every expense the user participates
in, plus time-limited presigned GETs for the user's **own** uploads and their own avatar. Other
members' receipt **bytes** are referenced by metadata, not inlined — inlining megabytes would break
§4.4's synchronous-JSON-no-job-queue constraint; the redaction posture ("nothing the in-app UI
wouldn't show") is unchanged, since a shared receipt is already visible in-app.

**PII inventory:** EXIF **GPS** in receipt photos (stripped, mandatory — EXT-D1e); avatar =
**face** (erased on D7). Storage keys are random `uuid`s (no PII). Extend §4.3 logging hygiene:
**presigned URLs are credential-bearing — never logged at any level;** keys and `sha256` are
ids-only material, INFO-safe.

### 1.7 Security notes (MANDATORY review — X6 · D6 · over-posting · rate limits)

**New unauthenticated surfaces = the presigned PUT and GET URLs (X6).** Each gets its own
posture:

- **Presigned PUT** — Content-Length range **and** Content-Type pinned into the signature
  (a body over the cap is rejected by storage, not by the API); target is a **write-only
  `staging/` key** that no GET is ever signed for (EXT-D1d); TTL in minutes; logically single-use
  — `complete` re-encodes from a snapshot, so a second PUT after `complete` changes nothing served.
- **Presigned GET** — per-request, minted **only after the §4.2 membership check** (D6 404-no-leak
  extends to storage); short TTL; unguessable `uuidv7` key defeats enumeration even post-expiry;
  **private bucket, zero public objects**.

**The re-encode is the boundary (EXT-D1e), restated as the review's core control:**

- **Decompression bomb** — read dimensions from the header and reject on a pixel-count cap
  **before** allocating the decode buffer (ImageSharp `Configuration` guard); a 0.5 MB PNG that
  decodes to 100k×100k never allocates.
- **Polyglot / GIFAR** — content-type is **sniffed from magic bytes**, never trusted from the
  client header or the file extension; decode+re-encode discards any appended ZIP/HTML payload.
- **EXIF / GPS** — re-encode drops **all** metadata segments; asserted by the §1.8 property test.
- **MIME allowlist enforced twice** — at presign and at `complete`; **SVG rejected** (script
  vector). PDFs are **never rendered or executed** (EXT-D1f) — `%PDF-` sniff, size cap, opaque
  store; a v3 page-1 raster, if built, runs in a sandboxed subprocess only.

**AuthZ (D6) & over-posting:**

- Every attachment/image endpoint runs the §4.2 membership-first check → non-member `404`; a
  receipt on another group's expense → `404`. The `…/url` mint is behind the same check, so a
  presigned GET is minted only for a proven member.
- Attachment `DELETE`: **uploader or admin** (mirrors §3.6 void rights); a member without rights →
  `403 attachment_delete_forbidden`.
- **Image URLs remain NOT bindable** on the `member`/`group` `PATCH` DTOs (the §3.12 / §4.3
  over-posting guard stands); the **only** way to set an image is the dedicated intent/complete
  endpoints, and the client **never supplies a URL or key** (EXT-D1m) — `uploaded_by`, `group_id`,
  `expense_id`, and the storage key are all server-derived from the route + auth, never the body,
  so there is no cross-tenant or SSRF injection vector.
- **Ghost image (E7):** self sets own member image; an **admin/owner** may set a **ghost's**
  (unlinked, can't authenticate); another **linked** member's image by anyone but that member →
  `403 insufficient_role`. Group icon → admin/owner only.

**Rate limits — new Appendix A partitions (X6: every new surface its own partition):**

| Limit | v3 value | Notes |
|---|---|---|
| attachment/avatar **intent** (presign PUT) | 30 / min / user | mints storage write credentials |
| attachment **complete** (re-encode) | 20 / min / user | CPU-bound; plus a **global concurrency cap** on the re-encode worker (decode-DoS ceiling) |
| **url** mint (presigned GET) | 60 / min / user | cheap, read-path |
| avatar set (`/me/avatar`, member image, icon) | 10 / day / user | avatars change rarely |
| blob size | ≤ 10 MB | per blob (EXT-D1o) |
| attachments per expense | ≤ 5 | `409 limit_exceeded` on the 6th |

### 1.8 Test plan

**Money invariant (the one that matters even though the feature carries no money):** every
attachment/avatar operation is **balance-neutral** — property-test that creating, completing, or
deleting a receipt, and any avatar op, leaves **`Σ net == 0` per currency** (§2.2) and every
member net **byte-identical** to before. Converted money never enters this feature, so there is
nothing for it to leak into the ledger — the test proves the isolation.

**Property cases (FsCheck + a fixture corpus):**

- **EXIF strip:** over a corpus of geotagged JPEGs (the client converts HEIC → JPEG on-device before
  upload, EXT-D1e), the stored clean object has **zero** EXIF / GPS segments.
- **Hash truth:** `attachments.sha256` == `sha256(clean stored object)`; the client's claimed raw
  hash is verified in transit → mismatch = `422 checksum_mismatch`.
- **Polyglot:** valid-JPEG-prefix + appended payload → after re-encode the trailing bytes are gone;
  a header/extension lie is caught by the magic-byte sniff at `complete`.
- **Decompression bomb:** a small file with a huge declared canvas → rejected pre-decode, no OOM.
- **Idempotency (D9 / X1):** replayed intent `(group_id, client_id)` → same row, no duplicate;
  replayed `complete` on a `ready` row → `200`, `ETag` unchanged.
- **Membership (D6):** non-member `GET …/url` → `404`; expired presigned URL → storage `403`, no
  metadata leak.
- **Two-phase delete (EXT-D1h / X5):** `DELETE` → tombstone + `blob_deletions` row; worker
  `DELETE` + `HEAD`-404 → `confirmed_at` set; crash between phases → sweeper retries; object
  `DELETE` of an already-gone key = success.
- **Orphan sweep:** intent without `complete` past TTL → `pending` row + staging blob reclaimed;
  **no** `ready` row, **no** `change_log` ever emitted.
- **Parent-gone (EXT-D1p):** expense soft-deleted before `complete` → `409 parent_gone`; no `ready`
  attachment; blob reclaimed.
- **GDPR divergence (EXT-D1n):** anonymize a user → their avatar blob enqueued + column nulled;
  their uploaded **receipts survive** (`uploaded_by` NULL, blob intact). Assert exactly this split.
- **Receipt-reaper (EXT-D1q):** a `ready` receipt whose parent expense was soft-deleted **> retention**
  ago → reaper enqueues `blob_deletions(reason='attachment_purged')` for the full **and** thumb keys,
  flips the attachment to `deleted`, emits the `/sync` tombstone; a receipt whose expense was
  soft-deleted **< retention** ago is **untouched**; a receipt on a **live** (non-deleted) expense is
  never reaped; a second reaper pass over the same rows is a **no-op** (idempotent — status flip drops
  them from the scan, `blob_deletions` PK absorbs the re-enqueue).

**Example cases:** JPEG/PNG canonicalized to a bounded-dimension JPEG + thumbnail; **`image/heic`
intent → `415`** (client must convert on-device first — EXT-D1e/o); PDF stored with `%PDF-` sniff,
no re-encode, no thumb (v3); 5th attachment OK / 6th `409 limit_exceeded`; 11 MB PUT rejected by the
Content-Length range; SVG → `415 unsupported_media_type`; ghost image set by admin OK / by
non-admin-non-self `403`; another linked member's image `403`; avatar replace → new versioned key,
old key enqueued, cache-bust on `n`; leave retains the member image, erase removes it (§3.8.3 /
§4.4).

**New problem codes (register in the Appendix B extensions catalog; additive, §3.11):**

| `code` | HTTP | Meaning |
|---|---|---|
| `unsupported_media_type` | 415 | `mime` not on the EXT-D1o allowlist (incl. SVG) |
| `blob_too_large` | 413 | Uploaded object over the 10 MB cap (caught at `complete` `HEAD`) |
| `checksum_mismatch` | 422 | Client's claimed raw `sha256` ≠ the uploaded object's |
| `upload_incomplete` | 409 | `complete` called before the staging object exists (`HEAD` 404) |
| `parent_gone` | 409 | Parent expense tombstoned before `complete` (EXT-D1p) |
| `attachment_limit_exceeded` | 409 | 6th attachment on one expense (or reuse `limit_exceeded`) |
| `attachment_delete_forbidden` | 403 | Non-uploader, non-admin attempts a receipt delete |

`insufficient_role` (existing, Appendix B) covers the ghost/linked-member/icon authz cases.

### 1.9 Dependencies · tier · effort

- **Hard prerequisite — E0 (blob infrastructure):** `IBlobStore` (Hetzner Object Storage / S3
  driver, private bucket, EU region), the presigned-URL helpers, the **orphan sweeper**, and the
  **blob-deletion worker**. This chapter designs E1/E7 *on top of* E0; it does not design E0.
- **Library:** **ImageSharp (Six Labors)** for decode/re-encode/thumbnail. It decodes **JPEG/PNG**
  natively but has **no HEIF/HEIC decoder** (libheif is not bundled) — which is exactly why the
  EXT-D1o allowlist carries **no `image/heic`**: the iOS client converts camera HEIC → JPEG on-device
  before the upload intent (EXT-D1e), keeping libheif and its native attack surface **out** of the
  Linux (Railway/Hetzner) container. ⚠️ Six Labors' Split License may require a **commercial license**
  at scale — flag for procurement; `SkiaSharp` or `Magick.NET` are drop-in alternatives behind the
  same re-encode interface (SkiaSharp's HEIF path is platform-codec-dependent and unreliable on Linux;
  Magick.NET decodes HEIC only against a libheif-enabled ImageMagick build — so **accepting HEIC
  server-side would be a deliberate libheif dependency add, not assumed here**). PDF page-1 raster
  (v3-optional) would add a sandboxed renderer (Docnet/PDFium) — **deferred**.
- **Cross-feature coordination (all additive):** §4.4 anonymize gains three avatar-blob enqueue
  hooks (auth/GDPR cluster); §3.5.5 bootstrap gains a per-expense attachment fetch; §3.5.2 append
  table gains the `attachment` rows; Appendix A gains the §1.7 constants; Appendix B gains the §1.8
  codes; `change_log`'s CHECK is extended; `groups.image_url` is added; the E1 **receipt-reaper**
  `BackgroundService` (EXT-D1q) joins the job set, and a partial index `ix_expenses_deleted` on
  `expenses(deleted_at)` is added (additive) to bound its scan.
- **Rollout:** expand/contract migration (§6) — new tables/columns + CHECK extension, **no
  backfill** (reserved columns were empty). Fully within `/v1` (§3.11) — **no `/v2`**.
- **Tier: v3 · Effort: L** (E0 blob pipeline + server-side re-encode boundary + two blob-lifecycle
  policies + the receipt-reaper + the offline outbox dependency chain are the weight; the endpoints
  themselves are thin).


## 2. E2 — Live FX (ČNB feed, CZK pivot)

> Governed by **X3** (converted money is structurally quarantined) and **X2** (no second
> authoritative store), respecting **D5** (per‑currency settle only; FX convergence deferred).
> Mandatory review gate: **money‑correctness** — the whole risk of this chapter is display money
> leaking into a ledger path.

### 2.1 Scope & user value

Replace the iOS client's static `FXRates` (a hard‑coded rate table — §1.2, §7) with a
**server‑authoritative daily rate feed** from the Czech National Bank (ČNB), so a mixed‑currency
group can *see* every bucket rendered in one familiar currency (CZK) without ever changing what is
owed. **Display‑only:** conversion is presentation, never ledger — native per‑currency buckets stay
the single truth (D5). E2 also lays the **immutable, date‑pinned, provenance‑stamped rate table**
that the deferred D5 cross‑currency *settlement convergence* will one day snapshot — and stops
there, building the seam, not the feature. **Tier v3, effort M.**

🔧 Closes the drift defect in v1 §1.2/§7: the client `FXRates` is a static constant baked into each
app build — it goes stale the day it ships and silently disagrees between app versions, so two
members on different releases see different converted totals. Making the server the one authority
(as it already is for balances, D4) ends that.

### 2.2 Decisions

Numbered `EXT‑D2<n>`; each a binding ruling + what it fixes, in the §0 D‑table style.

| # | Ruling | Why / what it fixes |
|---|--------|---------------------|
| **EXT‑D2a** | **FX is display‑only.** Converted money is a distinct domain type `DisplayMoney` (§2.4) with **no** constructor, operator, or method that yields a `Money` (§2.1). It cannot reach `BalanceCalculator`, `DebtSimplifier` (§2.4), `expense_splits`, or a `settlement`. Its only sink is the JSON `display` block. | X3, made structural. A converted number that *can* be mistaken for a ledger amount eventually **will** be. Type‑level separation makes the mistake unrepresentable — the review gate's core assertion. |
| **EXT‑D2b** | **The ČNB feed is the one rate authority; the client `FXRates` table is retired** to a disposable local cache of `GET /fx/rates`. | Ends the per‑build drift of §1.2/§7. Same posture as balances: server authoritative, client mirrors (D4). |
| **EXT‑D2c** | **CZK is the pivot.** All fixings are foreign→CZK (as ČNB publishes); any cross rate X→Y is folded through CZK. v1 quote is **CZK only**. | Matches the source and the product (a Czech app). One pivot, no dense rate matrix, no second store (X2). |
| **EXT‑D2d** | **Rates are stored as integers exactly as ČNB publishes** — `(per_amount, rate_scaled, scale)` — never a float, never `NUMERIC`. `24.315 CZK/EUR` → `per_amount=1, rate_scaled=24315, scale=3`. | The D1 no‑float discipline, extended to rates. Rates are not money, but they will feed D5 convergence and must be reproducible bit‑for‑bit years later. |
| **EXT‑D2e** | **`fx_rates` is APPEND‑ONLY and IMMUTABLE once written.** Writes are `INSERT … ON CONFLICT (currency, valid_on) DO NOTHING`; no UPDATE/DELETE path exists in app code. A later ČNB **revision of a past fixing is ignored** — the first‑fetched value stands; `source`/`fetched_at` record its provenance. | X2/D4: read‑time lookup is the **single deterministic** truth. A mutable rate is a stored balance by another name — it would silently re‑price historical displays and, later, break a snapshot the convergence feature already took. |
| **EXT‑D2f** | **Expense‑level display pins to the expense's own `expense_date`,** with **previous‑business‑day fallback** (ČNB skips weekends and Czech holidays): `WHERE currency=@c AND valid_on <= @date ORDER BY valid_on DESC LIMIT 1`. | The declared date is stable and matches the mental model "the rate on the day I spent it." Fallback absorbs ČNB's non‑publishing days without inventing a rate. |
| **EXT‑D2g** | **Balance‑level display converts each per‑currency net at the LATEST available fixing** and exposes an optional per‑member `displayTotal`. | A net is a *present‑time* quantity ("what I'm owed **now**") — a today‑rate question, not a per‑expense one. The single `rateDate` on the response says which fixing was used. |
| **EXT‑D2h** | **An amount with no fixing on or before its pin date gets NO `display` block** (the sub‑object is omitted); the response is still `200` and native fields are untouched. | Never fail a whole list over one unconvertible historical row (an expense predating the earliest backfilled fixing, §2.7); never fabricate a rate. |
| **EXT‑D2i** | **Exactly one rounding, at the display edge** — banker's/`ToEven`, matching `Money.FromDecimal` (§2.1); `Int128` intermediate. Everything before it is integer‑exact. | One auditable rounding site. `ToEven` is already the house rounding, so client preview and server display agree. |
| **EXT‑D2j** | **Converted totals do NOT zero‑sum; only native per‑currency buckets do (D5).** `displayTotal` and any converted `display.amount` carry **zero settlement authority**. | Per‑member display‑edge rounding leaves a bounded residual (Σ ≤ member count, §2.8). Stating it loudly is what stops someone settling on a converted figure — the exact X3 failure. |
| **EXT‑D2k** | **The feed runs on the E0 jobs substrate (§0.4.2):** a `fx.poll` job at ~14:45 Europe/Prague on ČNB business days (backoff‑retry until published), a `> 3 business‑day` staleness alert, and a one‑shot rate‑limited `fx.backfill` from ČNB annual files. All writes `ON CONFLICT DO NOTHING`, so a double‑fire is a no‑op. | X5 idempotence for free (E0's deterministic contract). No new stateful dependency — Postgres stays the only one (§6). |
| **EXT‑D2l** | **Scope fence for deferred D5.** E2 does **not** net across currencies, **not** settle a EUR debt in CZK, **not** add FX to `DebtSimplifier`. It ships **only** the interface a future convergence feature needs — an immutable rate‑by‑date + provenance (`IFxRateProvider`, §2.9) — and adds **no** columns to `settlements`. | Keeps the D5 deferral honest. The seam is the immutable `(currency, valid_on)` natural key a later settlement snapshot will reference; building more now is speculative (YAGNI) and risks a second store (X2). |
| **EXT‑D2m** | **`GET /fx/rates` and `?display=` are AUTHENTICATED — no new unauthenticated surface** (X6 N/A by construction). The outbound ČNB fetch targets an **allowlisted host** (`cnb.cz`) only, never a user URL. | Fewer public surfaces to harden; SSRF and over‑posting closed structurally (no user‑supplied URL, no user write path into `fx_rates`). |

### 2.3 Schema (DDL delta)

```sql
-- Append-only, immutable ČNB daily fixings. The CZK pivot leg is IMPLICIT (1 CZK = 1 CZK,
-- EXT-D2c) and never stored — ČNB does not publish a CZK→CZK row and neither do we.
CREATE TABLE fx_rates (
    currency    char(3)  NOT NULL REFERENCES currencies(code),   -- foreign leg (EUR|USD|GBP in v1's closed set)
    valid_on    date     NOT NULL,                               -- the fixing date (ČNB working day)
    per_amount  int      NOT NULL CHECK (per_amount > 0),        -- ČNB 'Amount'/'množství': 1 for EUR/USD/GBP, 100 for e.g. HUF/JPY
    rate_scaled bigint   NOT NULL CHECK (rate_scaled > 0),       -- ČNB 'Rate' × 10^scale as an integer (24.315 → 24315)
    scale       smallint NOT NULL DEFAULT 3 CHECK (scale BETWEEN 0 AND 6),  -- ČNB publishes 3 decimals
    source      text     NOT NULL,                               -- provenance: 'cnb.daily#130' | 'cnb.year.2019' | 'cnb.backfill'
    fetched_at  timestamptz NOT NULL DEFAULT now(),              -- FIRST-fetch time; a later revision is ignored (EXT-D2e)
    PRIMARY KEY (currency, valid_on)
);
-- No extra index: the PK btree (currency, valid_on) serves the previous-business-day fallback
-- `valid_on <= @d ORDER BY valid_on DESC LIMIT 1` (EXT-D2f) and the latest-fixing lookup
-- (EXT-D2g) via a backward scan — same discipline as §1.4's "no ix_change_log_seq".

-- Days ČNB is CLOSED beyond weekends (weekends are derived in code). Drives BOTH the
-- scheduler (don't poll / don't expect a fixing) and the staleness alert (EXT-D2k), so a
-- Czech public holiday never false-pages. Seeded years ahead; audit-visible.
CREATE TABLE fx_source_holidays (
    holiday_on date PRIMARY KEY,
    label      text NOT NULL                                     -- e.g. 'Státní svátek — 28.9.'
);
```

Deliberately **not** added: no column on `expenses`, `settlements`, or any balance path. The rate a
display uses is *looked up*, never *stored on the money row* (X2). No `version`, no `client_id`, no
`deleted_at` — `fx_rates` is neither client‑editable nor group‑scoped (X1 N/A, §2.5).

### 2.4 API surface

| Method | Path | Purpose | Auth |
|---|---|---|---|
| `GET` | `/fx/rates?date=<yyyy-MM-dd>&quote=CZK` | Read the pinned fixings for a date (previous‑business‑day fallback applied); feeds the client's disposable local rate cache | Yes |
| `GET` | `/groups/{g}/balances?display=CZK` | Balances (§3.3) **+** a quarantined `display` block per non‑CZK bucket and an optional per‑member `displayTotal` | Yes |
| `GET` | `/groups/{g}/expenses?display=CZK` | Expense list (§3.2) **+** a quarantined `display` block per item, pinned to each item's `expense_date` (EXT‑D2f) | Yes |

`display`/`quote` accept **`CZK` only** in v1 (the pivot, EXT‑D2c); any other value → `422
validation_failed` (field `display`) — no new problem code (§3.4, Appendix B unchanged). Omitting
`?display` returns the **exact v1 response** (the block is purely additive, §3.11).

**Worked example — the money‑correctness demonstration.** A group with a EUR bucket split four ways
(native nets sum to `"0"` exactly, D5) rendered in CZK at the 2026‑07‑03 fixing (`1 EUR = 24.315
CZK` → `per_amount=1, rate_scaled=24315, scale=3`):

```jsonc
// GET /groups/grp_KrkA/balances?display=CZK          (no body; D6 membership check first)
{
  "groupId": "grp_KrkA",
  "displayQuote": "CZK",                 // echoes ?display; ABSENT when not requested
  "rateDate": "2026-07-03",              // the single latest fixing used for balance-level conversion (EXT-D2g)
  "rateSource": "cnb",                   // rate provenance (X3)
  "byCurrency": [
    {
      "currency": "EUR",
      "balances": [
        { "memberId": "mem_alice", "net": "10000",                       // authoritative: 100.00 EUR (D1)
          "display": { "currency": "CZK", "amount": "2432",  "approx": true, "rateDate": "2026-07-03" } },
        { "memberId": "mem_bob",   "net": "-3333",
          "display": { "currency": "CZK", "amount": "-810",  "approx": true, "rateDate": "2026-07-03" } },
        { "memberId": "mem_carol", "net": "-3333",
          "display": { "currency": "CZK", "amount": "-810",  "approx": true, "rateDate": "2026-07-03" } },
        { "memberId": "mem_dave",  "net": "-3334",
          "display": { "currency": "CZK", "amount": "-811",  "approx": true, "rateDate": "2026-07-03" } }
      ],
      "simplified": [                    // DebtSimplifier (§2.4) runs on NATIVE EUR only — never FX (D5, EXT-D2l)
        { "from": "mem_bob",   "to": "mem_alice", "amount": "3333", "currency": "EUR" },
        { "from": "mem_carol", "to": "mem_alice", "amount": "3333", "currency": "EUR" },
        { "from": "mem_dave",  "to": "mem_alice", "amount": "3334", "currency": "EUR" }
      ]
    }
    // a CZK bucket, if present, carries NO `display` sub-blocks: bucket currency == quote (EXT-D2h family)
  ]
}
```

- **Σ native `net` (EUR) = `"0"` exactly** — D5's client‑checkable invariant (§3.3) is untouched by
  `?display`.
- **Σ the CZK `display.amount` = `+1`, NOT `0`** (`2432 − 810 − 810 − 811 = 1`). This is *expected*
  and *correct*: converted money is approximate and rounded per member at the display edge
  (EXT‑D2i/D2j). It never settles; `simplified` above proves settlement stays in native EUR.

`GET /fx/rates` returns the rate table shape the client caches (integers exactly as ČNB publishes,
EXT‑D2d):

```jsonc
// GET /fx/rates?date=2026-07-04&quote=CZK       (Saturday → fallback to Fri 2026-07-03, EXT-D2f)
{ "quote": "CZK", "requestedDate": "2026-07-04", "rateDate": "2026-07-03", "source": "cnb",
  "rates": [
    { "currency": "EUR", "perAmount": 1, "rateScaled": "24315", "scale": 3, "validOn": "2026-07-03" },
    { "currency": "USD", "perAmount": 1, "rateScaled": "20789", "scale": 3, "validOn": "2026-07-03" },
    { "currency": "GBP", "perAmount": 1, "rateScaled": "28456", "scale": 3, "validOn": "2026-07-03" } ] }
```
`rateScaled` travels as a JSON **string** (it is a scaled integer, and the D1 discipline — never a
float on the wire — applies to any scaled quantity); `perAmount`/`scale` are small plain integers.

**Conversion — integer‑exact, one rounding (EXT‑D2i).** Mirrors the §2.3 `Int128` guard:

```csharp
// A DELIBERATELY DIFFERENT TYPE from Money (§2.1). Nothing converts DisplayMoney → Money;
// it has no path into BalanceCalculator / DebtSimplifier / expense_splits / settlements (EXT-D2a, X3).
public readonly record struct DisplayMoney(long Minor, Currency Quote, DateOnly RateDate, string Source);
public readonly record struct FxRate(int PerAmount, long RateScaled, int Scale, DateOnly ValidOn, string Source);

// native (minor units of X) → CZK pivot minor units (CZK has 0 minor units, D2). ČNB says:
//   PerAmount units of X  =  RateScaled / 10^Scale  CZK.  So:
//   czk = amountMinor / 10^decX  ×  RateScaled / (PerAmount · 10^Scale)   (× 10^0 for CZK)
static long ToPivotMinor(long amountMinor, int decX, FxRate r)
{
    Int128 num = (Int128)amountMinor * r.RateScaled;                       // exact
    Int128 den = (Int128)r.PerAmount * Pow10(decX) * Pow10(r.Scale);       // exact
    return (long)RoundHalfToEven(num, den);                               // the ONE rounding, at the display edge
}
// General cross rate X→Y is folded into ONE fraction so there is still exactly one rounding —
// displayY = amountX·rX·perY·10^decY / (perX·rY·10^decX); the 10^Scale legs cancel at equal scale.
// v1 never calls it (quote = CZK only, EXT-D2c) — it exists for the deferred D5 convergence seam (§2.9).
```

### 2.5 Sync & offline impact

X1 checklist — **`fx_rates` does not join the sync contract, by design:**

- **Flows through `/sync`? No.** It is neither client‑editable nor group‑scoped — it is global public
  reference data, like `currencies` (§3.5 "Deliberately not synced").
- **`change_log` entity_type? None added.** No new enum member; nothing to reconcile per group.
- **Tombstones? None.** The table is append‑only and immutable (EXT‑D2e); rows are never deleted, so
  there is no tombstone to surface.
- **Bootstrap / caching.** The client fetches `GET /fx/rates` (ETag/date‑cacheable, like
  `/currencies`) into a **disposable** local table and refreshes it opportunistically. Losing that
  cache costs nothing — it is re‑fetchable and authoritative on the server (X2, D4).
- **The `display` block itself is computed server‑side on demand** and **never stored**, so it never
  enters the delta stream or the client's SwiftData mirror as durable state — it is recomputed on
  each read, exactly like `simplified` debts (§3.3, X2).

### 2.6 GDPR / privacy impact

D7/X4 checklist:

- **On user anonymize (`DELETE /me`, §4.4): nothing to do.** `fx_rates` and `fx_source_holidays`
  hold **zero PII** — public ČNB reference data with no `user_id`, no free text, no member reference.
  Neither table appears in the anonymize transaction or the erase scrub.
- **Blob classes? None.** E2 stores no blobs (X4 N/A).
- **New PII columns? None.** The `display` block is *derived* from ledger amounts the caller can
  already see (§3.3) plus public rates — it introduces **no new personal data** and appears only in
  authenticated, membership‑scoped responses (D6). `/me/export` (§4.4) is unaffected: it keeps
  exporting authoritative native amounts only — no converted columns leak in (X3).

### 2.7 Security notes

- **No new unauthenticated surface (X6 N/A).** `GET /fx/rates` and `?display=` are Bearer‑authed;
  `fx/rates` reveals only public ČNB data. *If* it is ever exposed unauth for a web tool, it must
  first get its own rate‑limit partition, uniform responses, and the X6 treatment (v1 §3.8/§4.3
  template) — not a silent `AllowAnyOrigin` (§4.3 CORS ruling).
- **Authz (D6).** `?display=` on balances/expenses changes nothing about authorization: the
  membership check is still the **first** statement; the display block is computed *after* authz,
  over rows the caller can already see. Non‑members still get `404`.
- **Over‑posting / write path.** `fx_rates` has **no user write path** — it is written *only* by the
  `fx.poll`/`fx.backfill` handlers from the allowlisted ČNB host (EXT‑D2m). No request DTO binds it.
- **SSRF.** The feed fetches a **fixed, allowlisted** origin (`cnb.cz` daily/annual endpoints); no
  user‑supplied URL ever enters the fetcher — the same posture as v1's "no user URLs in the system"
  (§3.12 avatars).
- **Input validation.** `?display`/`quote` is a closed‑enum parse (`CZK` only) → `422
  validation_failed` on anything else; `date` is the standard `yyyy-MM-dd` boundary validator.
- **Money‑safety (the gate).** The conversion path is **read‑only** and returns `DisplayMoney`
  (EXT‑D2a) — a type with no route into any ledger table. The feed writer can only `INSERT … ON
  CONFLICT DO NOTHING` into `fx_rates`; it cannot touch `expenses`, `expense_splits`, or
  `settlements`.
- **Rate limits (Appendix A style).** `GET /fx/rates` rides the standard authenticated bucket
  (cheap, cacheable); the *outbound* ČNB calls are rate‑limited on our side (backfill sequential
  with a fixed inter‑request delay — §2.9) to stay a good citizen.

### 2.8 Test plan

**Invariants (property‑tested, FsCheck):**

- **Native zero‑sum survives FX.** For any group, `Σ net(m,C) == 0` per currency (§2.2, §3.3) is
  **byte‑identical** whether `?display` is present or absent — enabling display never mutates a
  native field. *This is the gate's headline assertion.*
- **Converted money never enters the ledger.** No code path constructs a `Money` from a
  `DisplayMoney`; `BalanceCalculator`/`DebtSimplifier` inputs contain **only** `Money`. A test
  computes `simplified` with and without `?display` and asserts identical native edges (the §2.4
  example: EUR debts, never CZK).
- **Integer‑exactness + single rounding.** `ToPivotMinor` equals a reference rational computed in
  `BigInteger`/`decimal`, rounded once `ToEven`. Property: no intermediate overflow up to
  `amountMinor ≈ 10^14` (`Int128` guard, EXT‑D2i).
- **Converted totals do NOT zero‑sum.** Property: for a multi‑member cross‑currency group, the
  residual `|Σ display.amount|` over members is **bounded by the member count** and is **never**
  consumed by `DebtSimplifier`.

**Example cases:**

| Case | Expected |
|---|---|
| 4‑way EUR net `10000/‑3333/‑3333/‑3334` @ 24.315 | CZK displays `2432/‑810/‑810/‑811`; **Σ = +1**, native Σ = 0 (§2.4) |
| `.5` midpoint (`2431.5 CZK`) | `ToEven` → `2432` |
| Saturday expense, Fri fixing exists | picks Friday's `valid_on` (previous‑business‑day fallback, EXT‑D2f) |
| Expense dated **before** earliest backfilled fixing | `display` block **omitted**; `200`; native intact (EXT‑D2h) |
| `quote`/bucket == CZK | no `display` sub‑block (identity, not approximate) |
| ČNB **revises** a past fixing; re‑fetch | `ON CONFLICT DO NOTHING` — first value stands; `source`/`fetched_at` prove it (EXT‑D2e) |
| `fx.backfill` run twice | zero duplicate rows (idempotent, X5) |
| Staleness: 4 business days, no new fixing | alert **fires**; a weekend/holiday gap does **not** (holiday‑aware, EXT‑D2k) |
| `?display=EUR` (non‑pivot) | `422 validation_failed` (field `display`) |
| ČNB `per_amount=100` currency (future) | conversion divides by `per_amount` — no scale bug |

### 2.9 Dependencies · tier · effort

- **Depends on:** **E0** (§0.4.2) — the jobs substrate runs `fx.poll` (self‑rescheduling to the next
  ČNB business day ~14:45 Europe/Prague, backoff until published), the `> 3 business‑day` staleness
  alert, and the one‑shot `fx.backfill` (ČNB annual `year.txt` files, sequential + rate‑limited,
  `ON CONFLICT DO NOTHING`, floor `2000‑01‑01` to cover the `expense_date` lower bound of §1.3).
  Also the `currencies` scale authority (D2) and `Money` (§2.1).
- **The deferred‑D5 seam (built, not used).** The only interface convergence will need:

  ```csharp
  // Immutable rate-by-date + provenance. Deterministic: same (currency, on) → same result, forever
  // (EXT-D2e). A future cross-currency settlement will SNAPSHOT the returned rate onto the settlement
  // row; E2 adds no such column and writes no such snapshot — it only guarantees the value is stable.
  public interface IFxRateProvider
  {
      FxRate? RateOn(string currency, DateOnly on, CancellationToken ct);   // previous-business-day fallback (EXT-D2f)
  }
  ```
  E2 stops here: **no** cross‑currency netting, **no** settle‑EUR‑in‑CZK, **no** FX in
  `DebtSimplifier` (EXT‑D2l, D5).
- **Tier:** **v3.** **Effort:** **M** (one immutable table + a holiday seed, a read‑only conversion
  slice, three E0 jobs, and the `?display` projections).
- **Appendix A additions (new operational constants — existence + single location is the contract):**

  | Constant | v1 value | Notes |
  |---|---|---|
  | `fx.poll` schedule | **~14:45 Europe/Prague, ČNB business days** | ČNB publishes ~14:30; margin + backoff‑retry until published |
  | FX staleness alert threshold | **> 3 business days** without a new fixing → page | holiday‑aware (`fx_source_holidays`); mirrors the `Σ balances == 0` canary (§6) |
  | `fx.backfill` floor | **2000‑01‑01** | matches the `expense_date` lower bound (§1.3) |
  | ČNB fetch host allowlist | **`cnb.cz`** | daily + annual endpoints only; SSRF guard (EXT‑D2m) |
  | ČNB backfill inter‑request delay | **e.g. 1 req/s, sequential** | good‑citizen throttle on the annual files |


## 3. E3 — Recurring expenses

### 3.1 Scope & user value

Define a recurring expense **once** — rent, utilities, a streaming subscription — and the server
materializes a **real expense** on schedule so nobody has to remember to add it. A rule is a
**template**, not a ledger entry; the money only ever exists as ordinary `expenses` rows the rule
fires (X2). The flagship case is the flatmate split (rent, every 1st, split equally among whoever
currently lives there). **Tier v3, effort L.**

### 3.2 Decisions

| # | Ruling | Why / what it fixes |
|---|--------|---------------------|
| **EXT-D3a** | **Curated structured recurrence in typed columns — never RFC 5545 RRULE strings.** Exactly three `freq` values: `weekly` (+ `by_weekday` 1–7 ISO Mon–Sun), `monthly` (+ `by_month_day` 1–31), `yearly` (anchored on `starts_on`'s month+day; `by_*` NULL). All three take `interval ≥ 1` (every *N* periods). End bound: `ends_on` (inclusive date) **xor** `remaining_count` **xor** neither (open-ended). | An RRULE parser/serializer is a testing tarpit (BYSETPOS, RDATE/EXDATE, floating vs UTC DTSTART, `COUNT`+`UNTIL` interplay) for a product that needs three patterns. Typed columns make every recurrence a handful of integers with per-column `CHECK`/422 twins (§3.4) — no grammar, no ambiguity. |
| **EXT-D3b** | **End-of-month is a non-destructive per-occurrence clamp.** `by_month_day` beyond a month's length clamps to that month's **last day**; the stored `by_month_day` never mutates. `by_month_day = 31` ⇒ Jan 31 → **Feb 28/29** → **Mar 31** (not Mar 28). Feb-29 yearly anchor → Feb 28 in common years, Feb 29 in leap years. | The naive "carry the clamp forward" bug walks a 31st rule down to the 28th permanently after one February. Clamping fresh from the immutable anchor each occurrence is the only correct behavior, and it is the single most common recurrence defect — so it is pinned here and property-tested (§3.8). |
| **EXT-D3c** | **Timezone lives on the rule (IANA id, default = creator's).** "Fires on the 1st" means **local midnight in the rule's tz + a deterministic per-rule jitter**; `next_run_at timestamptz` is computed by a **tz-aware calendar walk** (NodaTime tzdb). The **local wall-clock anchor is stable; the UTC instant floats with the offset.** | Storing a bare UTC time drifts the local fire moment by an hour twice a year. Anchoring on local midnight and recomputing the UTC instant per occurrence keeps "the 1st at ~midnight Prague" true across DST. Jitter (0–15 min, `hash(rule_id)`) spreads the thundering herd off exact midnight. |
| **EXT-D3d** | **Two split-template modes, distinguished by whether `recurring_rule_splits` has rows.** **No rows = dynamic-equal** — equal split over **all active members at fire time** (this is the flatmate default). **Rows present = pinned** — an explicit member set with `exact`/`shares`/`percentage`/pinned-`equal` weights. **Only `equal` may be dynamic**; `exact`/`shares`/`percentage` require pinned rows (`422 recurring_split_required`). | The flatmate wants "split rent among current tenants" without editing the rule when someone moves in — dynamic-equal delivers that. A utilities-by-shares rule wants a frozen allocation — pinned delivers that. One column-presence bit, no mode enum. |
| **EXT-D3e** | **Member drift never guesses — two independent axes, payer and split.** **Payer (rule-level, EVERY rule):** a rule — dynamic-equal *or* pinned — whose `paid_by` is **soft-deleted** (admin remove, §3.8.3) at fire time **auto-pauses** (`status='paused'`, `pause_reason='payer_removed'`), notifies, and **does not materialize or advance**. `paid_by` is a rule column (§3.3), not a split concept, so it is checked identically in both split modes. **Split members:** dynamic-equal **recomputes** the active-member set every fire (that *is* the feature — a removed member is simply excluded next fire); a **pinned** rule whose **any pinned split member** is soft-deleted auto-pauses (`pause_reason='member_removed'`) rather than dropping the member or silently redistributing. GDPR **anonymize does not pause** on either axis: it turns the member into an active ghost (§3.8.3), which still holds balances, so the rule keeps firing. | Silently dropping a pinned member rewrites who-owes-what without consent; guessing a replacement payer invents debt. Pausing hands the decision to a human. The payer axis is live for the flagship **dynamic-equal** rent rule too: a payer can be admin-removed once every balance bucket nets to zero (v1 §3.8.3), so the flatmate who fronts rent can leave after the group settles up — and the normal expense-create path (§3.3) would then hit `422 member_deleted` on `paid_by` (v1 §3.8.3, newly adding a soft-deleted member), crash-looping the worker or silently skipping the charge (X5). Pausing on the payer axis pre-empts that dead-end for *any* rule, not just pinned. The anonymize/remove distinction is load-bearing — anonymize keeps the member row *active* (`deleted_at` stays NULL), so firing is still well-defined. |
| **EXT-D3f** | **Materialization is idempotent by construction (X5); the `rec:` clientId namespace is server-owned.** The fired expense's `client_id` is **deterministic**: `"rec:{rule_id}:{occurrence_date}"`. The D9 `UNIQUE(group_id, client_id)` makes a second fire of the *same* occurrence a **no-op** — but that no-op is only sound because the `rec:` prefix is **reserved**: the expense-create boundary **rejects any user-supplied `clientId` matching `^rec:`** (`422 reserved_client_id`, §3.7), so a member cannot pre-create an ordinary expense that squats a future occurrence's key. At fire time the worker treats an existing `(group_id, client_id)` row as **"already fired" only when its `recurring_rule_id` equals this rule** (identical self-replay → skip that occurrence and advance); **any other** pre-existing row on that key is an **operational error** — a telemetry/log event, the rule stays put for the next pass, **never** a silent advance. The scheduler may double-fire; the ledger may not double-charge, and it may not be tricked into skipping a genuine charge. | This is the entire safety story for at-least-once background work. Occurrence date (not run instant) is the key, so a retry, a two-worker race, or a crash-replay all collapse onto one row. Without the reserved prefix a co-tenant could read the rule id (`GET`) and the occurrence date (§3.4 preview) and pre-create a divergent expense under `rec:{rule}:{date}`; the worker's create would then hit v1's idempotency `409 client_id_conflict` (§3.4) on a *divergent* row, which is **not** a self-replay no-op — swallowing it silently skips a charge (X5), retrying stalls every future occurrence. The prefix reservation plus the `recurring_rule_id`-match rule closes both. |
| **EXT-D3g** | **Catch-up covers lag, not history; creation never back-fills.** First `next_run_at` = first occurrence ≥ **greatest(`starts_on`, `created_at::date`)** — a past `starts_on` sets the *anchor*, not a backfill. When the worker lags (rule stayed `active`), it materializes **every** missed occurrence (each idempotent, EXT-D3f) up to the **catch-up cap** (Appendix-A-style, §3.7); beyond the cap it **auto-pauses** (`backlog_exceeded`) rather than flooding. | A `starts_on` in the past must not dump a year of back-rent the instant the rule is saved. Downtime *must* recover the genuinely-due occurrences. The cap bounds the blast radius of a clock skew or a very long outage. |
| **EXT-D3h** | **Edits are future-only; fired expenses are independent.** A rule PUT affects **future occurrences only**. An already-materialized expense is an **ordinary expense** — independently editable/deletable, linked back by `recurring_rule_id`. **No retroactive rewrite** of past occurrences, ever. **Soft-deleting the rule keeps every past expense and its `recurring_rule_id`**; only a future hard-purge sets the FK NULL (`ON DELETE SET NULL`). | Retroactively rewriting settled history is the drift bug D4 exists to kill. Each occurrence snapshots the rule's state at fire time; changing the rule later cannot silently restate closed months. |
| **EXT-D3i** | **Status is a small state machine; the worker is a legitimate concurrent writer.** `active ⇄ paused`, both `→ ended` (terminal: `remaining_count` hit 0 or `ends_on` passed; `next_run_at := NULL`). **Resume rolls `next_run_at` forward to the first future occurrence — a pause skips its window, it does not back-charge** (contrast EXT-D3g). Firing (advance `next_run_at`/`last_run_at`, decrement `remaining_count`, or auto-pause) **bumps `version`** and emits a `/sync` row, so a user's stale-`If-Match` edit around a fire gets a correct `412` to merge (D8). Firing into an **archived group** (§3.7) auto-pauses (`group_archived`). | A pause means "stop charging me," so resuming must not surprise-bill the paused months — but a mere lagged worker (rule never paused) must recover them; the two paths are deliberately different. One `version` axis keeps concurrency honest: the rule genuinely changed when it fired. |
| **EXT-D3j** | **Scheduler-safe via E0's SKIP-LOCKED worker.** The worker claims due rules `FOR UPDATE SKIP LOCKED`, then **claim → materialize (all due occurrences) → advance `next_run_at` in one transaction** per rule. Two instances during a Railway deploy overlap never collide (SKIP LOCKED); a crash mid-transaction rolls back and re-fires; a crash that ever separates create from advance is absorbed by EXT-D3f/D9. | Two overlapping app instances is the normal deploy state, not an edge case. SKIP LOCKED gives lock-free parallelism; the single transaction gives atomicity; the deterministic `client_id` is the belt under the braces. |
| **EXT-D3k** | **Single currency, no FX, no converted money (X3, D5).** A rule carries one `amount_minor` + `currency`; the fired expense inherits both **exactly**. Recurring **never** converts, never emits a `display`/`approx` block, and never touches balance math directly — it only creates ordinary expenses, which §2.2 already nets per currency. | Recurring is a *scheduling* feature, not an FX feature. Keeping converted money structurally absent means recurring cannot possibly leak an approximate amount into the ledger. |
| **EXT-D3l** | **Any member manages rules (D6 first); the occurrence preview is computed, never stored (X2).** Create/edit/pause/resume/soft-delete are open to any group member (membership-check-first, non-members `404`); money-aggregate concurrency (`If-Match`) applies. `GET …/occurrences?until=` is a **pure computation** off the same tz-aware walk — no materialized calendar table. | A fired expense is already independently editable/deletable by any member, so gating rule *management* tighter buys nothing and adds entry friction. A stored occurrence table would be a second source of truth (D4/X2); the walk is deterministic and cheap. |

### 3.3 Schema (DDL delta)

```sql
-- E3: recurring expense rules. A rule is a TEMPLATE; the ledger truth is the
-- ordinary expenses it fires (X2 — no second authoritative money store).
CREATE TABLE recurring_rules (
    id              uuid PRIMARY KEY DEFAULT uuid_generate_v7(),
    group_id        uuid NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
    client_id       text,                                     -- offline id → (group_id, client_id) idempotency (D9, X1)
    title           text NOT NULL CHECK (length(title) BETWEEN 1 AND 140),
    amount_minor    bigint NOT NULL CHECK (amount_minor > 0), -- integer minor units (D1)
    currency        char(3) NOT NULL REFERENCES currencies(code),   -- = fired expense's currency; no FX (D5, EXT-D3k)
    paid_by         uuid NOT NULL REFERENCES group_members(id) ON DELETE RESTRICT,
    split_type      text NOT NULL CHECK (split_type IN ('equal','exact','shares','percentage')),
    category_id     uuid REFERENCES categories(id) ON DELETE SET NULL,
    icon_symbol     text,
    notes           text CHECK (length(notes) <= 2000),       -- bounded; 422 twin notes_too_long (§3.4)
    -- recurrence: curated typed columns, NOT an RRULE string (EXT-D3a)
    freq            text NOT NULL CHECK (freq IN ('weekly','monthly','yearly')),
    interval        int  NOT NULL DEFAULT 1 CHECK (interval BETWEEN 1 AND 60),
    by_month_day    int  CHECK (by_month_day BETWEEN 1 AND 31),   -- monthly only
    by_weekday      int  CHECK (by_weekday  BETWEEN 1 AND 7),     -- weekly only, ISO 1=Mon..7=Sun
    timezone        text NOT NULL,                            -- IANA tzdb id (validated at boundary); default creator's (EXT-D3c)
    starts_on       date NOT NULL,
    ends_on         date,                                     -- inclusive; NULL = open / count-bounded
    remaining_count int  CHECK (remaining_count >= 0),        -- occurrences left; NULL = open / date-bounded
    next_run_at     timestamptz,                              -- next fire instant (UTC); NULL iff status='ended' (EXT-D3i)
    last_run_at     timestamptz,
    status          text NOT NULL DEFAULT 'active' CHECK (status IN ('active','paused','ended')),
    pause_reason    text CHECK (pause_reason IN                -- machine reason on auto/manual pause (EXT-D3e/g/i)
                        ('user','member_removed','payer_removed','backlog_exceeded','group_archived')),
    version         int  NOT NULL DEFAULT 1,                  -- optimistic concurrency (D8, X1)
    created_by      uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    deleted_at      timestamptz,                              -- soft-delete tombstone → /sync (X1)
    UNIQUE (group_id, client_id),                             -- create idempotency (D9)
    -- recurrence shape guards (each has a 422 twin, §3.4):
    CHECK (freq <> 'weekly'  OR (by_weekday  IS NOT NULL AND by_month_day IS NULL)),
    CHECK (freq <> 'monthly' OR (by_month_day IS NOT NULL AND by_weekday  IS NULL)),
    CHECK (freq <> 'yearly'  OR (by_weekday  IS NULL AND by_month_day IS NULL)),
    CHECK (NOT (ends_on IS NOT NULL AND remaining_count IS NOT NULL)),   -- end bound is xor (EXT-D3a)
    CHECK (ends_on IS NULL OR ends_on >= starts_on),
    CHECK (status = 'ended') = (next_run_at IS NULL)          -- ended ⇔ no next run
);

-- Split template. NO rows for a rule ⇒ DYNAMIC-EQUAL (equal over all ACTIVE members at fire time,
-- EXT-D3d default). Rows present ⇒ PINNED set (exact/shares/percentage/pinned-equal).
CREATE TABLE recurring_rule_splits (
    rule_id         uuid NOT NULL REFERENCES recurring_rules(id) ON DELETE CASCADE,
    group_member_id uuid NOT NULL REFERENCES group_members(id) ON DELETE RESTRICT,
    weight          int,          -- shares template input (audit)
    basis_points    int,          -- percentage template input (audit)
    amount_minor    bigint,       -- exact template input (fixed per-occurrence share)
    PRIMARY KEY (rule_id, group_member_id)
);

-- Link every fired expense back to its rule. ON DELETE SET NULL: a future hard-purge of a rule
-- keeps the past expenses (EXT-D3h); v1 soft-delete keeps both the expense AND the link.
ALTER TABLE expenses
    ADD COLUMN recurring_rule_id uuid REFERENCES recurring_rules(id) ON DELETE SET NULL;

CREATE INDEX ix_recurring_due   ON recurring_rules(next_run_at)
    WHERE status = 'active' AND deleted_at IS NULL;                       -- the worker's claim scan
CREATE INDEX ix_recurring_group ON recurring_rules(group_id) WHERE deleted_at IS NULL;
CREATE INDEX ix_expenses_recurring ON expenses(recurring_rule_id) WHERE recurring_rule_id IS NOT NULL;

-- Additive (meta-rule, §3.11): extend the closed change_log entity_type set (§1.3, §3.5).
-- NOTE: Adds 'recurring_rule' to the change_log.entity_type CHECK (v1 §3.5) — the CHECK's
--       authoritative full value set is consolidated in Appendix A.
```

**Worker claim (E0, EXT-D3j)** — one transaction per claimed rule:

```sql
SELECT id FROM recurring_rules
WHERE status = 'active' AND deleted_at IS NULL AND next_run_at <= now()
ORDER BY next_run_at
FOR UPDATE SKIP LOCKED
LIMIT @batch;   -- two overlapping instances never claim the same rule
```

For each claimed rule the worker walks occurrences from `next_run_at` to `now()` (tz-aware,
capped), materializes each via the **normal expense-create path** (server-authoritative shares
§2.3, split-sum trigger §1.5) with the deterministic `client_id`, then advances
`next_run_at`/`last_run_at` (and `remaining_count`/`status`) — all in the same transaction.
Before create, the payer axis (EXT-D3e) is checked: a soft-deleted `paid_by` auto-pauses the
rule (`payer_removed`) with no fire and no advance, so the expense-create path never hits
`422 member_deleted` on the payer. **On-conflict rule (EXT-D3f):** if the `(group_id, client_id)`
insert collides, the collision counts as "already fired" — skip this occurrence and advance —
**only when the existing row's `recurring_rule_id` equals this rule**; any other pre-existing row
on that key (impossible for user creates, since `^rec:` is rejected at the boundary, §3.7) is an
**operational error** — logged/telemetered, the rule is left unadvanced for the next pass, never
silently skipped or double-charged.

### 3.4 API surface

| Method | Path | Purpose | Auth |
|---|---|---|---|
| `GET` | `/groups/{g}/recurring-rules` | List `active`+`paused` rules (cursor, §3.4) | member |
| `POST` | `/groups/{g}/recurring-rules` | Create a rule (`clientId` idempotency, D9) | member |
| `GET` | `/groups/{g}/recurring-rules/{r}` | Detail | member |
| `PUT` | `/groups/{g}/recurring-rules/{r}` | Full replace — future-only (EXT-D3h), `If-Match` (D8) | member |
| `DELETE` | `/groups/{g}/recurring-rules/{r}` | Soft-delete (`If-Match`); past expenses kept | member |
| `POST` | `/groups/{g}/recurring-rules/{r}/pause` | `active → paused` (`If-Match`); idempotent | member |
| `POST` | `/groups/{g}/recurring-rules/{r}/resume` | `paused → active`, roll `next_run_at` forward (`If-Match`) | member |
| `GET` | `/groups/{g}/recurring-rules/{r}/occurrences?until=<date>` | Computed preview (X2 — no writes, no stored table) | member |

Membership-check-first everywhere (D6, non-members `404`). `PUT`/`DELETE`/`pause`/`resume` are
money-aggregate mutations → `If-Match` **required** (`428 precondition_required` absent, `412
version_conflict` stale, §3.2b). `resume` on an `ended` rule → `409 rule_not_resumable`.

**Worked example — create a dynamic-equal monthly rent rule** (CZK `minor_units = 0`, so minor
units == koruna):

```jsonc
// POST /groups/grp_7Hk2/recurring-rules
{
  "clientId": "rec_local_A1B2",
  "title": "Rent",
  "amount": "24000", "currency": "CZK",         // string minor units (D1) — 24 000 Kč
  "paidBy": "mem_alice",
  "categoryId": "cat_housing_preset",
  "recurrence": {
    "freq": "monthly", "interval": 1, "byMonthDay": 1,
    "timezone": "Europe/Prague", "startsOn": "2026-08-01"
    // no endsOn / count → open-ended
  },
  "split": { "type": "equal" }                  // no `among` → DYNAMIC-EQUAL (all active at fire time, EXT-D3d)
}
```
```jsonc
// 201 Created    ETag: "1"
{
  "id": "rec_9Zt4", "clientId": "rec_local_A1B2", "groupId": "grp_7Hk2",
  "title": "Rent", "amount": "24000", "currency": "CZK", "paidBy": "mem_alice",
  "categoryId": "cat_housing_preset",
  "recurrence": {
    "freq": "monthly", "interval": 1, "byMonthDay": 1,
    "timezone": "Europe/Prague", "startsOn": "2026-08-01",
    "endsOn": null, "remainingCount": null
  },
  "split": { "type": "equal", "dynamic": true },  // dynamic flag (additive, optional field)
  "status": "active",
  "nextRunAt": "2026-07-31T22:07:00Z",            // 2026-08-01 00:00 Europe/Prague (UTC+2 summer) + 7-min jitter
  "lastRunAt": null,
  "createdBy": "usr_alice", "createdAt": "2026-07-07T09:00:00Z",
  "version": 1, "deleted": false
}
```

Pinned templates reuse the §3.2 split union verbatim: `{"type":"equal","among":[…]}` (pinned
equal), `{"type":"exact","amounts":[…]}`, `{"type":"shares","weights":[…]}`,
`{"type":"percentage","percents":[…]}` (integer basis points). The **only** memberless split is
`{"type":"equal"}` = dynamic.

**Computed occurrence preview** (X2 — the same tz-aware walk `next_run_at` uses, nothing stored):

```jsonc
// GET /groups/grp_7Hk2/recurring-rules/rec_9Zt4/occurrences?until=2026-11-30
{
  "ruleId": "rec_9Zt4", "timezone": "Europe/Prague",
  "occurrences": [
    { "occurrenceDate": "2026-08-01", "runsAt": "2026-07-31T22:07:00Z" },
    { "occurrenceDate": "2026-09-01", "runsAt": "2026-08-31T22:07:00Z" },
    { "occurrenceDate": "2026-10-01", "runsAt": "2026-09-30T22:07:00Z" },
    { "occurrenceDate": "2026-11-01", "runsAt": "2026-10-31T23:07:00Z" }   // Prague DST ended 2026-10-25 → UTC+1
  ]
}
```

The Nov `runsAt` is **23:07Z** (UTC+1) vs **22:07Z** (UTC+2) earlier: local midnight is preserved,
the UTC instant floats with the offset (EXT-D3c).

**What the fire produces** — an ordinary expense, delivered via the existing `expense` delta (§3.5)
plus a silent push (§4.5). Money is quarantine-free: one currency, real `amount`, no `display`
block (X3):

```jsonc
// materialized 2026-07-31; arrives as a /sync change of type "expense" (NOT a new type)
{ "type": "expense", "id": "exp_by7k", "groupId": "grp_7Hk2", "deleted": false,
  "data": {
    "id": "exp_by7k",
    "clientId": "rec:rec_9Zt4:2026-08-01",       // deterministic, server-owned `rec:` namespace (EXT-D3f) → a self-replay double-fire is a D9 no-op; user creates under `^rec:` are rejected 422 (§3.7)
    "recurringRuleId": "rec_9Zt4",
    "title": "Rent", "amount": "24000", "currency": "CZK", "paidBy": "mem_alice",
    "date": "2026-08-01",
    "split": { "type": "equal", "among": ["mem_alice","mem_bob","mem_carol","mem_dave"] },  // active set AT fire time
    "shares": [ {"memberId":"mem_alice","amount":"6000"}, {"memberId":"mem_bob","amount":"6000"},
                {"memberId":"mem_carol","amount":"6000"}, {"memberId":"mem_dave","amount":"6000"} ],
    "createdBy": "usr_alice", "createdAt": "2026-07-31T22:07:03Z", "version": 1, "deleted": false } }
```

### 3.5 Sync & offline impact (X1)

- **Flows through `/sync`?** Yes. New `change_log.entity_type = 'recurring_rule'` (additive to the
  §1.3 closed set). `data` = the full §3.4 rule representation.
- **Materialized expenses** ride the **existing** `expense` entity_type — a fired expense is an
  ordinary expense (EXT-D3h), never a bespoke sync type. The rule's own advance (`next_run_at`,
  `last_run_at`, `remaining_count`, auto-pause) emits a `recurring_rule` upsert so clients see
  status move (EXT-D3i).
- **§3.5.2 append-rule additions** (the exhaustive table gains):

  | Mutation | Rows appended |
  |---|---|
  | rule create / edit / pause / resume | `recurring_rule` upsert |
  | rule soft-delete | `recurring_rule` **delete** (tombstone) |
  | rule fire (per worker transaction) | `recurring_rule` upsert **+** one `expense` upsert **per** occurrence materialized |

- **Tombstone behavior:** rule soft-delete → `deleted:true` `recurring_rule` change; client
  soft-deletes the rule locally but **keeps rendering `recurringRuleId`** on historical expenses
  (never purge — same rule as removed members/deleted categories, §3.5.4).
- **Bootstrap / re-bootstrap (§3.5.5):** the per-group REST reads gain
  `GET /groups/{g}/recurring-rules` (active+paused). History from before a join never replays
  through `/sync`; the per-group fetch is the only route, exactly as for expenses.
- **Concurrency on the wire:** `version` + `If-Match` (D8), identical to expenses. A fire that
  bumps `version` around a user's offline edit produces a normal `412` carrying the current
  representation to merge (EXT-D3i) — not a spurious conflict, the rule really did change.

### 3.6 GDPR / privacy impact (D7 / X4)

- **On user anonymize (§4.4):** the user's member rows become **active ghosts** (`user_id := NULL`,
  name scrubbed, `deleted_at` stays NULL, §3.8.3). Because the ghost is still active, **rules keep
  firing** (EXT-D3e) — a rent rule survives a flatmate deleting their account. `recurring_rules.created_by`
  is a `users` FK that resolves to the anonymized ("Deleted user") row; no separate scrub — it is a
  uuid, not PII, and carries no ledger value.
- **The rule is not "personal data" to hard-scrub:** `title`/`notes` are user-authored text of the
  **same class as expense `title`/`notes`** — shared group-ledger content retained under the other
  members' legitimate interest (D7), not scrubbed on anonymize.
- **PII surface:** none new. No emails, no tokens, no free-form addresses beyond expense-class text;
  `timezone` is an IANA identifier, `icon_symbol` an SF Symbol name — neither is PII.
- **Blob classes (X4):** **none.** Recurring rules reference no Postgres-external blobs; the
  two-phase blob-deletion machinery does not apply here.
- **`GET /me/export` (§4.4):** additively includes the recurring rules the user authored — a full
  representation per rule, money as string minor units + `currency` (D1). Nothing the in-app UI
  wouldn't already show.

### 3.7 Security notes (X6 / D6 / over-posting / limits)

- **New unauthenticated surfaces (X6):** **none.** Every recurring endpoint is authenticated and
  group-scoped; the worker is internal (E0). X6's rate-limit-partition / hashed-token / uniform-404
  template does not apply — there is no magic-link, unsubscribe, or download URL here.
- **Authz (D6):** indexed `(group_id, user_id)` membership check is the first statement; non-members
  get `404` (no existence leak). Management is open to **any member** (EXT-D3l); admin-only would buy
  nothing since any member can already edit/delete the fired expenses. `pause`/`resume`/`delete`
  under an **archived** group → `409 group_archived` (the §3.7 filter covers these group-scoped
  writes automatically).
- **Over-posting:** explicit request DTO — server-owned fields (`id`, `groupId`, `recurringRuleId`,
  `version`, `status`, `nextRunAt`, `lastRunAt`, `pauseReason`, `createdBy`, `createdAt`) are not
  bound; a body that disagrees → `422 immutable_field` (§3.2b pattern).
- **Input validation twins (§3.4), new `code`s** (additive to Appendix B):

  | Rule | `code` | HTTP |
  |---|---|---|
  | `freq`/`interval`/`byMonthDay`/`byWeekday` shape invalid for the `freq` | `invalid_recurrence` | 422 |
  | `timezone` not an IANA tzdb id (rejects garbage that would stall the worker) | `invalid_timezone` | 422 |
  | both `endsOn` and `count` supplied | `recurrence_end_ambiguous` | 422 |
  | `exact`/`shares`/`percentage` template with no member rows | `recurring_split_required` | 422 |
  | user-supplied `clientId` matching `^rec:` (reserved fired-expense namespace, EXT-D3f) — applies to the **expense**-create boundary, not just recurring | `reserved_client_id` | 422 |
  | `resume`/`pause` on an `ended` rule | `rule_not_resumable` | 409 |

  Existing twins carry over unchanged: `amount_not_positive`, `title_length`, `notes_too_long`,
  `unsupported_currency`, `date_out_of_range` (`startsOn`/`endsOn`), `split_member_invalid`,
  `split_sum_mismatch`, `split_percent_sum_mismatch`, `share_negative`, `invalid_weight`,
  `invalid_basis_points`, `client_id_conflict`, `version_conflict`, `precondition_required`,
  `limit_exceeded`.
- **Rate limits & caps (Appendix-A style — one location, tunable):**

  | Constant | Suggested v1 value | Notes |
  |---|---|---|
  | Recurring rule create | **30 / day per user** | mirrors §4.3 create limits |
  | Active rules per group | **≤ 50** | on create → `409 limit_exceeded` |
  | Catch-up cap | **60 occurrences / rule / worker pass** | beyond → auto-pause `backlog_exceeded` (EXT-D3g) |
  | Occurrence-preview horizon | **≤ 24 months / ≤ 100 rows** | `?until=` clamped |
  | Fire jitter | **0–15 min, `hash(rule_id)`** | deterministic per rule (EXT-D3c) |

### 3.8 Test plan

**Property invariants** (FsCheck; every fired expense is a normal expense, so §2.6 carries):

- **Σ shares == amount** for every materialized expense (largest-remainder, §2.3).
- **Σ net == 0 per currency** holds after any fire, catch-up, pause, or resume.
- **No double-charge:** *N* fires of the same `(rule, occurrenceDate)` → **exactly one** expense
  (deterministic `client_id` + D9). Invoke the worker twice on one due rule, and simulate two
  SKIP-LOCKED instances — one row.
- **No missed-charge:** catch-up materializes **exactly** the missed occurrence set, gap-free and
  duplicate-free; the tz-aware walk advances one occurrence at a time.
- **No converted money (X3):** no rule or fired expense ever carries a `display`/`approx` block or a
  currency other than the rule's; assert the FX path is never reachable from recurring.

**Example cases:**

- **Clamp:** `by_month_day = 31`, monthly, Jan→Feb→Mar → `2026-01-31, 2026-02-28, 2026-03-31`
  (non-destructive, EXT-D3b).
- **Leap yearly:** `starts_on = 2024-02-29` → `2025-02-28, 2026-02-28, 2027-02-28, 2028-02-29`.
- **DST:** Prague monthly across the last Sunday of October → `next_run_at` UTC shifts +1h while
  local midnight is unchanged; a `UTC` rule (no DST) is the control and does not shift.
- **Crash between create and advance** (force two transactions) → re-run → still one expense (D9).
- **Reserved-namespace squat (EXT-D3f):** a member `POST`s an ordinary expense with
  `clientId = "rec:{rule}:{next occurrence date}"` (rule id from `GET`, date from the §3.4 preview) →
  **`422 reserved_client_id`** at the expense boundary, no row created. Then the occurrence fires
  normally → exactly one expense, correct payer/shares. Separately, inject a divergent pre-existing
  row on that key (bypassing the boundary) and fire → worker logs an **operational error**, rule
  **not advanced**, **no** silent skip and **no** double-charge (contrast a same-rule self-replay,
  which skips-and-advances to exactly one row).
- **Catch-up:** worker down 3 months on a monthly rule → 3 expenses on resume, distinct occurrence
  dates, each idempotent; backlog > cap → **auto-pause** `backlog_exceeded`, **zero** expenses.
- **No backfill on create:** `starts_on = 2025-01-01`, created 2026-07-07 → first fire is
  2026-08-01, **not** a year of back-rent (EXT-D3g).
- **Dynamic-equal drift:** member added between fires → next fire splits over the new set (Σ==amount);
  member removed → excluded next fire; Σ net == 0 throughout.
- **Pinned split member removed** (admin soft-delete) → rule **pauses** `member_removed`, no expense,
  no advance. Anonymize of a pinned member → rule keeps firing (active ghost, EXT-D3e).
- **Payer removed — dynamic-equal (flagship):** a dynamic-equal rent rule whose `paid_by` settles
  every currency bucket to zero and is then admin-removed (§3.8.3) → next due fire **pauses**
  `payer_removed`, **no expense, no advance** — the worker never reaches the `422 member_deleted`
  dead-end. Same assertion for a **pinned** rule's payer. Anonymize of the payer → keeps firing
  (active ghost).
- **Edit is future-only:** change `amount`, then fire → new occurrence uses the new amount; a
  previously-fired expense is unchanged; concurrent edit around a fire → `412` merge (EXT-D3h/i).
- **Soft-delete keeps history:** delete the rule → past expenses keep `recurringRuleId`, rule
  tombstoned via `/sync`; no future fires.
- **Resume skips the paused window:** pause a monthly rule for 2 months, resume → `next_run_at` rolls
  to the next future 1st, **no** back-charge (contrast catch-up).
- **Ended:** `remaining_count` reaches 0 (or `ends_on` passes) → `status='ended'`, `next_run_at NULL`,
  no further fires; `resume` → `409 rule_not_resumable`.
- **Idempotent create replay (D9):** re-POST the same `clientId` → `200` + existing rule, no second
  rule; divergent body → `409 client_id_conflict`.
- **Archived group:** a due rule in an archived group → fire suppressed, rule auto-pauses
  `group_archived`; member write to the rule → `409 group_archived`.

### 3.9 Dependencies · tier · effort

- **Hard prerequisite:** **E0** (durable jobs/scheduler worker with `FOR UPDATE SKIP LOCKED`) — the
  materialization worker runs on it (EXT-D3j).
- **Depends on:** §3.5 delta sync (new `recurring_rule` entity_type), §4.5 push (fire notification),
  §2.3 `ProportionalAllocate` (share math), §3.2/§3.2b (expense-create path reused verbatim),
  §3.7 (archived-group filter), Appendix B (new codes, additive per §3.11).
- **New library:** **NodaTime** (IANA tzdb calendar walk for `next_run_at`/DST, EXT-D3c).
- **Tier:** v3. **Effort:** L (worker + scheduler safety + recurrence math + tz/DST correctness +
  the money-drift property suite dominate; the CRUD surface is small).


## 4. E4 — Exports & reporting (CSV · PDF · stats · GDPR export)

Governed by **X3** (converted money quarantined) · **X6** (unauthenticated download surface).
Imports E0 primitives: the jobs substrate (§0.4.2), `IBlobStore` (§0.4.3). Review gate: **Security +
money‑light** (§0.5.3). Ships in two slices: **E4a** (CSV + stats) → **v2.5**; **E4b** (PDF +
async GDPR export) → **v3**.

### 4.1 Scope & user value

Members want their ledger *out* of the app: a CSV to reconcile against a bank statement or drop
into Excel, a printable PDF statement to settle a shared trip, in‑app charts ("where did the
money go this month"), and — the compliance floor — a machine‑readable dump of everything the
account holds (GDPR Art. 15/20). All four are **read‑only projections of the ledger**: they
compute or serialize, they never write a money row. Tier: CSV + stats are cheap enough to ship
in the v2.5 foundation; PDF rendering and the async GDPR pipeline land in v3.

### 4.2 Decisions

| # | Ruling | Why / what it fixes |
|---|--------|---------------------|
| **EXT‑D4a** | **Dual‑mode delivery.** A CSV export is a **synchronous streamed response** (`text/csv`, chunked, no job, no blob) because it builds in <1 s at this scale; a PDF and the async GDPR export are **CPU‑ or scan‑heavy → E0 jobs** (`kind='export.build'`, §0.4.2) that produce a stored artifact the client polls for and then downloads. | A streamed CSV needs no storage, no TTL, no download‑URL attack surface — the cheapest correct thing. Offloading PDF/GDPR to the queue keeps a slow render off the request thread (🔧 the v1 §4.4 synchronous `GET /me/export` is a timeout risk for a power user with tens of groups; §4.2/EXT‑D4i give it an async sibling). |
| **EXT‑D4b** | **PDF engine = QuestPDF**, pure‑.NET (no headless Chromium), behind an `IPdfRenderer` seam. Czech diacritics ride an **embedded, subsetted** font — the acceptance test (§4.8). Licensed under the QuestPDF **Community License**, free while Paybitch's annual gross revenue is **< $1M USD**. | Chromium/Playwright is a heavyweight, memory‑hungry dependency on a Railway box (§6) and a fragile font‑embedding story. QuestPDF renders glyphs we control. 🔧 The community‑license *revenue clause* is real: crossing $1M USD requires a paid Professional license (a one‑time purchase, **not** a re‑architecture — `IPdfRenderer` isolates the engine). Flag the license review as a revenue‑threshold trigger, not a code TODO. |
| **EXT‑D4c** | **CSV money = machine‑truth columns** (`amount_minor` integer string + `currency` code + ISO‑8601 `date`) are authoritative; an **optional localized `amount` column** is convenience only. **No amount ever passes through `double`/`decimal`** — the human string is built by integer surgery on minor units + `currencies.minor_units` scale (D2). Czech locale emits **UTF‑8 BOM, `;` delimiter, decimal comma**; `en` emits no‑BOM, `,` delimiter, decimal point. | 🔧 A naïve `amount/100.0` reintroduces exactly the float round‑trip D1 exists to kill — a 0‑decimal CZK value or a large EUR value can drift a unit. The integer/scale formatter is bit‑exact. The `;`+BOM+comma dialect is what makes the file open **correctly in Czech Excel** (comma is the cs decimal separator, so it can't double as the field delimiter). |
| **EXT‑D4d** | **Async results fold into the E0 `jobs` queue** (no second scheduler); a client‑facing **`export_results`** row is the pollable/downloadable artifact. The row stores **metadata + an opaque `IBlobStore` key** — the blob is **referenced, never owned by Postgres** (X4). | One queue, one drain loop, one reaper (§0.4.2). The `export_results` row gives the download endpoint a concrete authz‑scoped record to check, and its short TTL + two‑phase blob delete (X4) make the artifact disposable (X2) — a stored export is not a second authoritative store. |
| **EXT‑D4e** | **Download = authenticated stream by default.** `GET /exports/{id}/download` re‑checks authz (D6 / requester‑scope) on every hit and streams the blob through the API (server‑side `IBlobStore.PresignGetAsync`, never exposed). A **short‑TTL presigned GET** is a *large‑artifact* fallback only, and when used is X6‑hardened. | 🔧 A bare presigned URL to a whole‑group ledger is a **forwardable capability to the single most sensitive artifact the app produces** — one leaked URL, no login. The authenticated stream keeps D6 in force on every byte; the presigned fallback is opt‑in for egress offload, short‑TTL, and auto‑deleted (below). |
| **EXT‑D4f** | **Who may export a group ledger: any active member** — the export contains only data that member can already read in‑app (§3.1 reads, §3.3 balances), so it is not a privilege escalation. **Every export request is audit‑logged**, and **only the requester** (`requested_by`) may poll or download its result. | Restricting export to admins would be security theater — a member can already page the whole ledger via the REST reads. The real controls are the **audit trail** (a new additive `group.exported` verb, §3.10 — ids‑only metadata) and **requester‑scoped download** (another member's artifact → `404`, D6 posture). |
| **EXT‑D4g** | **Stats are computed on demand (X2), always per‑currency buckets**, mirroring the `/balances` `byCurrency` envelope (§3.3, D5) — a Dapper aggregate over the ledger, **no materialized stats table**. Bucketing axis is **`expense_date`** (a plain local `date`, §1.3); the `tz` param (default **`Europe/Prague`**) resolves *open‑ended / relative* windows only. | A stored stat is a stored balance by another name (X2) — drift bait. Per‑currency `GROUP BY` makes a cross‑currency sum structurally impossible (D5): CZK spend and EUR spend never add. 🔧 `expense_date` is already the economic local date the user assigned — re‑projecting a plain `date` through a tz is a no‑op‑or‑bug; the tz only decides where a *relative* window ("this month") falls. |
| **EXT‑D4h** | **Approximate converted totals are display‑only and STRUCTURALLY QUARANTINED (X3):** they arrive in a distinct `display` block — string minor units of the **target** currency + explicit **rate provenance**, sourced from **E2** (Live FX) — **never** in an authoritative `byCurrency` bucket or an export column. Absent entirely when `convert` is omitted or E2 hasn't shipped. | A converted number that can be mistaken for a ledger amount *will* be, by some chart or CSV consumer (X3). Type‑level separation makes the mistake unrepresentable, and graceful absence keeps E4a shippable in v2.5 **before** E2 (v3). |
| **EXT‑D4i** | **GDPR export scope = group‑scoped data the user can already see in‑app** (inherits the §4.4 redaction posture verbatim), serialized as **machine‑readable JSON** (Art. 20 portability). The async **`POST /me/export`** is **additive** to v1's synchronous `GET /me/export` (§4.4) — the meta‑rule holds: the sync route stays for small accounts, the async route handles large ones and yields a download artifact. | Expenses reference *other members'* in‑group names and shared amounts — but those are **shared** records the requester already renders in‑app, not third‑party leakage (§4.4 ruling, unchanged). JSON, not PDF, satisfies portability. Additive by construction: no v1 field changes meaning (§3.11). |
| **EXT‑D4j** | **CSV free‑text cells are formula‑neutralized (CWE‑1236).** Before RFC‑4180 quoting, any **free‑text** column whose value starts with `= + - @`, TAB (`0x09`), or CR (`0x0D`) is prefixed with a single `'` (a leading space is the equivalent mitigation). Applies to `title`, `paid_by_name` (a member `display_name`), and `category` **only**. **Machine‑truth columns are untouched** — `amount_minor`, `date`, `currency`, member ids, and the `split_type` allowlist are numeric/enum/id, injection‑safe, and stay **byte‑exact** (D1 / EXT‑D4c). | 🔧 A member can set a title or display name beginning with `=` (e.g. `=WEBSERVICE(…)`, `=cmd\|'/c calc'!A1`, a DDE payload). When **another** member opens the .csv in Excel/LibreOffice/Sheets, the cell is evaluated as a formula → data exfiltration or code execution. The whole selling point of EXT‑D4c is "opens correctly in Czech Excel" — that same auto‑open **is** the attack surface. This is orthogonal to the `;`/BOM/comma dialect (EXT‑D4c), which governs *field parsing*, not *cell evaluation*; neutralizing the trigger prefix defuses the injection without touching D1 money truth. |

### 4.3 Schema (DDL delta)

No new money columns and **no ledger writes** — E4 only reads. The one new table is the
client‑facing artifact record; the queue itself is the E0 `jobs` table (`kind='export.build'`,
§0.4.2), and the artifact bytes live in `IBlobStore` (§0.4.3), referenced by an opaque key (X4).

```sql
-- Pollable / downloadable export artifact. Ephemeral: short TTL, two-phase blob delete (X4),
-- never synced (§4.5). The blob is REFERENCED here, never stored in Postgres.
CREATE TABLE export_results (
    id            uuid PRIMARY KEY DEFAULT uuid_generate_v7(),
    requested_by  uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,  -- self-scope authz (EXT-D4f)
    group_id      uuid REFERENCES groups(id) ON DELETE CASCADE,          -- NULL ⇒ a GDPR /me export
    kind          text NOT NULL CHECK (kind IN ('csv','pdf','gdpr')),
    content_type  text NOT NULL CHECK (content_type IN
                      ('text/csv','application/pdf','application/json')),
    params        jsonb NOT NULL DEFAULT '{}'::jsonb,  -- from/to/granularity/tz — scalars + ids ONLY, no money/PII
    status        text NOT NULL DEFAULT 'pending'
                      CHECK (status IN ('pending','ready','failed','expired')),
    storage_key   text,           -- opaque IBlobStore key; NULL until 'ready' (X4)
    byte_size     bigint,
    content_hash  bytea,          -- OUR content hash (EXT-D0e HEAD verify), not a provider checksum
    error         text,           -- truncated; never a stack trace, never PII (§4.3 logging hygiene)
    requested_at  timestamptz NOT NULL DEFAULT now(),
    completed_at  timestamptz,
    expires_at    timestamptz NOT NULL           -- artifact TTL; lifecycle + blob.hard_delete reclaim the blob
);
CREATE INDEX ix_export_results_user ON export_results (requested_by, requested_at DESC);
-- Collapse a mashed "Export" button: at most one live build per (user, group, kind, params).
-- Mirrors uq_jobs_dedupe (§0.4.2) at the artifact layer.
CREATE UNIQUE INDEX uq_export_results_inflight
    ON export_results (requested_by, group_id, kind, md5(params::text))
    WHERE status = 'pending';
```

Notes: `ON DELETE CASCADE` on `requested_by` is a **backstop only** — D7 is *soft* delete, so it
never fires (§4.4's "dead code for GDPR"); the anonymize transaction deletes these rows and
two‑phase‑reclaims their blobs **explicitly** (§4.6). No `version`/`client_id`/`deleted_at`:
an export result is **not** a client‑editable synced entity (X1 N/A — §4.5).

### 4.4 API surface

| Method | Path | Purpose | Auth |
|---|---|---|---|
| `GET` | `/groups/{g}/expenses/export.csv?from=&to=&locale=` | **Synchronous streamed** CSV ledger export (EXT‑D4a/c) | Yes · member |
| `GET` | `/groups/{g}/stats?granularity=month&from=&to=&tz=&convert=` | On‑demand per‑currency spend stats for in‑app charts (EXT‑D4g/h) | Yes · member |
| `POST` | `/groups/{g}/exports` | Enqueue an async export (`{"kind":"pdf"…}`) → `202 {id}` (EXT‑D4a/d) | Yes · member |
| `POST` | `/me/export` | Enqueue an async GDPR export (Art. 15/20) → `202 {id}` (EXT‑D4i) | Yes · self |
| `GET` | `/exports/{id}` | Poll export status | Yes · requester |
| `GET` | `/exports/{id}/download` | Download the ready artifact (authenticated stream; presigned fallback) (EXT‑D4e) | Yes · requester |

Group‑scoped routes run the D6 membership check first (non‑member → `404`, no leak). `/exports/{id}`
and `/download` are **requester‑scoped**: `WHERE id=@id AND requested_by=@me` — another user's id
→ `404` (D6 posture, EXT‑D4f). `POST /me/export` is additive to v1's synchronous `GET /me/export`
(§4.4) — both stay.

**Worked example — stats with a quarantined converted block (X3).** Money is a JSON string of
minor units + sibling `currency` (D1); the approximate converted total lives in `display`,
never in `byCurrency`.

```jsonc
// GET /groups/grp_7Hk2/stats?granularity=month&from=2026-01-01&to=2026-06-30&tz=Europe/Prague&convert=CZK
{
  "groupId": "grp_7Hk2",
  "granularity": "month",
  "tz": "Europe/Prague",
  "byCurrency": [                              // authoritative, per-currency, never summed across (D5)
    {
      "currency": "CZK",
      "totalSpend": "412050",                  // Σ amount_minor, string minor units (D1)
      "byPeriod": [
        { "period": "2026-05", "spend": "284000" },
        { "period": "2026-06", "spend": "128050" }
      ],
      "byCategory": [
        { "categoryId": "cat_dining", "spend": "210000" },
        { "categoryId": null,         "spend": "202050" }   // uncategorized
      ],
      "byMember": [
        { "memberId": "mem_alice", "paid": "300000", "share": "137350" },
        { "memberId": "mem_bob",   "paid": "112050", "share": "137350" }
      ]
    },
    {
      "currency": "EUR",
      "totalSpend": "10000",
      "byPeriod": [ { "period": "2026-06", "spend": "10000" } ],
      "byCategory": [ { "categoryId": "cat_housing", "spend": "10000" } ],
      "byMember": [ { "memberId": "mem_alice", "paid": "10000", "share": "3300" } ]
    }
  ],
  "display": {                                 // X3 QUARANTINE — display only, sourced from E2 (Live FX)
    "currency": "CZK",                         // TARGET currency
    "amount": "414550",                        // 412050 CZK + (100.00 EUR × 25.00 → 2500 CZK); string minor units of the TARGET currency, NOT an authoritative bucket
    "approx": true,
    "rateDate": "2026-06-30",
    "source": "cnb",                           // ČNB is the one rate authority (E2)
    "note": "Indicative conversion; excluded from balances, settlements and exports."
  }
}
```

> 🔧 **Cross‑currency conversion goes through major units — never `sourceMinor × rate`.** Above,
> 100.00 EUR → 2500 CZK, so `totalSpendApprox = 412050 + 2500 = **414550**`, **not**
> `412050 + (10000 × 25) = 662050`. EUR minor units (scale 2) and CZK minor units (scale 0) sit on
> **different scales** (D2): multiplying EUR‑minor by the rate directly is off by
> `10^(eurScale − czkScale) = 100×` — the exact minor‑unit conflation D1/D2 exist to kill. Convert via
> major units — `(sourceMinor / 10^sourceScale) × rate`, re‑scaled to the target with `× 10^targetScale`
> — or equivalently apply the `10^(targetScale − sourceScale)` cross‑scale factor. This is **E2's**
> arithmetic (EXT‑D4h); E4 only *renders* the figure, but a wrong number published here would ship the
> 100× bug an implementer copies.

`convert` omitted (or E2 not yet shipped) → the `display` key is **absent**; the client
renders native buckets only. Illustrative on‑demand aggregate (Dapper, no tracking — X2):

```sql
SELECT e.currency,
       to_char(date_trunc('month', e.expense_date), 'YYYY-MM') AS period,
       e.category_id,
       SUM(e.amount_minor)::bigint AS spend_minor        -- integer minor units, never a float
FROM expenses e
WHERE e.group_id = @groupId AND e.deleted_at IS NULL
  AND e.expense_date BETWEEN @from AND @to
GROUP BY e.currency, period, e.category_id;              -- per-currency ⇒ cross-currency sum impossible (D5)
```

**Async export flow (PDF / GDPR).**

```jsonc
// POST /groups/grp_7Hk2/exports          { "kind": "pdf", "from": "2026-01-01", "to": "2026-06-30" }
// 202 Accepted   Location: /exports/exp_res_8Kd2
{ "id": "exp_res_8Kd2", "kind": "pdf", "status": "pending" }

// GET /exports/exp_res_8Kd2   (poll)  →  200
{ "id": "exp_res_8Kd2", "kind": "pdf", "status": "ready",
  "contentType": "application/pdf", "byteSize": 48213,
  "expiresAt": "2026-07-08T09:12:44Z",
  "download": "/exports/exp_res_8Kd2/download" }
// → GET /exports/exp_res_8Kd2/download  streams application/pdf, Content-Disposition: attachment
```

**CSV wire (Czech `locale=cs`).** First bytes are the UTF‑8 BOM (`EF BB BF`); delimiter `;`;
decimal comma; `amount_minor` is machine truth, `amount` is the localized convenience column
(identical to `amount_minor` for 0‑decimal CZK, differs for 2‑decimal EUR — proof the human
column is built from scale, not a float). The free‑text cells (`paid_by_name`, `category`,
`title`) are **formula‑neutralized** before quoting (EXT‑D4j / §4.7): a value beginning with
`= + - @`, TAB, or CR is prefixed with a `'`. The machine‑truth columns below carry no such
prefix — they are byte‑exact:

```text
<BOM>expense_id;date;currency;amount_minor;amount;paid_by_member_id;paid_by_name;category;split_type;title
exp_9Qm4;2026-06-27;CZK;84000;84000;mem_alice;Alice Nováková;dining;equal;Dinner at Lokál
exp_3Yt8;2026-06-29;EUR;10000;100,00;mem_alice;Alice Nováková;housing;percentage;Hotel room
```

### 4.5 Sync & offline impact (X1 checklist)

- **Flows through `/sync`?** **No.** An export result is server‑owned ephemeral job output, in the
  same "deliberately not synced" class as `jobs`, `devices`, and `activity_log` (§3.5.1).
- **`change_log` entity_type?** **None** — no new type. (X1's full sync contract governs
  *client‑cached, client‑editable* entities; an export is neither — it is disposable, X2.)
- **Tombstones?** N/A — expiry is not a tombstone; the client re‑requests instead of reconciling.
- **Bootstrap?** N/A — never part of the §3.5.5 re‑bootstrap. The client polls `GET /exports/{id}`
  directly; a silent‑push trigger is unnecessary (a foreground poll is cheap and the artifact is
  short‑lived).
- **Offline authoring?** No — export requires connectivity; there is no outbox entry, no
  `client_id`, no `version`. Stats and CSV are live reads.

### 4.6 GDPR / privacy impact (D7 · X4 checklist)

- **New PII columns?** None on the ledger. `export_results` holds only `requested_by` (a user id),
  `group_id`, an opaque `storage_key`, timestamps, and scalar `params` — no names, no amounts.
- **The artifact blob is a distinct blob class — `export`** (X4): **short TTL** (Appendix A),
  auto‑deleted by bucket lifecycle **and** the two‑phase `blob.hard_delete` job (§0.4.3). Its
  retention **diverges** from the ledger by design — the ledger is retained on anonymize, the
  export snapshot (a full copy of a user's or group's data) is destroyed.
- **On user anonymize (`DELETE /me`, §4.4):** because D7 is a *soft* delete the FK cascade never
  fires, so the anonymize transaction **explicitly** (a) cancels the user's queued `export.build`
  jobs, (b) hard‑deletes their `export_results` rows, and (c) enqueues a `blob.hard_delete`
  (§0.4.3, X4) for every `storage_key` — an export blob is a verbatim copy of the subject's data and
  must not outlive the account. Add this as a step in the §4.4 anonymize sequence.
- **GDPR export content (EXT‑D4i):** inherits §4.4's documented redaction posture unchanged —
  group‑scoped data the requester can already see in‑app (other members' in‑group names, shared
  amounts) is included as *shared* records, not third‑party leakage; nothing the in‑app UI wouldn't
  show is exported. JSON, for Art. 20 machine readability.
- **Audit:** group exports write a `group.exported` activity row (§3.10, ids‑only metadata — no
  amounts); GDPR self‑exports are recorded in the Serilog audit stream (not group‑scoped). Every
  export request is attributable.

### 4.7 Security notes (X6 · D6 · over‑posting · rate limits)

- **New unauthenticated surface?** By default **none** — every route is Bearer‑authenticated and
  the download is an **authenticated stream** (EXT‑D4e), so D6 stays in force on every byte.
- **Presigned‑GET fallback (large artifacts) is the one capability URL, and gets the full X6
  treatment** (template: invite tokens, §3.8 / §4.3): **short TTL** (Appendix A, minutes not
  hours), high‑entropy opaque `storage_key` (uuidv7 + random suffix — non‑enumerable),
  **no `List`** on `IBlobStore` (EXT‑D0e — the bucket cannot be walked), blob auto‑deleted at
  expiry (two‑phase, X4). It is opt‑in for egress offload, never the default for a group ledger.
- **Authz (D6):** membership check first on group routes; `/exports/{id}[/download]` are
  **requester‑scoped** (`requested_by = @me`) — another member's artifact → `404`, not `403`
  (no existence leak). Membership is re‑checked at request time; a member removed after enqueue
  can still download their *own* already‑authorized snapshot (it is their record), which is
  acceptable and audit‑logged.
- **Over‑posting:** explicit request DTOs (no binding to entities, §4.3). `granularity` ∈
  `{day,week,month}` and `kind` ∈ `{csv,pdf,gdpr}` are **allowlists**, never interpolated into SQL;
  `tz` is validated against the IANA tz database (invalid → `422 validation_failed`); `from`/`to`
  are bounded dates. Dapper stays parameterized.
- **CSV formula / content injection (CWE‑1236, EXT‑D4j):** the CSV writer neutralizes **free‑text**
  cells only — `title`, `paid_by_name` (a member `display_name`), and `category`. A value starting
  with `= + - @`, TAB, or CR is prefixed with a single `'` **before** RFC‑4180 quoting, so a hostile
  `title` like `=cmd|'/c calc'!A1` or `=WEBSERVICE(…)` set by one member cannot execute when
  **another** member opens the file in Excel/LibreOffice/Sheets. The machine‑truth columns
  (`amount_minor`, `date`, `currency`, member ids, `split_type`) are **never** rewritten — numeric/
  enum/id, injection‑safe, byte‑exact (D1). This defends *cell evaluation* and is distinct from the
  `;`/BOM/comma dialect (EXT‑D4c), which only fixes *field parsing*.
- **DoS / resource:** the CSV endpoint **streams** (chunked, `IAsyncEnumerable`) — never buffers the
  whole ledger in memory. PDF/GDPR are queued (§0.4.2), isolating a slow render from request threads;
  `@leaseTimeout` (EXT‑D0h) is tuned above the export‑build runtime (the long pole, §0.4.2). The
  `uq_export_results_inflight` index (§4.3) collapses a mashed Export button into one build.
- **Rate limits — new Appendix A rows (proposed), cited §4.3 style:**

  | Constant | Proposed value | Notes |
  |---|---|---|
  | Rate limit — CSV stream (`GET …/export.csv`) | **20 / hour per user** | full‑ledger scan |
  | Rate limit — stats (`GET …/stats`) | **60 / min per user** | cheap aggregate, chart‑driven |
  | Rate limit — export build (`POST …/exports`) | **10 / hour per user** | queued, CPU‑heavy |
  | Rate limit — GDPR export (`POST /me/export`) | **3 / day per user** | full per‑user scan (mirrors §4.4 posture) |
  | Export artifact TTL (`export_results.expires_at`) | **24 h** | blob lifecycle + two‑phase delete (X4) |
  | Presigned download URL TTL (fallback) | **5 min** | X6; default path is authenticated stream |

### 4.8 Test plan

**Property (FsCheck):**
- **No‑float round‑trip (EXT‑D4c):** for random `amount_minor` across all four currencies,
  `parseMinor(formatHuman(minor, scale)) == minor` — formatting never routes through `double`.
  Assert the CSV formatter and the JSON string serializer produce byte‑identical minor units.
- **Per‑currency isolation (EXT‑D4g, D5):** for any multi‑currency group, each `byCurrency` bucket
  is independent; `Σ byPeriod == Σ byCategory == totalSpend` **within** a currency; **no** value is
  ever the sum of two different currencies.
- **Read‑only ⇒ ledger invariant intact:** no export or stats path issues an `INSERT`/`UPDATE`/
  `DELETE` on a money table, so `Σ net == 0` per currency (§3.3) is preserved by construction —
  assert via a write‑guard integration test (run every export, re‑assert balances unchanged).
- **Converted money never authoritative (X3):** no `byCurrency` bucket and no CSV/PDF authoritative
  column ever contains a `display` (converted) value; a converted number appears **only** inside
  the `display` block with rate provenance.

**Example:**
- CZK 0‑decimal formats as `"840"` (no separator); EUR 2‑decimal as `"8,40"` (cs) / `"8.40"` (en).
- CSV first three bytes are `EF BB BF` (BOM); delimiter `;`; a title containing a comma is **not**
  mis‑split (delimiter ≠ decimal separator).
- **CSV formula neutralization (EXT‑D4j, CWE‑1236):** an expense with `title = "=1+1"` and a payer
  whose `display_name = "=WEBSERVICE(\"…\")"` exports those cells as `'=1+1` / `'=WEBSERVICE(\"…\")`
  (leading `'`, then RFC‑4180 quoted); assert the same row's `amount_minor` is **byte‑identical** to
  the stored minor units — neutralization touches free‑text only, never money truth. Repeat for
  leading `+`, `-`, `@`, TAB, and CR; a benign title (`"Dinner at Lokál"`) is emitted unprefixed.
- **Czech‑diacritics PDF acceptance test (EXT‑D4b):** render the pangram
  *„Příliš žluťoučký kůň úpěl ďábelské ódy"* plus *"Alice Nováková"*; assert the output PDF embeds a
  subsetted font covering `ř ž ť č ň ů á é í ó ď` — **zero `.notdef`/tofu glyphs**, verified by
  parsing the PDF font table (not a screenshot).
- Empty date range → header‑only CSV / empty `byCurrency` — never a 500.
- Large export (10 000 expenses) streams without OOM; the async PDF build survives a scheduler
  double‑fire idempotently (`uq_export_results_inflight` collapses to one artifact, X5).
- Download after `expires_at` → `410`; download by a non‑requester → `404` (D6).
- `tz` boundary: an open‑ended "current month" window resolves against `Europe/Prague`, so an
  expense dated at the month edge lands in the expected period; explicit `from`/`to` (plain dates,
  §1.3) bucket verbatim regardless of `tz`.
- GDPR export includes expenses referencing other members' in‑group names (shared records, §4.4);
  the same account's `DELETE /me` afterward hard‑deletes its `export_results` and enqueues
  `blob.hard_delete` for each `storage_key`.

### 4.9 Dependencies · tier · effort

- **Depends on:** **E0** — jobs substrate (§0.4.2, `kind='export.build'`; reaper EXT‑D0h) for
  PDF/GDPR, `IBlobStore` (§0.4.3, EXT‑D0e) for artifact storage + two‑phase delete, `blob.hard_delete`
  job (§0.4.3) for reclaim. **QuestPDF** (E4b, Community License — EXT‑D4b). **E2** (Live FX) is an
  *optional* enhancer for `display` (EXT‑D4h) — E4a/stats ship in v2.5 **before** E2 and
  simply omit the block. Reuses v1 §3.3 `byCurrency` envelope, §4.4 redaction posture, §3.10
  activity feed (new additive `group.exported` verb).
- **Blocks:** nothing — leaf extension.
- **Tier:** **E4a** (CSV + stats) → **v2.5**; **E4b** (PDF statement + async GDPR export) → **v3**.
- **Effort:** **M–L** — CSV/stats are small (a formatter + two Dapper aggregates); the weight is
  the PDF layout/font pipeline and the async result lifecycle (poll, TTL, two‑phase blob delete).
- **Review gate (§0.5.3):** **Security + money‑light** — download‑URL authz & X6 hardening; CSV
  free‑text formula‑neutralized (EXT‑D4j, CWE‑1236); every export authoritative column carries D1
  minor units only; converted totals quarantined (X3) and cross‑scale‑correct (EXT‑D4h).


## 5. Notifications deepening & invite landing (E5 · E9a)

> Coordination note (numbering): this chapter's decisions are labelled **EXT‑D5*** (notifications)
> and **EXT‑D9a‑*** (invite landing) per the design brief. The §0.5.1 index rows for
> email/notification work (E6 email invites, E10 digests) and the E9a slot are reconciled to
> point here at assembly time — the descriptive heading is renumber‑safe.

### 5.1 Scope & user value

v1 ships **silent APNs only** — `content-available:1` as a *sync trigger*, never an event, with
the bare‑trigger payload rule "never push the payload itself" (§4.5, §7). This chapter deepens
that into **user‑visible notifications**: "Alice added an expense in Chata 2026" pushes, a
per‑user/per‑group **preference matrix** so those alerts are tunable, **transactional email**
(server‑sent invites first — closing the v1 §3.12 cut — digests much later), and an
**unauthenticated invite landing page** (E9a) so an invite link shared to a non‑user renders a
preview and routes to the App Store instead of dead‑ending in a browser. Everything rides the
E0 jobs substrate + `IEmailSender` (§0.4) — **no new stateful dependency**.

**Tier:** invite email + basic push prefs (on/off per event) + invite landing = **v2.5**;
full per‑user/per‑group prefs matrix + Live Activity tokens = **v3**; digests + smart reminders
= **v4+**. **Effort: M.**

### 5.2 Decisions

**Notifications (E5).**

| # | Ruling | Why / what it fixes |
|---|--------|---------------------|
| **EXT‑D5a** | **Nothing user‑identifying rides Apple's pipes — no amounts, no titles, and no names (§4.5 upheld literally).** A user‑visible push is `mutable-content:1` + a Notification Service Extension that **pulls the entity locally over the authenticated channel** (payload carries only `{kind, groupId, entityId}` — the strongest form). The alert body is a **generic, name‑free localized `loc-key` with no `loc-args`** (`NOTIF_NEW_ACTIVITY` → "New activity in a group"); the NSE hydrates the **actor's first name, the group name, AND the amount on device** after fetching over TLS + bearer auth. | Amounts and who‑owes‑whom are financial PII (§4.3); §4.5's rule is verbatim "no amounts, titles, or **names** ever ride Apple's pipes." That rule was authored for v1's silent trigger ("never push the payload itself"); this chapter adds a **new visible‑push class** and honors §4.5 **literally** for it — actor name + group name are re‑rendered on device by the NSE, never transmitted to or through Apple. The pre‑hydration floor (if iOS drops the NSE under resource pressure) is the generic string — still name‑ and amount‑free. This is deliberately **not** a relaxation of §4.5 from "no names" to "no financial PII": the visible push keeps names off the wire. |
| **EXT‑D5b** | **Prefs are stored as OVERRIDES, resolved per‑group → global → default matrix.** `notification_prefs(user_id, group_id NULL, event_type, channel, enabled)`; a `group_id` row overrides the `group_id IS NULL` (global) row, which overrides a hard‑coded default matrix. | A full `user × group × event × channel` table is combinatorial and ~all defaults. Store only the deltas the user actually set; resolve the rest. No materialized "effective matrix" table (X2 — it is derivable). |
| **EXT‑D5c** | **The notification event taxonomy IS the §3.10 activity `verb` taxonomy — one list, not two.** `event_type` accepts any §3.10 verb; the **default‑on subset** is enumerated in §5.4. Extension‑introduced verbs (`recurring.materialized` → E3, `export.ready` → E4) are added **additively** (§3.11). | 🔧 A parallel notification enum (e.g. a hand‑invented `member.joined`) would drift from the feed's verbs — a second source of truth for "what happened" (X2 in spirit). Unify: the feed and the notifier name events identically. |
| **EXT‑D5d** | **Provider = AWS SES `eu-central-1` (Frankfurt), behind `IEmailSender` (EXT‑D0f) with in‑app templates.** Comparison in §5.2.1. Swapping provider is config‑only. | v1 §3.12 declared server‑sent invite email an extension with **no provider chosen and no delivery designed**. Residency (EU), a signed DPA, bounce/complaint webhooks, and low <10k/mo cost pin SES‑EU; server‑side rendering (EXT‑D0f) keeps it swappable. |
| **EXT‑D5e** | **Fan‑out reads `activity_log` (§3.10) as the single source; a `notification.fanout` worker on the E0 jobs substrate consumes it via a `notification_cursor` watermark, applies prefs, collapses bursts, and dispatches.** At‑least‑once + idempotent (X5). | 🔧 This is the **multi‑instance fix** §4.5 flagged: v1's in‑process `Channel<T>` only sees its own instance's writes. A DB‑backed cursor + the E0 drain make fan‑out correct across N instances and across a rolling deploy (EXT‑D0h). |
| **EXT‑D5f** | **Invite email is security‑critical (X6).** The v1 invite token (hashed at rest, §1.3) now travels in an email link → the E9a landing URL; **7‑day TTL** (Appendix A, unchanged), **no token in logs**, **SPF + DKIM + DMARC on `paybitch.app`**, and **per‑user + per‑group daily send caps**. | An unbounded "email this invite" endpoint turns the API into a mail cannon (spam, reputation burn, address‑enumeration). The token in an email is a new exposure of existing v1 credential material — it inherits the invite‑token posture wholesale. |
| **EXT‑D5g** | **Live Activity (v3) = a distinct APNs token class.** `devices.kind ∈ {'apns','apns_live_activity'}`; a Live Activity token registers on its **own** `devices` row, leaving the §1.3 PK/uniqueness untouched. Additive (§3.11). | Live Activity push‑to‑start / update tokens are separate from the standard device token; tagging by `kind` routes each fan‑out to the right APNs path without a schema break. |
| **EXT‑D5h** | **Digests (v4+) are opt‑in, per‑user send‑time timezone, RFC 8058 one‑click `List-Unsubscribe`, and a signed no‑auth unsubscribe endpoint (X6). Digest consent is a per‑user preference (`users.digest_opt_in`, default `false`) — NOT a row in `email_suppressions`. The one‑click unsubscribe sets `digest_opt_in = false`; it never writes a suppression row (EXT‑D5i).** Digest content is **computed on demand** (X2); cross‑currency totals appear only in an X3 `display` block (needs E2 Live FX). | Digests are marketing‑adjacent mail: consent + one‑click opt‑out are non‑negotiable (GDPR). But **consent and deliverability suppression are different facts** — unsubscribing from a weekly digest must not drop a magic‑link recovery code — so consent lives in a per‑user flag and `email_suppressions` stays reserved for hard bounce/complaint/manual. Storing a digest "balance" would be a stored balance by another name (D4/X2). |
| **EXT‑D5i** | **Transactional sends check HARD suppression only; digests additionally check consent.** Invite / `magic_link` (E6) / `export_ready` send **iff** `IsSuppressedAsync` (hard: `reason IN ('bounce','complaint','manual')`) is false. A digest sends **iff** not hard‑suppressed **AND** `users.digest_opt_in = true`. The one‑click unsubscribe writes the consent flag, never a suppression row. | 🔧 **This is the account‑recovery DoS fix.** As E0 §0.4.4 ships it, `IsSuppressedAsync` is a **single blanket bool** over `email_suppressions` whose `reason` CHECK includes `'unsubscribe'` — so writing an unsubscribe row there (the naïve reading of E0's one‑click note) makes `IsSuppressedAsync` return true on **all** later sends and silently drops that user's account‑recovery magic link **and every group invite** — a self‑inflicted DoS. This chapter **narrows** the E0 path: `email_suppressions` is written **only** by the bounce/complaint webhook (+ manual admin), so a hard bounce/complaint still suppresses transactional too (you cannot deliver to a dead address), while a digest opt‑out never touches it. **Coordination note:** E0 §0.4.4's `IsSuppressedAsync` comment (`bounce/complaint/unsubscribe`) and the `'unsubscribe'` `reason` value are refined here to hard‑suppression semantics; the digest opt‑out home moves to `users.digest_opt_in`. |

#### 5.2.1 EU email provider comparison (→ EXT‑D5d)

| Provider | EU residency | DPA / SCCs | Deliverability | Price <10k/mo | Bounce/complaint webhooks | Exit friction |
|---|---|---|---|---|---|---|
| **AWS SES `eu-central-1`** ✅ | Frankfurt region | DPA + SCCs | Good (needs warm‑up + own DKIM) | **Cheapest** (~$0.10/1k) | SNS notifications (delivery/bounce/complaint) | Low — plain SMTP/API behind our interface |
| Postmark | US‑hosted | DPA (US) | **Best‑in‑class** (transactional‑only) | ~$15/10k | First‑class webhooks | Low, but data leaves EU |
| Resend | US **or** EU region | DPA | Good, great DX | ~$20/mo tier | Svix‑signed webhooks | Low |
| Scaleway TEM / Brevo / Mailjet | ✅ EU entities (FR) | EU DPA | Adequate | Competitive | Yes | Low, EU‑native |

**Chosen: SES `eu-central-1`** — EU residency + DPA/SCCs + lowest cost + native bounce/complaint
feed, all behind `IEmailSender` (EXT‑D0f). Templates render **in‑app** (cs/en), so if
deliverability ever forces a move to Postmark/Resend it is a **config swap**, not a template
migration — and the Postgres‑mirrored `email_suppressions` list (E0 §0.4.4) survives the swap.

**Invite landing (E9a).**

| # | Ruling | Why / what it fixes |
|---|--------|---------------------|
| **EXT‑D9a‑a** | **Unauthenticated `GET /i/{token}` renders a minimal HTML landing: group name, inviter display name, expiry, and a store/Universal‑Link button — and STRICTLY LESS than the §3.8.1 JSON preview.** No member list, **no balances, no ghost nets.** IP rate‑limited, own X6 partition. | 🔧 The §3.8.1 JSON preview is IP‑rate‑limited but *authenticated‑adjacent* (an app rendering the accept screen); the landing is a **public, crawlable, shareable web page** — putting a ghost's per‑currency nets there would leak financial PII to anyone with the link. The landing is deliberately narrower than the preview (D6 posture, tightened). |
| **EXT‑D9a‑b** | **Universal Links route straight into the app.** An `apple-app-site-association` maps `/i/*`; if the app is installed the OS opens it directly to the §3.8.1 accept flow; otherwise the page shows the App Store link and **carries the token through** so a post‑install first launch resumes the accept. | A raw invite link today opens nothing useful in a browser. Universal Links + token‑carry make "tap the link → land in the accept sheet" work whether or not the app is installed. |
| **EXT‑D9a‑c** | **The landing is served by the SAME .NET app (a tiny Razor/static page), `noindex` + `Referrer-Policy: no-referrer`, token never logged, user‑controlled names HTML‑escaped, and unknown/expired/revoked tokens all render one uniform "invite unavailable" page.** | No new service (keeps the §6 Hetzner exit). `noindex`/`no-referrer` stop the token leaking into search indexes or downstream `Referer` headers. Uniform not‑found = no token oracle (D6; §3.12 already makes revoked ≡ never‑issued). Escaping closes stored‑XSS via a malicious group/inviter name. |

### 5.3 Schema (DDL delta)

No money rows: notifications create **nothing** in the ledger; any amount shown is fetched
(EXT‑D5a) or computed (X2). Money that *does* appear in digest content is authoritative per
currency (D1) plus an X3 `display` block — neither is stored (§5.4).

```sql
-- E5 · per-user / per-group / per-event / per-channel notification preferences.
-- NOT a group-scoped X1 sync entity — an account-settings surface like `devices` (§3.5.1).
CREATE TABLE notification_prefs (
    id         uuid PRIMARY KEY DEFAULT uuid_generate_v7(),
    user_id    uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    group_id   uuid REFERENCES groups(id) ON DELETE CASCADE,        -- NULL = GLOBAL override (all groups)
    event_type text NOT NULL,                                       -- a §3.10 activity verb (unified — EXT-D5c)
    channel    text NOT NULL CHECK (channel IN ('push','email')),   -- twin: 422 invalid_channel
    enabled    boolean NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now()
);
-- One override row per (user, scope, event, channel). 🔧 Partial unique indexes instead of a
-- magic-sentinel UUID — the exact pattern v1 uses for `categories` (§1.3). This realizes the
-- intended PRIMARY KEY(user_id, COALESCE(group_id,…), event_type, channel) WITHOUT a sentinel
-- (a real PRIMARY KEY cannot hold a COALESCE expression in Postgres):
CREATE UNIQUE INDEX uq_notif_prefs_global ON notification_prefs (user_id, event_type, channel)
    WHERE group_id IS NULL;
CREATE UNIQUE INDEX uq_notif_prefs_group  ON notification_prefs (user_id, group_id, event_type, channel)
    WHERE group_id IS NOT NULL;
CREATE INDEX ix_notif_prefs_user ON notification_prefs (user_id);

-- E5 · fan-out watermark. Server-internal worker state — never synced, never user-visible.
CREATE TABLE notification_cursor (
    worker           text PRIMARY KEY,       -- logical worker, e.g. 'notification.fanout'
    last_activity_id uuid NOT NULL,          -- last activity_log.id dispatched (uuidv7, time-ordered)
    last_created_at  timestamptz NOT NULL,   -- lower bound of the overlap re-scan (EXT-D5e)
    updated_at       timestamptz NOT NULL DEFAULT now()
);

-- E5 · Live Activity is a distinct APNs token class. Additive (§3.11).
ALTER TABLE devices
    ADD COLUMN kind text NOT NULL DEFAULT 'apns'
        CHECK (kind IN ('apns','apns_live_activity'));   -- twin: 422 invalid_device_kind
-- A Live Activity token registers on its OWN devices row (own client-generated id, §4.5),
-- so the §1.3 id PK and UNIQUE(user_id, apns_token) are untouched; `kind` routes fan-out.

-- E5 · digest consent (v4+) is a per-user PREFERENCE — NOT a suppression row (EXT-D5i).
-- Additive (§3.11); default false = opt-IN semantics. The one-click unsubscribe flips it false;
-- it NEVER writes email_suppressions, so a digest opt-out can't silence transactional mail.
ALTER TABLE users
    ADD COLUMN digest_opt_in boolean NOT NULL DEFAULT false;
```

`email_suppressions` (E0 §0.4.4, `citext` PK + `reason` + `created_at`) is **reused, not
redefined**. This chapter adds its *population* path — **only** the bounce/complaint webhook
(§5.7) plus manual admin — and its *read* path (`IsSuppressedAsync` before every send, EXT‑D0f).
🔧 Per **EXT‑D5i** the one‑click digest unsubscribe does **NOT** write here (that would make
`IsSuppressedAsync` drop the user's transactional mail too — the account‑recovery DoS); digest
consent lives in `users.digest_opt_in`. So `email_suppressions` means exactly one thing —
*this address hard‑bounced or complained; never mail it again, transactional or otherwise.*

### 5.4 API surface

| Method | Path | Purpose | Auth |
|---|---|---|---|
| `GET` | `/me/notification-prefs` | Raw overrides + default matrix + resolved effective set | Yes |
| `PUT` | `/me/notification-prefs` | Full‑replace the caller's overrides; each per‑group row runs a **D6 membership check** | Yes |
| `PUT` | `/me/devices/{id}` | v1 §4.5 body gains optional `kind ∈ {apns, apns_live_activity}` (additive) | Yes |
| `GET` | `/i/{token}` | **Unauthenticated** invite landing (HTML): minimal preview + Universal/Store link | No |
| `GET` | `/unsubscribe?token=` | One‑click **confirmation** page (prefetch‑safe, no state change) | No · signed token |
| `POST` | `/unsubscribe` | Digest opt‑out: set `users.digest_opt_in = false` (RFC 8058 `List-Unsubscribe-Post`) — **no `email_suppressions` write** (EXT‑D5i) | No · signed token |
| `POST` | `/webhooks/email/{provider}` | Bounce/complaint → `email_suppressions`; provider‑**signature** auth | No · provider‑signed |

**Default‑on matrix** (EXT‑D5b/c) — `event_type` accepts *any* §3.10 verb; these are the
notifiable defaults, everything else defaults **off** (user‑enable‑able):

| `event_type` (§3.10 verb) | push default | email default |
|---|---|---|
| `expense.created`, `expense.updated` | on | off |
| `settlement.recorded` | on | off |
| `member.added`, `member.claimed`, `invite.accepted` | on | off |
| `recurring.materialized` (E3, additive) | on | off |
| `export.ready` (E4, additive) | on | **on** |

Worked request/response — set two overrides, keep everything else at default:

```jsonc
// PUT /me/notification-prefs        If-Match: "4"
{
  "prefs": [
    { "groupId": null,       "eventType": "expense.created",     "channel": "push",  "enabled": true  },
    { "groupId": "grp_7Hk2", "eventType": "expense.created",     "channel": "push",  "enabled": false }, // mute THIS group
    { "groupId": null,       "eventType": "settlement.recorded", "channel": "email", "enabled": true  }
  ]
}
```
```jsonc
// 200 OK   ETag: "5"
{
  "version": 5,
  "resolved": [                                   // effective set after per-group → global → default
    { "groupId": "grp_7Hk2", "eventType": "expense.created",     "channel": "push",  "enabled": false, "source": "group"   },
    { "groupId": "grp_7Hk2", "eventType": "settlement.recorded", "channel": "email", "enabled": true,  "source": "global"  },
    { "groupId": "grp_7Hk2", "eventType": "member.added",        "channel": "push",  "enabled": true,  "source": "default" }
  ]
}
```

APNs payload for `expense.created` — **ids + a generic, name‑free `loc-key` only** (no
`loc-args`); the NSE re‑renders actor, group, and amount locally (EXT‑D5a):

```json
{
  "aps": { "mutable-content": 1, "sound": "default",
           "alert": { "loc-key": "NOTIF_NEW_ACTIVITY" } },
  "v": 1, "kind": "expense.created", "groupId": "grp_7Hk2", "entityId": "exp_9Qm4"
}
```
The NSE fetches `GET /groups/grp_7Hk2/expenses/exp_9Qm4` over the authenticated channel and
rewrites the body **on device** — hydrating actor name, group name, and amount — to "Alice added
Dinner at Lokál — 840 Kč in Chata 2026". No name ever leaves the device via APNs: the wire alert
is `NOTIF_NEW_ACTIVITY` → "New activity in a group", shown only if iOS drops the NSE under
resource pressure (the name‑ and amount‑free floor). This keeps §4.5's "no names" literal.

Digest content (v4+, rendered server‑side by `IEmailSender`, EXT‑D0f) — authoritative money is
D1; the cross‑currency total is **quarantined** in an X3 `display` block, never an authoritative `net`:

```jsonc
// digest content model — computed on demand (X2), NOT a stored or /sync entity
{
  "userId": "usr_alice",
  "period": { "start": "2026-07-01", "end": "2026-07-07" },
  "groups": [{
    "groupId": "grp_7Hk2", "name": "Chata 2026",
    "net": [                                       // AUTHORITATIVE per currency (D1) — string minor units
      { "currency": "CZK", "net": "-53000" },
      { "currency": "EUR", "net": "1200" }
    ],
    "display": {                                   // X3 QUARANTINE — display only, never a settlement/export column
      "currency": "CZK",                           // user's default = TARGET currency
      "amount": "-24600",                          // string minor units of TARGET (D1 shape, X3 block)
      "approx": true,
      "rateDate": "2026-07-06",
      "source": "cnb"                              // ČNB rate authority (E2 Live FX); provenance per D5
    }
  }]
}
```

**Transactional vs marketing (EXT‑D5i).** Invite / `export_ready` / `magic_link` (E6) are
**transactional** — sent **iff** the address is not **hard‑suppressed** (`IsSuppressedAsync` →
`reason IN ('bounce','complaint','manual')`). They are **structurally immune** to a digest
opt‑out, because that opt‑out writes `users.digest_opt_in`, **never** a suppression row.
**Digests** are marketing‑adjacent — sent **iff** not hard‑suppressed **AND** `digest_opt_in =
true` (EXT‑D5h). A hard bounce/complaint suppresses *everything* (you cannot deliver to a dead
address); only a digest opt‑out is purpose‑scoped. New problem codes (`invalid_channel`,
`unknown_event_type`, `invalid_device_kind`, all 422) register additively in Appendix B (§3.11).

### 5.5 Sync & offline impact (X1 checklist)

- **Flows through `/sync`? No.** `notification_prefs` is an **account‑settings** surface, joining
  `devices` in §3.5.1's "deliberately not synced" set. It is **not** group‑cached ledger data, so
  it takes **no** `change_log.entity_type`, no `client_id`, and no per‑group tombstone — X1 binds
  *group‑scoped client‑editable* entities, which this is not.
- **Concurrency:** the prefs set carries an aggregate `version`/`ETag`; `PUT` is a full replace
  guarded by `If-Match` (last‑write‑safe across the user's own devices), not a per‑row merge.
  Low‑stakes settings do not need the reject‑and‑merge ceremony of money aggregates (D8).
- **Tombstones:** "reset to default" = **delete** the override row; resolution falls back to
  global/default. The client re‑reads `/me/notification-prefs` (no tombstone needed — nothing
  group‑cached references it).
- **`notification_cursor`** is server‑internal worker state — never synced, never user‑visible.
  **`devices.kind`** is an additive column on the already‑unsynced `devices` table — zero sync impact.
- **Bootstrap:** a fresh install `GET`s `/me/notification-prefs` alongside `/groups` (§3.5.5),
  same as it registers a device.
- **The landing (E9a)** is an unauthenticated web surface — outside `/sync` entirely.

### 5.6 GDPR / privacy impact (D7 / X4 checklist)

- **On `DELETE /me` anonymize (§4.4):** `notification_prefs` rows are **hard‑deleted** in the
  anonymize transaction — pure account settings, zero ledger value — joining the §4.4 step 7
  purge cluster (`devices`, `refresh_tokens`, `auth_identities`). Live Activity + standard push
  tokens vanish with their `devices` rows there. The `users.digest_opt_in` flag is a pure
  preference reset with the anonymized `users` row (no ledger value).
- **`email_suppressions` SURVIVES erasure (documented ruling).** A **bounced or complained**
  address must **never** be re‑mailed even after the account is gone; the row holds only
  `email + reason + created_at`, is unlinked from the ledger, and its retention is a
  purpose‑limited legal/legitimate‑interest basis (honoring a hard deliverability signal).
  Optionally store it hashed for extra minimization. A **digest opt‑out is not here** — it lives
  in `users.digest_opt_in` (EXT‑D5i) and is moot once the account is anonymized. This is the
  ruling the E10a/digest review gate demands.
- **PII in notifications:** EXT‑D5a keeps amounts, who‑owes‑whom, **and names off** APNs (ids only;
  NSE pulls over the authenticated channel and hydrates actor + group + amount on device). The
  pre‑hydration `loc-key` fallback is a **generic, name‑free** string ("New activity in a group"),
  so §4.5's "no names" holds literally on the wire. **Digest email** carries amounts (financial PII)
  — but only to the user's **own** verified address, over a consented, suppressible,
  unsubscribe‑able channel.
- **Invite email (EXT‑D5f):** carries the group name + inviter's own name (the inviter consented
  by inviting) + the token link — no other member's PII, no balances.
- **Blobs (X4): none.** This feature owns no blob class.
- **Landing (E9a):** the token is never written to logs; IP is retained only transiently for
  rate limiting. The page exposes strictly less than the JSON preview (no nets, no member list).

### 5.7 Security notes (mandatory review — X6)

Three **new unauthenticated surfaces**; each gets its own X6 treatment (own rate‑limit partition,
enumeration‑safe uniform responses, hashed/ signed tokens — the v1 §3.8/§4.3 invite pattern as template).

1. **Invite‑token‑in‑email → landing `GET /i/{token}` (EXT‑D5f, EXT‑D9a‑*).** The token is the
   *existing* v1 invite token, **hashed at rest** (§1.3) — the email is a new *exposure* of it, so:
   token **never logged** (redacted in access logs + Serilog); resolved by hash; unknown / expired
   / revoked all render one **uniform** "invite unavailable" page (no oracle — §3.12 already makes
   revoked ≡ never‑issued); `noindex` + `Referrer-Policy: no-referrer` so it can't be crawled or
   leak via `Referer`; user‑controlled group/inviter names are **HTML‑escaped** (stored‑XSS).
   Own IP rate‑limit partition (§5.7.1). SPF + DKIM + DMARC (`p=quarantine`→`reject`) on
   `paybitch.app` so the invite mail isn't spoofable and lands in inboxes.
2. **Unsubscribe forgery → signed token (EXT‑D5h).** The unsubscribe token is an **HMAC signature**
   over `{userId, digestType, purpose}` with a server secret — self‑verifying, so forging an
   unsubscribe for *another* user is infeasible without the secret. 🔧 `GET /unsubscribe` is a
   **safe, no‑state confirmation page** (so Gmail/Apple link‑prefetch bots can't silently
   unsubscribe everyone); the **actual opt‑out** — setting `users.digest_opt_in = false`, **not**
   an `email_suppressions` write (EXT‑D5i, so a forged/successful unsubscribe can never suppress
   a victim's *transactional* mail) — happens on `POST /unsubscribe` via RFC 8058
   `List-Unsubscribe: <...>, List-Unsubscribe-Post: List-Unsubscribe=One-Click`. Idempotent;
   own rate‑limit partition (per IP + per token).
3. **Webhook auth → provider signature (EXT‑D5d).** `POST /webhooks/email/{provider}` **verifies
   the provider's cryptographic signature before any mutation** — for SES‑EU: validate the SNS
   message signature against Amazon's X.509 cert and confirm the subscription; reject
   unsigned/invalid → `403`. The route path carries a high‑entropy secret segment as
   defense‑in‑depth, and the endpoint **never trusts the payload's email** without a valid
   signature (else anyone could suppress an arbitrary address = a targeted mail‑DoS). Own
   rate‑limit partition sized for provider batch bursts.

Additional:
- **authz (D6):** `/me/notification-prefs` is caller‑scoped. Writing a **per‑group** override runs
  the indexed `(group_id, user_id)` membership check **first** → `404` to non‑members, so prefs
  can't be used to probe group existence.
- **PII‑in‑push ruling (EXT‑D5a):** verified by test (§5.8) — the APNs payload is asserted to
  contain no amount/title/name.
- **Over‑posting (§4.3):** explicit DTOs. `notification_prefs` `PUT` binds only
  `{groupId?, eventType, channel, enabled}`; `event_type` validated against the §3.10 verb set
  (`422 unknown_event_type`), `channel`/`kind` against their enums (`422 invalid_channel` /
  `invalid_device_kind`). No binding to entities.

#### 5.7.1 Rate limits (Appendix A style — new rows, register in Appendix A)

| Constant | Proposed v1‑style value | Partition |
|---|---|---|
| Invite **email** send — per user | **20 / day** | per user (mirrors invite‑create, Appendix A) |
| Invite **email** send — per group | **50 / day** | per group (stops one member spamming a group's would‑be members) |
| Invite **landing** `GET /i/{token}` | **60 req/min** | per IP (own partition) |
| Unsubscribe `GET`+`POST /unsubscribe` | **30 req/min** | per IP **and** per token |
| Email webhook `POST /webhooks/email/{provider}` | **600 req/min** | per provider/IP (bounce bursts are batchy) + signature‑gated |
| Push burst‑collapse | reuse **30 s/device** (Appendix A) + collapse ≥3 same‑group events into one summary push (name‑free `loc-key` on the wire; the NSE renders "3 new expenses in Chata 2026" on device) | per device |

### 5.8 Test plan

**Money invariants (property).** This feature writes **nothing** to the ledger, so the property
is *non‑interference*: hash the `(expenses, expense_splits, settlements)` state before and after
a full fan‑out batch, a digest render, an email send, and a landing render → **unchanged**;
`Σ net == 0` per currency (§3.3) holds because no path touches money rows. **Converted money
never enters the ledger:** assert the digest `display` block (X3) is display‑only — it is never
written back, never appears in an authoritative column, and carries `rateSource`/`rateDate`
provenance (D5).

**Preference resolution (property + example).**
`resolve(user, group, event, channel)` = first non‑null of {per‑group override, global override,
default matrix}. Example: global `expense.created/push = false` + group override `= true` → that
group notifies, others silent. Idempotent `PUT` (same body twice → same resolved set, `version`
bumps once). Unknown `event_type`/`channel` → `422`.

**Fan‑out (X5 idempotency + at‑least‑once).** 🔧 The `notification_cursor` deliberately does **not**
need §3.5.3's advisory‑lock monotonicity: notifications are at‑least‑once + idempotent, so the
worker scans `activity_log` by `(created_at, id)` with a **bounded overlap re‑scan** and lets
idempotency absorb duplicates — unlike `/sync`, which cannot skip a `seq`. Tests: a worker
double‑fire on one `activity_id` → the `email.send` job's `IdempotencyKey = notify:{activityId}:{userId}`
dedupes → **exactly one** email; a duplicate push is coalesced/harmless; a worker killed
mid‑batch resumes from the last committed `notification_cursor` with **no dropped event** (E0
stale‑lease reclaim, EXT‑D0h). Burst collapse: 3 `expense.created` in one group within the
window → one summary push.

**Landing (E9a).** Valid token → minimal preview (group name + inviter + expiry, **no nets, no
member list**); revoked token → byte‑identical "unavailable" page as an expired one (no oracle);
malicious group/inviter name → escaped in the HTML (no XSS); `noindex` + `no-referrer` headers
present; token absent from access logs.

**Unsubscribe.** Tampered/forged token → HMAC fail → rejected; `GET` performs **no** state change
(prefetch‑safe); `POST` suppresses; replay idempotent.

**Webhook.** Unsigned / bad‑signature payload → `403`, **no** `email_suppressions` write; valid
bounce → suppression upsert; a subsequent send to that address is **skipped** (`IsSuppressedAsync`),
transactional included.

**Transactional immunity to digest opt‑out (EXT‑D5i — regression for the account‑recovery DoS).**
Unsubscribe from digests (`POST /unsubscribe` → `digest_opt_in = false`; assert **no**
`email_suppressions` row was written), then assert: (a) a subsequent `invite` **and** `magic_link`
(E6) send to that address is **NOT** skipped; (b) a subsequent **digest** send IS skipped (consent
gone); (c) by contrast a `bounce` webhook for the same address **DOES** skip the next
invite/magic_link (hard suppression outranks purpose). This pins the invariant that a marketing
opt‑out can never suppress account‑recovery or invite mail.

**PII‑in‑push (EXT‑D5a).** Assert the APNs payload has no `amount`/title/**name** — only
`{kind, groupId, entityId}` + a name‑free `loc-key` (`NOTIF_NEW_ACTIVITY`) with **no `loc-args`**
(literal §4.5); assert the actor first name + group name appear **only** in the NSE‑rendered body,
which the test drives through the mutable‑content NSE fetching the authenticated endpoint.

**authz.** `PUT` a per‑group pref for a group the caller isn't in → `404` (D6).

### 5.9 Dependencies · tier · effort

- **Depends on:** **E0** — jobs substrate (EXT‑D0a/b/h) for the fan‑out worker + `email.send`,
  `IEmailSender` (EXT‑D0f), `email_suppressions` (E0 §0.4.4); **v1** — `activity_log` source of truth
  (§3.10), `devices`/APNs (§4.5), the invite token + preview (§3.8.1), rate limiting (§4.3),
  anonymize transaction (§4.4), Appendix A/B. **Digests (v4+) additionally depend on E2 (Live FX)** for the
  X3 cross‑currency `display` block (FX snapshot, D5). **Live Activity (v3)** depends on APNs
  Live Activity push‑to‑start/update tokens.
- **Tier:** **v2.5** — server‑sent invite email (closes the §3.12 cut) + basic push prefs (on/off) +
  invite landing (E9a). **v3** — full per‑user/per‑group prefs matrix + Live Activity tokens.
  **v4+** — digests + smart reminders.
- **Effort:** **M.**
- **Mandatory review gate:** **Security** — invite‑token‑in‑email exposure, unsubscribe forgery,
  webhook signature auth, the digest‑opt‑out‑vs‑hard‑suppression split (EXT‑D5i — no transactional
  DoS), and the PII‑in‑push ruling (EXT‑D5a — no names/amounts on the wire, §4.5 literal).


## 6. E6 — Email auth (magic-link / OTP) as a second provider

> **Mandatory review gate: Security** (§0.5.3) — the *highest-priority* security review of the
> whole extensions phase. Three vectors dominate: **account-takeover via cross-provider
> merge**, **enumeration**, and **identity-linking abuse**. Every ruling below is written to
> close one of them.

### 6.1 Scope & user value

A second identity provider — passwordless email — alongside Sign in with Apple. A user enters
an email, receives a 6-digit one-time code (also delivered as a tap-to-login universal link),
and is signed in or provisioned. This unlocks **non-Apple platforms**, **account recovery**
when a device is lost, and **invite-accept without SIWA friction** for people who don't want
to sign in with Apple. It lifts the v1 §4.1 reservation (`auth_identities.provider = 'email'`
was "rejected everywhere in v1") — an **additive** change (§3.11): a new endpoint accepts a
value the schema already reserved; no existing field changes meaning. **Tier v3, effort M.**

### 6.2 Decisions

| # | Ruling | Why / what it fixes |
|---|--------|---------------------|
| **EXT-D6a** | **Passwordless only.** The credential is a single-use OTP; there is **no password column, ever**. "Reset" *is* login — the same OTP flow. | A password store is the single largest breach liability, and reset already *is* a magic link — so passwords add credential-stuffing surface, HIBP k-anonymity checks, and Argon2id tuning for **zero** UX gain over OTP. Passkeys/WebAuthn are a **v4+** note, not a v3 promise. |
| **EXT-D6b** | **Identity authority is `auth_identities(provider, subject)` (v1 §1.3 `UNIQUE`), not `users.email`.** For the email provider, `subject` = the normalized (lower-cased, trimmed; citext) address. **v1's `users.email UNIQUE` is dropped**; `email` is redefined as a non-authoritative *contact* attribute. | 🔧 Closes a real collision: a SIWA user with a real `users.email = alice@x` would make a later email-OTP provision of `alice@x` violate `UNIQUE(email)` → 500 or a forced merge. Uniqueness belongs on the credential (`provider, subject`), which already guarantees one email-login per address globally. Dropping a **DB** constraint no wire client ever observed is not a `/v2` (§3.11 is about the API surface). |
| **EXT-D6c** | **NEVER auto-merge on email equality — separate-by-default.** Two identities are the same human **only** when explicitly linked (EXT-D6d). If `alice@x` already exists as an email account and the same human later signs in with Apple presenting a real `alice@x`, SIWA provisions a **distinct** `users` row. Apple **private-relay / Hide-My-Email** addresses (`@privaterelay.appleid.com`, the iCloud relay variants) are **rejected as email-provider subjects** (`422 relay_email_not_allowed`). | The classic pre-verified-email takeover: auto-merging by email lets a compromise of *one* factor reach the *other* account's ledger. Email equality is never proof of same-human. Relay addresses are Apple-routed forwarders, never a mailbox we can prove control of — letting one become an email credential would shadow the Apple identity. |
| **EXT-D6d** | **Linking a second identity requires FRESH proof of BOTH sides:** a valid **logged-in session** (side A — the existing account) **and** a completed **OTP to the target email** (side B — control of the new address). Two calls: `link/start` → `link/verify`, both authenticated. Never link from an unauthenticated flow; never link on email equality. | This is the *only* safe merge path. Session-only would let a hijacked session bolt on a credential; OTP-only would let an attacker who guessed an email attach it to *their* session. Requiring both makes silent cross-account attachment unrepresentable. |
| **EXT-D6e** | **OTP token design (X6).** 6-digit code; stored as `SHA-256(pepper ‖ code)` (app-level pepper from secret env, so a DB-only leak isn't offline-reversible); **10-min TTL**; **single-use** (`consumed_at`); **≤5 verify attempts** then the row is invalidated; **per-`purpose`** (`login`/`link`/`verify_change`); bound to `email` (and `user_id` for link/change). Rate-limited **per-email AND per-IP** on `…/start`, per-IP on `…/verify`. | Mirrors the invite/refresh hashing of v1 §3.8/§4.1. A 6-digit space is only ~20 bits, so security is the **online** envelope — attempts cap × TTL × rate limits — not the hash; the pepper covers the offline-leak case. Per-purpose binding stops a `login` code being replayed into a `link`. |
| **EXT-D6f** | **Enumeration-safe uniform response.** `POST /auth/email/start` (and `…/link/start`, `…/email/change/start`) **always return `200 {"status":"sent"}`** — existing address, unknown address, suppressed address, all identical, constant-time. Suppression is checked *before* sending but **never leaked**. `…/verify` failures collapse to **one** code `invalid_or_expired_code` (wrong ≡ expired ≡ consumed ≡ attempts-exhausted). | "If that address exists, we sent a code." Account existence must not be probeable through any auth surface (D6 posture, extended to unauthenticated endpoints). Collapsing verify failures denies an oracle for "is this the right code but stale?". |
| **EXT-D6g** | **Session parity — one session mechanism.** `…/verify` issues the **same** access-JWT + opaque rotating refresh family as SIWA (v1 §4.1); no second session type. A brand-new address **provisions like first SIWA** (`display_name` via the §4.1 precedence table **minus** row 1 `fullName`: email local-part → localized default), subject to a **per-email provisioning cap** (Appendix A, mirroring §4.3's per-Apple-`sub` cap). | Reuses the audited session, refresh-rotation-with-grace, and `token_epoch` machinery wholesale. A second session mechanism would be a second thing to get wrong. |
| **EXT-D6h** | **Email change** = OTP to the **new** address (`purpose='verify_change'`, authenticated), then set `users.email`, stamp `email_verified_at`, **bump `token_epoch`**, and update the email identity's `subject` if one exists. A **grace security-notice** goes to the **old** address (informational, not a gate). Pending `invites.email` addressed to the old address are **not** rewritten — that field is a non-binding hint (v1 §3.12); the token authorizes, not the email. | `token_epoch++` is the clean "the account's contact/credential moved, re-establish" signal. Chasing `invites.email` would be churn with no security value — invites are token-authorized (§3.8). |
| **EXT-D6i** | **`DELETE /me/identities/{provider}` refuses removing the *last* usable identity** (`409 last_identity` — use `DELETE /me` to close the account instead). Unlink **hard-deletes** the `auth_identities` row (pure credential material) and **bumps `token_epoch`**. Unlinking **Apple** additionally **revokes the upstream Apple token** (v1 §4.4 step 9 mechanics). | Removing the last identity is self-lockout; the DB can't authenticate a ghost of an account. Hard-delete + epoch bump make the unlink take effect within the §4.1 bound. Discarding the Apple grant honors App Review 5.1.1(v) parity. |
| **EXT-D6j** | **An erased email is FREED, not tombstoned.** The v1 §4.4 anonymize already nulls `users.email` (step 2) and hard-deletes `auth_identities` (step 7); E6 adds **`DELETE FROM email_login_tokens WHERE user_id = @uid OR email = @preScrubEmail`** to the same transaction. After erasure the address maps to **no identity** and a future controller of that mailbox may provision a fresh account with it. `email_suppressions` (E0 §0.4.4) is **retained** as legitimate-interest deliverability state. | The strongest right (D7) demands the credential and its transient artifacts vanish. Tombstoning the address would permanently deny a real future owner of a common mailbox — over-reach. Suppression is a bounce/complaint fact about the *mailbox*, correctly inherited by any later account on it. |
| **EXT-D6k** | **Deliverability = auth availability, stated honestly.** If email delivery is down, email-only users can't obtain a **new** OTP; **SIWA is always available on iOS** as the escape hatch. Already-logged-in email users survive an outage until their refresh chain lapses (Appendix A) — only *new logins / recovery* are blocked. Provider **bounce/complaint webhooks** feed the E0 §0.4.4 suppression pipeline (shared with **E5** digests); email-auth health is a **status-page** line, not hidden. | An auth provider whose availability equals a third party's SMTP uptime must say so. The universal-link path degrades to manual code entry if Universal Links fail; the code always works. |

### 6.3 Schema (DDL delta)

No new **synced** entity and **no money** — this is auth plumbing, so X1's sync columns do
**not** apply (like `refresh_tokens` / `invites` / `devices`, `email_login_tokens` is
deliberately outside `/sync`, §3.5.1). X3/X4 are N/A (no converted money, no blobs).

```sql
-- (1) users: email becomes a non-authoritative CONTACT attribute (EXT-D6b), + verification stamp.
ALTER TABLE users DROP CONSTRAINT IF EXISTS users_email_key;   -- v1 §1.3 `email citext UNIQUE`
ALTER TABLE users ADD  COLUMN email_verified_at timestamptz;   -- set by email verify / change / link
CREATE INDEX ix_users_email ON users (email)
    WHERE email IS NOT NULL AND deleted_at IS NULL;            -- lookups only; NOT unique (EXT-D6b)

-- (2) auth_identities: provider 'email' is now permitted (v1 reserved it, §4.1).
--     subject = normalized lower-cased email; UNIQUE(provider, subject) (v1 §1.3) is the
--     single authority for "one email-login per address, globally". No column change needed;
--     the boundary rejects relay addresses as subjects (EXT-D6c).

-- (3) OTP store — one row per issued code. NOT group-scoped, NOT synced, no version/client_id.
CREATE TABLE email_login_tokens (
    id          uuid PRIMARY KEY DEFAULT uuid_generate_v7(),
    email       citext NOT NULL,                              -- target mailbox (lower-cased)
    code_hash   bytea  NOT NULL,                              -- SHA-256(pepper ‖ 6-digit code) — EXT-D6e
    purpose     text   NOT NULL CHECK (purpose IN ('login','link','verify_change')),
    user_id     uuid   REFERENCES users(id) ON DELETE CASCADE,-- NULL for 'login' of a new/unknown address;
                                                              --   bound for 'link'/'verify_change' (side-A proof)
    expires_at  timestamptz NOT NULL,                         -- created_at + 10 min (Appendix A)
    consumed_at timestamptz,                                  -- single-use; conditional UPDATE on verify
    attempts    int    NOT NULL DEFAULT 0,                    -- ≥ MAX_OTP_ATTEMPTS ⇒ invalidate (Appendix A)
    created_ip  inet,                                         -- abuse forensics; PII → purged with the row
    created_at  timestamptz NOT NULL DEFAULT now()
);
-- Newest live code per (email, purpose): the verify lookup and the per-email rate probe.
CREATE INDEX ix_email_tokens_lookup ON email_login_tokens (email, purpose, created_at DESC)
    WHERE consumed_at IS NULL;
CREATE INDEX ix_email_tokens_expiry ON email_login_tokens (expires_at);   -- prune sweep
-- CHECK/boundary twin: `purpose` union parse → 422; relay-domain email → 422 relay_email_not_allowed.
```

Rows are pruned past expiry by a `kind='email_token.prune'` job on the E0 §0.4.2 substrate
(idempotent `DELETE … WHERE expires_at < now() - grace`); their `created_ip` therefore never
outlives the short TTL.

### 6.4 API surface

| Method | Path | Purpose | Auth |
|---|---|---|---|
| `POST` | `/auth/email/start` | Request a login/provision OTP for an address (uniform 200) | **No** (X6) |
| `POST` | `/auth/email/verify` | Consume OTP → session; provisions if the address is new | **No** (X6) |
| `GET` | `/me/identities` | List linked identities (provider + masked subject) — so the client knows if unlink is safe | Yes |
| `POST` | `/me/identities/link/start` | Send OTP to a target email to attach it (side-B proof) | Yes |
| `POST` | `/me/identities/link/verify` | Consume OTP → attach `email` identity to the caller | Yes |
| `DELETE` | `/me/identities/{provider}` | Unlink an identity; refuses the last one (`409 last_identity`) | Yes |
| `POST` | `/me/email/change/start` | Send OTP to a **new** contact address | Yes |
| `POST` | `/me/email/change/verify` | Consume OTP → set `users.email`, bump `token_epoch`, notify old | Yes |

Worked flow — first-time email login (provision). **No money crosses this surface**, so the
X3 quarantine block is N/A; the response is exactly the v1 §4.1 token shape.

```jsonc
// POST /auth/email/start          (unauthenticated; per-email + per-IP rate-limited, Appendix A)
{ "email": "Alice@x.com" }
```
```jsonc
// 200 OK — ALWAYS this body, whatever the address's state (EXT-D6f)
{ "status": "sent" }
```
The email carries the 6-digit code **and** a universal link
`https://paybitch.app/auth/email?e=alice%40x.com&c=402913` that pre-fills the same code.

```jsonc
// POST /auth/email/verify         (unauthenticated; per-IP rate-limited)
{ "email": "alice@x.com", "code": "402913" }
```
```jsonc
// 200 OK — same envelope as POST /auth/apple (v1 §4.1); a NEW email identity + user provisioned
{
  "accessToken": "eyJhbGciOiJFUzI1NiIs…",     // JWT, 15 min, carries the `epoch` claim (§4.1)
  "expiresIn": 900,
  "refreshToken": "b64u_7d1aFk…",              // opaque, stored hashed, rotation family (§4.1)
  "user": {
    "id": "usr_alice2", "displayName": "alice",  // local-part — §4.1 precedence minus fullName (EXT-D6g)
    "email": "alice@x.com", "emailVerified": true,
    "defaultCurrency": "CZK", "locale": "cs",
    "isNewUser": true
  }
}
```

Failure surfaces: bad/expired/consumed/exhausted code → `401 invalid_or_expired_code`
(uniform, EXT-D6f); relay address at `…/start` → `422 relay_email_not_allowed`; rate trip →
`429 rate_limited` + `Retry-After`; `link/verify` where the address already backs **another**
user → `409 identity_taken`; unlinking the last identity → `409 last_identity`.

### 6.5 Sync & offline impact (X1 checklist)

- **Flows through `/sync`?** **No.** `email_login_tokens` and `auth_identities` are
  server-side auth state, joined to v1 §3.5.1's *"deliberately not synced"* list (`invites`,
  `refresh_tokens`, `devices`). There is no client-cached, offline-editable entity here — X1's
  `client_id`/`version`/tombstone contract does not apply.
- **`change_log.entity_type`?** None added. A user's `email`/`email_verified_at` live on
  `users`, which is not in the sync stream (only `group_members` is); a display-name change is
  unaffected. No new `entity_type`.
- **Tombstone behavior?** N/A — token rows are pruned, not tombstoned; identity unlink is not
  a synced event.
- **Bootstrap?** Untouched. `GET /me` (v1 §3.1) already carries the profile; it gains
  `emailVerified` and (via `GET /me/identities`) the linked-provider list — both **additive**
  optional fields (§3.11). Offline outbox/delta-pull (§7) is not involved.

### 6.6 GDPR / privacy impact (D7 / X4 checklist)

- **On `DELETE /me` (anonymize, v1 §4.4):** the address is already scrubbed (step 2 nulls
  `users.email`) and the `auth_identities('email', …)` row already hard-deleted (step 7 deletes
  **all** the user's identities). E6 adds **one statement to the same transaction**:
  `DELETE FROM email_login_tokens WHERE user_id = @uid OR email = @preScrubEmail` — ordered with
  the step-4 `@preScrubEmail` capture so it runs before the email is lost. The erased address is
  then **freed / re-claimable** (EXT-D6j), never tombstoned.
- **Blob classes?** None — E6 owns no blobs (X4 N/A).
- **PII in this feature:** the target `email` and `created_ip` on `email_login_tokens`. Both are
  transient (10-min TTL + prune) and both vanish in the anonymize transaction above.
  `email_verified_at` is a timestamp, not identifying on its own, and rides `users` through the
  existing scrub. `email_suppressions` (E0 §0.4.4) is retained as legitimate-interest deliverability
  state (EXT-D6j) — *coordination note:* if a stricter erasure posture is wanted, hashing the
  suppression key is an E0-owned change, out of scope here.
- **Grace notice (EXT-D6h)** to the old address on email change contains no ledger data — a bare
  "your Paybitch email was changed" security notice via `IEmailSender` (E0 §0.4.4).

### 6.7 Security notes (X6 checklist — the mandatory review)

- **Unauthenticated surfaces (X6):** `/auth/email/start`, `/auth/email/verify`. Each gets its
  **own rate-limit partition** (Appendix A): `start` per-email **and** per-IP; `verify` per-IP
  (caps online code brute-force spread across many addresses); a **per-email/day provisioning
  cap** (mirrors §4.3's per-Apple-`sub` cap). Trusted-proxy forwarded-header config is mandatory
  (v1 §4.3) or the IP partition is spoofable.
- **Enumeration (X6):** `…/start` returns a **constant** `200 {"status":"sent"}` — existing,
  unknown, and suppressed addresses are indistinguishable and constant-time (always do the
  hash-and-enqueue-or-skip work without an observable branch). `…/verify` failures collapse to
  one `invalid_or_expired_code`. No endpoint reveals whether an address maps to an account.
- **Hashed at rest (X6):** `code_hash = SHA-256(pepper ‖ code)`; single-use `consumed_at`;
  10-min TTL; ≤5 attempts then invalidate — the invite/refresh pattern of v1 §3.8/§4.1.
- **Verify race:** consume via a conditional `UPDATE … SET consumed_at = now() WHERE id = @id
  AND consumed_at IS NULL` — 0 rows ⇒ lost the race ⇒ uniform error, exactly the v1 §3.8.1
  invite-accept idempotency shape.
- **Takeover defenses:** never auto-merge (EXT-D6c); linking needs both proofs (EXT-D6d);
  relay addresses rejected as subjects; OTP requires **inbox control**, so a pre-verified-email
  takeover of `victim@x` is impossible without `victim`'s mailbox. Per-`purpose` binding + the
  `user_id` binding on `link`/`verify_change` stop a `login` code being cross-used to attach an
  address to a different account.
- **Authz (D6):** the `/me/*` identity and email endpoints are strictly self-scoped (the session
  *is* the subject); there is no group scope, so no membership check applies — but likewise no
  cross-user reach.
- **Over-posting:** explicit request DTOs (v1 §4.3) — `verify` binds only `{email, code}` or
  `{code}`; `user_id`, `provider`, `email_verified_at`, `consumed_at` are **never** wire-bound.
- **Session hygiene:** `token_epoch++` on email change (EXT-D6h) and on identity unlink
  (EXT-D6i); unlink hard-deletes the credential; Apple unlink revokes upstream. `created_ip` is
  abuse-forensic only (ids-at-INFO logging hygiene, v1 §4.3) and is PII → purged with the row.

### 6.8 Test plan

**Invariants (the money guardrail).** E6 touches no ledger table. Property test: an email
login / provision / link / change / unlink creates or edits **only** `users`, `auth_identities`,
`email_login_tokens`, `refresh_tokens` — **never** `expenses`, `expense_splits`, `settlements`,
or `group_members`; therefore `Σ net == 0` per currency is preserved vacuously, and **no
converted money ever enters the ledger** (X3 N/A but asserted). Provisioning a fresh email user
yields zero `group_members` rows.

**Enumeration property.** `/auth/email/start` returns a **byte-identical** body and lies within
a fixed timing tolerance for (existing user) vs (unknown address) vs (suppressed address).

**Takeover / linking example cases.**
1. Email-OTP for `victim@x` without inbox control → verify can never succeed (no code obtained).
2. Email account `alice@x` exists; the same human then SIWA-signs-in with real `alice@x` → **two
   distinct `users` rows**, not merged (EXT-D6c); no `UNIQUE(email)` violation (EXT-D6b).
3. Apple relay `k3xw9@privaterelay.appleid.com` submitted to `/auth/email/start` →
   `422 relay_email_not_allowed`; no identity created.
4. `link/start` called unauthenticated → `401` (endpoint requires a session).
5. Logged-in caller `link/verify`s an address they never OTP'd → rejected (no matching live
   `purpose='link'` row bound to their `user_id`).
6. A `purpose='login'` code replayed at `link/verify` → rejected (purpose + `user_id` mismatch).
7. `link/verify` of an address already backing another user → `409 identity_taken`; the victim's
   identity is untouched.

**Token-lifecycle example cases.**
8. 6th verify attempt on one code → row invalidated, uniform `invalid_or_expired_code`.
9. Code used past 10-min TTL → uniform error, indistinguishable from a wrong code.
10. Consumed code reused → uniform error.
11. Two concurrent verifies of the same code → **exactly one** issues a session (conditional
    `consumed_at` UPDATE; the loser gets the uniform error).
12. 11th `/auth/email/start` for one email (or one IP) within the window → `429 rate_limited`.

**Session / lifecycle example cases.**
13. Email change → old access token → `401 token_epoch_stale` on next request; refresh re-reads
    the bumped epoch.
14. Unlink the last identity → `409 last_identity`; account still logs in via the remaining one.
15. Unlink Apple → upstream Apple revoke enqueued (v1 §4.4 step 9 mechanics); email login still
    works.
16. Anonymize a user, then a new human provisions the **same** freed address → succeeds as a
    brand-new account (EXT-D6j); the prior account's ledger is untouched (D7).

### 6.9 Dependencies · tier · effort

- **Depends on:** **E0 §0.4.4** `IEmailSender` + `email_suppressions` (OTP/notice delivery,
  suppression, bounce/complaint webhook) and **E0 §0.4.2** jobs substrate (`kind='email.send'`,
  `kind='email_token.prune'`). Reuses v1 **§4.1** session issuance + refresh rotation +
  `token_epoch`; **§4.4** anonymize (adds the token-purge statement); **§4.3** rate-limit infra
  and DTO/over-posting posture; the **§3.8/§4.3** invite-token hashing pattern (X6 template).
- **Additive wire changes (§3.11):** new endpoints (§6.4); new optional `GET /me` fields
  `emailVerified` + linked-provider list; the `auth_identities.provider = 'email'` value (already
  reserved in v1 §1.3). New **problem codes** (Appendix B, additive): `invalid_or_expired_code`
  (401), `relay_email_not_allowed` (422), `identity_taken` (409), `last_identity` (409);
  `rate_limited` (429) reused. New **Appendix A constants:** OTP TTL **10 min**, max verify
  attempts **5**, `/auth/email/start` rate limits (per-email + per-IP), email provisioning cap
  (per-email/day), `email_token.prune` cadence + grace.
- **Internal migration:** drop `users_email_key`, add `users.email_verified_at` + `ix_users_email`,
  create `email_login_tokens` — an expand step, no wire break.
- **Tier v3 · effort M.** Two OTP round-trips × three flows (login / link / change) over one
  hashed-token table, riding entirely on E0 primitives and the v1 §4.1 session; the hard work is
  the security review (takeover, enumeration, linking), not new infrastructure.


## 7. Comments on expenses (E10a)

### 7.1 Scope & user value

A flat, chronological comment thread hangs off every expense — the place where "wait, wasn't
this only a deposit?" gets said **next to the bill**, not in a side chat the ledger can't see.
Splitwise-parity; it is the single cheapest driver of real retention (a group with active
threads is a group that opens the app). Comments touch **no money** — they are pure discussion
surface — which is what makes them a safe, high-value v3 feature. **Tier v3, effort M.**

### 7.2 Decisions

| # | Decision | Why / what it fixes |
|---|----------|---------------------|
| **EXT-DC1** | **A comment's author is a real `users` row (`author_user`), never a ghost.** Ghosts have no session, so they cannot author. Display name is resolved **at read time via the author's `group_members` row** in that group. | Comments are speech acts — they need an accountable identity that can later edit/delete *its own* words. Deliberate divergence from §1.1: money FKs point at member rows (ghost-capable, because a bill can name someone with no account); **authorship** is an authenticated act, so it points at the user. |
| **EXT-DC2** | **A comment body is free-text PII → SCRUBBED-IN-PLACE on GDPR erasure (the D7 boundary).** On `DELETE /me` (§4.4) the erased user's comment `body` is overwritten at rest with a localized neutral placeholder and `version` is bumped; the row is **NOT tombstoned** and the erase emits a `comment` **upsert** (not a delete). The anonymized comment survives and keeps rendering — author → "former member" (EXT-DC1), body → the placeholder. | States the D7 line explicitly: **ledger rows are retained** (other members' financial records, legitimate-interest basis, D7) — and a comment **shell** is retained the same way, because a live dispute thread is audit trail worth keeping readable (EXT-DC8). What is destroyed is only the *body text*, the data subject's own unstructured words with **no counterparty-record value**. Scrubbing in place rather than tombstoning is exactly what makes the placeholder body meaningful: an erased author's comment still holds its slot in the transcript, wordlessly. |
| **EXT-DC3** | **Comments join the FULL X1 sync contract.** `client_id` + `UNIQUE(group_id, client_id)` create-idempotency (D9), `version int` + `If-Match`/`412` (D8), soft-delete tombstones surfaced in `/sync` (§3.5), indexed membership-check-first with `404` to non-members (D6). `change_log.entity_type = 'comment'`. | X1 is non-negotiable for any client-cached, client-editable entity. A comment authored offline must dedup on outbox retry and merge on concurrent edit exactly like an expense — one contract, zero special cases. |
| **EXT-DC4** | **Flat list only — no threading, no `parent_id`, no replies.** Comments are a chronological log per expense. | Thread UIs are scope rot: nested render, collapse state, per-subtree notification fan-out. A dispute next to a bill wants a transcript, not Reddit. Escape hatch stays additive (§3.11): a nullable `parent_comment_id` can be added later without a `/v2`. |
| **EXT-DC5** | **Author-only edit (no time limit); author-or-admin delete; `edited` surfaced via `version > 1`.** An admin/owner may **tombstone** (moderate) any comment in their group but may **never edit** it. | No server-side edit window: an edit created offline may drain from the outbox arbitrarily late (§7), and a time bound would poison that entry — so edits are allowed indefinitely, and the client shows "edited" whenever `version > 1` (equivalently `updatedAt > createdAt`). Admins get a removal lever without the power to put words in someone's mouth (mirrors §3.6 void authz: creator or admin+). |
| **EXT-DC6** | **Comment notifications route through the E5 notification-preference gate from day one; the silent sync trigger always fires; no body ever rides the wire.** | Comments are chattier than ledger events and MUST be muteable per-group at launch or they are a spam regression. The §4.5 **silent** push (`content-available:1`) still fires so the thread syncs (correctness floor, §7); any *visible* new-comment alert is an E5 concern and honors E5 prefs. Bodies are PII (§4.3 logging hygiene) — never in a payload. No second notification-policy store (X2). |
| **EXT-DC7** | **`author_user` is server-derived from the authenticated caller — never client-supplied.** The create/patch DTOs bind only `{clientId, body}` / `{body}`. | Anti-impersonation + over-posting guard (§4.3). A body claiming a different author is impossible because the field does not exist on the wire; the caller already passed the D6 membership check, so the author is provably a current member of the group. |
| **EXT-DC8** | **Parent-tombstone rule: a new comment on a soft-deleted expense → `404`; existing comments survive their parent's soft-delete.** A comment is **never** auto-tombstoned when its expense is deleted. | A tombstoned aggregate is immutable (§3.2b), and adding discussion to a gone expense is meaningless — but the *existing* dispute thread is exactly the audit trail you want to keep readable, so it stays (like a soft-deleted member still renders in history, §3.8.3). |

### 7.3 Schema (DDL delta)

```sql
CREATE TABLE comments (
    id           uuid PRIMARY KEY DEFAULT uuid_generate_v7(),
    group_id     uuid NOT NULL REFERENCES groups(id)   ON DELETE CASCADE,
    expense_id   uuid NOT NULL REFERENCES expenses(id) ON DELETE CASCADE,   -- expenses are soft-deleted (§4.4) ⇒ cascade is a backstop, never fires
    author_user  uuid NOT NULL REFERENCES users(id)    ON DELETE RESTRICT,  -- a real account, never a ghost (EXT-DC1); users are soft-deleted, so RESTRICT never fires — the §4.4 scrub is explicit
    client_id    text,                                                      -- offline id → idempotency (D9/X1)
    body         text NOT NULL CHECK (length(body) BETWEEN 1 AND 2000),     -- free-text PII (EXT-DC2); 422 twins comment_empty / comment_too_long
    version      int  NOT NULL DEFAULT 1,                                   -- optimistic concurrency (D8/X1)
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),                        -- updatedAt > createdAt ⇒ client renders "edited" (EXT-DC5)
    deleted_at   timestamptz,                                               -- soft-delete tombstone → /sync (§3.5)
    UNIQUE (group_id, client_id)                                            -- per-group create idempotency (D9/X1)
);

CREATE INDEX ix_comments_expense ON comments(expense_id, created_at, id) WHERE deleted_at IS NULL;  -- chronological thread page
CREATE INDEX ix_comments_author  ON comments(author_user);                                          -- the D7 erase-scrub route (EXT-DC2)

-- change_log gains a 'comment' entity_type — additive, no /v2 (§3.11):
-- NOTE: Adds 'comment' to the change_log.entity_type CHECK (v1 §3.5) — the CHECK's
--       authoritative full value set is consolidated in Appendix A.
```

No money columns exist on `comments` by construction (§7.1), so the D1 minor-units rule and the
X3 quarantine block are **trivially satisfied — there is nothing to quarantine.** The
author-∈-group invariant needs no trigger: the author is the caller, and the D6 membership-first
check (§4.2) already proved the caller is a live member of `group_id`.

### 7.4 API surface

| Method | Path | Purpose | Auth |
|---|---|---|---|
| `GET`  | `/groups/{g}/expenses/{e}/comments` | List a thread (cursor, `created_at ASC`) | Yes (member) |
| `POST` | `/groups/{g}/expenses/{e}/comments` | Add a comment | Yes (member) |
| `PATCH`| `/groups/{g}/expenses/{e}/comments/{c}` | Edit — **author only**, `If-Match` required | Yes (author) |
| `DELETE`| `/groups/{g}/expenses/{e}/comments/{c}` | Soft-delete — **author or admin**, `If-Match` required | Yes (author / admin) |

`If-Match` is **required** on `PATCH` and `DELETE` (absent → `428 precondition_required`; stale
→ `412 version_conflict` with the current representation) — the same D8 machinery money
aggregates use, reused verbatim so comments add **zero** new concurrency code.

```jsonc
// POST /groups/grp_7Hk2/expenses/exp_9Qm4/comments
{
  "clientId": "cmt_local_A1B2C3D4",        // offline id → (groupId, clientId) idempotency (D9/X1)
  "body": "Tohle byla jen záloha — doplatek přijde příští týden."
}
```
```jsonc
// 201 Created   ETag: "1"
{
  "id": "cmt_5Kp9",
  "clientId": "cmt_local_A1B2C3D4",
  "groupId": "grp_7Hk2",
  "expenseId": "exp_9Qm4",
  "author": {                              // hydrated via the author's member row (EXT-DC1)
    "userId": "usr_bob",
    "memberId": "mem_bob",
    "displayName": "Bob"                   // → localized "former member" once the author is GDPR-anonymized; the comment still renders (EXT-DC2 scrub-in-place, not tombstone)
  },
  "body": "Tohle byla jen záloha — doplatek přijde příští týden.",
  "createdAt": "2026-07-07T09:14:02Z",
  "updatedAt": "2026-07-07T09:14:02Z",
  "edited": false,                         // version == 1
  "version": 1,
  "deleted": false
}
```

There is **no `amount`/`currency` field anywhere in this resource** — comments are money-inert.
Writes to an **archived** group are rejected `409 group_archived` by the standard §3.7 filter
(one code path, no per-handler drift). A `POST` under a soft-deleted expense → `404` (EXT-DC8).
Idempotent replay of a create follows §3.4 exactly: identical canonical body (`body`) → `200` +
existing record; divergent body under the same `(groupId, clientId)` → `409 client_id_conflict`
with `existingId`.

### 7.5 Sync & offline impact (X1 checklist)

- **Flows through `/sync`?** Yes. Wire `type: "comment"`; `data` = the full §7.4 representation
  (no money, so no D1 concern), tombstones carry `data: null`.
- **`change_log` entity_type:** `comment` (DDL delta §7.3). New append rows extend the §3.5.2
  table: comment **create / edit** → `comment` upsert; comment **delete** → `comment` delete.
  Written in the **same transaction** as the mutation via the existing `ChangeLogWriter` — a
  comment mutation that appends nothing is a sync bug by definition (§3.5.2).
- **Tombstone behavior:** a comment tombstone is applied as a local soft-delete — the client
  keeps the `id` (idempotency on replay) but **stops rendering the body**; the comment simply
  **leaves the thread.** 🔧 This is the one X1 nuance vs. §3.5.4's "never purge" rule: a deleted
  *member* or *category* keeps rendering inside historical expenses, but a deleted *comment* has
  no host row to render in — removed content should disappear. It is still a soft-delete locally
  (the tombstone id is retained), not the hard purge reserved for `access` revoke.
- **Bootstrap:** §3.5.5 re-bootstrap step 2 gains one per-group paginated read —
  `GET …/expenses/{e}/comments` — fetching **live** comments only (`deleted_at IS NULL`); absent
  ⇒ dropped, so no `includeDeleted` variant is needed (contrast members/categories). Join/claim
  bootstrap is the same path scoped to one group.
- **Client apply:** idempotent upsert keyed by `id`, apply if incoming `version` ≥ local;
  `deleted: true` always wins; apply multiple same-`id` rows in `seq` order (§3.5.4).

### 7.6 GDPR / privacy impact (D7 / X4 checklist)

- **On `DELETE /me` (anonymize, §4.4):** add a step to the anonymize transaction —
  `UPDATE comments SET body = <localized neutral placeholder>, version = version + 1
  WHERE author_user = @uid` — **destroying the body text at rest** (EXT-DC2) while leaving
  `deleted_at` untouched: the comment is scrubbed **in place, not tombstoned.** Then emit a
  `comment` **upsert** `change_log` row (§3.5.2) for every comment still live so each device
  **replaces** the cached body with the placeholder (an already-tombstoned comment was purged
  from clients at delete time and needs no further wire row — its at-rest body is scrubbed all
  the same, for the D7 boundary). This runs in the same transaction as the §4.4 member-row
  scrub; `author_user` still points at the surviving (anonymized) `users` row, so `NOT NULL`
  holds and the retained comment resolves its author to "former member" at read time (EXT-DC1) —
  the dispute thread stays readable, only the PII words are gone (EXT-DC8).
- **The D7 boundary, stated:** ledger numbers (expenses/splits/settlements) keep their
  **values**; a comment keeps its **shell** but loses its **body text**. Both artifacts are
  retained and anonymized — but only the comment's content is destroyed, because the two have
  different lawful bases (other members' financial records vs. the data subject's own free text,
  which has no counterparty value).
- **Other users' comments are untouched.** We scrub only `author_user = @uid`. If an erased
  user's name is quoted *inside another member's* comment, that is another person's speech and is
  not parsed or scrubbed — a documented, defensible residual, consistent with §4.4's shared-record
  posture.
- **Blob classes (X4):** none. Comments are text-only; there are no attachments and therefore no
  two-phase blob delete here. If image/file comments ever ship, the blob lives under the
  attachments extension (E1/E7) and obeys X4 there — this feature owns metadata-free text only.
- **`/me/export` (Art. 15/20):** gains a `comments` array (the user's authored comments) —
  additive (§3.11). No money, so the D1 string rule is moot.
- **Logging:** comment bodies are financial-adjacent PII — never logged at INFO, never in
  `activity_log.metadata` (server stores ids + field names only, §3.10), never in a push payload.

### 7.7 Security notes (X6 checklist)

- **New unauthenticated surface? None.** Every comment endpoint is Bearer-JWT and group-scoped,
  so X6's hashed-token / enumeration-safe / rate-limit-partition template (invite pattern, §4.3)
  **does not apply** — there is no public landing, verify, or download URL to harden.
- **AuthZ (D6):** indexed `(group_id, user_id)` membership check is the **first** statement of
  every comment endpoint; non-members get `404`, never `403`/`409` — no existence leak. Edit by a
  non-author → `403 comment_edit_forbidden`; delete by a non-author non-admin →
  `403 comment_delete_forbidden` (the member is proven, so `403` is not a leak, per §3.8.2).
- **Over-posting (EXT-DC7):** request DTOs bind `{clientId, body}` (create) / `{body}` (patch)
  only. `author_user`, `version`, timestamps, `deleted_at` are server-owned; unknown fields are
  rejected (§4.3). Author identity is taken from the token, never the body.
- **XSS:** the server stores raw text and never interpolates a body into HTML or an email; the
  iOS client renders as plain text. Body is bounded `1–2000` chars (CHECK backstop + 422 twins).
- **Rate limit (Appendix A style):** register **comment create — 60/min per user** (spam floor;
  authoritative value lives in Appendix A, this section only names it). Trips → `429 rate_limited`
  with `Retry-After`.
- **New problem codes (register in Appendix B, additive §3.11):** `comment_empty` (422),
  `comment_too_long` (422), `comment_edit_forbidden` (403), `comment_delete_forbidden` (403).

### 7.8 Test plan

**The money invariant is the headline, by absence.** Comments are money-inert: assert that **no**
comment code path (`create/edit/delete`, and the §4.4 erase-scrub, EXT-DC2) writes to `expenses`,
`expense_splits`, or `settlements`, and that `Σ net == 0` per currency (§2.2) is **byte-identical
before and after** any comment operation. This is the strongest form of X3/D1 compliance — there
is no converted or approximate money to keep out of the ledger because there is no money field at
all.

Property cases (FsCheck):
- Create is idempotent on `(group_id, client_id)`: replay → `200`, same row; divergent body → `409`.
- Concurrent edit: stale `If-Match` → `412` carrying the current representation; absent → `428`.
- Membership-first: a non-member hits every endpoint → uniform `404`, identical `title`/`detail`.
- Author cannot be spoofed: for any body, persisted `author_user` == authenticated caller.
- Sync round-trip: any create/edit/delete emits exactly one `comment` `change_log` row; the row
  replays as an idempotent upsert (apply-if `version ≥ local`).

Example cases:
- Comment on an expense → `201`, `ETag: "1"`, author hydrated via the member row.
- Edit → `version 2`, `edited: true`; second reader sees "edited".
- Author deletes → `comment` tombstone in `/sync`, thread drops it locally.
- Admin deletes another member's comment → tombstone; **admin edit attempt → `403`**
  (`comment_edit_forbidden` — moderation removes, never rewrites).
- Non-author non-admin edit/delete → `403`.
- Comment on an **archived** group → `409 group_archived`; on a **soft-deleted** expense →
  `404` (EXT-DC8); pre-existing comments on that expense still list.
- Body `""` → `422 comment_empty`; 2001 chars → `422 comment_too_long`.
- **D7 erase:** the author's comment bodies are scrubbed at rest with the rows **retained, not
  tombstoned** (`deleted_at` stays `NULL`); a DB probe finds no residual text; the comments
  **still list**, rendering author → "former member" with the placeholder body; the erase emits
  a `comment` **upsert** (not a delete) per live comment; **other members' comments are intact**;
  the ledger and every `Σ net` are unchanged.
- Re-bootstrap (`410 sync_cursor_expired`) re-fetches live comments; offline create replay dedups.

### 7.9 Dependencies · tier · effort

- **Depends on:** v1 core — expenses (§3.2), delta sync (§3.5), and decisions D6/D7/D8/D9. Soft
  dependency on **E5 (notification preferences)** for push gating (EXT-DC6): comments ship without
  E5 — the silent sync trigger fires regardless — but any *visible* new-comment alert routes
  through E5 the moment E5 lands.
- **Coordination:** the §4.4 anonymize transaction gains the EXT-DC2 body-scrub step (auth/GDPR
  cluster); §3.5.2's append table and §3.5.5's bootstrap gain the `comment` rows (sync cluster);
  Appendix A gains the create rate-limit constant; Appendix B gains the four codes above.
- **Tier:** v3. **Effort:** M (one table, one `change_log` enum widen, four CRUD endpoints reusing
  the D6/D8/D9 machinery, one anonymize step, one bootstrap read — no new domain math).

### 7.10 E10 sibling assessment (verdicts, no deep design)

| Feature | Verdict | Rationale |
|---|---|---|
| **Reactions** (emoji on expenses/comments) | **v4+ — cheap once comments exist** | A reaction is a degenerate comment: `author_user` + target + an emoji token, no editable body. It reuses this chapter's X1 sync/authz machinery wholesale and is *simpler* on GDPR (an emoji is not PII — erase just tombstones the row, no body to scrub). Genuinely nice-to-have, not retention-load-bearing the way threaded disputes are, so it waits for v4. |
| **Server-side full-text search** | **NOT worth a backend — keep it client-side (affirms the §3.12 cut)** | The offline-first client already holds **every** expense in SwiftData (§7), so client-side search is free, instant, and works offline — a server round-trip is strictly worse. Czech FTS is also awkward: Postgres ships **no Czech stemmer**, so quality-parity needs `unaccent` + `pg_trgm` and still underperforms. And re-opening a server search re-introduces **server-side pagination**, which returns a *subset* view — a regression against the complete local mirror. Revisit only if a future breaks the offline-mirror assumption (a web client, or groups too large to mirror). |
| **Budgets** (per-group / per-category spend caps) | **v4+ — money-adjacent, per-currency, on-write soft check** | When built, it MUST obey the money rules: **per-currency only** (a budget lives in one currency and is checked against that currency's bucket — never a cross-currency sum, D5/X3); **computed on demand** (progress = Σ expenses in category+currency vs. limit, like balances — no materialized budget-progress store, D4/X2); and an **advisory on-write check that warns, never blocks** — a hard `4xx` on overrun would poison the offline outbox (the client can't know server-side budget state at write time), so overage is a display signal, not a rejection. Defer to v4. |


## 8. E8 — Scale & ops extensions + Hetzner exit runbook

### 8.1 Scope & user value

Not a feature — **insurance**. E8 keeps the v1 promise that leaving a managed host is
"point DNS, restore dump, `docker compose up`" (§6) *actually true* by writing the exit as a
tested, rehearsed runbook while the system is still small, and by pinning the exact
**trigger conditions** under which single-instance shortcuts taken in v1 (§4.1, §4.3, §4.5)
must be undone. Users never see any of it; what they get is a backend that can move off
Railway to Hetzner (full EU data ownership, ~€5/mo) in under an hour without losing a cent
or a sync cursor, and that scales past one instance without silently doubling every rate
limit. **Tier:** the exit runbook is a **document written now (v3, while small)**; the
distributed limiter / PgBouncer / read replicas are **trigger-gated infra (v4+)**, built only
when their EXT-D threshold actually fires — never on a calendar date.

### 8.2 Decisions

Scale work is the classic YAGNI trap: infra added "to be safe" is infra you now have to
migrate on the Hetzner exit. Every ruling below is therefore either (a) a **trigger** with a
measurable threshold and a named remedy, or (b) part of the **runbook artifact**. Nothing here
is scheduled; it is all conditional. The meta-rule holds throughout: E8 changes **deployment
topology, not the wire contract** — no field changes meaning, no `/v2` is forced (§3.11).

| # | Decision | Why / what it fixes |
|---|----------|---------------------|
| **EXT-D8a** | **Scale changes are trigger-gated, not roadmap-gated.** Each remedy below has a numeric trip-wire read off the observability stack (§6); until it trips, the v1 single-instance choice stands and is *correct*. | Premature infra (Redis, PgBouncer, replicas) is dead weight that complicates the exit + backups. The honest default at indie scale is **one instance + one Postgres**, and most triggers never fire. |
| **EXT-D8b** | **>1 app instance ⇒ a distributed rate limiter is REQUIRED before the second instance serves traffic.** The v1 `RateLimiter` is **in-memory, per-instance** (§4.3): two instances = 2× every Appendix A limit and independently poisonable partitions. The `token_epoch` per-instance cache (§4.1) moves to the **same shared store in the same change** — §4.1 explicitly says "move it to a shared store together with the rate limiter, not before." | §4.3 shipped the in-memory limiter as a *declared* single-instance shortcut. Adding a second instance without this silently halves the effectiveness of every auth/invite/provisioning cap — an auth-bruteforce and account-provisioning regression, not a perf one. |
| **EXT-D8c** | **The distributed store is Postgres, not Redis, by default** — a `rate_limit_counters` table with an atomic fixed-window `UPSERT … RETURNING` (§8.3). The `token_epoch` shared read is a keyed `SELECT` on the same DB. | A second stateful service is a second thing to back up, restore, secure, and *carry across the Hetzner exit*. The DB you already run absorbs multi-instance rate limiting to thousands of users at these limits (tens of writes/sec) with **zero new infra**. |
| **EXT-D8d** | **Redis must clear a bar before it is adopted:** sustained limiter/cache write contention that Postgres cannot serve at p95 (measured, not assumed) **and** a second independent need for an in-memory store (e.g. E5 fan-out at a scale `LISTEN/NOTIFY` can't hold). One need does not justify a second stateful service; two might. | Forces the tradeoff to be *paid for twice* before you take on the exit/backup cost. Until then, Postgres-for-everything keeps the runbook to one `pg_restore`. |
| **EXT-D8e** | **DB connections sustained >70% of `max_connections` ⇒ PgBouncer (transaction pooling)** — but **try Npgsql multiplexing + a right-sized pool first**, and only reach for PgBouncer if that doesn't hold. | Connection exhaustion crash-loops under load. Npgsql multiplexing often removes the need entirely; PgBouncer is the heavier answer and carries the prepared-statement pitfall in EXT-D8f. |
| **EXT-D8f** | **If PgBouncer lands in transaction mode, disable server-side prepared statements:** Npgsql `Max Auto Prepare=0` and `No Reset On Close=true`; SCRAM auth to the bouncer. | Transaction pooling hands each transaction a *different* backend, so a prepared statement cached on one is absent on the next → runtime `prepared statement "…" does not exist`. Multiplexing (EXT-D8e) sidesteps this because Npgsql multiplexes over its **own** pooled connections without an external pooler. |
| **EXT-D8g** | **Read replicas: the bar is deliberately near-unreachable.** Trip-wire = a *sustained read p95 regression* that survives indexing and query fixes. Honest ruling: with **D4 on-demand balance math (sub-ms)** and no materialized read models (X2), an indie-scale Paybitch is **write-light and read-cheap — replicas very likely never happen.** Documented so nobody builds them speculatively. | Replicas add replication lag as a new correctness variable (a balance read off a lagging replica could disagree with a just-committed write — a money-app footgun) for a bottleneck this workload doesn't have. |
| **EXT-D8h** | **The exit runbook is a TESTED artifact rehearsed quarterly against a scratch VPS — or it is fiction.** A restore that has never been performed is an assumption, not a capability. Rehearsal cadence and the smoke gate are part of the ruling, not aspiration. | §6 promised "tested restores"; this pins *tested* to a schedule with a pass/fail gate (§8.8), so the promise is falsifiable. |
| **EXT-D8i** | **Default cutover = `pg_dump`/`pg_restore` with a brief write-freeze** (small DB, RTO ≤ 30 min). **Logical-replication cutover (near-zero downtime) is reserved** for when the DB is too large to freeze — not built until then. | KISS: at v1 DB sizes a custom-format dump restores in minutes. Logical replication is real complexity (sequences don't replicate — EXT-D8k) bought only when downtime actually hurts. |
| **EXT-D8j** | **The sync spine must survive the migration byte-faithfully:** `change_log`, `change_log_watermark`, **and the `change_log.seq` bigserial value** are carried in the dump; post-restore `max(seq)` and `pruned_through_seq` are asserted non-regressed. | 🔧 If cursors don't survive, **every client `410`s** (§3.5.5) and re-bootstraps on first poll after cutover — a self-inflicted thundering herd. If the sequence isn't advanced past the restored max, the next insert collides or the watermark logic breaks. |
| **EXT-D8k** | **Blob migration is a no-op *iff* E1 chose portable object storage** (Hetzner Object Storage / any S3-compatible endpoint). DB rows hold **metadata + an opaque storage key** (X4), so a key-preserving bucket copy needs **no DB rewrite**; a provider-locked store forces a copy-and-re-key step (metadata `UPDATE`). | This coupling is exactly **why E1's provider ruling is load-bearing for the exit.** X4's "blobs referenced, never owned by Postgres" is what lets ledger data and blobs migrate on independent tracks. |
| **EXT-D8l** | **Cutover choreography is fixed:** DNS TTL pre-dropped to 60 s ≥ 24 h ahead; migrations applied as the discrete EF bundle step on the new host **before** any traffic (§6 — already multi-instance-safe); **secrets rotated selectively** — DB credentials + provider tokens rotate, **JWT signing keys are carried across** (`JWT_SIGNING_KEY_PEM`/`JWT_PREVIOUS_KEY_PEM`, §4.1) so a clean move forces **no mass re-auth** and `token_epoch` is **not** bumped; the smoke suite (§8.8) is the **hard gate before the DNS flip**. | A host move is not a compromise, so nuking every session is gratuitous. But the decommissioned host's disk holds the full ledger + PII, so either it is securely wiped or JWT keys + DB creds rotate post-cutover (EXT-D8n). |
| **EXT-D8m** | **Multi-instance audit is a gate on every new mutating component**, v1 and extension alike (§8.5 table). Anything that holds state in-process (a `Channel<T>`, an in-memory cache, a lease) must declare its multi-instance story before a second instance ships. | 🔧 The v1 `PushNotificationWorker` `Channel<T>` (§4.5) "only sees its own instance's writes" — a declared single-instance shortcut. Enumerating these once prevents each from becoming a silent data-loss bug the day the second instance boots. |
| **EXT-D8n** | **Backups and the decommissioned host are in scope for D7/EU residency.** Backups live in **EU-region, encrypted-at-rest** storage; a retired host's volume is **securely wiped or its secrets rotated**; `rate_limit_counters` (IP + user-id PII) is **excluded from the migration dump** and short-lived (X2 disposable). | The ledger's PII doesn't stop being PII because it's in a backup or on an old disk. GDPR anonymize (D7) operates on *live* rows; retention/erasure of backups is a separate, explicit policy. |

### 8.3 Schema (DDL delta)

**Baseline: none.** E8 introduces **no** money-bearing tables, no client-cached entities, and
touches no v1 column. X1 (sync contract) and X3 (quarantined converted money) are **n/a** — E8
owns no entity a client caches and no converted money.

The **single** table below is materialized **only when EXT-D8b fires** (a second app instance
is provisioned). It is disposable operational state, not a ledger store (X2), and deliberately
absent single-instance.

```sql
-- MATERIALIZED ONLY WHEN EXT-D8b TRIPS. The "distributed" limiter store is the DB you
-- already run (EXT-D8c). Fixed-window counters; NOT ledger-derivable (X2 n/a), never in
-- /sync (§3.5.1 entity_type set is unchanged), excluded from the exit dump (EXT-D8n).
CREATE TABLE rate_limit_counters (
    partition_hash bytea       NOT NULL,   -- HMAC of 'surface:dimension:value', e.g.
                                           -- HMAC('auth:ip:203.0.113.7') — hashed at rest (X6),
                                           -- so the table is not a plaintext IP log
    window_start   timestamptz NOT NULL,   -- floor(now(), window) — one row per window
    count          int         NOT NULL DEFAULT 0,
    PRIMARY KEY (partition_hash, window_start)
);
CREATE INDEX ix_rate_limit_gc ON rate_limit_counters (window_start);   -- GC by E0 job (X5)

-- Atomic increment-and-test, one round trip, safe across N instances:
--   INSERT INTO rate_limit_counters (partition_hash, window_start, count)
--   VALUES (@ph, @w, 1)
--   ON CONFLICT (partition_hash, window_start)
--   DO UPDATE SET count = rate_limit_counters.count + 1
--   RETURNING count;              -- reject with 429 when RETURNING count > limit
```

Fixed-window is chosen over sliding-log for KISS: at Appendix A limits the boundary-burst
weakness (up to 2× the cap across a window edge) is immaterial for auth/invite throttles, and
the single `UPSERT … RETURNING` is one statement. Windows are GC'd by the E0 job past the
widest configured window; a stale row is harmless (X5-idempotent). The `token_epoch` shared
read (EXT-D8b) is a plain keyed `SELECT users.token_epoch` on the existing table — **no new
schema**.

### 8.4 API surface

**No new client-facing endpoints.** The wire contract is unchanged (§3.11 additive meta-rule);
`rate_limited` (429, `Retry-After`) already exists in Appendix B and its shape is untouched —
E8 only changes *where the counter lives*. The one operational surface E8 hardens is the
existing readiness probe (§6), which becomes the **cutover gate's** machine-readable check.

| Method | Path | Purpose | Auth |
|---|---|---|---|
| `GET` | `/health` | Liveness (process up) — unchanged (§6) | No |
| `GET` | `/health/ready` | Readiness: DB, migration head, shared limiter store (once EXT-D8b fired), **sync watermark** — gates the DNS flip (EXT-D8l) | No (public body minimal; detail behind ops network / basic auth — §8.7) |

```jsonc
// GET /health/ready   (detailed body — ops network only)   200 when the target host is serviceable
{
  "status": "ready",
  "checks": {
    "db": "up",                    // SELECT 1 on the write pool
    "migrations": "current",       // EF bundle applied through the latest migration id (§6)
    "rateLimiterStore": "n/a",     // "up" once EXT-D8b fired; "n/a" while single-instance
    "syncWatermark": 41837         // max(change_log.seq) — NON-ZERO proves change_log restored
                                   //   and cursors survive the cutover (EXT-D8j); 0 ⇒ ABORT
  }
}
```

No money crosses E8's wire, so D1/X3 have nothing to quarantine here. The public body is
`{"status":"ready"}` — the detail above never leaks topology to the internet (§8.7).

### 8.5 Sync & offline impact

**Nothing E8 introduces flows through `/sync`.** `rate_limit_counters` is server-only
operational state; it is **not** in the §3.5.1 `entity_type` set and must never be added
(doing so would contradict X2's disposability intent). No `change_log` rows, no tombstones, no
`entity_type`, no bootstrap change.

The load-bearing sync impact is the **inverse**: the exit runbook must **preserve** the v1 sync
spine, or offline clients break en masse (EXT-D8j).

- **`change_log` + `change_log_watermark` migrate byte-faithfully** in the dump. Post-restore,
  assert `max(change_log.seq)` and `change_log_watermark.pruned_through_seq` match the source.
- **The `change_log.seq` bigserial is advanced** past the restored max (`pg_restore` of a
  custom-format dump restores sequence state — *verify it*; on the logical-replication path,
  sequences don't replicate — `setval` explicitly at switchover, EXT-D8k).
- **The `pg_advisory_xact_lock(CHANGE_LOG_LOCK_KEY)` visibility serialization (§3.5.3) needs no
  migration attention** — it is a Postgres advisory lock, already cross-instance-safe by
  construction (audit row below). It "just works" the moment a second instance boots.
- **Client-visible outcome of a clean cutover: zero `410 sync_cursor_expired`.** A spike in
  `410`s in the post-flip observation window (§8.9) means cursors did **not** survive — roll
  back, don't push through.

**Multi-instance audit (EXT-D8m).** Every stateful component, v1 + extensions, enumerated
assuming one instance today:

| Component | v1 state | Multi-instance status | Remedy |
|---|---|---|---|
| `RateLimiter` (§4.3) | in-memory, per-instance | **BROKEN** — 2× every cap | EXT-D8b: `rate_limit_counters` (§8.3) |
| `token_epoch` cache (§4.1) | per-instance, TTL ≤ 60 s | **Tolerable** — bounded by TTL + access-token ceiling; §4.1 says move it *with* the limiter | EXT-D8b: shared keyed `SELECT`, same change |
| `PushNotificationWorker` `Channel<T>` (§4.5) | in-process queue | **BROKEN** — a worker sees only its own instance's writes; other instances' mutations never fan out | Replace with **E5's durable outbox worker** (`FOR UPDATE SKIP LOCKED` poll) — E5 owns it; E8 records the dependency |
| E0 background jobs (prune, purge, counter GC) | scheduled | **Already safe** — at-least-once + idempotent (X5); add a single-flight **leader advisory lock** so N instances don't all fire one prune | E0-owned; lock is one `pg_try_advisory_lock` |
| `change_log` visibility lock (§3.5.3) | `pg_advisory_xact_lock` | **Already safe** — DB-level lock, not in-process | none |
| EF migrations (§6) | discrete bundle, pre-traffic | **Already safe** — never migrate-on-startup | none |

🔧 The two "BROKEN" rows are exactly the two shortcuts §4.3 and §4.5 *declared* as
single-instance-only. E8 does not fix them speculatively — it names the trigger (EXT-D8b) and
the owner (E5 for push) so they're fixed **in the same change** that adds the second instance,
never after it's already dropping pushes.

### 8.6 GDPR / privacy impact

| Concern | Ruling |
|---|---|
| **`rate_limit_counters` PII** | Partition keys derive from **IP addresses and user ids** — IP is personal data under GDPR. Stored **HMAC-hashed at rest** (§8.3, X6), never joined to the ledger, GC'd past the widest window (minutes), and **excluded from the exit dump** (EXT-D8n) so stale IP data is never carried to a new host. No D7 anonymize hook — the rows expire on their own and hold nothing user-authoritative. |
| **Blob classes (X4)** | E8 owns no blobs. The runbook's blob-migration step (EXT-D8k) is provider-mechanics only; blob *deletion* semantics and two-phase tombstoning remain E1/E7's (X4). A key-preserving copy touches no DB metadata, so no PII is rewritten. |
| **Backups (EXT-D8n)** | Backups contain the **full ledger + PII** (names, emails, amounts). They live in **EU-region, encrypted-at-rest** storage with an explicit retention policy. D7 anonymize operates on *live* rows only; erasing a user does **not** reach into historical backups — that divergence is documented, not hidden, and backups age out under retention. |
| **Decommissioned host** | Its volume holds the same ledger + PII. On retirement: **securely wipe** the disk, **or** rotate JWT signing keys + DB credentials (EXT-D8l/n) so any recovered image is inert. Outbound provider creds (APNs `.p8`, Apple client-secret key) that lived only on the old host are rotated regardless. |
| **This feature's own PII** | Only the transient hashed rate-counter partitions. Nothing new is logged at INFO (§4.3 logging hygiene stands — the readiness detail body carries no amounts or names). |

### 8.7 Security notes

- **X6 — the distributed limiter is still an X6 surface.** Every partition preserves v1's
  **trusted-proxy forwarded-header** config (§4.3) — a spoofable key is *worse* shared, because
  one poisoned partition now affects all instances. Partitions stay **per-surface** exactly as
  v1 (auth-by-IP, provisioning-by-Apple-`sub`, invite-create-by-user, invite-preview-by-IP —
  Appendix A), one HMAC-hashed key each (§8.3). Uniform `429 rate_limited` + `Retry-After`
  (Appendix B) — no enumeration signal, no change in body.
- **Readiness endpoint does not leak topology.** Public body is `{"status":"ready"}`; the
  detailed `checks` body (instance/DB/watermark) is served **only on the ops network or behind
  basic auth** — same posture as the §5 OpenAPI/Scalar scoping (dev/staging only, never public).
  `syncWatermark` and migration ids are operational fingerprints, not public facts.
- **Authz (D6) unchanged.** E8 adds no group-scoped route; the membership-check-first / 404
  posture is untouched. `/health*` are unauthenticated by design and carry no group data.
- **Over-posting: n/a** — no new request DTO binds user input (the limiter counter is written by
  the pipeline from the connection's derived key, never from a request body).
- **PgBouncer auth (EXT-D8f):** SCRAM to the bouncer, bouncer→Postgres creds in secret env
  (§6), never in the compose file. Transaction mode + `Max Auto Prepare=0` is a correctness
  *and* a "no cross-tenant statement leakage" requirement.
- **Cutover secrets (EXT-D8l/n):** DB creds + provider tokens rotate on move; JWT signing keys
  carried to avoid mass re-auth; `token_epoch` **not** bumped (a clean move is not a compromise).
  If the old disk isn't provably wiped, treat the move as a compromise and rotate JWT keys
  post-cutover on the §4.1 grace-window schedule.
- **Rate-limit partitions for any new unauthenticated surface** an extension adds (E5
  unsubscribe, E1 download URL, a future magic-link) get their **own** partition here, hashed
  and trusted-proxy-guarded — the invite-token pattern (§3.8.1/§4.3) is the template (X6).

### 8.8 Test plan

**The cutover smoke suite IS the primary test — and the DNS-flip gate (EXT-D8h/l).** Run
against the **restored** data on the new host, before any traffic:

1. `auth → POST /auth/apple` (or refresh) yields a valid access token.
2. `POST /groups` → `POST /groups/{g}/expenses` (EQUAL, 3 members) returns server-authoritative
   `shares` summing to `amount` (§3.2).
3. `GET /groups/{g}/balances` on a **restored** group asserts **`Σ net == "0"` per currency**
   (§3.3) — the money invariant, re-checked post-restore.
4. `GET /health/ready` → `syncWatermark > 0` and matches source `max(change_log.seq)` (EXT-D8j).
5. `GET /sync?since=<a pre-migration cursor>` returns a normal delta, **not** `410` (cursors
   survived).

**Green on all five ⇒ flip DNS. Any red ⇒ abort, stay on the old host.**

Property + example cases:

| Kind | Case | Invariant / expectation |
|---|---|---|
| Property | Limiter under N concurrent requests across 2 instances sharing `rate_limit_counters` | Admits **exactly `limit` per window** (± fixed-window boundary tolerance) — the whole point of EXT-D8b: two instances enforce the **same aggregate cap** one instance did |
| Property | Atomic `UPSERT … RETURNING` under contention | No lost increments; `count` monotone within a window; concurrent inserts serialize on the PK |
| Example | Restore rehearsal (quarterly, EXT-D8h) | Row counts per table match source; `max(change_log.seq)` and `pruned_through_seq` non-regressed; smoke suite green on a scratch VPS |
| Example | Sequence advance (EXT-D8j) | First `change_log` insert post-restore gets `seq > max(restored seq)` — no collision, no cursor gap |
| Example | Money faithfulness across migration | `Σ net == 0` per currency holds **byte-identically** before and after — integer minor units (D1) don't drift under dump/restore; no conversion happens, so **converted money never enters the ledger** (X3) is vacuously preserved |
| Example | PgBouncer transaction mode + `Max Auto Prepare=0` | A prepared-statement-heavy read path runs without `prepared statement does not exist` (EXT-D8f) |
| Example | Leader lock (E0, §8.5) | With 2 instances, exactly one runs the `change_log` prune per cycle; the other's `pg_try_advisory_lock` returns false and it skips |

If the quarterly rehearsal is skipped, the runbook is **presumed broken** (EXT-D8h) and the
"can we leave Railway" claim is downgraded to unverified until the next green rehearsal.

### 8.9 The Hetzner exit runbook

**Targets** (mirroring Appendix A's "existence + location is the contract; values are tunable"):

| Target | Value | Path |
|---|---|---|
| RPO (data loss) | **≤ 5 min** (PITR) / **≤ final-dump age** (dump path, with write-freeze ⇒ ~0) | either |
| RTO (downtime) | **≤ 30 min** | dump/restore (EXT-D8i default) |
| RTO (downtime) | **≤ 5 min** | logical-replication switchover (large-DB path) |
| Rehearsal cadence | **quarterly**, scratch VPS, smoke-gated | EXT-D8h |

**Path A — `pg_dump`/`pg_restore` (default, EXT-D8i).** Brief write-freeze; small DB.

1. **≥ 24 h before:** drop DNS A/AAAA TTL to **60 s** (EXT-D8l) so the flip propagates fast.
2. Provision **Hetzner CX22** (EU region), install Docker + Compose, clone `docker-compose.yml`
   (§6), load secrets: **rotated** DB creds, **carried** `JWT_SIGNING_KEY_PEM`/
   `JWT_PREVIOUS_KEY_PEM`, APNs `.p8`, Apple client-secret key.
3. Run the **EF migration bundle** against the new empty Postgres (discrete step, §6) — schema
   first, no traffic.
4. Deploy the app image on the new host, **kept out of DNS**; point its `/health/ready` at the
   new DB.
5. **Freeze writes** on the old host (maintenance flag → `503` + `Retry-After`), take a final
   `pg_dump --format=custom`.
6. `pg_restore` into the new Postgres. **Verify (EXT-D8j):** per-table row counts;
   `SELECT max(seq) FROM change_log`; `SELECT pruned_through_seq FROM change_log_watermark`;
   confirm the `change_log` sequence is advanced past `max(seq)` (custom-dump restores it —
   check, don't assume).
7. **Run the §8.8 smoke suite** against the new host. **GATE: green or abort.**
8. **Flip DNS** to the new host; wait one TTL.
9. **Observe** (≥ one TTL + a few minutes): error rate, the **`Σ balances == 0` alert** (§6),
   and the **`410 sync_cursor_expired` rate — must stay ~0** (EXT-D8j). Old host still up,
   writes still frozen = instant rollback.
10. **Decommission** old host (EXT-D8n): securely wipe the volume **or** rotate JWT keys + DB
    creds; rotate old-host-only provider creds; retain old backups in EU cold storage per policy.

**Point of no return:** step 8's flip is reversible *only while writes remain frozen*. Once the
new host **accepts a write**, rollback means restoring new→old (reverse dump) and losing nothing
only if you keep the freeze until confident. Keep the observation window short and the freeze on
until it passes.

**Path B — logical replication (near-zero downtime).** Only when the DB is too large to freeze:
publication on old, subscription on new, let it catch up; short switchover (quiesce writes
seconds, confirm replication lag 0, promote new, flip DNS). **Sequences do not replicate over
logical replication — `setval` `change_log`'s (and every other) sequence at switchover**
(EXT-D8j/k), or the first insert collides.

**Blob migration (EXT-D8k, X4):** if E1 stored blobs in Hetzner Object Storage / any
S3-compatible endpoint, the store is already portable → **config endpoint change only, no data
copy, no DB rewrite** (rows hold opaque keys, X4). If E1 chose a provider-locked store, insert a
bucket-copy step; a **key-preserving** copy needs no metadata change, a re-key needs a metadata
`UPDATE`. This is why E1's provider ruling is a hard dependency of a clean exit.

**Ops checklist (in lieu of adversarial review).**

Pre-cutover:
- [ ] DNS TTL dropped to 60 s ≥ 24 h ago.
- [ ] New host provisioned, EU region, Docker up, compose cloned.
- [ ] Secrets loaded: DB (rotated), JWT keys (carried), APNs, Apple client-secret.
- [ ] EF migration bundle applied to the new DB; `/health/ready.migrations == "current"`.
- [ ] Backup/restore path proven in the **last quarterly rehearsal** (EXT-D8h) — date on file.

Cutover:
- [ ] Writes frozen on old host (`503` + `Retry-After`).
- [ ] Final `pg_dump --format=custom` taken.
- [ ] `pg_restore` complete; row counts match; `max(change_log.seq)` + `pruned_through_seq`
      verified; sequence advanced (EXT-D8j).
- [ ] Blob store reachable from new host (no-op if portable; else copy done — EXT-D8k).
- [ ] **Smoke suite green** (§8.8) — auth, expense, `Σ net == 0`, watermark > 0, no `410`.
- [ ] DNS flipped; one TTL elapsed.

Post-cutover:
- [ ] `Σ balances == 0` alert quiet; error rate nominal; **`410` rate ~0**.
- [ ] Old host held (frozen) through the observation window, then decommissioned per EXT-D8n.
- [ ] Secrets rotation / disk wipe confirmed; old provider creds revoked.
- [ ] Runbook updated with anything the rehearsal or real cutover surprised you with.

Rollback (any pre-flip gate red, or post-flip alarms before the point of no return):
- [ ] DNS flipped back to old host; writes un-frozen there.
- [ ] New host quarantined; delta (if any writes reached it) reconciled or discarded.
- [ ] Root cause captured; next rehearsal targets it.

### 8.10 Dependencies · tier · effort

- **Dependencies:** **E0** (job scheduler for counter GC, `change_log`/`activity_log` prune, and
  the single-flight leader lock — X5); **E1** (blob provider ruling — portable object storage
  makes EXT-D8k a no-op); **E5** (durable outbox worker with `FOR UPDATE SKIP LOCKED` that
  replaces the §4.5 `Channel<T>` under multi-instance). Inherits Appendix A (limit values) and
  §6 (hosting, migration-as-discrete-step, observability, the `Σ balances` alert).
- **Tier:** **exit runbook = DOCUMENT v3** — authored and first-rehearsed while the system is
  small (a runbook written under load is a runbook written too late). **Distributed limiter,
  PgBouncer, replicas = trigger-based v4+** — each shipped only when its EXT-D trip-wire fires,
  and replicas quite possibly never (EXT-D8g).
- **Effort: M, spread.** Near-term cost is runbook authoring + the first green rehearsal + the
  E0 leader lock (small). The distributed-limiter change is a focused S when EXT-D8b trips;
  PgBouncer is an infra S when EXT-D8e trips. Most of the chapter is **latent insurance**, not
  work to do now — which is the point.


## 9. Deferred‑with‑interface — D5 cross‑currency settlement convergence

> Coordination note (numbering): these are the document's **closing sections**, slotted after the
> last feature chapter (E8 — Scale & ops). Section numbers `9`/`10`/`11` are renumber‑safe; every
> cross‑reference below anchors on **stable IDs** (v1 §‑numbers, D1–D9, X1–X6, and the target
> chapter's `EXT‑D*`), not on the churny extension‑doc section numbers.

Governed by **D5** (per‑currency settle only; a settlement is in the debt's currency; FX
convergence is deferred **and snapshot‑backed**), **X3** (converted money structurally
quarantined), and **X2** (no second authoritative store). **This section does not design the
feature.** It records what **E2 (Live FX)** already guarantees a future *"settle a EUR debt in
CZK"* capability, the invariants that capability is **pre‑committed** to preserve, and the hard
problems it will face. Nothing here adds a table, a column, or a wire field — the seam is built
(E2 `EXT‑D2l`); the feature is not.

### 9.1 What E2 already guarantees the future feature (the built seam)

E2 shipped the immutable, date‑pinned, provenance‑stamped rate substrate **and stopped there**
(its scope fence, `EXT‑D2l`). Everything convergence needs to be *reproducible* already exists;
everything convergence needs to *decide* does not.

| Guarantee E2 provides | Where it lives | Why it is load‑bearing for convergence |
|---|---|---|
| **Immutable rate‑by‑date.** `fx_rates(currency, valid_on)` is append‑only; writes are `INSERT … ON CONFLICT DO NOTHING`; a later ČNB revision of a past fixing is **ignored**. | E2 `EXT‑D2e` | A settlement that snapshots a rate can reproduce it **bit‑for‑bit years later** — the value can never move under a settled debt. |
| **Integer‑exact rates, never float.** `(per_amount, rate_scaled, scale)` — `24.315` → `1, 24315, 3`. | E2 `EXT‑D2d` | The snapshot copied onto a settlement is integers only (the D1 no‑float discipline, extended to rates). Deterministic re‑derivation, no drift. |
| **Provenance stamped.** `source` + `fetched_at` per row. | E2 `EXT‑D2d/e` | The settlement can record **which fixing** it used, so an export or audit can *prove* the rate applied (X3 provenance requirement). |
| **One deterministic lookup with fallback.** `IFxRateProvider.RateOn(currency, on)` → `FxRate?`, previous‑business‑day fallback (ČNB skips weekends/holidays). | E2 §2.9, `EXT‑D2f` | The single call the settlement makes **at settle time** to fetch the rate to snapshot. Same `(currency, on)` → same result, forever. |
| **CZK pivot, one folded fraction, one rounding.** Cross rate X→Y folds through CZK into a single `Int128` fraction, rounded once `ToEven`. | E2 `EXT‑D2c/i` | The X→Y rate a EUR‑debt‑settled‑in‑CZK needs is already derivable exactly, with exactly one auditable rounding site. |
| **A clean ledger to start from.** E2 adds **no** column to `settlements`, keeps `DisplayMoney` type‑quarantined from `Money` (no constructor bridges them), and never nets across currencies. | E2 `EXT‑D2a/l` | Convergence begins from an untouched ledger — no half‑built converted state to reconcile or unwind. |

The natural key `(currency, valid_on)` **is** the seam: a future cross‑currency settlement row will
reference/snapshot the fixing on that key. E2 guarantees the value is stable; it writes no such
snapshot itself.

### 9.2 Invariants the convergence feature is pre‑committed to preserve

These are not open questions — they are **binding constraints** the feature inherits the day it is
picked up. Any design that violates one is wrong by construction.

| # | Pre‑committed constraint | Why (what it protects) |
|---|--------------------------|------------------------|
| **CONV‑1** | **Per‑currency zero‑sum survives.** After a cross‑currency settlement, `Σ net(m, C) == "0"` must still hold for **every** currency C (D5, §2.2, §3.3). The mechanism must be **two native legs** (a debt‑currency leg + a payment‑currency leg) tied by a snapshot rate — **never one row that lives in no currency** and nets EUR against CZK. | The zero‑sum is the client‑checkable money invariant. A single cross‑currency amount would break it in *both* buckets and is the exact D5 hazard the v1 deferral exists to avoid. |
| **CONV‑2** | **The rate is snapshotted at settle time, never re‑looked‑up.** The integer `(per_amount, rate_scaled, scale, valid_on, source)` returned by `RateOn` is **copied onto the settlement at create**. A later fixing — or a ČNB revision (already ignored, `EXT‑D2e`) — must never re‑price a settled debt. | This is D5's "snapshot‑backed" clause made physical. Re‑pricing on read is a stored‑balance drift bug (X2) with a money‑app blast radius. |
| **CONV‑3** | **Balance math never sees a cross‑currency amount.** `BalanceCalculator` / `DebtSimplifier` stay strictly per‑currency (D5, §2.4). The snapshot rate is **provenance on the settlement**, never an **operand in netting**. | Simplification is called once per currency bucket (§2.4). The moment a rate enters netting, EUR and CZK are being added — the thing D5 forbids. |
| **CONV‑4** | **The converted figure is quarantined on the wire (X3).** The two authoritative columns are the native leg amounts (D1 minor units of *their own* currency); the snapshot rate travels in a provenance/`approx` block, **never** as an `amount`. Exports keep only native authoritative columns (X3; E4 `EXT‑D4h`). | A converted number that can be mistaken for a ledger amount eventually will be, by some export or settlement consumer. Type‑ and wire‑level separation makes the mistake unrepresentable. |
| **CONV‑5** | **It is still a money‑creating write.** D9 `(group_id, client_id)` create‑idempotency + D8 `version`/`If‑Match` apply. The **snapshot rate joins the idempotency canonical set** (§3.4), so a replay at a *different* fixing is a `409 client_id_conflict`, not a silent match. Void (§3.6) reverses **both** legs atomically and restores both buckets' zero‑sums. | Without the rate in the canonical set, an at‑least‑once retry (X5) that lands on a new fixing double‑prices. Void that reverses one leg only strands the other's zero‑sum. |

**Illustration of CONV‑1 (invariant, not design).** Bob owes Alice `"10000"` EUR (100.00 EUR;
Bob's EUR net = `"-10000"`, Alice's = `"+10000"`). He settles in CZK at the 2026‑07‑03 fixing
`1 EUR = 24.315 CZK` → snapshot `(per_amount 1, rate_scaled 24315, scale 3, valid_on 2026‑07‑03,
source 'cnb.daily#130')`. Bob actually pays `"2432"` CZK (100.00 EUR converted once, `ToEven`,
E2 `EXT‑D2i`). For CONV‑1 to hold, the record must **extinguish the full `"10000"` EUR debt on
the EUR side** *and* register a `"2432"` CZK payment on the CZK side, the two tied by the snapshot
— so `Σ net(EUR)` returns to `"0"` and `Σ net(CZK)` stays `"0"`. It may **not** be a lone
`Bob → Alice "2432"` in some ambiguous currency that nets against the EUR bucket.

### 9.3 The hard problems it will face (open — stated, not solved)

| # | Hard problem | Why it has no free answer |
|---|--------------|---------------------------|
| **HP‑1** | **The residual / FX dust.** `"2432"` CZK back‑converts at the same snapshot rate to ≈ 100.02 EUR, not exactly 100.00 — integer rounding cannot round‑trip. Does the EUR debt **fully extinguish** (rounding write‑off) or does sub‑unit EUR dust **persist** (a remainder that never simplifies)? | Both are defensible and both must keep `Σ net(EUR) == "0"`. Silently vanishing the residual is the X3 failure (approx money leaking into the ledger); leaving dust breaks debt simplification's "each step zeroes ≥1 party" (§2.4). |
| **HP‑2** | **Which rate date.** Settle‑time fixing, or the debt's incurrence date? A debt accreted across many expenses over months **has no single date**. | `EXT‑D2f` resolves a rate *for* a date; *which* date is a **product ruling with money consequences** — the FX drift between the two dates is real gain or loss to someone. |
| **HP‑3** | **Who bears FX gain/loss.** The rate moves between incurring the EUR debt and settling it in CZK; one party ends up better off. There is **no hedge** in a shared‑expense app. Is the creditor made whole in EUR (debtor bears the drift) or in CZK (creditor bears it)? | A **fairness ruling, not a math one** — and it is visible to users as "I paid what I owed but the app says I still owe 12 Kč." Whatever is chosen must be legible, not emergent from rounding. |
| **HP‑4** | **Partial cross‑currency settlement.** Settling *part* of a EUR debt in CZK multiplies HP‑1/HP‑3 per payment and interacts with which native debt edge (§2.4) is being paid down. | Simplification recomputes on demand (D4); a sequence of partial converted payments must remain associative — the order of two CZK payments against one EUR debt may not change the final EUR net. |
| **HP‑5** | **Export & GDPR reach.** The snapshot rate must survive to exports as **provenance** (X3; authoritative columns stay native, E4) and through the D7 anonymize scrub. | `fx_rates` rows are public reference data (no PII — E2 §2.6), but the settlement **legs are ledger** and are **retained**, not deleted (D7). The provenance block must not become a converted authoritative column in the export (X3). |

### 9.4 What E2 deliberately did **not** build (fence reaffirmed)

E2 shipped **only** `IFxRateProvider` + the immutable `fx_rates` table, and added **no** column to
`settlements`, **no** cross‑currency netting, **no** FX in `DebtSimplifier` (E2 `EXT‑D2l`, D5). This
section adds **nothing** to schema or wire either — it is the **pre‑commitment ledger** for a
feature nobody is building yet. Building more now is speculative (YAGNI) and risks a second
authoritative store for something the ledger already owns (X2). Convergence ships when there is
demonstrated demand to settle across currencies — and when it does, CONV‑1…CONV‑5 are already law.

---

## 10. Explicitly not doing (with re‑open triggers)

The counterpart to v1 §3.12 ("declared cuts"): capabilities **actively declined**, each with a
**concrete, measurable trip‑wire** that would re‑open it. A cut without a trigger is an accident
waiting to be re‑argued; a cut *with* one is a decision. None of these is on any roadmap — they
ship if and only if their trigger fires.

| Not doing | Why deferred | Concrete re‑open trigger |
|-----------|--------------|--------------------------|
| **Server‑side full‑text search** | The offline‑first client holds **every** expense in SwiftData (§7), so client‑side search is free, instant, and offline‑capable — a server round‑trip is strictly worse. Czech FTS is also awkward (Postgres ships **no Czech stemmer**; parity needs `unaccent` + `pg_trgm` and still underperforms). Affirms the §3.12 / E10a `§7.10` cut. | **The offline full‑mirror assumption breaks** — i.e. **server‑side pagination** is introduced (a group too large to mirror, or a web client, §11 E9c). The moment clients stop holding full history, client‑side search stops being complete and the server must index. |
| **Web companion app (E9c)** | iOS‑only is the product today; the sync engine, auth, and full‑mirror model are all native‑client‑shaped (§7). See the §11 assessment for the architectural cost. | **Meaningful, measured non‑iOS demand** (support requests / waitlist / churn citing "no web or Android"). Not a hunch — a number. |
| **WebSockets / realtime beyond silent APNs** | v1's silent `content-available:1` push (§4.5) already triggers a delta pull within seconds; the delta‑sync spine (§3.5) is the transport. A persistent socket is a stateful, multi‑instance‑fanout dependency the §6 Hetzner exit is designed to avoid. | **A genuine sub‑second collaboration need** — concurrent live co‑editing where seconds‑latency sync is visibly wrong (e.g. two people splitting a bill at the table in real time). Push‑triggered pull covers everything short of that. |
| **Admin panel (E9b)** | Support volume is low enough that ad‑hoc SQL / scripts suffice, and *not* building it keeps the highest‑value breach surface (cross‑user financial data) at **zero**. See §11. | **Support toil becomes real** — a sustained rate of manual interventions (stuck jobs, GDPR requests, invite resends) that scripts can't keep up with, justifying an audited, least‑privilege console. |
| **Reactions (E10b)** | A reaction is a degenerate comment; meaningless before comments (E10a) exist, and not retention‑load‑bearing the way threaded disputes are. Assessed in E10a `§7.10`. | **Comments have shipped *and* there is demand** — users asking for lightweight acknowledgement on expenses/comments. Both conditions, not either. |
| **Budgets (E10d)** | Money‑adjacent, per‑currency, on‑demand — real design weight for a feature no one has asked for yet. Rulings pre‑pinned in E10a `§7.10`. | **Demonstrated demand** — repeated user requests for per‑group / per‑category spend caps. |
| **Redis / PgBouncer / read replicas (E8b)** | Premature infra is dead weight that complicates backups and the Hetzner exit. At indie scale, **one instance + one Postgres** is not a shortcut — it is correct (E8 `EXT‑D8a`). | **The specific numeric thresholds in E8** fire: `>1` app instance (`EXT‑D8b` → distributed limiter), sustained `>70%` of `max_connections` after Npgsql multiplexing (`EXT‑D8e` → PgBouncer), or a sustained read‑p95 regression surviving indexing (`EXT‑D8g` → replicas). Read off the E8a observability stack (§6), never a calendar. |

---

## 11. v4+ area assessments

Honest scope‑plus‑verdict for each demand‑triggered area — **no deep design**, one sharp open
question apiece. These consolidate and expand the sibling tables already embedded in the feature
chapters (E10a `§7.10`, E8 `§8.2`, E5 `EXT‑D5h/i`); where a ruling is already pinned there, it is
inherited, not re‑litigated.

**E5c — Digest emails.** An opt‑in periodic email summarizing a group's activity and the reader's
net position, per‑user send‑time timezone, RFC 8058 one‑click `List‑Unsubscribe`. **Verdict:
v4+, cheap once E5's `IEmailSender` + fan‑out cursor exist, and already de‑risked** — the load‑
bearing rulings are pinned: consent lives in `users.digest_opt_in` (default `false`), **not** in
`email_suppressions` (the account‑recovery DoS fix, `EXT‑D5h/i`); content is **computed on demand**
(X2 — no stored digest "balance"); cross‑currency totals appear only in an X3 `approx` block (needs
FX). It is a retention nicety, not a load‑bearing feature. **Hard question:** what earns an open
without training users to unsubscribe? A digest of "nothing changed" is spam, so the send must be
gated on *material* change — and "material" is a per‑user relevance judgment the server has to make
**without** storing per‑user digest state (X2).

**E8b — Scale infra.** The v1 single‑instance shortcuts (§4.1/§4.3/§4.5) undone: distributed rate
limiter, connection pooling, read replicas. **Verdict: trigger‑gated, not roadmap‑gated, and most
triggers never fire** (E8 `EXT‑D8a`). Each has a numeric trip‑wire — `>1` instance → shared limiter
(`EXT‑D8b`), `>70%` `max_connections` → PgBouncer after trying Npgsql multiplexing (`EXT‑D8e`),
sustained read‑p95 regression → replicas (`EXT‑D8g`, a deliberately near‑unreachable bar given D4
sub‑ms balance math). The default is **Postgres‑for‑everything** (`EXT‑D8c`) precisely to keep the
Hetzner exit to one `pg_restore`. **Hard question:** the *first* trigger to fire is almost certainly
`EXT‑D8b` (the instant a second instance serves traffic) — does the "distributed limiter on Postgres"
bet actually hold at that point, or does a second instance quietly make Redis unavoidable and drag a
second stateful service across the exit (`EXT‑D8d`)?

**E9b — Admin panel.** An internal support/ops console: look up a user or group, inspect state,
action a stuck job, honor a GDPR request, resend an invite. **Verdict: deferred until support toil
is real; not a user feature.** Doing it by SQL/scripts at low volume keeps the attack surface at
zero, which is a feature, not a gap. **Hard question:** how do you give support enough visibility to
help **without** building a god‑mode financial/PII console that sidesteps D6 (every admin read is a
cross‑user access that 404‑no‑leak exists to prevent) and D7 (anonymize‑not‑delete)? It demands its
own authz model, a **mandatory audit log**, and least‑privilege scoping — none free — because it
would instantly become the single largest breach surface in the system.

**E9c — Web companion.** A browser client (read‑mostly first) so a group is reachable without the
iOS app. **Verdict: deferred until measured non‑iOS demand — and the single biggest architectural
stress‑test in the roadmap.** It breaks the load‑bearing offline‑first assumption: the native client
holds the *full* group history in SwiftData (§7), which is exactly what makes client‑side search free
and server‑side pagination unnecessary (§3.12, E10a `§7.10`). **Hard question:** a web client can't
mirror everything and can't use native Apple Sign‑In, so it **re‑introduces server‑side pagination +
full‑text search** (reversing two declared §10 cuts) *and* leans entirely on E6 email auth for
identity — is that a "companion," or a second product with its own sync engine and its own threat
model?

**E10b — Reactions.** Emoji reactions on expenses and comments. **Verdict: v4+ and cheap once
comments (E10a) ship** — a reaction is a degenerate comment (author + target + emoji token, no
editable body), reusing the X1 sync/authz machinery wholesale and *simpler* on GDPR (an emoji is not
PII; erase just tombstones the row, no body to scrub). Nice‑to‑have, not retention‑load‑bearing the
way threaded disputes are. Expands E10a `§7.10`. **Hard question:** does a reaction deserve its own
`change_log` `entity_type` and a sync round‑trip **plus** notification fan‑out, or does that turn a
lightweight social signal into per‑tap sync/push noise that then has to be collapsed — i.e., is the
fan‑out cost worth the tap?

**E10d — Budgets.** Per‑group / per‑category spend caps with progress display. **Verdict: v4+,
money‑adjacent but not ledger money**, with the money rules pre‑pinned in E10a `§7.10`: **per‑currency
only** (a budget lives in one currency, checked against that currency's bucket, never a cross‑currency
sum — D5/X3); **computed on demand** (progress = Σ category+currency expenses vs. limit, like balances
— no materialized budget‑progress store, D4/X2); and an **advisory on‑write check that warns, never
blocks** (a hard `4xx` on overrun poisons the offline outbox, which cannot know server‑side budget
state at write time). **Hard question:** in a multi‑currency group a "5000 CZK groceries" cap is
meaningless against a EUR grocery expense — how does the UX express and enforce a cap that only ever
applies to *one* currency's bucket without users reading it as a group‑wide limit?


## Appendix A — Consolidated migration inventory

Every DDL delta introduced across all extension chapters, in a **safe apply order**. The order
obeys two hard rules: **E0 first** (the jobs/blob/email substrate is a hard prerequisite for every
later chapter), and **FK targets before referents** (a table is created before anything that
references it). Everything is **additive within `/v1`** (§3.11 meta-rule) except two deliberate
`ALTER`s on v1 tables that are noted as such; nothing renames or retypes an existing column.

**Legend — `class`:** `NEW` = new table (additive-only, no v1 table touched) · `+COL` = additive
column on a v1 table · `~CON` = constraint change on a v1 table · `IDX` = index (additive).

### A.1 Apply order

| # | Object (table / column) | Chapter | Depends-on | Class |
|---|-------------------------|---------|-----------|-------|
| **Phase 0 — E0 substrate (blocks everything)** ||||
| 1 | `jobs` | E0 | — (no FK) | NEW · additive-only |
| 2 | `email_suppressions` | E0 | — (no FK) | NEW · additive-only |
| **Phase 1 — new tables (depend only on v1 + E0)** ||||
| 3 | `blob_deletions` | E1 | — (no FK) | NEW · additive-only |
| 4 | `attachments` | E1 | v1 `groups`,`expenses`,`users` | NEW · additive-only |
| 5 | `fx_rates` | E2 | v1 `currencies` | NEW · additive-only |
| 6 | `fx_source_holidays` | E2 | — (no FK) | NEW · additive-only |
| 7 | `recurring_rules` | E3 | v1 `groups`,`currencies`,`group_members`,`categories`,`users` | NEW · additive-only |
| 8 | `recurring_rule_splits` | E3 | **`recurring_rules` (#7)**, v1 `group_members` | NEW · additive-only |
| 9 | `export_results` | E4 | v1 `users`,`groups` | NEW · additive-only |
| 10 | `notification_prefs` | E5 | v1 `users`,`groups` | NEW · additive-only |
| 11 | `notification_cursor` | E5 | — (no FK) | NEW · additive-only |
| 12 | `email_login_tokens` | E6 | v1 `users` | NEW · additive-only |
| 13 | `comments` | E10a | v1 `groups`,`expenses`,`users` | NEW · additive-only |
| 14 | `rate_limit_counters` | E8a | — (no FK) | NEW · additive-only · **materialize only when EXT-D8b trips (2nd instance)** |
| **Phase 2 — ALTERs on v1 tables** ||||
| 15 | `groups.image_url` ADD COLUMN | E1 | v1 `groups` | **+COL · ALTERs v1** |
| 16 | `expenses.recurring_rule_id` ADD COLUMN (FK→recurring_rules) | E3 | **`recurring_rules` (#7)**, v1 `expenses` | **+COL · ALTERs v1** (FK-dependent) |
| 17 | `devices.kind` ADD COLUMN | E5 | v1 `devices` | **+COL · ALTERs v1** |
| 18 | `users.digest_opt_in` ADD COLUMN | E5 | v1 `users` | **+COL · ALTERs v1** |
| 19 | `users.email_verified_at` ADD COLUMN | E6 | v1 `users` | **+COL · ALTERs v1** |
| 20 | `users` DROP CONSTRAINT `users_email_key` | E6 | v1 `users` | **~CON · ALTERs v1** — the **only non-additive** delta (drops the v1 `email UNIQUE`; DB-only, no wire client observed it — EXT-D6b) |
| 21 | `change_log` entity_type CHECK — **single consolidated widen** | E1 + E3 + E10a | v1 `change_log` | **~CON · ALTERs v1** — see A.2 |
| **Phase 3 — indexes (additive; after their table exists)** ||||
| 22 | `ix_expenses_deleted` (partial, on v1 `expenses`) | E1 | v1 `expenses` | IDX · additive |
| 23 | `ix_attachments_expense` · `ix_attachments_group` · `ix_attachments_pending` | E1 | `attachments` (#4) | IDX · additive |
| 24 | `ix_blob_deletions_pending` | E1 | `blob_deletions` (#3) | IDX · additive |
| 25 | (E2: **no extra index** — PK `(currency, valid_on)` serves fallback + latest lookup) | E2 | `fx_rates` (#5) | — |
| 26 | `ix_recurring_due` · `ix_recurring_group` · `ix_expenses_recurring` | E3 | `recurring_rules` (#7), `expenses.recurring_rule_id` (#16) | IDX · additive |
| 27 | `ix_export_results_user` · `uq_export_results_inflight` | E4 | `export_results` (#9) | IDX · additive |
| 28 | `uq_notif_prefs_global` · `uq_notif_prefs_group` · `ix_notif_prefs_user` | E5 | `notification_prefs` (#10) | IDX · additive |
| 29 | `ix_users_email` (partial, on v1 `users`) | E6 | v1 `users` | IDX · additive |
| 30 | `ix_email_tokens_lookup` · `ix_email_tokens_expiry` | E6 | `email_login_tokens` (#12) | IDX · additive |
| 31 | `ix_comments_expense` · `ix_comments_author` | E10a | `comments` (#13) | IDX · additive |
| 32 | `ix_rate_limit_gc` | E8a | `rate_limit_counters` (#14) | IDX · additive |

**Not a DDL delta (recorded to avoid re-adding):** E7 avatars reuse the already-present, previously
*reserved* v1 columns `users.avatar_url` and `group_members.image_url` — **no migration** (un-reserved
in code only; they were empty in v1, so no backfill). E6 permits `auth_identities.provider = 'email'`
(v1 reserved the value) — **no column change**. E4's async queue is the **E0 `jobs` table**
(`kind='export.build'`), not a new `export_jobs` table; `export_results` (#9) is only the pollable
artifact record.

### A.2 The consolidated `change_log.entity_type` CHECK

E1, E3, and E10a **each** add exactly one new `change_log.entity_type` value (E1 adds `attachment`;
E3 adds `recurring_rule`; E10a adds `comment`). Their chapter DDL now carries only a **NOTE**
deferring to this consolidation — not a per‑chapter `DROP`/`ADD CONSTRAINT … CHECK (entity_type IN
(…))` that lists only its own member, which, applied in sequence as written, would have had a later
chapter's ALTER **regress** the earlier additions. The single consolidated migration collapses all
three into **one final** widen (this is the authoritative full value set):

```sql
ALTER TABLE change_log DROP CONSTRAINT change_log_entity_type_check;
ALTER TABLE change_log ADD  CONSTRAINT change_log_entity_type_check
    CHECK (entity_type IN ('group','member','expense','settlement','category','access',
                           'attachment','recurring_rule','comment'));
```

### A.3 Additive-vs-ALTER summary

- **Additive-only (new tables + indexes):** rows 1–14, 22–32 — 14 new tables, ~20 indexes.
- **ALTERs a v1 table (additive columns / enum widen):** rows 15–19, 21 — 5 new columns
  (`groups.image_url`, `expenses.recurring_rule_id`, `devices.kind`, `users.digest_opt_in`,
  `users.email_verified_at`) + the consolidated `change_log` CHECK widen.
- **The single non-additive ALTER:** row 20 — `DROP CONSTRAINT users_email_key`. It is safe as a
  `/v1` change because uniqueness was a DB constraint no wire client ever observed (EXT-D6b); `email`
  becomes a non-authoritative contact attribute and identity authority moves to
  `auth_identities(provider, subject)`.

---

## Appendix B — X-rule conformance matrix

Cross-check that **no extension violated a generalization rule** (X1–X6, frontmatter §0.2). Rows are
the extension areas as the **chapter files** name them (E1 = receipts/attachments, E2 = live FX,
E3 = recurring, E5 = notifications, E7 = avatars, E8 = scale/ops, E9a = invite landing,
E10a = comments) — matching the canonical E-names in the frontmatter §0.5 tier map. Cell =
`✓ (how)` or `n/a (why)`.

| Ext | X1 sync contract | X2 no 2nd store | X3 converted-$ quarantine | X4 blobs referenced / 2-phase | X5 jobs at-least-once + idempotent | X6 unauth surface hardened |
|-----|------------------|-----------------|---------------------------|-------------------------------|------------------------------------|----------------------------|
| **E0** platform | n/a (jobs/blob/email are server infra, not client-cached) | ✓ (`jobs` = disposable op state, not a ledger store) | n/a (no money) | ✓ (`IBlobStore` refs only; two-phase `blob.hard_delete`) | ✓ (SKIP-LOCKED drain, backoff+poison, stale-lease reclaim EXT-D0h; deterministic `client_id` contract) | ✓ (`IEmailSender` suppression + presigned-URL/token primitives built to the X6 template) |
| **E1** attachments | ✓ (own `attachment` entity: `client_id`+`UNIQUE`, `version`/`If-Match`, tombstone, membership-first) | ✓ (metadata only; bytes in blob store; thumbnails derived) | n/a (balance-neutral; carries no money) | ✓ (`blob_deletions` two-phase; receipt-reaper; orphan sweep) | ✓ (two-phase delete + sweep idempotent, at-least-once) | ✓ (presigned PUT/GET: private bucket, unguessable `uuid` keys, membership-gated mint, minute TTL) |
| **E2** live FX | n/a (`fx_rates` global reference data, not group-scoped/editable — like `currencies`) | ✓ (`display` computed on read; append-only immutable rate table is source, not derived) | ✓ (`DisplayMoney` type-quarantined; no bridge to `Money`) | n/a (no blobs) | ✓ (`fx.poll`/`fx.backfill` on E0; `ON CONFLICT DO NOTHING`) | n/a (all authed; ČNB host allowlisted — SSRF closed structurally) |
| **E3** recurring | ✓ (own `recurring_rule` entity: full X1 contract) | ✓ (rule = template; occurrence preview computed, no stored calendar) | ✓ (single currency, converted money structurally absent — EXT-D3k) | n/a (no blobs) | ✓ (deterministic `rec:{rule}:{date}` `client_id` + reserved namespace; D9 absorbs double-fire) | n/a (all authed, group-scoped; worker internal) |
| **E4** exports | n/a (`export_results` server-owned ephemeral, not synced/editable) | ✓ (stats on-demand; artifact disposable, not a 2nd store) | ✓ (`display` block quarantined; CSV authoritative cols = D1 minor units) | ✓ (artifact blob referenced by key; two-phase delete; `export` blob class) | ✓ (`export.build` on E0; `uq_export_results_inflight` collapses double-fire) | ✓ (download = authed stream by default; presigned fallback X6-hardened; requester-scoped 404) |
| **E5** notifications | n/a (`notification_prefs` = account settings, aggregate `version` but no `change_log`) | ✓ (prefs = overrides resolved; no materialized effective matrix; digest computed on demand; taxonomy unified with §3.10 verbs) | ✓ (digest cross-currency total only in X3 `display` block) | n/a (owns no blob class) | ✓ (`notification.fanout` cursor worker at-least-once + idempotent; `IdempotencyKey`; stale-lease reclaim) | ✓ (3 new surfaces: landing `/i/{token}`, unsubscribe, webhook — each own partition, uniform responses, hashed/signed tokens) |
| **E6** email auth | n/a (`email_login_tokens`/`auth_identities` = auth plumbing, not synced) | ✓ (no 2nd store; identity authority on `(provider,subject)`) | n/a (no money crosses this surface) | n/a (no blobs) | ✓ (`email.send` + `email_token.prune` on E0; single-use conditional `consumed_at` UPDATE) | ✓ (`/auth/email/start`+`/verify`: SHA-256(pepper‖code) at rest, uniform `200 {"status":"sent"}` + one `invalid_or_expired_code`, per-email+per-IP partitions) |
| **E7** avatars | ✓ (no new entity — rides existing `member`/`group` upserts; versioned key + cache-bust) | ✓ (singleton on reserved column; no 2nd store) | n/a (no money) | ✓ (avatar two-phase delete; `gdpr_erase` hard-delete; versioned keys) | ✓ (blob-deletion worker + orphan sweep idempotent) | ✓ (same presigned PUT/GET surfaces as E1; server-minted keys, no client URL — EXT-D1m) |
| **E8** scale/ops | n/a (owns no client-cached entity) | ✓ (`rate_limit_counters` disposable op state; no materialized read models — X2 intent) | n/a (no money on the wire) | n/a (blob-migration step is E1/E7 mechanics only) | ✓ (counter-GC + prune leader-lock jobs at-least-once idempotent; `UPSERT … RETURNING` atomic) | ✓ (distributed limiter is still an X6 surface: partition keys HMAC-hashed at rest, trusted-proxy, per-surface partitions) |
| **E9a** invite landing | n/a (unauthenticated web surface, outside `/sync`) | ✓ (landing computed; no store) | n/a (no money; strictly less than JSON preview) | n/a (no blobs) | n/a (no job of its own) | ✓ (`GET /i/{token}`: own IP partition, uniform "invite unavailable", v1 invite token hashed at rest, `noindex`+`no-referrer`) |
| **E10a** comments | ✓ (own `comment` entity: full X1 contract) | ✓ (no 2nd store; notif policy via E5 gate, no 2nd policy store) | ✓ (money-inert — trivially satisfied, nothing to quarantine) | n/a (text-only; blob comments would live under E1/E7) | n/a (no background worker; erase-scrub runs in the §4.4 anonymize tx, not a job) | n/a (all Bearer + group-scoped; no public landing/verify/download) |

**Reading the matrix:** every `✓` names the concrete mechanism; every `n/a` names why the rule cannot
engage (no money → X3 n/a; no blob → X4 n/a; no background job → X5 n/a; no unauthenticated surface →
X6 n/a; not a client-cached group-scoped entity → X1 n/a). No cell is a violation — the two rules with
the widest blast radius (X3 converted-money quarantine, X5 idempotent jobs) are `✓` or structurally
`n/a` in every area that touches money or the queue.


