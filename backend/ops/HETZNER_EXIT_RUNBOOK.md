# Hetzner Exit Runbook

> Move Paybitch off the managed host (Railway) to a **Hetzner CX22 + Docker Compose** (EU, ~€5/mo, full
> data ownership) in **under an hour, losing neither a cent nor a sync cursor**. Condensed from
> BACKEND_EXTENSIONS §8.9 (E8). This is a **tested artifact** — see [Rehearsal](#rehearsal-ext-d8h).
>
> **EXT-D8h — a restore that has never been performed is an assumption, not a capability.** If the last
> quarterly rehearsal is missing or red, this runbook is **presumed broken** and "we can leave Railway"
> is downgraded to *unverified* until the next green rehearsal.

## Targets

| Target | Value | Path |
|---|---|---|
| RPO (data loss) | ≤ 5 min (PITR) / ~0 with write-freeze | either |
| RTO (downtime) | ≤ 30 min | dump/restore (Path A, default — EXT-D8i) |
| RTO (downtime) | ≤ 5 min | logical-replication switchover (Path B, large-DB only) |
| Rehearsal cadence | quarterly, scratch VPS, smoke-gated | EXT-D8h |

## What moves, what rotates, what is carried (EXT-D8l / D8n)

| Item | Action | Why |
|---|---|---|
| DB credentials | **Rotate** | old host disk holds the ledger + PII |
| Provider tokens (APNs `.p8`, Apple client-secret key) | **Rotate** if they lived only on the old host | inert any recovered image |
| `JWT_SIGNING_KEY_PEM` / `JWT_PREVIOUS_KEY_PEM` | **Carry across unchanged** | a clean move is not a compromise → **no mass re-auth**; `token_epoch` is **not** bumped |
| `change_log`, `change_log_watermark`, `change_log.seq` | **Carry byte-faithfully** in the dump | else every client `410`s and re-bootstraps → self-inflicted thundering herd (EXT-D8j) |
| `rate_limit_counters` | **Exclude from the dump** | IP + user-id PII, disposable, short-lived (EXT-D8n) |
| Blobs | **No-op if portable object storage** (S3-compatible); else bucket-copy (EXT-D8k) | rows hold opaque keys (X4) |

---

## Path A — `pg_dump` / `pg_restore` (default, EXT-D8i)

Brief write-freeze; correct at v1 DB sizes.

### Pre-cutover
- [ ] **≥ 24 h before:** drop DNS A/AAAA TTL to **60 s** (EXT-D8l).
- [ ] Provision **Hetzner CX22**, EU region; install Docker + Compose; clone `docker-compose.yml`.
- [ ] Load secrets: **rotated** DB creds; **carried** `JWT_SIGNING_KEY_PEM` / `JWT_PREVIOUS_KEY_PEM`; APNs `.p8`; Apple client-secret key. (Never in the compose file — §8.7.)
- [ ] Apply the **EF migration bundle** to the new empty Postgres — discrete step, schema first, no traffic:
      `./efbundle --connection "$PAYBITCH_DB"` (built in CI via `dotnet ef migrations bundle`).
- [ ] Verify `GET /health/ready` on the new host → `migrations: "current"`.
- [ ] Confirm the **last quarterly rehearsal is green** and dated (EXT-D8h).

### Cutover
1. **Freeze writes** on the old host (maintenance flag → `503` + `Retry-After`).
2. Take a final dump:
   ```bash
   pg_dump --format=custom --no-owner --dbname "$OLD_DB" \
           --exclude-table-data=rate_limit_counters -f paybitch.dump   # EXT-D8n
   ```
3. Restore into the new Postgres:
   ```bash
   pg_restore --no-owner --dbname "$NEW_DB" paybitch.dump
   ```
4. **Verify the sync spine survived (EXT-D8j) — any failure ⇒ ABORT:**
   ```sql
   SELECT count(*)            FROM change_log;                 -- matches source row count
   SELECT max(seq)            FROM change_log;                 -- == source max(seq)
   SELECT pruned_through_seq  FROM change_log_watermark;       -- == source (non-regressed)
   SELECT last_value          FROM change_log_seq_seq;         -- advanced PAST max(seq) — check, don't assume
   ```
   A custom-format dump restores sequence state; **verify it** rather than trusting it.
5. Confirm the blob store is reachable from the new host (no-op if portable — EXT-D8k).
6. **Run the smoke suite** below against the new host. **GATE: green or abort.**

### Smoke suite — the DNS-flip gate (§8.8, EXT-D8h/l)

Run against the **restored** data, before any traffic. Green on all five ⇒ flip DNS. Any red ⇒ abort, stay on the old host.

1. `POST /auth/apple` (or refresh) → a valid access token.
2. `POST /groups` → `POST /groups/{g}/expenses` (EQUAL, 3 members) → server `shares` sum to `amount`.
3. `GET /groups/{g}/balances` on a **restored** group → **`Σ net == "0"` per currency** (the money invariant, re-checked post-restore).
4. `GET /health/ready` → `syncWatermark > 0` **and** equals source `max(change_log.seq)`.
5. `GET /sync?since=<a pre-migration cursor>` → a normal delta, **not** `410` (cursors survived).

### Flip + observe
7. **Flip DNS** to the new host; wait one TTL.
8. **Observe** ≥ one TTL + a few minutes: error rate; the **`Σ balances == 0` alert** (§6); the **`410 sync_cursor_expired` rate — must stay ~0** (EXT-D8j). Old host stays up, writes frozen = instant rollback.

> **Point of no return:** the flip is reversible **only while writes stay frozen on the old host**. Once the
> new host **accepts a write**, rollback means a reverse dump. Keep the freeze on and the observation window
> short until you are confident.

### Decommission (EXT-D8n)
9. **Securely wipe** the old volume **or** rotate JWT keys + DB creds so any recovered image is inert.
10. Revoke old-host-only provider creds. Retain old backups in **EU cold storage** per the retention policy.
11. Update this runbook with anything the cutover surprised you with.

---

## Path B — logical replication (near-zero downtime)

**Only when the DB is too large to freeze.** Publication on old, subscription on new; let it catch up; short
switchover (quiesce writes seconds, confirm replication lag 0, promote new, flip DNS).

> **Sequences do NOT replicate over logical replication.** `setval` `change_log_seq_seq` (and every other
> sequence) at switchover past the source max, or the first insert on the new host collides (EXT-D8j/k).

---

## Rehearsal (EXT-D8h)

**Quarterly, against a scratch VPS, smoke-gated.** Each rehearsal asserts:

- Per-table row counts match source.
- `max(change_log.seq)` and `pruned_through_seq` non-regressed; sequence advanced past max.
- The 5-step smoke suite is **green**.
- Money faithfulness: `Σ net == 0` per currency holds **byte-identically** before and after (integer minor
  units, D1, don't drift under dump/restore; no conversion happens, so X3 is vacuously preserved).

Record the **date + result** somewhere durable. A skipped rehearsal ⇒ runbook presumed broken.

## Backups & residency (EXT-D8n / §8.6)

- Backups hold the **full ledger + PII** → **EU-region, encrypted-at-rest**, explicit retention policy.
- D7 GDPR-anonymize operates on **live rows only**; erasing a user does **not** reach historical backups —
  they age out under retention. This divergence is documented, not hidden.
- `rate_limit_counters` (hashed IP / user-id partitions) is **never** carried — excluded from the dump above.
