# Paybitch — Backend Design (.NET 9 + PostgreSQL)

> Reconciled design for the Paybitch backend. Produced from a 10‑agent design pass
> (schema · architecture · API · settlement · auth · sync · devops) followed by two
> adversarial reviews (money‑correctness + security/architecture). The raw slices
> **contradicted each other** on several load‑bearing decisions; this document is the
> single reconciled source of truth. Every "🔧 fix" note marks a defect the review
> caught and how it's resolved here.

**Target stack:** .NET 9 / ASP.NET Core · EF Core 9 (writes) + Dapper (shaped reads) ·
PostgreSQL 16+ · Npgsql · Railway (EU) → Hetzner later.

---

## 0. The reconciled decisions (read this first)

The independent design agents disagreed. These are the binding rulings — everything
below obeys them.

| # | Decision | Why / what it fixes |
|---|----------|---------------------|
| **D1** | **Money = integer minor units (`bigint` / C# `long`), never float, never `NUMERIC`.** Wire = JSON **string** of minor units + sibling `currency`. | Makes "splits sum to total" a provable integer‑partition invariant. 🔧 Fixes the schema‑vs‑domain split (one slice said `NUMERIC(19,4)`, four said `bigint`). |
| **D2** | **A `currencies` table is the scale authority** (`minor_units`: CZK=0, EUR/USD/GBP=2). The client already encodes this in `Currency.decimals`. | 🔧 A fixed 4‑decimal `NUMERIC` could store uncharge­able amounts (`10.2550 CZK`). Per‑currency scale closes that. |
| **D3** | **Splits use largest‑remainder (Hamilton) allocation**, and `expense_splits` rows are the **single** source of participants. | 🔧 Client's current `distributeEqually` dumps the remainder on the *last* member — unfair and order‑dependent. Also kills the "participants live in 3 places" hazard. |
| **D4** | **Balances are computed on‑demand, per currency, including settlements. No materialized balance table.** | 🔧 A stored‑balance table is a dual source of truth → drift bugs, the worst class in money apps. On‑demand is sub‑ms at this scale. |
| **D5** | **Per‑currency settle only (v1).** A settlement is in the **same currency as the debt**. No silent FX‑drift nudge; FX convergence is a deferred, snapshot‑backed feature. | 🔧 "Settle in default currency" + "balances per currency" were mutually inconsistent and broke the zero‑sum invariant. |
| **D6** | **One authorization path:** an indexed `(group_id, user_id)` membership check is the **first** statement of every group‑scoped request; non‑members get **404** (no existence leak). | 🔧 One slice used `409 Conflict` after loading the whole group — an IDOR/existence‑leak. |
| **D7** | **GDPR delete = anonymize, not hard‑delete.** Soft‑delete the user, null `user_id` on their member slots, scrub PII, **retain the ledger**. | 🔧 Cascade‑deleting financial rows destroys *other members'* records. |
| **D8** | **Optimistic concurrency via a real `version int` column + `If-Match`/`412`.** Money entities reject‑and‑merge, never silent last‑write‑wins. | 🔧 `updated_at` collides at ms resolution; LWW silently discards an edit that changes who owes what. |
| **D9** | **One idempotency mechanism:** a permanent `(group_id, client_id)` unique constraint on create. | 🔧 Requiring *both* a 24h `Idempotency-Key` and `(group_id, client_id)` is two overlapping dedups with a gap. |

---

## 1. Data model

### 1.1 The one pattern that makes the product work: a participant ≠ a user

The current app's `Member` is "a name + id, **not** an account." That is exactly the
Splitwise **ghost/placeholder** concept, and we preserve it:

> A participant in a group is a `group_members` row. That row **may or may not** be
> linked to a real `users` account (`user_id NULL` = ghost).

Every money‑bearing foreign key points at **`group_members.id`**, never at `users.id`:
`expenses.paid_by`, `expense_splits.group_member_id`, `settlements.from_member` / `to_member`.

Consequences:
- You can split a bill with people who have no account yet.
- A ghost is **claimed** later by setting `user_id` — **no rewrite** of expenses/splits,
  because everything references the member row, not the user.
- The same human is a *different* member row in each group (correct — balances are per‑group).

### 1.2 Mapping the current Swift models

| Swift (main branch today) | Server |
|---|---|
| `Member { id, name, imageUrl?, iconSymbol? }` | a `group_members` row (+ nullable `user_id`, `role`) |
| `Group { id, name, memberIds[], defaultCurrency }` | `groups` row + N `group_members`; `memberIds[]` becomes the join table |
| `Expense.amount: Decimal` | `expenses.amount_minor bigint` (Decimal↔minor at the API edge) |
| `Expense.currency: Currency` (CZK/EUR/USD/GBP) | `expenses.currency char(3)` → `currencies` |
| `Expense.splitType: equal/exact/shares` | `expenses.split_type` + `expense_splits` rows |
| `Expense.date: Date` | `expenses.expense_date date` |
| `BalanceCalculator` / `DebtSimplifier` / `FXRates` | mirrored server‑side (server is authoritative) |

### 1.3 DDL (reconciled — `bigint` minor units everywhere)

```sql
CREATE EXTENSION IF NOT EXISTS pg_uuidv7;   -- maintained, time-ordered UUIDs (NOT a hand-rolled shim)
CREATE EXTENSION IF NOT EXISTS citext;

-- Scale authority. CZK=0, EUR/USD/GBP=2. Seed at migration time.
CREATE TABLE currencies (
    code        char(3) PRIMARY KEY,
    minor_units smallint NOT NULL CHECK (minor_units BETWEEN 0 AND 4),
    symbol      text NOT NULL
);

CREATE TABLE users (
    id               uuid PRIMARY KEY DEFAULT uuid_generate_v7(),
    display_name     text NOT NULL,
    email            citext UNIQUE,                       -- nullable (Apple private relay)
    default_currency char(3) NOT NULL DEFAULT 'CZK' REFERENCES currencies(code),
    locale           text NOT NULL DEFAULT 'cs',
    avatar_url       text,
    token_epoch      int NOT NULL DEFAULT 0,              -- bump → invalidate all access tokens
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    deleted_at       timestamptz                          -- soft delete (GDPR anonymize)
);

CREATE TABLE auth_identities (
    id         uuid PRIMARY KEY DEFAULT uuid_generate_v7(),
    user_id    uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    provider   text NOT NULL,                             -- 'apple' | 'email'
    subject    text NOT NULL,                             -- Apple 'sub' / email
    apple_refresh_token_enc bytea,                        -- 🔧 Apple refresh token from the §4.1 code exchange,
                                                          --    encrypted at rest; upserted every sign-in,
                                                          --    revoked + hard-deleted by the §4.4 erase
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (provider, subject)
);

CREATE TABLE refresh_tokens (
    id          uuid PRIMARY KEY DEFAULT uuid_generate_v7(),
    user_id     uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_hash  bytea NOT NULL UNIQUE,                    -- SHA-256 of the opaque token
    family_id   uuid NOT NULL,                            -- rotation family (reuse detection)
    replaced_by uuid REFERENCES refresh_tokens(id),
    expires_at  timestamptz NOT NULL,
    revoked_at  timestamptz,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE groups (
    id               uuid PRIMARY KEY DEFAULT uuid_generate_v7(),
    name             text NOT NULL CHECK (length(name) BETWEEN 1 AND 100),
    default_currency char(3) NOT NULL REFERENCES currencies(code),
    icon_symbol      text,
    version          int NOT NULL DEFAULT 1,              -- optimistic concurrency
    created_by       uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    archived_at      timestamptz,                         -- reversible read-only flag (§3.7)
    deleted_at       timestamptz                          -- tombstone ONLY (no group-delete path in v1)
);
-- 🔧 fix: groups.deleted_at previously doubled as the "archive" flag while every other
-- table's deleted_at means tombstone. Split: archived_at = reversible archive,
-- deleted_at = pure tombstone, reserved for a future hard-purge path.

CREATE TABLE group_members (
    id           uuid PRIMARY KEY DEFAULT uuid_generate_v7(),
    group_id     uuid NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
    user_id      uuid REFERENCES users(id) ON DELETE SET NULL,   -- NULL = ghost
    display_name text NOT NULL,
    role         text NOT NULL DEFAULT 'member' CHECK (role IN ('owner','admin','member')),
    icon_symbol  text,
    image_url    text,
    version      int NOT NULL DEFAULT 1,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    deleted_at   timestamptz,
    former_user_id uuid REFERENCES users(id) ON DELETE SET NULL,  -- 🔧 set on leave; the D7 erase scrub's ONLY route to unlinked rows (§3.8.3)
    UNIQUE (group_id, user_id),                           -- a real user appears once per group
    CHECK (user_id IS NOT NULL OR role = 'member')        -- 🔧 a ghost can never HOLD owner/admin (§3.8.2)
);
-- Invariant: every group keeps ≥1 active owner (constraint trigger §1.5 + app rule). Only
-- LINKED rows can be owners (CHECK above), so a ghost can never satisfy — or hold open —
-- that invariant while the last real owner walks out.

CREATE TABLE categories (
    id          uuid PRIMARY KEY DEFAULT uuid_generate_v7(),
    group_id    uuid REFERENCES groups(id) ON DELETE CASCADE,    -- NULL = global preset
    name        text NOT NULL,
    icon_symbol text,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),   -- 🔧 fix: was missing — categories must flow through /sync (§3.5)
    deleted_at  timestamptz                           -- 🔧 fix: soft delete; expenses keep category_id, deleted categories render as historical
);
-- Two partial unique indexes instead of a magic-sentinel UUID. Live rows only, so a
-- soft-deleted name can be reused:
CREATE UNIQUE INDEX uq_categories_global ON categories (lower(name)) WHERE group_id IS NULL AND deleted_at IS NULL;
CREATE UNIQUE INDEX uq_categories_group  ON categories (group_id, lower(name)) WHERE group_id IS NOT NULL AND deleted_at IS NULL;
-- Global presets (group_id IS NULL) are seeded in the same migration as `currencies`,
-- with FIXED UUIDs so clients can localize by id. Seed list + endpoints: §3.9.

CREATE TABLE expenses (
    id           uuid PRIMARY KEY DEFAULT uuid_generate_v7(),
    group_id     uuid NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
    client_id    text,                                    -- offline id → idempotency
    title        text NOT NULL CHECK (length(title) BETWEEN 1 AND 140),
    amount_minor bigint NOT NULL CHECK (amount_minor > 0),
    currency     char(3) NOT NULL REFERENCES currencies(code),
    paid_by      uuid NOT NULL REFERENCES group_members(id) ON DELETE RESTRICT,
    split_type   text NOT NULL CHECK (split_type IN ('equal','exact','shares','percentage')),
    category_id  uuid REFERENCES categories(id) ON DELETE SET NULL,
    icon_symbol  text,
    expense_date date NOT NULL CHECK (expense_date BETWEEN '2000-01-01' AND (now()::date + 2)),  -- 🔧 +2d UTC buffer ≥ any client's local today+1 (§3.4)
    notes        text CHECK (length(notes) <= 2000),      -- 🔧 bounded; 422 twin notes_too_long (§3.4)
    version      int NOT NULL DEFAULT 1,
    created_by   uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    deleted_at   timestamptz,
    UNIQUE (group_id, client_id)                          -- per-group create idempotency (D9)
);

CREATE TABLE expense_splits (
    expense_id      uuid NOT NULL REFERENCES expenses(id) ON DELETE CASCADE,
    group_member_id uuid NOT NULL REFERENCES group_members(id) ON DELETE RESTRICT,
    share_minor     bigint NOT NULL CHECK (share_minor >= 0),   -- resolved owed amount
    weight          int,                                  -- shares mode input (audit)
    basis_points    int,                                  -- percentage mode input (audit)
    PRIMARY KEY (expense_id, group_member_id)
);

CREATE TABLE settlements (
    id           uuid PRIMARY KEY DEFAULT uuid_generate_v7(),
    group_id     uuid NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
    client_id    text,
    from_member  uuid NOT NULL REFERENCES group_members(id) ON DELETE RESTRICT,
    to_member    uuid NOT NULL REFERENCES group_members(id) ON DELETE RESTRICT,
    amount_minor bigint NOT NULL CHECK (amount_minor > 0),
    currency     char(3) NOT NULL REFERENCES currencies(code),  -- = the debt's currency (D5)
    method       text,
    settled_on   date NOT NULL,
    notes        text CHECK (length(notes) <= 2000),      -- 🔧 bounded (Appendix A); 422 twin `notes_too_long` (§3.4)
    version      int NOT NULL DEFAULT 1,
    created_by   uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),   -- 🔧 fix: was missing — void (§3.6) and /sync (§3.5) need it
    deleted_at   timestamptz,                          -- void = soft delete (§3.6)
    CHECK (from_member <> to_member),
    UNIQUE (group_id, client_id)
);

CREATE TABLE invites (
    id          uuid PRIMARY KEY DEFAULT uuid_generate_v7(),
    group_id    uuid NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
    token_hash  bytea NOT NULL UNIQUE,                    -- hash of a 128-bit single-use token
    email       citext,
    member_id   uuid REFERENCES group_members(id) ON DELETE SET NULL,  -- ghost to claim
    invited_by  uuid REFERENCES users(id) ON DELETE SET NULL,
    expires_at  timestamptz NOT NULL,
    accepted_at timestamptz,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE activity_log (                               -- append-only event log; see §3.10
    id          uuid PRIMARY KEY DEFAULT uuid_generate_v7(),
    group_id    uuid NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
    actor_user  uuid REFERENCES users(id) ON DELETE SET NULL,
    verb        text NOT NULL,                            -- closed taxonomy (§3.10), e.g. 'expense.created'
    target_type text NOT NULL,                            -- 'expense'|'settlement'|'member'|'group'|'invite'
    target_id   uuid,
    metadata    jsonb,                                    -- ids + non-money field NAMES only — never amounts,
                                                          -- titles or display names. 🔧 The feed hydrates
                                                          -- display data at read time (§3.10); GDPR scrub trivial.
                                                          -- App-enforced ≤ 4 KB serialized (Appendix A; §3.4) —
                                                          -- server-written, so no 422 twin.
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE devices (
    id         uuid PRIMARY KEY,                          -- 🔧 CLIENT-generated stable device UUID — no server
                                                          --    default; PUT /me/devices/{id} upserts on it (§4.5)
    user_id    uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    apns_token text NOT NULL,                             -- APNs tokens rotate; upsert refreshes in place
    platform   text NOT NULL DEFAULT 'ios',
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (user_id, apns_token)                          -- leading user_id also serves the push fan-out lookup;
                                                          -- PUT absorbs same-token rows before insert (§4.5), so
                                                          -- this can never 500 a re-registering client
);

-- Sync feed (§3.5). Append-only. Rows are written by APPLICATION CODE in the same
-- transaction as every mutation — never by triggers: the synthetic 'access' entries
-- need actor/membership context a trigger doesn't have. Pruned past the retention
-- window (90 days — authoritative value: Appendix A) by a background job — cut by SEQ,
-- never by created_at (mechanics + why: §3.5.5).
CREATE TABLE change_log (
    seq         bigserial PRIMARY KEY,
    group_id    uuid NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
    entity_type text NOT NULL CHECK (entity_type IN
                    ('group','member','expense','settlement','category','access')),
    entity_id   uuid NOT NULL,               -- for 'access' rows: the affected users.id
    is_delete   boolean NOT NULL DEFAULT false,
    created_at  timestamptz NOT NULL DEFAULT now()  -- informational only; NOT the prune key (§3.5.5)
);

-- Prune watermark (§3.5.5): highest seq ever pruned, written in the SAME transaction as
-- the prune DELETE. /sync serves 410 iff a cursor's seq < pruned_through_seq — this stays
-- correct even when change_log has been pruned to empty, where min(seq) would be NULL.
CREATE TABLE change_log_watermark (
    one                boolean PRIMARY KEY DEFAULT true CHECK (one),  -- single-row table
    pruned_through_seq bigint NOT NULL DEFAULT 0,
    pruned_at          timestamptz
);
INSERT INTO change_log_watermark (one) VALUES (true);
```

### 1.4 Indexes

```sql
CREATE INDEX ix_group_members_group ON group_members(group_id)        WHERE deleted_at IS NULL;
CREATE INDEX ix_group_members_user  ON group_members(user_id)         WHERE user_id IS NOT NULL AND deleted_at IS NULL;
CREATE INDEX ix_expenses_group_date ON expenses(group_id, expense_date DESC, id DESC) WHERE deleted_at IS NULL;
CREATE INDEX ix_expenses_paid_by    ON expenses(paid_by);
CREATE INDEX ix_splits_member       ON expense_splits(group_member_id);   -- balance math still joins expenses for paid_by+currency
CREATE INDEX ix_settlements_group   ON settlements(group_id, settled_on DESC) WHERE deleted_at IS NULL;
CREATE INDEX ix_activity_group_time ON activity_log(group_id, created_at DESC);
CREATE INDEX ix_refresh_user        ON refresh_tokens(user_id)         WHERE revoked_at IS NULL;
CREATE INDEX ix_change_log_group  ON change_log(group_id, seq);   -- membership-filtered delta pages
CREATE INDEX ix_change_log_access ON change_log(entity_id, seq) WHERE entity_type = 'access';
-- no ix_change_log_seq: seq is the PK; its index already serves `WHERE seq > @cursor ORDER BY seq`.
```

### 1.5 DB-enforced invariants

- **Splits sum to total.** A `DEFERRABLE INITIALLY DEFERRED` constraint trigger asserts
  `SUM(share_minor) = amount_minor` per expense at COMMIT, taking `SELECT … FOR UPDATE`
  on the expense row so two interleaved split edits can't both pass. (🔧 closes the TOCTOU
  the review flagged.)
- **Split member ∈ group.** `expense_splits.group_member_id` and `expenses.paid_by` must
  belong to `expenses.group_id` — enforced at the boundary + a trigger.
- **Currency legality.** `amount_minor`/`share_minor` are integers; with `currencies` as the
  scale authority there is no representable‑but‑uncharge­able amount.
- **≥1 owner per group**, **`from_member ≠ to_member`**, **bounded `expense_date`**.
- **Bounded free text.** `notes ≤ 2000` chars on expenses & settlements (CHECK);
  `activity_log.metadata` ≤ 4 KB serialized (authoritative values: Appendix A) — app‑enforced
  only, since it is server‑written
  with no user input path. Every **user‑reachable** CHECK in this schema has a
  FluentValidation **twin** that rejects with `422` at the boundary (§3.4); the DB constraint
  is the backstop, never the primary rejection path. Two deliberate non‑twins:
  `currencies.minor_units` is seed‑only (no user input path), and
  `group_members.role IN ('owner','admin','member')` gets its twin in the membership cluster.
- **A ghost never holds a privileged role.** `CHECK (user_id IS NOT NULL OR role = 'member')`
  on `group_members` — the structural backstop for §3.8.2's "a future claimer must never
  silently inherit admin": claim, leave, and the D7 unlink cannot mint a privileged ghost
  even if app code forgets the demote.
- **No non‑zero‑balance soft‑delete.** Same TOCTOU family — and the same medicine — as the
  split‑sum trigger above: member remove takes `SELECT … FOR UPDATE` on the target row
  *before* computing buckets, and expense/settlement writes take `SELECT … FOR UPDATE` on
  every referenced `group_members` row *before* validating `member_deleted`. 🔧 The FK
  insert's implicit `FOR KEY SHARE` is **not** enough — it does not conflict with the
  remove's `FOR NO KEY UPDATE` on `deleted_at` (a non‑key column), so under READ COMMITTED
  both sides would commit. Protocol: §3.8.3. Belt: `/balances` includes any soft‑deleted
  row whose net ≠ 0 (§3.8.3), so `Σ net == 0` holds even against the unknown‑unknowns.

**Migrations:** EF Core migrations, applied as a **discrete deploy step via an EF bundle**
(never migrate‑on‑startup with multiple instances). See §6.

---

## 2. Core domain — balances & settlement (the heart)

Pure C# (`Paybitch.Domain`), **zero** EF/HTTP dependencies, fully unit‑testable. Money is
integer minor units tagged with a currency; scale is per‑currency (CZK minor unit = 1 Kč).

### 2.1 Money value object

```csharp
// Scale is INJECTED, never derived (D2). The application edge constructs Currency
// from the currencies table; the domain never looks a scale up — it receives it.
public readonly record struct Currency(string Code, int Decimals)
{
    // v1 closed set — presets for tests & domain convenience ONLY. Runtime instances
    // are built at the edge from currencies.minor_units, not from this list.
    public static readonly Currency CZK = new("CZK", 0), EUR = new("EUR", 2),
                                    USD = new("USD", 2), GBP = new("GBP", 2);

    private static readonly long[] Pow10 = { 1, 10, 100, 1_000, 10_000 };  // minor_units CHECK: 0..4
    public long MinorPerMajor => Pow10[Decimals];
}

public readonly record struct Money(long Minor, Currency Currency)
{
    public static Money Zero(Currency c) => new(0, c);
    public Money Add(Money o)      { Ensure(o); return this with { Minor = checked(Minor + o.Minor) }; }
    public Money Subtract(Money o) { Ensure(o); return this with { Minor = checked(Minor - o.Minor) }; }
    public decimal ToDecimal() => (decimal)Minor / Currency.MinorPerMajor;          // JSON edge only
    public static Money FromDecimal(decimal major, Currency c) =>
        new((long)Math.Round(major * c.MinorPerMajor, 0, MidpointRounding.ToEven), c);
    private void Ensure(Money o) { if (Currency != o.Currency) throw new CurrencyMismatchException(Currency, o.Currency); }
}
```

**One scale authority (D2).** 🔧 fix: the previous snippet hardcoded
`Code == "CZK" ? 0 : 2` — a **second** scale authority that would silently disagree with the
`currencies` table the moment a 0‑ or 3‑decimal currency (JPY, KWD…) is seeded. Scale is now a
constructor argument: the application edge resolves `Decimals` from `currencies.minor_units`
(4 immutable rows, cached at startup) and hands the domain a fully formed `Currency`. Adding a
currency later = table seed + client enum release — **zero domain change**.

**The v1 currency set is CLOSED: `{CZK, EUR, USD, GBP}`** — exactly the iOS `Currency` enum.
Any other code is rejected at the boundary with `422 unsupported_currency` before it reaches
the domain or the DB (the `REFERENCES currencies(code)` FK is the backstop, per the §3.4 twin
rule). `GET /currencies` returns this closed set.

### 2.2 Net balance — definition

For each member `m` and currency `C`:

```
net(m, C) =  Σ paid(m,e)  −  Σ share(m,e)  +  Σ settlementEffect(m,s)
```

- `paid(m,e)` = full `e.amount` if `m` paid, else 0.
- `share(m,e)` = `m`'s allocated split share (shares sum to `e.amount` exactly).
- settlement `from→to A`: `+A` to `from`, `−A` to `to`.

Sign: `net > 0` ⇒ the group owes `m`; `net < 0` ⇒ `m` owes. **Invariant:** `Σ net(m,C) = 0`
for every currency, always. This is the first thing the property tests assert.

> 🔧 The current Swift `BalanceCalculator` **omits settlements** and converts to one currency
> too early. Port the per‑currency + settlement model below.

### 2.3 Splitting with correct rounding (largest‑remainder)

EQUAL / SHARES / PERCENTAGE are one problem: distribute `total` minor units by integer
weights, then hand the leftover units one‑each to the **largest remainders** (deterministic
tie‑break). Shares always sum to the total exactly; the error is spread fairly, not dumped
on the last member.

```csharp
private static IReadOnlyList<(string, Money)> ProportionalAllocate(
    long total, Currency c, IReadOnlyList<string> members, Func<string,long> weightOf)
{
    long W = members.Sum(weightOf);
    if (W <= 0) throw new InvalidSplitException("Total weight must be positive.");

    var rows = new List<(string Id, long Floor, long Rem, long W)>(members.Count);
    long allocated = 0;
    foreach (var m in members)
    {
        long w = weightOf(m);
        long num = checked(total * w);           // use Int128 if total*w can overflow long
        long floor = num / W, rem = num % W;
        rows.Add((m, floor, rem, w));
        allocated += floor;
    }
    long leftover = total - allocated;           // 0 ≤ leftover < members.Count

    var order = rows.OrderByDescending(r => r.Rem)
                    .ThenByDescending(r => r.W)
                    .ThenBy(r => r.Id, StringComparer.Ordinal)   // 🔧 simple, unbiased tie-break
                    .ToList();
    var bonus = new Dictionary<string,long>();
    for (int k = 0; k < leftover; k++) bonus[order[k].Id] = bonus.GetValueOrDefault(order[k].Id) + 1;

    return rows.Select(r => (r.Id, new Money(r.Floor + bonus.GetValueOrDefault(r.Id), c))).ToList();
}
```

Worked examples: **100.00 EUR ÷ 3** → `3334, 3333, 3333` (sum 10000). **100 Kč ÷ 3** →
`34, 33, 33` (same code, scale lives in `Currency`). EXACT bypasses this and is validated to
sum exactly to `amount_minor`.

### 2.4 Debt simplification — greedy min‑cash‑flow

Match the largest creditor with the largest debtor, transfer the smaller magnitude, repeat.
Each step zeroes ≥1 party ⇒ **≤ N−1 transfers**. Integer math, exact.

```csharp
public static IReadOnlyList<DebtEdge> Simplify(IReadOnlyCollection<MemberBalance> balances)
{
    var currency = balances.First().Net.Currency;            // single-currency by contract (D5)
    var creditors = new PriorityQueue<string,long>();        // net > 0
    var debtors   = new PriorityQueue<string,long>();        // net < 0
    var owed = new Dictionary<string,long>();
    foreach (var b in balances) {
        long n = b.Net.Minor;
        if (n > 0) { creditors.Enqueue(b.MemberId, -n); owed[b.MemberId] = n; }
        else if (n < 0) { debtors.Enqueue(b.MemberId, n); owed[b.MemberId] = n; }
    }
    var edges = new List<DebtEdge>();
    while (creditors.Count > 0 && debtors.Count > 0) {
        var cr = creditors.Dequeue(); var dr = debtors.Dequeue();
        long pay = Math.Min(owed[cr], -owed[dr]);
        edges.Add(new DebtEdge(dr, cr, new Money(pay, currency)));
        if ((owed[cr] -= pay) > 0) creditors.Enqueue(cr, -owed[cr]);
        if ((owed[dr] += pay) < 0) debtors.Enqueue(dr, owed[dr]);
    }
    return edges;
}
```

Honest about optimality: this is the algorithm Splitwise‑class apps ship. It's `O(N log N)`
and within ~1 transfer of optimal; the exact minimum is NP‑hard and not worth it. **Multi‑
currency: call `Simplify` once per currency bucket** — never net EUR against CZK (D5).

### 2.5 Where it lives & how it's triggered

Pure functions in `Paybitch.Domain`; a thin `BalanceService` in the application layer
projects DB rows → immutable domain inputs, calls the math, maps `Money.ToDecimal()` at the
JSON edge. **On‑demand compute, no materialized balances** (D4) — `O(expenses × members)`
integer adds, sub‑ms for real groups. Add a *derived* cache (keyed on group version) only if a
read‑hot group ever appears; never a second authoritative store.

### 2.6 Test scenarios that MUST pass (property + example)

Invariants (property‑tested with FsCheck): `Σ net == 0` per currency; `Σ shares == amount`;
`Σ edges == total credit`; edges ≤ N−1; same input → identical output. Example cases:
uneven cents (100/3), CZK 0‑decimals, 1‑cent split among 3, payer also in split, settlement
exactly extinguishes, **settlement exceeds debt → flips** (no clamp), removed member with
outstanding balance still tracked, currency mismatch throws, EXACT not summing → `422`,
all‑zero weights → error not divide‑by‑zero, fairness over 1000× (10/3).

> 🔧 **"Removed member with outstanding balance still tracked" = the *leave* case.** Leaving unlinks
> the member (`user_id := NULL` → ghost) at **any** balance and every net survives — that is what this
> scenario asserts. Admin *remove* is a different operation that soft‑deletes and is allowed only at
> all‑zero buckets (`409 balance_not_zero`). No contradiction with §3.1 — see §3.8.3.

---

## 3. REST API contract (v1)

`https://api.paybitch.app/v1` · Bearer JWT · timestamps UTC ISO‑8601 `Z` · expense `date` =
`yyyy-MM-dd` · **money = JSON string of minor units + `currency`** (D1).

### 3.1 Endpoints (abridged)

| Method | Path | Purpose | Auth |
|---|---|---|---|
| `POST` | `/auth/apple` | Apple identity‑token exchange → access + refresh; first call provisions user | No |
| `POST` | `/auth/refresh` | Rotate refresh → new access + refresh | No |
| `POST` | `/auth/logout` | Revoke refresh (optionally all sessions); optional `deviceId` also deletes that device row, revoke‑all deletes them all (§4.5) | Yes |
| `GET`/`PATCH`/`DELETE` | `/me` | Profile / settings / GDPR delete (anonymize, §4.4) | Yes |
| `GET` | `/me/export` | GDPR data export (Art. 15/20) — synchronous JSON dump (§4.4) | Yes |
| `PUT` | `/me/devices/{id}` | Upsert device by **client‑generated** stable device UUID; registers/refreshes APNs token (§4.5) | Yes |
| `DELETE` | `/me/devices/{id}` | Unregister device (push opt‑out) | Yes |
| `GET`/`POST` | `/groups` | List mine (archived included, flagged `"archived": true`) / create (caller = owner) | Yes |
| `GET`/`PATCH`/`DELETE` | `/groups/{g}` | Detail / rename · **unarchive** (`If-Match`) / **archive** (reversible, idempotent, no `If-Match` — §3.7) | Yes (archive/unarchive: admin) |
| `GET`/`POST` | `/groups/{g}/members` | List / add **ghost** member | Yes |
| `PATCH` | `/groups/{g}/members/{m}` | Rename / icon (self; image URLs **not writable** — reserved columns, §3.12) — role changes only via `…/role` | Yes |
| `PUT` | `/groups/{g}/members/{m}/role` | Change role (only an owner grants/revokes owner or admin; `409 last_owner`) — §3.8.2 | admin |
| `POST` | `/groups/{g}/members/me/leave` | Leave: unlink self → ghost, **any** balance, ledger preserved — §3.8.3 | Yes |
| `DELETE` | `/groups/{g}/members/{m}` | Remove: soft‑delete — all buckets `0` (`409 balance_not_zero`); an `owner`/`admin` target requires an **owner** caller — §3.8.3 | admin |
| `POST`/`GET` | `/groups/{g}/invites` | Create (**link‑only** in v1, §3.12 — generic **or ghost‑claim** via body `memberId`, §3.8.1) / list | Yes |
| `DELETE` | `/groups/{g}/invites/{i}` | Revoke = hard delete (§3.12); accepted → `409 invite_already_accepted` | Yes |
| `GET` | `/invites/{token}` | Preview (rate‑limited by IP): group name+size; ghost invites add **that ghost's own nets** — §3.8.1 | No |
| `POST` | `/invites/{token}/accept` | Join **or claim a ghost** (`claimMemberId` + `acceptInheritedLedger`) — §3.8.1 | Yes |
| `GET`/`POST` | `/groups/{g}/expenses` | List (cursor) / create | Yes |
| `GET`/`PUT`/`DELETE` | `/groups/{g}/expenses/{e}` | Detail / **full replace** / soft‑delete — `If-Match` required on `PUT` **and** `DELETE` (§3.2b) | Yes |
| `GET`/`POST`/`DELETE` | `/groups/{g}/settlements[/{s}]` | List / record / void (soft delete + `If-Match`, §3.6) | Yes |
| `GET` | `/groups/{g}/balances` | Per‑member nets **and** simplified debts | Yes |
| `GET` | `/groups/{g}/activity` | Activity feed (cursor, hydrated at read time — §3.10) | Yes |
| `GET` | `/currencies` | Codes + `minorUnits` (scale authority) | No |
| `GET` | `/categories` | Global preset categories (fixed seed — §3.9) | Yes |
| `GET`/`POST` | `/groups/{g}/categories` | List group customs / create (**any member**) | Yes |
| `DELETE` | `/groups/{g}/categories/{c}` | Soft‑delete a custom category | admin |
| `GET` | `/sync?since=<cursor>&limit=n` | Delta sync — changes + tombstones (§3.5); omit `since` ⇒ mint a head cursor | Yes |

> 🔧 `POST /groups/{g}/members/{m}/claim` from the raw design is **removed**: a claimer is not yet
> a member, so D6's membership‑first check would `404` the very request that makes them one — and an
> out‑of‑band `user_id` write can trip `UNIQUE(group_id, user_id)` unguarded. The **invite token is
> the authorization** for both join and claim; the flow is folded into `POST /invites/{token}/accept`
> (§3.8.1).

#### `GET /groups` — list item shape

The group list is the app's home screen; one response must render it with **zero follow‑up
calls**. Each item = group header + the **caller's** per‑currency net only (full per‑member
balances stay on `GET /groups/{g}/balances`, §3.3).

```jsonc
// GET /groups                          (cursor envelope per §3.4)
{
  "data": [{
    "id": "grp_7Hk2",
    "name": "Praha flat",
    "icon": "house.fill",
    "defaultCurrency": "CZK",
    "memberCount": 4,
    "archived": false,
    "version": 7,                            // feeds If-Match on PATCH /groups/{g} (D8)
    "callerBalance": [                       // caller's nets only; zero buckets omitted
      { "currency": "CZK", "net": "96050" }, // + ⇒ the group owes the caller (§2.2 sign)
      { "currency": "EUR", "net": "-1250" }
    ]
  }],
  "page": { "nextCursor": "b64…", "hasMore": false, "limit": 25 }
}
```

- `callerBalance` is computed in **one** Dapper query across **all** the caller's groups — a
  single `GROUP BY group_id, currency` aggregate over (amounts paid − shares owed ± settlement
  effects) joined to the caller's `group_members` rows. **No per‑group N+1**, and no
  materialized store (D4).
- `archived` = `groups.archived_at IS NOT NULL` (§3.7 — `deleted_at` is a pure tombstone,
  never the archive flag). Archived groups still list (history stays reachable); any write to
  one → `409 group_archived` (Appendix B).
- Money on the wire stays D1: `net` is a JSON **string** of minor units with a sibling
  `currency` — never a float.

### 3.2 Create expense (EQUAL) — request → response

```jsonc
// POST /groups/grp_7Hk2/expenses
{
  "clientId": "exp_local_0F8B21C4",        // offline id → (groupId, clientId) idempotency (D9)
  "title": "Dinner at Lokál",
  "amount": "84000", "currency": "CZK",    // string minor units (D1)
  "paidBy": "mem_alice",
  "date": "2026-06-27",
  "split": { "type": "equal", "among": ["mem_alice","mem_bob","mem_carol"] }
}
```
```jsonc
// 201 Created   ETag: "1"
{
  "id": "exp_9Qm4", "clientId": "exp_local_0F8B21C4", "groupId": "grp_7Hk2",
  "amount": "84000", "currency": "CZK", "paidBy": "mem_alice",
  "split": { "type": "equal", "among": ["mem_alice","mem_bob","mem_carol"] },
  "shares": [                               // server-authoritative
    {"memberId":"mem_alice","amount":"28000"},
    {"memberId":"mem_bob","amount":"28000"},
    {"memberId":"mem_carol","amount":"28000"}],
  "createdBy": "usr_alice", "createdAt": "2026-06-28T14:03:11Z", "version": 1, "deleted": false
}
```
EXACT split body: `{"type":"exact","amounts":[{"memberId":"…","amount":"40000"},…]}` —
must sum exactly to `amount` (`422` otherwise). The split member set **is** the participant
set (no separate `among`). SHARES: `{"type":"shares","weights":[{"memberId":"…","weight":2},…]}`.

PERCENTAGE: `{"type":"percentage","percents":[{"memberId":"…","basisPoints":2500},…]}`.
Percentages travel as integer **basis points** (1% = 100 bp) — a plain JSON integer, never a
float (the money‑string rule of D1 is for minor‑unit amounts only; the no‑float rule is for
everything). 🔧 Closes a gap: `split_type = 'percentage'` and `expense_splits.basis_points`
existed in the DDL with no wire contract.

Validation (`422 application/problem+json` on failure):

| Rule | Machine `code` |
|---|---|
| Σ `basisPoints` **== 10000 exactly** | `split_percent_sum_mismatch` |
| every entry `basisPoints > 0` (integer) | `invalid_basis_points` |
| entry set **is** the participant set — every `memberId` in the group, no duplicates, non‑empty (mirrors EXACT/SHARES) | `split_member_invalid` |

Shares are **server‑authoritative** via the same §2.3 `ProportionalAllocate` with
`weightOf = basisPoints` — no percentage‑specific rounding code exists. Each input
`basisPoints` is persisted on its `expense_splits.basis_points` row as the audit input,
exactly like `weight` for SHARES; `share_minor` remains the single resolved truth for
balance math. Since Σ weights is pinned at 10 000, the §2.3 `total * w` product overflows
`long` only past ~9.2 × 10¹⁴ minor units — unreachable for real expenses.

```jsonc
// POST /groups/grp_7Hk2/expenses      (100.00 EUR at 50 / 30 / 20)
{
  "clientId": "exp_local_5C11D9AA",
  "title": "Hotel room",
  "amount": "10000", "currency": "EUR",
  "paidBy": "mem_alice",
  "date": "2026-06-29",
  "split": { "type": "percentage", "percents": [
    {"memberId":"mem_alice","basisPoints":5000},
    {"memberId":"mem_bob","basisPoints":3000},
    {"memberId":"mem_carol","basisPoints":2000}] }
}
```
```jsonc
// 201 Created — shares are an exact integer partition (Σ == amount, always)
"split": { "type": "percentage", "percents": [
  {"memberId":"mem_alice","basisPoints":5000},
  {"memberId":"mem_bob","basisPoints":3000},
  {"memberId":"mem_carol","basisPoints":2000}] },
"shares": [
  {"memberId":"mem_alice","amount":"5000"},
  {"memberId":"mem_bob","amount":"3000"},
  {"memberId":"mem_carol","amount":"2000"}]
```

Rounding rides the largest‑remainder machinery: `10.00 EUR` at 3333/3333/3334 bp →
`333, 333, 334` (Σ = 1000; the leftover cent goes to the largest remainder, not the last
member).

> Coordination note: `split_percent_sum_mismatch` and `invalid_basis_points` are **new**
> machine codes — registered in the error‑code catalog (constants cluster, Appendix B) next to
> `split_sum_mismatch` (EXACT). One scheme across split types: sum mismatch =
> `split_sum_mismatch` (EXACT) / `split_percent_sum_mismatch` (PERCENTAGE) — kept distinct
> because the client remedy differs (fix percentages vs fix amounts); per‑entry value violation
> = `invalid_basis_points` (PERCENTAGE), parallel to `share_negative` (EXACT) and
> `invalid_weight` (SHARES); participant‑set violation (member not in group / duplicate /
> empty set) = `split_member_invalid`, shared by **all** split types.

### 3.2b Replace / delete expense — full‑aggregate semantics

`PUT /groups/{g}/expenses/{e}` **replaces the whole aggregate** (expense row + all split rows).
It is not a merge‑patch: an optional field omitted from the body (`categoryId`, `iconSymbol`,
`notes`) is **cleared**, not kept. 🔧 fix: the raw slices left PUT semantics undefined; this
pins them.

| Class | Fields | Rule |
|---|---|---|
| **Mutable** | `title`, `amount`, `currency`, `paidBy`, `split` (type **and** members), `categoryId`, `iconSymbol`, `date`, `notes` | Anything goes. Changing `currency` **moves the expense between balance buckets** (D5); that's legal and free — balances are computed on demand (D4), so nothing is re‑netted or backfilled. The new `amount` is minor units of the **new** currency. |
| **Immutable** | `id`, `groupId`, `clientId`, `createdBy`, `createdAt` | Server‑owned echo fields. Omit them or echo them unchanged; a body that **disagrees** → `422` `code:"immutable_field"`, `errors[]` naming each offending field. Moving an expense across groups = delete + recreate, never PUT. |

One transaction:

1. Membership check (D6), then load the expense `FOR UPDATE` — missing, another group's, or
   soft‑deleted → `404` (tombstoned aggregates are immutable).
2. Compare `If-Match` to `version`. Header absent → `428` `precondition_required`; stale →
   `412` (worked flow below).
3. **Delete all `expense_splits` rows and reinsert** the new allocation. The server recomputes
   `shares` (largest‑remainder, §2.3) against the new currency's scale. Splits are rows *of*
   the aggregate, not resources — they carry **no version of their own**.
4. Bump `version` **once**; the `DEFERRABLE INITIALLY DEFERRED` sum trigger (§1.5) proves
   `Σ share_minor = amount_minor` at COMMIT.
5. `200` + the full new representation (same shape as the `201` in §3.2) + `ETag: "<version>"`.

`DELETE` (soft) runs the same protocol: **`If-Match` required**, `version` bumped once, and the
tombstone leaves with that final `version` via `/sync` (tombstone wire shape: §3.5.4).
A replayed `DELETE` finds the tombstone → `404`; the outbox treats `DELETE`+`404` as success.
🔧 fix: §3.4's concurrency rule said "edits send `If-Match`", leaving `DELETE` ambiguous — but
deleting an expense changes who‑owes‑what exactly as much as editing it, so D8 binds **every**
mutation of the aggregate, including its soft‑delete.

Worked `412` flow — two phones edit `exp_9Qm4` (from §3.2, `version: 1`) after being offline.
Phone A already committed a price correction (`amount: "90000"` → `version: 2`). Phone B sends:

```jsonc
// PUT /groups/grp_7Hk2/expenses/exp_9Qm4     If-Match: "1"
{
  "title": "Dinner at Lokál",
  "amount": "84000", "currency": "CZK",
  "paidBy": "mem_bob",                        // B's intent: Bob actually paid
  "date": "2026-06-27",
  "split": { "type": "equal", "among": ["mem_alice","mem_bob","mem_carol"] }
}
```
```jsonc
// 412 Precondition Failed          application/problem+json
{
  "type": "https://api.paybitch.app/problems/version_conflict",
  "title": "Expense was modified since you read it",
  "status": 412,
  "code": "version_conflict",
  "current": {                                // full current representation — merge from THIS
    "id": "exp_9Qm4", "clientId": "exp_local_0F8B21C4", "groupId": "grp_7Hk2",
    "title": "Dinner at Lokál",
    "amount": "90000", "currency": "CZK", "paidBy": "mem_alice",
    "split": { "type": "equal", "among": ["mem_alice","mem_bob","mem_carol"] },
    "shares": [
      {"memberId":"mem_alice","amount":"30000"},
      {"memberId":"mem_bob","amount":"30000"},
      {"memberId":"mem_carol","amount":"30000"}],
    "createdBy": "usr_alice", "createdAt": "2026-06-28T14:03:11Z", "version": 2, "deleted": false
  }
}
```

Phone B merges its intent **into `current`** (keep A's `"90000"`, apply `paidBy: "mem_bob"`)
and retries with `If-Match: "2"` → `200`, `ETag: "3"`. Re‑sending the stale body against the
fresh version is forbidden by the client contract — that's last‑write‑wins with extra steps,
exactly what D8 exists to prevent.

### 3.3 Balances

```jsonc
// GET /groups/grp_7Hk2/balances        (v1: native currency buckets, no FX collapse — D5)
{
  "groupId": "grp_7Hk2",
  "byCurrency": [{
    "currency": "CZK",
    "balances": [
      {"memberId":"mem_alice","net":"96050"},
      {"memberId":"mem_carol","net":"-53000"},
      {"memberId":"mem_dave","net":"-43050"}],
    "simplified": [
      {"from":"mem_dave","to":"mem_alice","amount":"43050"},
      {"from":"mem_carol","to":"mem_alice","amount":"53000"}]
  }]
}
```
`Σ net == "0"` **per currency** (a client‑checkable invariant).

### 3.4 Conventions

- **Pagination:** cursor (`?cursor=<opaque>&limit=25`), envelope `{ data, page:{ nextCursor, hasMore, limit } }`. Cursor over offset because the ledger mutates mid‑scroll.
- **Idempotency:** `(groupId, clientId)` permanent dedup on create (D9). A replayed create is
  diffed against the existing record on its **canonical fields**: `title`, `amount`, `currency`,
  `paidBy`, `date`, `split` (type + membership; member arrays sorted by id; `weights`/`amounts`
  entries sorted by `memberId`), `categoryId`, `iconSymbol`, `notes` — normalized (absent
  optional field ≡ `null`; the boundary validator rejects non‑canonical amount strings like
  `"0084"`, so byte‑equality is well‑defined) and compared **byte‑equal**. Server‑set fields
  (`id`, `shares`, `version`, `createdBy`, `createdAt`) and the `clientId` echo are ignored.
  Settlements replay the same way over their own canonical set (`from`, `to`, `amount`,
  `currency`, `method`, `settledOn`, `notes`).
  - Identical → **`200`** (not `201`) + the existing record + its current `ETag`.
  - **Any** divergence → **`409`** `code:"client_id_conflict"` with `existingId` — the server
    **never** silently applies the divergent body and never pretends it matched.

  ```jsonc
  // 409 Conflict          application/problem+json
  { "type": "https://api.paybitch.app/problems/client_id_conflict",
    "title": "clientId already used in this group with different content",
    "status": 409, "code": "client_id_conflict", "existingId": "exp_9Qm4" }
  ```

  Corner case, by design: the record was **edited after** the original create (the `201` never
  reached the client) — the replay now diverges from the *current* row and gets the same `409`.
  Correct outbox reaction to any `client_id_conflict`: drop the pending create and pull
  `existingId` — a record with your `clientId` exists, so the create succeeded either way.
  Minting a fresh `clientId` and re‑POSTing is how a group gets double‑charged; never do it.
- **Concurrency:** every mutable resource returns `ETag: "<version>"`. For money aggregates
  (expenses, settlements) `If-Match` is **required** on `PUT` *and* `DELETE` — absent → `428`
  `precondition_required`; stale → `412` with the current representation (client merges, never
  clobbers — D8). `version` belongs to the aggregate (splits have none) and bumps exactly once
  per successful `PUT` and once per soft‑`DELETE` (§3.2b). 🔧 fix: this bullet previously said
  "edits send `If-Match`" — a stale‑read `DELETE` also rewrites who‑owes‑what, so leaving it
  optional there was ambiguity by omission; D8 covers any mutation of who‑owes‑what.
- **Errors:** RFC 9457 `application/problem+json` with a stable machine `code` and `errors[]`. **Never** echo raw exception messages or ids in `detail` (no existence leak — D6).
- **Validation twins (CHECK ↔ 422):** every **user‑reachable** DB `CHECK` constraint has a
  FluentValidation twin at the request boundary that rejects with `422` and a stable `code`.
  The CHECK is the **backstop, never the primary path** — a `23514` surfacing as a `500`
  means a twin is missing; treat it as a bug. 🔧 fix: previously `amount: "0"`, an over‑long
  `notes`, or a negative EXACT share hit the constraint and produced an opaque `500`.

  | DB constraint | Twin rule | `code` |
  |---|---|---|
  | `expenses.title` length 1–140 | `NotEmpty()`, `MaximumLength(140)` | `title_length` |
  | `groups.name` length 1–100 | `NotEmpty()`, `MaximumLength(100)` | `name_length` |
  | `amount_minor > 0` (expenses, settlements) | integer string, `> 0` | `amount_not_positive` |
  | `expense_date ∈ [2000‑01‑01, utc_today+2]` | date ∈ `[2000‑01‑01, local_today+1]` | `date_out_of_range` |
  | `notes` ≤ 2000 (expenses, settlements) | `MaximumLength(2000)` | `notes_too_long` |
  | `currency` ∈ closed v1 set (FK backstop) | member of `{CZK,EUR,USD,GBP}` (§2.1) | `unsupported_currency` |
  | `split_type ∈ ('equal','exact','shares','percentage')` | tagged‑union parse | `invalid_split_type` |
  | `expense_splits.share_minor >= 0` | EXACT: every `amounts[].amount` a non‑negative integer string | `share_negative` |
  | `share_minor >= 0` (via computed shares) | SHARES: every `weights[].weight` an integer ≥ 1 | `invalid_weight` |
  | `share_minor >= 0` (via computed shares) | PERCENTAGE: every `basisPoints` an integer ≥ 1 | `invalid_basis_points` |
  | `SUM(share_minor) = amount_minor` (deferred trigger, §1.5) | EXACT: amounts sum exactly to `amount` (§3.2) | `split_sum_mismatch` |
  | `SUM(share_minor) = amount_minor` (same trigger, via computed shares) | PERCENTAGE: Σ `basisPoints` == 10000 exactly (§3.2) | `split_percent_sum_mismatch` |
  | `expense_splits` PK `(expense_id, group_member_id)` + member FK | split entry set = participant set — every member in the group, no duplicates, non‑empty (**all** split types) | `split_member_invalid` |
  | `from_member <> to_member` | must differ | `self_settlement` |

  Sum‑to‑total alone does **not** imply non‑negative shares (`["-100","84100"]` sums to
  `84000`); both EXACT rules run, so a negative share is a `422 share_negative`, never a
  `23514`. The per‑item `weight ≥ 1` / `basisPoints ≥ 1` twins subsume §2.3's aggregate
  `W > 0` guard at the boundary and keep every computed `share_minor` non‑negative before it
  reaches the DB.

  **`expense_date` timezones — a one‑directional guarantee.** The server CHECK evaluates in
  **UTC** with a `+2 day` upper buffer (§1.3 🔧); clients validate in **local** time with a
  `+1 day` bound. For every timezone up to UTC+14, `local_today ≤ utc_today + 1`, hence
  `local_today + 1 ≤ utc_today + 2`: **anything a client‑side validator accepts, the server
  accepts.** The reverse direction is not claimed and does not matter — the server is
  deliberately the more permissive side, so an offline‑validated entry can never bounce off
  the date bound on push (no poisoned outbox entry). `activity_log.metadata` is
  server‑written, so its ≤ 4 KB serialized bound is enforced in app code at write time, not
  as a `422`.
  *(Coordination notes: the `code` values above register in the central problem+json error
  catalog owned by the constants cluster (Appendix B). Two CHECKs are deliberately absent from this table —
  `group_members.role IN ('owner','admin','member')`, whose twin lives in the membership
  cluster, and `currencies.minor_units BETWEEN 0 AND 4`, which is seed‑only with no user
  input path.)*
- **Split wire format:** clean tagged union — `{"type":"equal","among":[…]}` ·
  `{"type":"exact","amounts":[…]}` · `{"type":"shares","weights":[…]}` ·
  `{"type":"percentage","percents":[…]}` (integer basis points, §3.2). 🔧 The Swift
  `SplitType` enum's *default* `Codable` emits `{"exact":{"_0":…}}` — ship a
  **hand‑written `Codable`** covering **all four** cases (including `percentage`, which the
  client must *decode* even before it can author it) to match the contract.

### 3.5 Delta sync

`GET /sync?since=<cursor>&limit=n` — **one global cursor per user**, not per group. The server
filters the change stream by the caller's *current* memberships, so a device never receives
rows for groups it can't see.

> 🔧 fix: the pre‑reconciliation schema couldn't serve this at all — `settlements` had no
> `updated_at`, `categories` had neither `updated_at` nor `deleted_at`, nothing recorded
> "you lost access", and `updated_at` cursors aren't monotonic under concurrent commits.
> Closed by `change_log` (§1.3) + the rules below.

#### 3.5.1 What syncs

| Wire `type` | Source | Notes |
|---|---|---|
| `group` | `groups` | `data` includes the archived flag (archive semantics: §3.7) |
| `member` | `group_members` | admin **remove** ⇒ tombstone; **leave** ⇒ upsert (the row goes ghost, §3.8.3) — the client keeps the row for history rendering either way |
| `expense` | `expenses` + splits | `data` is exactly the §3.2 representation, server‑authoritative `shares` embedded — split rows are **never** separate entries |
| `settlement` | `settlements` | void (§3.6) ⇒ tombstone |
| `category` | `categories` | group‑scoped only; global presets (`group_id IS NULL`) are static seed data served by the category endpoints, not the stream |
| `access` | *synthetic* | grant/revoke of the **caller's** visibility of a whole group; `data` always `null` |

**Deliberately not synced:** `invites` (server‑side state — joining materializes as `member` +
`access` entries), `activity_log` (own cursor endpoint), `currencies` (static `GET /currencies`),
`devices`.

#### 3.5.2 change_log — the append rule

Every mutating handler appends its `change_log` rows **in the same transaction** as the
mutation, through one `ChangeLogWriter` in Infrastructure. **Never triggers** — the synthetic
`access` entries need actor/membership context a trigger doesn't have, and one code path keeps
the entity→wire mapping in one place.

| Mutation | Rows appended |
|---|---|
| group create | `group` upsert **+** `member` upsert (the creator's owner row, §3.1) **+** `access` grant for the creator — uniform with join/claim, so a second device treats a created group like any other group appearing |
| group rename / archive | `group` upsert |
| group unarchive (§3.7) | `group` upsert |
| ghost member add / member rename | `member` upsert |
| member role change (the admin‑only path, §3.8.2) | `member` upsert |
| sole‑owner auto‑promote (during the D7 anonymize, §3.8.2) | `member` upsert |
| member **remove** (admin, §3.8.3) | `member` delete (tombstone) **+** `access` revoke for the removed row's `user_id` (if linked) |
| member **leave** (§3.8.3) | `member` **upsert** (the row becomes a ghost, stays live — a leave must **never** emit a `member` tombstone) **+** `access` revoke addressed to the leaver |
| ghost claim (after explicit accept, §3.8.1) | `member` upsert **+** `access` grant for the claimer |
| invite accept (new member) | `member` upsert **+** `access` grant for the joiner |
| GDPR anonymize (D7, §4.4) | `member` upsert in every group where a slot was unlinked or a `former_user_id` row was scrubbed; optionally also per‑group `access` revokes addressed to the deleted user's own id (§4.4 step 5) |
| anonymize‑triggered group archive (§3.8.2 sole‑owner rule) | `group` upsert |
| expense create / edit / soft‑delete | `expense` upsert / upsert / delete |
| settlement record / void (§3.6) | `settlement` upsert / delete |
| category create / soft‑delete | `category` upsert / delete — no category *edit* in v1 (§3.9); a `category` edit‑upsert row is **reserved** for a future `PATCH` |

This table is **exhaustive**. A new mutating endpoint doesn't ship until its row is added
here — a mutation that appends nothing is a sync bug by definition (its effect reaches new
devices never, and existing devices only via some later unrelated write to the same entity).

#### 3.5.3 Cursor & the monotonicity trap

The cursor is an **opaque** string encoding the last applied `change_log.seq`. Clients never
parse it: `nextCursor` in, `since` out.

🔧 fix (the trap): `bigserial` values are assigned at INSERT but become **visible at COMMIT**.
Unguarded, a page could serve `seq` 110 while an in‑flight transaction later commits `seq` 105 —
the cursor jumps to 110 and row 105 is skipped *forever*. Rule: `ChangeLogWriter` takes
`pg_advisory_xact_lock(CHANGE_LOG_LOCK_KEY)` (a named bigint constant — authoritative value in
Appendix A) immediately before its
INSERTs. The lock is held to commit end, so **seq order == commit‑visibility order** and plain
`WHERE seq > @cursor ORDER BY seq` is gap‑free. Cost: mutations serialize on one lock for the
few ms of INSERT + fsync — fine at this scale; if a write‑hot future arrives, switch to an
`xid8`‑watermark cursor (documented alternative, deliberately not built now). An aborted
transaction burns a seq — a permanent hole the cursor simply passes over.

Read (Dapper, no tracking; fetch `limit + 1` to set `hasMore`):

```sql
-- Illustrative: the production query also reads change_log_watermark in this same
-- statement for the 410 check — one snapshot, §3.5.5.
SELECT seq, group_id, entity_type, entity_id, is_delete
FROM change_log
WHERE seq > @after
  AND ( (entity_type <> 'access'
         AND group_id IN (SELECT group_id FROM group_members
                          WHERE user_id = @me AND deleted_at IS NULL))
     OR (entity_type = 'access' AND entity_id = @me) )
ORDER BY seq
LIMIT @limit;
```

`access` rows are addressed to a single user (`entity_id` = `users.id`) and delivered
**regardless of membership** — after a removal the membership filter would hide the group
forever, so the revocation must ride outside it. Other members never receive your `access`
rows (the `entity_type <> 'access'` guard on the membership branch).

#### 3.5.4 Wire format

Own envelope — deliberately not the §3.4 `data/page` wrapper (the cursor is durable sync
state, not scroll state). `data` = the full current representation (money = string minor
units + `currency`, D1); tombstones carry `data: null`. Page size: default **100**, max
**500** (authoritative: Appendix A). An empty page echoes `since` as `nextCursor`.

```jsonc
// GET /sync?since=c9x4KzAAAaN0&limit=200
{
  "changes": [
    { "type": "expense", "id": "exp_9Qm4", "groupId": "grp_7Hk2", "deleted": false,
      "data": { /* exactly the §3.2 expense representation, shares embedded */ } },
    { "type": "settlement", "id": "set_2Xw1", "groupId": "grp_7Hk2", "deleted": true, "data": null },
    { "type": "access", "id": "grp_9Ppl", "groupId": "grp_9Ppl", "deleted": true, "data": null }
  ],
  "nextCursor": "c9x4KzAAAbQ2",
  "hasMore": false
}
```

`GET /sync` **without** `since` returns `{ "changes": [], "nextCursor": "<head>", "hasMore": false }`
— a cheap "mint me a cursor" call, load‑bearing for the bootstrap below. Head =
`max(seq)`, falling back to `pruned_through_seq` when `change_log` is empty (§3.5.5) —
never `0`, or the very next poll would `410`.

Client apply rules:
- Changes are **idempotent upserts** keyed by `id`: apply if incoming `version` ≥ local
  (categories carry `updatedAt` instead of `version` — last received wins). `deleted: true`
  always wins. The same `id` may appear several times in one page — apply in `seq` order.
- `deleted: true` = **soft‑delete locally, never purge**: removed members and deleted
  categories keep rendering inside historical expenses. The only hard purge is `access` revoke.
- `access` + `deleted: true` → purge everything for `groupId`.
  `access` + `deleted: false` → run the per‑group bootstrap (§3.5.5) after finishing the page.

#### 3.5.5 Expired cursor → 410 Gone → re‑bootstrap

`change_log` is pruned past a retention window (90 days — authoritative: Appendix A). A
`since` that predates the pruned range can't be served without silent data loss, so:

```jsonc
// 410 Gone · application/problem+json
{ "type": "https://api.paybitch.app/problems/sync_cursor_expired",
  "title": "Sync cursor expired", "status": 410, "code": "sync_cursor_expired" }
```

**Prune & detection mechanics.** 🔧 fix: "older than the oldest retained row" is undefined
exactly when it matters — after a quiet retention window the table is *empty*, `min(seq)`
is NULL, and the naive check hands an expired cursor an ordinary empty page; the device
believes it's current and every tombstone it missed is lost forever. Hence a persisted
watermark (`change_log_watermark`, §1.3):

- **Prune cuts by seq, never by `created_at`**: `created_at` defaults to `now()` =
  transaction‑*start* time, while seq order is commit order (the §3.5.3 lock) — the two can
  invert, so a timestamp `DELETE` could remove a higher seq while retaining a lower one,
  breaking the contiguity `WHERE seq > @cursor` relies on. The job computes
  `watermark = min(seq) among rows younger than the retention cutoff, − 1` (no such rows ⇒
  `max(seq)`; table already empty ⇒ keep the stored value), runs
  `DELETE FROM change_log WHERE seq <= @watermark`, and updates
  `change_log_watermark.pruned_through_seq` **in the same transaction**.
- **`/sync` serves 410 iff decoded `since` < `pruned_through_seq`.** No `min(seq)` probe —
  the rule keeps firing when the table is fully pruned. `since = pruned_through_seq` is
  still valid: everything above the watermark is retained.
- **One snapshot**: the watermark check and the §3.5.3 page SELECT execute as a single
  statement (join `change_log_watermark` into the page query), so a prune committing
  mid‑request can't slip rows past an in‑flight page — either the whole page predates the
  prune or the request sees the new watermark and `410`s.

Re‑bootstrap procedure (client):
1. `GET /sync` with no `since` → capture the head cursor **before touching any data**.
   Anything committed during the bootstrap is ≥ this cursor and replays afterwards —
   idempotent upserts make the overlap harmless.
2. `GET /groups`, then per group the paginated REST reads (members **including soft‑deleted**
   for history rendering, expenses, settlements, categories — §3.1).
3. Replace the local store with the fetched state; resume delta polling from the captured cursor.

**Join/claim bootstrap is the same path scoped to one group.** A member who just accepted an
invite or claimed a ghost (or their second device, seeing the `access` grant) fetches that
group via the REST reads, then continues on its existing global cursor. History from before
the join never replays through `/sync` — the per‑group fetch is the only way to get it.

### 3.6 Void a settlement

`DELETE /groups/{g}/settlements/{s}` + `If-Match: "<version>"` → `204`.

- **Void = soft delete** (`deleted_at`, `version + 1`, `updated_at`), guarded by
  `If-Match`/`412` (D8). **No compensating entry** — balances are computed on demand (D4), so
  a voided settlement simply drops out of `Σ settlementEffect`; the zero‑sum invariant holds
  by construction.
- **Who may void:** the settlement's `created_by`, either counterparty's linked user
  (`from_member`/`to_member` whose `user_id` = caller), or a group admin/owner. Membership
  check first as always (non‑member → `404`, D6); a member without void rights → `403`
  problem+json, `code: "settlement_void_forbidden"`.
- A stale retry of a void → `412` with the current representation (`deleted: true`) — the
  client treats that as success. No un‑void in v1: record a new settlement instead.
- Appends a `settlement` delete entry to `change_log` in the same transaction (§3.5.2).

🔧 fix: `settlements` had no `updated_at`, so a void could neither be timestamped nor flow
through `/sync`. Column added in §1.3.

### 3.7 Group archive semantics

🔧 `DELETE /groups/{g}` said "archive" but archive was never defined, and `groups.deleted_at`
doubled as the flag. Defined here; `archived_at` (§1.3) is the flag, `deleted_at` stays a tombstone.

- **Archive = reversible read‑only.** `DELETE /groups/{g}` sets `archived_at = now()` and bumps
  `version`. **Owner/admin only** — membership check first (non‑members get `404`, D6); a plain
  `member` gets `403` `insufficient_role`. Allowed at **any** balance: unsettled debts stay
  visible; nothing is destroyed. Archive (`DELETE`) is **idempotent and exempt from
  `If-Match`** — archiving an archived group is a no‑op `204` — while **unarchive requires
  `If-Match`** (it is an ordinary `PATCH /groups/{g}` write, below).
- **Reads keep working.** `GET /groups/{g}` and everything under it (expenses, settlements,
  balances, activity) respond normally; the group carries `"archived": true` there and in
  `GET /groups`. The change flows through `/sync` as an ordinary group update with the flag
  (delta shape owned by the sync cluster — §3.5).
- **All mutations → `409`, code `group_archived`.** Expense create/edit/delete, settlement
  record/void, member add/edit/remove, invite create/accept, ghost claim — every write under an
  archived group is rejected. Enforced as an endpoint filter right after the D6 membership
  check — one code path, no per‑handler drift. Exception to the *filter* (not the rule): the
  invite‑accept and ghost‑claim routes (`POST /invites/{token}/accept`, §3.8.1) are
  **token‑authorized, not membership‑authorized**, so no D6 filter runs there — those handlers
  check the group's archived status **inside the handler** and reject with the same
  `409 group_archived`.

```jsonc
// POST /groups/grp_7Hk2/expenses on an archived group → 409 application/problem+json
{
  "type": "https://api.paybitch.app/problems/group_archived",
  "title": "Group is archived",
  "status": 409,
  "code": "group_archived",
  "detail": "Unarchive the group to make changes."
}
```

- **Unarchive.** `PATCH /groups/{g}` with body `{"archived": false}` + `If-Match` (owner/admin)
  clears `archived_at` and bumps `version`. This PATCH is the **one** write an archived group
  accepts; a no‑op if the group isn't archived.
- **True deletion (v1): none.** Archived‑forever is acceptable for v1; a hard purge path comes
  later with retention policies (`groups.deleted_at` is reserved for it).

### 3.8 Membership lifecycle

#### 3.8.1 Invite accept & ghost claim — one flow, one authorization

Creating an invite: `POST /groups/{g}/invites` with optional `{"memberId":"mem_dave"}` → a
**ghost‑claim invite** (`invites.member_id` set). Target must be an active, unclaimed ghost,
else `422 not_a_ghost`. Without `memberId` it's a **generic** invite (join as a fresh member).

**Preview** — `GET /invites/{token}` (unauthenticated; IP rate‑limited per §4.3). D6's no‑leak
posture applied to strangers: a token holder learns exactly what informed consent requires —
the ledger *they* would inherit — and nothing about anyone else.

| Invite kind | Preview contains | Never contains |
|---|---|---|
| generic (`member_id NULL`) | group name, member count, default currency, inviter display name, expiry | member list, any balances |
| ghost (`member_id` set) | the above **+** the ghost's display name and the ghost's **own** per‑currency nets | other members' names or nets |

```jsonc
// GET /invites/{token}        (ghost invite)
{
  "group": { "name": "Chata Krkonoše", "memberCount": 5, "defaultCurrency": "CZK" },
  "invitedBy": "Alice",
  "expiresAt": "2026-07-13T10:00:00Z",
  "claim": {                                   // null for a generic invite
    "memberId": "mem_dave",
    "displayName": "Dave",
    "netByCurrency": [
      { "currency": "CZK", "net": "-43050" },  // string minor units (D1)
      { "currency": "EUR", "net": "1200" }
    ]
  }
}
```

**Accept** — `POST /invites/{token}/accept` (authenticated). Body is empty for generic invites;
for ghost invites **both** fields are required:

```jsonc
{ "claimMemberId": "mem_dave", "acceptInheritedLedger": true }
// 200 → { "groupId": "grp_7Hk2", "memberId": "mem_dave", "role": "member" }
```

`claimMemberId` must echo the invite's `member_id` — proof the client actually rendered the
preview, not a blind accept.

| Case | Result |
|---|---|
| generic; caller has no member row | new `group_members` row (role `member`) → `200` |
| generic; caller has an active member row | `409 already_member` |
| generic; caller has a **soft‑deleted** row | row reactivated (`deleted_at := NULL`, version++) — 🔧 respects `UNIQUE(group_id, user_id)`; their history reattaches for free |
| ghost; `claimMemberId` ≠ invite's `member_id` | `422 claim_member_mismatch` |
| ghost; `acceptInheritedLedger` missing/false | `422 ledger_acceptance_required` |
| ghost; caller already has **any** member row in the group | `409 already_member` — one human = one member row per group (D6/§1.3); merging two rows is not a v1 feature |
| ghost; happy path | link: `UPDATE group_members SET user_id=@me, role='member', former_user_id=NULL, version=version+1 WHERE id=@m AND user_id IS NULL AND deleted_at IS NULL` → `200` |

The happy‑path `SET role='member', former_user_id=NULL` is deliberate, not decorative:
a claimer **never** inherits a role — the §1.3 `CHECK (user_id IS NOT NULL OR role='member')`
already guarantees a ghost row *holds* `member`, but the UPDATE states it rather than relying
on it (§3.8.2) — and clearing `former_user_id` hands the row's rendered name to its new human,
so the previous occupant's D7 erase no longer touches it (§3.8.3, erase hook).

Everything runs in **one transaction**, invite consumed first:

```sql
UPDATE invites SET accepted_at = now()
WHERE id = @invite AND accepted_at IS NULL;   -- 0 rows ⇒ lost the race
```

| Race | Loser gets | Why this status |
|---|---|---|
| same token accepted twice by the **same user** (mobile retry) | the original `200` replayed | idempotent retry — same posture as §4.1's refresh grace window |
| same token accepted twice by **different users** | `410 invite_consumed` | the token itself is spent: the resource is *gone*, not merely conflicting |
| two **different** invites target the same ghost | `409 ghost_already_claimed` | loser's token is still valid; the *request* conflicts with current group state (the linking `UPDATE` matched 0 rows) |

Token validity: unknown or revoked → `404` (as if it never existed — no probe oracle);
expired → `410 invite_expired`. `UNIQUE(group_id, user_id)` is the DB backstop for every
`already_member` race the boundary check misses.

#### 3.8.2 Roles & owner transfer

`PUT /groups/{g}/members/{m}/role`, body `{"role":"owner"|"admin"|"member"}`. Standard
`ETag`/`If-Match` (§3.4); D6 membership check first, as always.

| Rule | Enforcement |
|---|---|
| caller must be admin or owner | else `403 insufficient_role` (caller *is* a member — existence is already known to them, so 403 is not a leak) |
| granting **or revoking** `owner`/`admin` requires an **owner** caller | `403 insufficient_role`. In the 3‑role model that is every change today; the admin floor keeps the route's authz stable if lesser roles are ever added |
| change would leave zero owners | `409 last_owner` — backstopped by the ≥1‑owner constraint trigger (§1.5), not just app code |
| target is a ghost and role ≠ `member` | `422 ghost_cannot_hold_role` — an unlinked ghost can't authenticate, and a future claimer must never silently inherit admin. Structural backstop: the §1.3 `CHECK (user_id IS NOT NULL OR role='member')` makes a privileged ghost **unrepresentable**, whatever the code path |
| same role | `200` no‑op (idempotent) |

**Owner transfer** = two calls: promote the successor to `owner` (two owners momentarily —
legal), then demote yourself. No bespoke transfer endpoint.

**Sole‑owner account deletion** (`DELETE /me`, D7): inside the anonymize transaction, for every
group where the deleting user is the only active owner, auto‑promote — in order — the
longest‑standing active **admin** (earliest `created_at`, tie‑break `id`), else the
longest‑standing active **linked** member. If the deleter is the only linked member left, the
group is **archived** instead. Promotion runs *before* `user_id` is nulled and logs
`member.role_changed` with `{"reason":"owner_auto_promoted"}`.

When the deleter is one of **several** owners, no promotion fires — but the unlink still
demotes: 🔧 *every* path that nulls `user_id` (leave §3.8.3, the D7 anonymize) sets
`role := 'member'` in the **same statement**, and the §1.3 CHECK rejects anything else. Without
that, a two‑owner deletion would strand a ghost row with `role='owner'` — claimable by anyone
holding a ghost invite (any member can create one), i.e. a silent owner handoff, and a fake
prop for the ≥1‑owner trigger while the last real owner leaves. The CHECK closes both.

*Coordination:* the anonymize transaction itself is specified in §4 (auth/GDPR cluster) and
archive semantics by the archive cluster — this rule pins the owner‑succession order **and**
the unlink demote (`role := 'member'` rides in the UPDATE that nulls `user_id`).

#### 3.8.3 Leave vs. remove — two operations, not one

| | **Leave** (self‑service) | **Remove** (admin) |
|---|---|---|
| Endpoint | `POST /groups/{g}/members/me/leave` | `DELETE /groups/{g}/members/{m}` |
| Who may act | the member themself | admin+ for a `member` target; **owner** for an `admin`/`owner` target, else `403 insufficient_role` — 🔧 remove is a strictly stronger revocation than the demote §3.8.2 reserves to owners, so it inherits the same rank rule (caller ≥ target). Self‑exit for admins/owners stays available via leave |
| Balance precondition | none — **any** balance | every currency bucket exactly `0`, else `409 balance_not_zero` |
| Effect on the row | `user_id := NULL` (reverts to **ghost**), `former_user_id := user_id` (erase hook, below), `role := 'member'` (CHECK‑forced, §1.3), version++ | `deleted_at := now()`, version++ — **never** a hard `DELETE` |
| Ledger | fully preserved; the ghost keeps netting in `/balances` | preserved; the row remains a valid historical reference |
| Way back | ghost‑claim invite (§3.8.1) | re‑invite → soft‑deleted row reactivated (§3.8.1) |
| Last owner | `409 last_owner` — transfer first (§3.8.2) | `409 last_owner` |

Why `POST …/members/me/leave` and not `DELETE …/members/me`: leaving is a **state transition**
(unlink → ghost), not a deletion — a `DELETE` verb would falsely mirror the admin remove's
soft‑delete semantics, and leave must **not** set `deleted_at`.

**Remove is serialized against ledger writes.** The zero‑balance precondition is
money‑relevant, so it gets the §1.5 treatment (the deferred‑trigger/`FOR UPDATE` family), not
hope:

- the remove transaction takes `SELECT … FOR UPDATE` on the target member row **first**, then
  computes the buckets;
- expense and settlement create/edit take `SELECT … FOR UPDATE` on **every** `group_members`
  row they reference (payer, each split member, `from`/`to`) — in canonical `id` order, so
  concurrent writes can't deadlock — *before* validating `member_deleted`.

🔧 The FK insert's implicit `FOR KEY SHARE` is **not** enough: remove's
`UPDATE … SET deleted_at` locks the row `FOR NO KEY UPDATE` (non‑key column), which does
**not** conflict with `FOR KEY SHARE` — under READ COMMITTED an expense insert and a remove
would *both* commit, stranding a nonzero net on a "removed" row. With the explicit locks one
side waits and re‑reads: remove first ⇒ the write revalidates → `422 member_deleted`; write
first ⇒ remove recomputes the buckets → `409 balance_not_zero`.

Hard rules:

- Member rows are **never hard‑deleted**. `expenses.paid_by` and `expense_splits` are
  `ON DELETE RESTRICT` (§1.3), so the DB refuses even a buggy attempt.
- A soft‑deleted member still renders in historical expenses and remains a valid split
  reference — and **editing such an expense stays legal** (history is editable; the member may
  stay payer or keep its existing split slot). What's forbidden is **newly adding** it: to a
  new expense or settlement, or into a split it wasn't already in (`422 member_deleted` at
  validation).
- `GET /groups/{g}/members` omits soft‑deleted rows by default; `?includeDeleted=true`
  returns them so clients can render history.
- `/balances` includes **every** member row whose net ≠ 0 in any currency — ghosts and
  soft‑deleted rows alike; a soft‑deleted row is omitted only while it nets exactly zero
  everywhere. 🔧 The zero‑balance precondition holds *at remove time*, but a later edit to a
  historical expense (previous bullet) can move a removed member's net off zero — an
  omit‑always rule would silently break §3.3's client‑checkable `Σ net == 0` per currency
  (the §6 alerting canary) and hide real, unsettleable debt. Include‑when‑nonzero makes the
  invariant unconditional, and doubles as the belt under the lock protocol above.

**Erase hook (D7).** Ruling, stated once and mirrored in §4.4: **voluntary leave retains
`display_name`** (and `image_url`) — the user chose to keep participating as history, and the
remaining members' legitimate interest in an intact ledger covers it as long as the account
lives. **GDPR erasure scrubs `display_name` to a localized neutral placeholder** — erasure is
the strongest right (D7 "scrub PII"), and it reaches **every** member row of the deleter: the
rows still linked at erase time (unlinked + scrubbed by §4.4 step 5) *and* the previously‑left
rows. Leave therefore stamps `former_user_id := user_id` before nulling (§1.3): a left row's
`user_id` is `NULL`, so without this back‑reference the D7 erase could never find it.
`DELETE /me` scrubs every row `WHERE former_user_id = @deleter` — `display_name` → a localized
neutral placeholder, `image_url` → `NULL`, `former_user_id` → `NULL` — in the same anonymize
transaction that handles the still‑linked rows (§4.4 step 6). A later claim clears
`former_user_id` (§3.8.1): the rendered name then belongs to the claimer, and the previous
occupant's erase must not touch it.
*Coordination:* the anonymize transaction lives in §4.4 (auth/GDPR cluster); this section pins
the hook it consumes.

### 3.9 Categories

The table + FK exist (§1.3); these are the missing endpoints and the preset seed.

**Global presets** — seeded at migration time with fixed UUIDs (`group_id IS NULL`). Names are
canonical English slugs; the client localizes presets **by id**, the server stores no
translations.

| name | `icon_symbol` (SF Symbol) |
|---|---|
| groceries | `cart.fill` |
| dining | `fork.knife` |
| transport | `car.fill` |
| housing | `house.fill` |
| utilities | `bolt.fill` |
| entertainment | `popcorn.fill` |
| travel | `airplane` |
| health | `cross.case.fill` |
| shopping | `bag.fill` |
| other | `ellipsis.circle.fill` |

| Endpoint | Rules |
|---|---|
| `GET /categories` | presets only; static, ETag‑cacheable |
| `GET /groups/{g}/categories` | group customs only — the client concatenates presets; D6 membership check first |
| `POST /groups/{g}/categories` | **any member** may create — picker friction kills expense entry. `409 category_exists` on case‑insensitive collision with a group custom (DB: `uq_categories_group`) **or** a preset name (boundary check — the partial indexes can't span both scopes) |
| `DELETE /groups/{g}/categories/{c}` | **admin** — it edits everyone's picker. Soft‑delete: expenses keep the FK and render historically; assigning it to new/edited expenses → `422 category_deleted`. Preset ids are outside group scope → `404` (D6 posture) |

No `PATCH` in v1 — delete + recreate.

```jsonc
// POST /groups/grp_7Hk2/categories
{ "name": "Ski pass", "iconSymbol": "figure.skiing.downhill" }
// 201 → { "id": "cat_9Ax2", "groupId": "grp_7Hk2", "name": "Ski pass",
//          "iconSymbol": "figure.skiing.downhill" }
```

*Coordination:* `updated_at`/`deleted_at` columns on `categories` and their tombstone emission
through `/sync` are specified by the sync cluster's DDL patch.

### 3.10 Activity feed

**Generation.** Handlers write `activity_log` rows **in the same transaction as the
mutation**. Never DB triggers — a trigger doesn't know the actor. One row per user‑visible
event; an idempotent create replay (D9) writes **no** second row.

**Verb taxonomy (closed set — stable machine codes):**

| Target | Verbs |
|---|---|
| expense | `expense.created` · `expense.updated` · `expense.deleted` |
| settlement | `settlement.recorded` · `settlement.voided` |
| member | `member.added` · `member.removed` · `member.left` · `member.claimed` · `member.role_changed` |
| group | `group.created` · `group.renamed` · `group.archived` · `group.unarchived` |
| invite | `invite.created` · `invite.accepted` · `invite.revoked` |

**Metadata = ids + non‑money field *names* only.** 🔧 Resolves the latent contradiction
between §4.3 ("amounts never in `activity_log.metadata`") and a feed that must *display*
amounts: the log stores *which* fields changed (`{"changed":["amount","split"]}`), never
their values. `GET /groups/{g}/activity` **hydrates display data (title, amount, actor and
member names) by joining the live target row at read time.** Amounts are never stored in the
log, the GDPR erase scrub stays trivial (nothing to scrub), and the UI still shows them.
If the target row is gone or anonymized, `target`/`actor` hydrate to `null` and the client
renders a generic line ("An expense was updated").

Cursor envelope per §3.4; ordered by `created_at DESC, id DESC` (uuidv7 is time‑ordered).
Non‑members get `404` (D6), like every group‑scoped read.

```jsonc
// GET /groups/grp_7Hk2/activity?limit=25
{
  "data": [{
    "id": "act_01HZX4",
    "verb": "expense.updated",
    "actor": { "userId": "usr_alice", "displayName": "Alice" },   // hydrated; null if anonymized
    "targetType": "expense",
    "targetId": "exp_9Qm4",
    "metadata": { "changed": ["amount", "split"] },               // field NAMES, never values
    "target": {                                                   // hydrated from the live row
      "title": "Dinner at Lokál",
      "amount": "84000", "currency": "CZK",                       // string minor units (D1)
      "deleted": false
    },
    "createdAt": "2026-06-28T14:03:11Z"
  }],
  "page": { "nextCursor": "b3BhcXVl", "hasMore": true, "limit": 25 }
}
```

### 3.11 API versioning & deprecation (v1 policy)

- **Additive‑only inside `/v1`.** New endpoints and new *optional* response fields may appear
  at any time; clients MUST ignore unknown fields (Codable's default already does). Nothing in
  `/v1` is ever renamed, removed, or retyped.
- **Breaking changes get `/v2`**, deployed side‑by‑side; `/v1` keeps serving until an announced
  sunset.
- **Minimum‑supported client.** Every request carries `X-Client-Build: <int>` (set by the iOS
  `APIClient`, §7). A build below the server's floor gets `426 force_update_required`
  (Appendix B) on **every** endpoint; the app renders a blocking force‑update screen. The floor
  is runtime config and moves only when an older client would corrupt money data.
- New problem codes (Appendix B) and new activity `verb`s (§3.10) are additive changes.

### 3.12 Declared v1 cuts (deliberate, with named exits)

Each item below was a dangling thread in the raw slices — half‑designed or unreachable. v1
pins the cheap, safe behavior; the full feature is a **named extension** in
`BACKEND_EXTENSIONS.md` (owned by the extensions pass — referenced here, not designed here).

- **Invites are link‑only.** 🔧 Fixes: email invites were creatable but no delivery was ever
  designed. `POST /groups/{g}/invites` returns the single‑use invite URL **once** (only its
  hash is stored — §1.3); the inviter delivers it via the iOS **share sheet**. `invites.email`
  is retained purely as an optional label/hint on the pending row — the server sends **no
  email** in v1 (no SMTP dependency, no template/bounce surface). Server‑sent invite email =
  extension **E5**.
- **Avatars are `icon_symbol` (SF Symbols) only.** 🔧 Fixes: avatar upload was unreachable —
  `users.avatar_url` / `group_members.image_url` existed with no endpoint able to write them.
  They stay as *reserved* columns: no v1 endpoint writes them, PATCH DTOs don't bind them, and
  unknown request fields are rejected (over‑posting guard, §4.3) — which doubles as an
  SSRF/content‑injection guard: no user‑supplied URLs enter the system. Upload + storage =
  extensions **E1/E7**.
- **Invite revoke = hard delete.** `DELETE /groups/{g}/invites/{i}` deletes the row — nothing
  references `invites`, and the create/accept trail already lives in `activity_log`. Revoking
  an already‑accepted invite → `409 invite_already_accepted`; the membership it produced is
  untouched (remove the member via `DELETE /groups/{g}/members/{m}` instead). Consequence for
  the wire contract: after revoke the token's hash resolves to **no row**, so a revoked token
  is *deliberately* indistinguishable from a never‑issued one — both get the uniform
  `404 not_found` body (D6; also defeats token probing). `410 invite_expired` is reserved for
  rows that still **exist** and are past `expires_at` (Appendix B).
- **No expense‑list filter params.** `GET /groups/{g}/expenses` accepts `cursor` + `limit`,
  nothing else. The offline‑first client filters/searches its local SwiftData mirror (§7);
  server‑side search/filtering is an extension decision, not a v1 promise.

---

## 4. Authentication, authorization & security

### 4.1 AuthN — Sign in with Apple (primary)

1. iOS gets an Apple identity token (with a client `nonce`).
2. `POST /auth/apple`: server validates the token against Apple's **JWKS** (signature),
   `iss`, `aud`, `exp`, **and the `nonce`** (reject if either side's nonce is absent —
   🔧 not just unequal). Parse `email_verified` as **string *or* bool** (Apple varies).
3. On success, issue a short‑lived **access JWT** (10–15 min, `jti`, **no group claims** —
   membership changes must take effect immediately) + an **opaque refresh token**
   (stored **hashed**, in a rotation `family`).
4. iOS stores tokens in the **Keychain**.

**`POST /auth/apple` — wire contract.** 🔧 The body was previously unspecified, and
`users.display_name NOT NULL` cannot be satisfied from the identity token alone — Apple
delivers `fullName` **exactly once, client-side**, at first consent; it is never inside the
JWT. The client must forward it on the first exchange or the name is gone for good.

```jsonc
// POST /auth/apple            (unauthenticated; rate-limited per §4.3)
{
  "identityToken": "eyJraWQiOiJXNldjT0tCIiw…",   // Apple JWS — validated per step 2 above
  "authorizationCode": "c1a9f2b47ed…",           // 🔧 required — redeemed server-side; see code exchange below
  "nonce": "1f2d4a9c0b…",                        // raw nonce; server SHA-256s it, compares to the token's `nonce` claim
  "fullName": {                                  // nullable — present only on Apple's first-consent callback
    "givenName": "Alice",
    "familyName": "Nováková"
  }
}
```
```jsonc
// 200 OK
{
  "accessToken": "eyJhbGciOiJFUzI1NiIs…",        // JWT, 10–15 min, carries the `epoch` claim (see token_epoch enforcement below)
  "expiresIn": 900,
  "refreshToken": "b64u_9f2cQx…",                // opaque, stored hashed, rotation family
  "user": {
    "id": "usr_alice", "displayName": "Alice Nováková",
    "email": "k3xw9@privaterelay.appleid.com",   // nullable (private relay / hidden email)
    "defaultCurrency": "CZK", "locale": "cs",
    "isNewUser": true
  }
}
```

**Authorization-code exchange (revocation material).** 🔧 Account deletion must call
Apple's `POST https://appleid.apple.com/auth/revoke` (§4.4 step 9; App Review 5.1.1(v)),
and that endpoint accepts **only an Apple-issued refresh or access token** — obtainable
solely by redeeming the sign-in `authorizationCode` at
`https://appleid.apple.com/auth/token`. The identity token is useless there. So the body
**requires** `authorizationCode` (iOS always has it:
`ASAuthorizationAppleIDCredential.authorizationCode`), and on **every** exchange the
server redeems it inline (`grant_type=authorization_code`, client secret = the **ES256
client-secret JWT** signed with the Sign in with Apple key) and **upserts** the returned
Apple `refresh_token` — encrypted at rest — into `auth_identities.apple_refresh_token_enc`
(schema: PATCH 5). The code is single-use with a ~5-minute TTL, so there is no
retry-later: a failed redemption is logged but does **not** fail the login (the *identity
token*, already JWKS-validated, is the authentication proof) — any previously stored
refresh token simply stays current.

`display_name` resolution — applied **only when `(provider, subject)` is new** (the first
exchange provisions the user). On every later login `fullName` is **ignored**, so a stale or
malicious client can never rename an existing account:

| Precedence | Source |
|---|---|
| 1 | `fullName.givenName + " " + familyName` (trimmed; skipped if blank) |
| 2 | email local-part (`alice` from `alice@…`) |
| 3 | localized default (`cs` → „Nový uživatel", else "New user") |

`display_name` stays user-editable afterwards via `PATCH /me`.

Failure modes: bad signature / `iss` / `aud` / `exp` / nonce mismatch → `401` problem+json
`code: "apple_token_invalid"`; nonce **absent** on either side → same `401` (🔧 absent ≠ pass);
`authorizationCode` missing → `400` validation error (code redemption failure alone is
non-fatal, per above).

**v1 ships Sign in with Apple only.** `provider = 'email'` in `auth_identities` is a
**reserved** enum value for a future magic-link extension and is **rejected everywhere in
v1** — no endpoint accepts it, no code path writes it.

**Refresh rotation with a grace window** (🔧 fixes the mobile‑retry self‑DoS): revoke is an
**atomic conditional** `UPDATE … WHERE id=@id AND revoked_at IS NULL` (0 rows ⇒ lost the
race); a *just‑rotated* token presented within N seconds returns the already‑issued successor
instead of nuking the family; only an older revoked token (or a re‑rotated successor) is
treated as theft.

**`token_epoch` enforcement.** 🔧 The column existed with no enforcement path — a bumped
epoch invalidated nothing. Closed as follows:

- Access JWTs carry an **`epoch` claim**, stamped from a **live DB read** of
  `users.token_epoch` at issuance (never from cache). The epoch is monotonic, so a claim
  **greater than** the authoritative value is impossible — that asymmetry drives the check.
- Every authenticated request — after signature/expiry validation — compares the claim
  against `users.token_epoch`, read through a **per-instance in-memory cache, TTL ≤ 60 s**
  (authoritative value: Appendix A), **direction-aware** (🔧 a naive any-mismatch reject would loop a freshly minted
  epoch-N+1 token against a cache still holding N — refresh mints another N+1, fails
  identically, and the client spins until the TTL expires: the same mobile-retry self-DoS
  shape the refresh-rotation grace window above exists to prevent):
  - `claim < epoch` → `401` problem+json `code: "token_epoch_stale"`; the client refreshes
    (the refresh grant re-reads the live epoch — or fails too, if the family was revoked).
  - `claim > cachedEpoch` → the **cache** is stale, not the token. Re-read
    `users.token_epoch` from the DB, update the cache entry, then compare against the
    fresh value.
- Any instance that **bumps** the epoch or **issues** a token writes the value it just
  read/wrote into its own cache entry in the same operation — a single-instance deploy
  therefore converges immediately, not after the TTL.
- **Invalidation bound (documented, not hand-waved):** a bump takes effect **within the
  cache TTL** (≤ 60 s) on any *other* instance that has the user cached; the access token's
  own TTL (10–15 min) is the **hard ceiling**. The per-instance cache is fine
  single-instance; move it to a shared store together with the rate limiter (§4.3), not before.

- Bumped by: GDPR erase (§4.4 step 3), `POST /auth/logout` with `allSessions: true`
  (revoking refresh families alone never kills outstanding access tokens), refresh-token
  reuse (theft) detection, and manual compromise response.

**Access‑token signing keys:** **ES256**, with `kid` in the JWT header on every token. Keys
are PEM via secret env (`JWT_SIGNING_KEY_PEM` = active; `JWT_PREVIOUS_KEY_PEM` = optional,
verify‑only), never in source (§6). **Rotation:** publish the new `kid`, sign with the new
key, keep **accepting both** kids for one access‑token TTL (15 min), then drop the old key.
No JWKS endpoint in v1 — the API is the only verifier. **Compromise response:** drop the old
`kid` immediately (skip the grace window); every outstanding access token dies and clients
transparently refresh.

### 4.2 AuthZ — multi‑tenant by group (the single path, D6)

```csharp
// FIRST statement of every group-scoped endpoint. Indexed (group_id, user_id) lookup.
var membership = await groupAccess.GetMembershipAsync(groupId, currentUserId, ct);
if (membership is null || membership.DeletedAt is not null)
    return Results.NotFound();                  // 404, never 403/409 — no existence leak
// admin-only actions additionally check membership.Role is 'owner' or 'admin'.
```

- **No leaking existence** — non‑members get `404`, and exception messages never embed ids.
- **Role escalation blocked** — self may edit name/avatar only; `role` changes require admin,
  only an owner grants owner/admin, and the last owner can't be demoted (DB‑guaranteed ≥1 owner).
- **Ghost‑claim consent** — claiming happens only through `POST /invites/{token}/accept` (§3.8.1):
  the preview surfaces the ghost's inherited per‑currency nets and acceptance requires an explicit
  `acceptInheritedLedger: true` plus a `claimMemberId` echo (you can't silently inherit fabricated
  debt). A claimer also never inherits a *role* — the claim sets `role='member'` and the §1.3
  CHECK makes a privileged ghost unrepresentable. 🔧 The **invite token — not group membership —
  is the authorization** for claiming: a claimer isn't a member yet, so any group‑scoped claim
  route would `404` under D6.

### 4.3 Hardening checklist

- **Rate limiting** (ASP.NET Core `RateLimiter`): per‑IP on `/auth/*` **and** per‑Apple‑`sub`/email
  provisioning caps; rate‑limit invite **create** and the unauthenticated invite **preview** by IP.
  🔧 Trusted‑proxy forwarded‑header config is **mandatory** (else the partition key is spoofable);
  the in‑memory limiter is single‑instance only — move to Redis/Postgres when you scale.
- HTTPS/HSTS; EF parameterizes (audit any raw Dapper SQL); over‑posting blocked via explicit
  request DTOs (no binding straight to entities).
- **Logging hygiene:** ids only at INFO; **amounts and who‑owes‑whom are financial PII** — DEBUG
  behind a flag, never in `activity_log.metadata` at INFO. Include `activity_log` in the erase scrub.
- **GDPR (EU dev):** data export + delete = **anonymize** (D7) — full procedure in §4.4;
  retention window on `activity_log`.
- **CORS: no policy is registered in v1 — deny‑all by default.** The only client is the
  native iOS app, which sends no `Origin`. Do **not** add `AllowAnyOrigin` later "to make a
  web tool work"; a browser client gets its own reviewed, origin‑pinned policy or nothing.

### 4.4 Account deletion & data export (GDPR)

**Why a procedure at all:** D7 makes erase a *soft* delete — `users.deleted_at` is set, the
row stays. 🔧 That means every `ON DELETE SET NULL / CASCADE` clause hanging off `users` is
**dead code for GDPR purposes** — they fire only on a hard `DELETE`, which never happens.
The anonymize transaction below performs those effects explicitly.

**`DELETE /me` → the anonymize transaction.** Steps 1–8 run in **one DB transaction**, in
this order; step 9 is a post-commit outbox job — network I/O never inside a transaction,
but its outbox row (including the revocation payload) **is** written inside it, in step 7.

1. `UPDATE users SET deleted_at = now()` — soft delete; every group-scoped authz check
   (§4.2) now treats the account as gone.
2. Capture `@preScrubEmail := email` (step 4 needs it after this scrub), then scrub PII on
   the same row: `display_name = 'Deleted user'`, `email = NULL`, `avatar_url = NULL`,
   `locale = 'cs'` (reset to default — the locale is the user's data).
3. `token_epoch = token_epoch + 1` — every outstanding access token dies within the §4.1
   bound.
4. 🔧 Scrub the user's email copies in **invites** (they survived the previous draft —
   invites addressed to the user, including accepted ones pointing at their claimed member
   row): `UPDATE invites SET email = NULL WHERE email = @preScrubEmail OR member_id IN
   (SELECT id FROM group_members WHERE user_id = @uid)` — ordered **before** step 5 nulls
   `user_id`, or the member-id resolution comes up empty. (`@preScrubEmail` may be `NULL`
   for private-relay users; SQL `email = NULL` matches nothing, which is correct — the
   `member_id` arm still fires.)
5. *Sole-owner hook — ordered here, **before** the NULL-out, so promotion still sees the
   real row:* for each group where this user is the only active owner, apply the sole-owner
   rule (auto-promote / archive) — mechanics owned by §3.8.2 (membership cluster); the
   auto-promotion emits a `member` upsert and an anonymize-triggered archive emits a
   `group` upsert (§3.5.2). Then
   `UPDATE group_members SET user_id = NULL, role = 'member',
   display_name = <localized neutral placeholder>, image_url = NULL
   WHERE user_id = @uid` — each row becomes a **ghost** (§1.1); the demotion to `member`
   prevents a later ghost-claim from silently inheriting owner/admin (§3.8.2), and 🔧 the
   PII scrub rides the same statement: **erasure is the strongest right (D7 "scrub PII")**,
   so `display_name` is replaced with a localized neutral placeholder and `image_url` is
   nulled — a member photo is plausibly the data subject's face, and a name is PII too
   (`icon_symbol` stays: an SF Symbol name is not PII). Contrast, stated as the ruling in
   §3.8.3 as well: a voluntary *leave* retains `display_name` — the user chose to keep
   participating as history; erasure does not. Per unlinked row, emit a **`member` upsert**
   `change_log` entry (§3.5.2) — that is what tells other members' devices the row became
   a ghost with a scrubbed name; optionally **also** emit per-group `access` revokes
   addressed to the deleted user's **own** `users.id` — belt-and-braces for any of their
   devices still holding a live access token within the §4.1 epoch bound. 🔧 fix: the
   earlier draft emitted *only* access revocations — but `access` rows are addressed to a
   single user and invisible to everyone else (§3.5.3), so other members would never have
   learned the row went ghost; the `member` upsert is the emission that informs them.
6. **Erase hook for previously-left rows (§3.8.3):** scrub every member row
   `WHERE former_user_id = @uid` — `display_name` → the same localized neutral
   placeholder, `image_url` → `NULL`, `former_user_id` → `NULL` — and emit a `member`
   upsert per affected row (§3.5.2). Without this step, rows the user *left* before
   deleting the account (their `user_id` is already `NULL`) would keep rendering the real
   name forever — `former_user_id` is the erase's only route to them.
7. 🔧 Snapshot the revocation material **before destroying it**: copy
   `auth_identities.apple_refresh_token_enc` (still encrypted) into the step-9 **outbox
   row's payload** — step 9 runs post-commit, when the source row is gone. Then
   hard-`DELETE` all the user's `devices`, `refresh_tokens`, and `auth_identities` rows —
   APNs handles, token hashes, the Apple `sub` and the Apple refresh token are pure
   PII/credential material with zero ledger value.
8. `UPDATE activity_log SET actor_user = NULL WHERE actor_user = @uid` — the
   `ON DELETE SET NULL` that never fires; `metadata` is ids-only by design (§1.3), so
   nothing else to scrub there.
9. **Apple token revocation** (post-commit outbox job, retried with backoff):
   `POST https://appleid.apple.com/auth/revoke` with `token = <Apple refresh token from
   the outbox payload>`, `token_type_hint = refresh_token`, authenticated with the Apple
   **client-secret JWT** (ES256, signed with the Sign in with Apple key — the same
   credential the §4.1 authorization-code exchange uses). Required by App Review
   (5.1.1(v)) for any app that offers account deletion. If no refresh token was ever
   stored (every §4.1 code redemption failed — logged, rare), log and complete. On
   success — or terminal failure after bounded retries — **delete the outbox row**: its
   payload is itself credential material.

What survives, deliberately (D7): the member rows — as ghosts whose `display_name` is the
**localized neutral placeholder** (the real name does **not** survive erasure on the user's
own member rows; only a voluntary leave retains it — §3.8.3 ruling) — and every expense /
split / settlement, so the ledger's *numbers* stay legible to the *other* members (their
financial records; legitimate-interest basis). `Σ net = 0` still holds.

Response: `204 No Content`. The access token that authorized the call is itself dead on the
next request (step 3).

**`GET /me/export` — Art. 15/20 data export.** Synchronous JSON dump (no job queue at this
scale), `Content-Disposition: attachment`. Contains the profile row, all memberships, and
every expense / split / settlement the user **authored or participates in** (via any linked
member row). Money = string minor units + sibling `currency` (D1), same as everywhere.

```jsonc
// GET /me/export → 200, application/json (abridged)
{
  "exportedAt": "2026-07-06T09:12:44Z",
  "profile": { "id": "usr_alice", "displayName": "Alice Nováková",
               "email": "k3xw9@privaterelay.appleid.com", "defaultCurrency": "CZK",
               "locale": "cs", "createdAt": "2026-01-12T08:00:00Z" },
  "memberships": [
    { "groupId": "grp_7Hk2", "groupName": "Chata 2026", "memberId": "mem_alice",
      "role": "owner", "joinedAt": "2026-01-12T08:01:30Z" } ],
  "expenses": [
    { "groupId": "grp_7Hk2", "id": "exp_9Qm4", "title": "Dinner at Lokál",
      "amount": "84000", "currency": "CZK", "paidBy": "mem_alice", "date": "2026-06-27",
      "myShare": "28000", "createdBy": "usr_alice" } ],
  "settlements": [
    { "groupId": "grp_7Hk2", "id": "set_2Xw8", "from": "mem_carol", "to": "mem_alice",
      "amount": "53000", "currency": "CZK", "settledOn": "2026-07-01" } ]
}
```

**Redaction posture (documented, defensible):** the export includes group-scoped data the
user can already see in-app — other members' in-group names and amounts on shared expenses
are *shared* records, not third-party leakage. Nothing the in-app UI wouldn't show is
exported. Rate-limit the endpoint alongside §4.3 (it is a full per-user scan by design).

### 4.5 Push notifications & device lifecycle

**Transport.** APNs **token‑based auth** (`.p8` signing key): `APNS_KEY_PEM`, `APNS_KEY_ID`,
`APNS_TEAM_ID`, `APNS_BUNDLE_ID` via secret env, never in source (§6). HTTP/2 to
`api.push.apple.com` (sandbox host in non‑prod).

**Trigger.** After **every committed group mutation** the handler enqueues
`(groupId, actorUserId)` on the in‑process `Channel<T>` behind `PushNotificationWorker`
(§5). Fan‑out targets the APNs tokens of **all ACTIVE members' devices (membership
`deleted_at IS NULL`) except the actor's** — the actor's device already has the change.
Mirrors the §4.2 membership rule: a removed member is a non‑member (D6), so the batch
query must never join soft‑deleted `group_members` rows — even a bare `groupId` ping
leaks group‑activity timing to an ex‑member.

**Coalescing.** Debounce **30 s per device** (authoritative: Appendix A) inside the worker.
Silent pushes are *sync triggers, not events* (§7): ten rapid edits collapse into one push,
and a dropped push costs nothing because the foreground poll is the correctness floor.

**Payload — bare trigger, never data** (matches §7's "never push the payload itself";
no amounts, titles, or names ever ride Apple's pipes):

```json
{ "aps": { "content-available": 1 }, "groupId": "grp_7Hk2" }
```

**Token hygiene.** APNs `410 Unregistered` / `BadDeviceToken` → **delete the `devices`
row**. Other 4xx → log and drop; 5xx/timeout → bounded retry with jitter.

**Device lifecycle** (`{id}` = **client‑generated stable device UUID**, minted once per
install and kept in the Keychain — `PUT` is an idempotent upsert keyed on it, with
same‑token absorption so a regenerated UUID can never strand a stale row):

| Event | Effect on `devices` |
|---|---|
| `PUT /me/devices/{id}` | Upsert resolving **both** conflict axes: first **delete any existing rows — any user's — holding the same `apns_token`** (a regenerated Keychain UUID with a stable token must absorb its stale row, not 500 on `UNIQUE (user_id, apns_token)`), then `ON CONFLICT (id) DO UPDATE` refreshing `apns_token`/`updated_at`, in one transaction. A different signed‑in user upserting the same `{id}` takes the row over — combined with the token absorb, exactly **one account receives pushes per physical device**. |
| `DELETE /me/devices/{id}` | Delete the row (push opt‑out) |
| 11th `PUT /me/devices/{id}` (device cap, Appendix A: ≤ 10 per user) | Evicts the **least‑recently‑updated** row (`updated_at`), then the upsert proceeds — registration never fails on the cap |
| `POST /auth/logout` with `deviceId` | Delete that row alongside the refresh revoke |
| `POST /auth/logout` revoke‑all | Delete **all** the caller's device rows |
| APNs `410` / `BadDeviceToken` | Delete the row (server‑side, see above) |
| Account deletion (GDPR) | Device purge is owned by the auth‑gdpr cluster (coordination note) |

```jsonc
// PUT /me/devices/6F9619FF-8B86-D011-B42D-00C04FC964FF
{ "apnsToken": "740f4707bebcf74f9b7c25d48e3358945f6aa01da5ddb387462c7eaf61bb78ad", "platform": "ios" }
// 200 OK — created or refreshed, idempotent
```

⚠️ **Multi‑instance caveat:** the in‑process `Channel<T>` only sees its own instance's
writes. Acceptable for the v1 single‑instance deploy; multi‑instance fan‑out
(`LISTEN/NOTIFY` or an outbox) is owned by the scaling extension doc (coordination note).

---

## 5. .NET solution architecture

**Vertical Slice, lightly layered. 4 projects.** Rejects full Clean/Onion project sprawl +
MediatR; keeps its one real win (an isolated, unit‑testable money/settlement core).

```text
Paybitch.sln
├── src/
│   ├── Paybitch.Api/                 # only deployable — Minimal APIs + feature slices
│   │   ├── Program.cs                # composition root, pipeline, DI
│   │   ├── Features/                 # Expenses/ Groups/ Settlements/ Auth/ Sync/
│   │   │   └── Expenses/CreateExpense/{Request,Validator,Handler,Response}.cs
│   │   ├── Common/                   # Errors(ProblemDetails) · Validation filter · CurrentUser · Auth policies
│   │   └── BackgroundWork/           # PushNotificationWorker (Channel<T> + BackgroundService)
│   ├── Paybitch.Domain/             # PURE C#, zero framework deps
│   │   ├── Money.cs · Splitting/ · Settlement/{BalanceCalculator,DebtSimplifier}.cs
│   ├── Paybitch.Infrastructure/     # EF Core + Npgsql + Dapper
│   │   ├── AppDbContext.cs · Configurations/ · Converters/MoneyConverter.cs (Money↔bigint) · Migrations/
└── tests/
    ├── Paybitch.Domain.Tests/        # fast, pure — money & settlement (most tests)
    └── Paybitch.Api.Tests/           # WebApplicationFactory + Testcontainers (real Postgres)
```

Dependency direction: `Api → Infrastructure → Domain`; `Domain` depends on nothing.

| Concern | Choice |
|---|---|
| Endpoints | **Minimal APIs** + route groups + endpoint filters |
| Writes | **EF Core 9** (the `Expense`+`ExpenseSplit` aggregate, transactions, migrations) |
| Shaped reads (balances/lists) | **Dapper** (exact SQL, no tracking) |
| Dispatch | plain DI handler classes (no MediatR) |
| Validation | **FluentValidation** as an endpoint filter (cross‑field + injected rules) |
| Mapping | manual `ToResponse()` (few entities, AOT‑friendly) |
| Errors | `IExceptionHandler` → RFC 9457 ProblemDetails |
| Request limits | Kestrel `MaxRequestBodySize = 256 KB` (JSON API, no uploads in v1; Appendix A) → `413 payload_too_large` |
| Logging | **Serilog** structured JSON |
| Docs | built‑in OpenAPI + Scalar UI |

**OpenAPI/Scalar exposure (🔧 scoped):** the Scalar UI and the OpenAPI JSON document are
mapped in **Development only** (`app.Environment.IsDevelopment()`); staging may enable them
behind basic auth; production **never maps them** — the API surface is not public documentation.

🔧 **Dropped from the raw design:** the `BalanceRecalcQueue` + materialized `member_balances`
table (D4 — on‑demand instead). The `MoneyConverter` is `Money ↔ bigint`, consistent with D1
(the `NUMERIC(19,4)` column idea is gone). Keep only the push‑notification worker.

---

## 6. DevOps & delivery

- **Hosting:** **Railway (Amsterdam / EU region)** now — push‑to‑deploy, managed Postgres +
  backups, EU residency, ~€10–25/mo, near‑zero ops. Everything is containerized, so the exit to
  a **Hetzner CX22 + Docker Compose** (~€5/mo, full EU data ownership) later is "point DNS,
  restore dump, `docker compose up`." Azure/AWS are a Series‑A target, not indie day‑one.
- **Containers:** multi‑stage `Dockerfile` (.NET 9, chiseled non‑root runtime) + `docker-compose.yml`
  for local dev (api + postgres + pgAdmin).
- **Migrations:** EF **bundle** run as a **discrete deploy step before** swapping app traffic —
  **never** migrate‑on‑startup with multiple instances (that race crash‑loops rollouts). Use
  expand/contract for zero‑downtime schema changes.
- **CI/CD (GitHub Actions):** every PR → build + test + migration‑check gate. On `main` →
  build → push image → migrate → deploy → smoke‑test, deploys non‑cancellable.
- **Observability:** Serilog JSON → Seq/Loki; split `/health` (liveness) + `/health/ready`
  (DB check); OpenTelemetry OTLP → Grafana Cloud / SigNoz; Sentry (EU). **Alert on the
  `Σ balances == 0` invariant above all** — it's the canary for a money bug.
- **DB ops:** managed backups + *tested* restores (pgBackRest/PITR on a VPS); Npgsql pooling
  until you need PgBouncer; secrets via env/manager, never in source.
- **Testing pyramid (weighted to settlement):** property‑test "total in == total out" and the
  zero‑sum invariant; integration‑test on real Postgres via **Testcontainers**; a few contract
  tests over the iOS sync seam.

---

## 7. iOS ↔ backend integration (offline‑first sync)

The main branch already has the seam: `protocol ExpenseRepository/GroupRepository/MemberRepository`
with `// Local impl now → swap with REST (.NET + Postgres) later`. The backend slots straight in.

- **Networking:** an `actor APIClient` (URLSession + async/await), typed `APIError`, Codable
  DTOs mirroring §3, a `TokenProvider` (Sign in with Apple + Keychain). Views stay behind the
  existing repository protocols — **zero view changes** to swap local → remote.
- **Local store:** **SwiftData** (`@Model` mirrors + sync metadata: `serverId`, `dirty`,
  `updatedAt`) for offline read **and** optimistic writes.
- **Sync engine:** client‑generated UUID `clientId` as the idempotency key; a local **outbox**
  (mutation queue) → push pending, then **delta pull** (`/sync?since=cursor` — contract in §3.5)
  with tombstones; on `410 Gone` run the §3.5.5 re‑bootstrap (capture a fresh head cursor
  **before** re‑fetching).
  🔧 **Conflict policy for money entities = reject‑and‑merge via `If-Match`/`412`, not silent
  LWW** (LWW would discard an edit that changes who owes what). Drain the outbox from a
  `BGAppRefreshTask` so offline writes sync even if the app isn't reopened on Wi‑Fi.
- **Realtime:** **silent APNs** (`content-available:1`) as a *trigger* for `SyncEngine.runOnce()`
  (never push the payload itself) + a foreground poll as the floor. WebSockets are YAGNI here.

**Required client change for wire compatibility:** hand‑written `Codable` for `SplitType`
(tagged union) covering **all four** cases — including `{"type":"percentage","percents":[…]}`,
because other members' clients or a newer app version can create percentage expenses even
while the iOS roadmap lists percentage *authoring* as v2, and one undecodable expense must
not poison the whole sync pull. Plus `amount` decoded as `String` →
`Decimal(string:)/10^minorUnits` (never `Double`), and upgrade `distributeEqually` to
largest‑remainder so client preview == server truth.

---

## 8. Build order (backend)

1. **Schema + `currencies` seed + `Paybitch.Domain`** (Money, splitting, BalanceCalculator,
   DebtSimplifier) with the full property‑test suite — *correctness before endpoints*.
2. **Auth slice** (Apple exchange, refresh rotation, the single membership authz path).
3. **Group + member CRUD + invites/ghost‑claim via accept (§3.8.1) + roles/leave/remove
   (§3.8.2–3) + categories (§3.9).**
4. **Expense CRUD** (create with embedded split + server‑authoritative `shares`).
5. **Settlements + `/balances`** (on‑demand compute).
6. **`/sync` delta + idempotency + ETag concurrency.**
7. **Push (APNs) + activity feed.**
8. **CI/CD, observability, Testcontainers integration tests, deploy to Railway.**

> Source: 10‑agent design pass + money‑correctness and security/architecture adversarial reviews.
> All cross‑slice contradictions (money type, balance storage, GDPR delete, concurrency, authz)
> are reconciled in §0. The raw per‑slice outputs and both full reviews are available on request.

---

## Appendix A — v1 operational constants

🔧 Fixes: the raw slices left **every** operational number unpinned (token TTLs given as a
range, invite expiry as "days", no caps anywhere). Every tunable number now lives **here and
only here**. Exact values are tunable; **their existence and this single location are the
contract.** Ships as one strongly‑typed options block (`IOptions<OperationalConstants>`) bound
from configuration — changing a value is a config edit + deploy, never a code hunt.

| Constant | v1 value | Notes |
|---|---|---|
| Access token TTL | **15 min** | pins §4.1's "10–15 min" |
| Refresh token — sliding lifetime | **60 days** | each successful rotation re‑arms it (§4.1) |
| Refresh token — absolute lifetime | **180 days** | family dies regardless of activity; re‑auth via Apple |
| Refresh rotation grace window | **30 s** | §4.1's "N seconds" — a just‑rotated token returns the already‑issued successor |
| Invite TTL | **7 days** | `invites.expires_at`; after → `410 invite_expired` |
| Rate limit — `/auth/*` | **10 req/min per IP** | §4.3 |
| Rate limit — provisioning (first `/auth/apple` per identity) | **5 / day per Apple `sub`** | §4.3 |
| Rate limit — invite create | **20 / day per user** | §4.3 |
| Rate limit — invite preview (`GET /invites/{token}`) | **30 req/min per IP** | unauthenticated endpoint (§4.3) |
| `activity_log` retention | **24 months** | the GDPR retention window named in §4.3; scheduled purge |
| `change_log` retention | **90 days** | = the sync‑cursor lifetime; an older cursor → `410 sync_cursor_expired` and a full resync (§3.5.5). *Coordination note: `change_log` itself is owned by the sync design (§3.5); this row only pins its retention.* |
| `/sync` page size | default **100**, max **500** | `?limit=n`, clamped to the max (§3.5.4) |
| `CHANGE_LOG_LOCK_KEY` | **`0x5041594249545348`** | named bigint constant (ASCII `PAYBITSH`) — the `pg_advisory_xact_lock` key that serializes `change_log` INSERT visibility (§3.5.3) |
| Push coalescing debounce | **30 s per device** | silent‑push fan‑out debounce inside `PushNotificationWorker` (§4.5) |
| `token_epoch` cache TTL | **60 s** | upper bound of the per‑instance in‑memory epoch cache (§4.1) |
| `activity_log.metadata` cap | **≤ 4 KB serialized** | app‑enforced at write time — server‑written, no `422` twin (§3.4, §3.10) |
| Devices per user | **≤ 10** | `PUT /me/devices/{id}`; an 11th registration evicts the least‑recently‑updated row (§4.5 lifecycle table) |
| Members per group | **≤ 50** | checked on any op that **creates** a `group_members` row: add ghost, accept of a non‑ghost invite → `409 limit_exceeded`. Ghost claim and accept of a ghost‑targeted invite link an *existing* row (§1.1) — never blocked by this cap |
| Groups per user | **≤ 200** | checked on any op that **links the caller into a new group**: create, accept invite (both flavors), ghost claim → `409 limit_exceeded` |
| `notes` length | **≤ 2000 chars** | expenses + settlements → `422 notes_too_long` (Appendix B) |
| Request body size | **≤ 256 KB** | Kestrel `MaxRequestBodySize` → `413 payload_too_large` |

---

## Appendix B — problem‑code catalog (v1, closed list)

🔧 Fixes: §3.4 mandates RFC 9457 `application/problem+json` with a stable machine `code`, but
the raw slices never enumerated the codes. This is the v1 catalog — the **complete closed
list**: every code any endpoint emits appears here. Codes are `snake_case`,
stable forever, and part of the wire contract; **adding** a code is additive (§3.11), renaming
or removing one is breaking.

Worked shape — every non‑2xx body looks like this:

```jsonc
// 422 — EXACT split that doesn't sum (D3)
{
  "type": "https://api.paybitch.app/problems/split_sum_mismatch",
  "title": "Split shares must sum to the expense amount",
  "status": 422,
  "code": "split_sum_mismatch",
  "errors": [
    { "field": "split.amounts", "message": "Shares sum to \"83900\", amount is \"84000\" CZK." }
  ]
}
```

| `code` | HTTP | Meaning | Owner |
|---|---|---|---|
| `unauthenticated` | 401 | Missing / expired / invalid access token | §4.1 |
| `token_epoch_stale` | 401 | Access token's `epoch` claim < `users.token_epoch` (logout‑all / erase / theft response); client refreshes | §4.1 |
| `apple_token_invalid` | 401 | Apple identity token failed signature / `iss` / `aud` / `exp` / nonce validation (nonce absent ⇒ same code) | §4.1 |
| `insufficient_role` | 403 | A **proven member** lacks the required role for the action (role change, admin remove, archive/unarchive, category delete). Never shown to non‑members — they get `404` (D6) | §3.8.2 (also §3.7, §3.8.3, §3.9) |
| `settlement_void_forbidden` | 403 | A member without void rights (not creator, counterparty, or admin+) attempts a settlement void | §3.6 |
| `not_found` | 404 | Resource absent **or caller isn't a member** — one uniform body either way, identical `title`/`detail` (D6, no existence leak). Also covers revoked and never‑issued invite tokens, indistinguishably (§3.12) | §3.4 / D6 |
| `balance_not_zero` | 409 | Member removal blocked while any currency bucket ≠ 0 | §3.8.3 |
| `group_archived` | 409 | Write attempted against an archived group (incl. invite accept / ghost claim, checked in‑handler) | §3.7 |
| `already_member` | 409 | Invite accept / ghost claim by someone already in the group | §3.8.1 |
| `ghost_already_claimed` | 409 | Ghost‑claim race lost: another invite already linked that ghost; the loser's token stays valid | §3.8.1 |
| `last_owner` | 409 | Demote/remove/leave would leave zero owners (§1.5 invariant) | §3.8.2 |
| `invite_already_accepted` | 409 | Revoking an invite that was already accepted (§3.12) | §3.12 |
| `category_exists` | 409 | Case‑insensitive name collision with a group custom or a preset | §3.9 |
| `client_id_conflict` | 409 | Replayed create whose canonical fields **diverge** from the existing `(groupId, clientId)` record; body carries `existingId`. Identical replay → `200` with the existing record | §3.4 (Idempotency) |
| `limit_exceeded` | 409 | An Appendix A cap was hit (members/group, groups/user) | Appendix A |
| `invite_expired` | 410 | Invite row still exists but is past its 7‑day TTL. A *revoked* invite is hard‑deleted (§3.12) and therefore returns `404 not_found`, not this code | §3.8.1 / §3.12 |
| `invite_consumed` | 410 | Invite token already spent by a **different** user (same‑user retry replays the original `200`) | §3.8.1 |
| `sync_cursor_expired` | 410 | `/sync` cursor older than `change_log` retention (Appendix A) → client performs the §3.5.5 re‑bootstrap | §3.5.5 |
| `version_conflict` | 412 | `If-Match` ≠ current `version`; body carries the current representation to merge against (D8) | §3.4 / §3.2b |
| `payload_too_large` | 413 | Body over the 256 KB cap (Appendix A) | §5 / Appendix A |
| `validation_failed` | 422 | Request shape/field invalid (generic); `errors[]` carries per‑field detail | §3.4 |
| `title_length` | 422 | `title` empty or > 140 chars | §3.4 (twins) |
| `name_length` | 422 | Group `name` empty or > 100 chars | §3.4 (twins) |
| `amount_not_positive` | 422 | `amount` not a positive integer string of minor units | §3.4 (twins) |
| `date_out_of_range` | 422 | `date` outside `[2000‑01‑01, local_today+1]` | §3.4 (twins) |
| `notes_too_long` | 422 | `notes` > 2000 chars on expenses / settlements (Appendix A) | §3.4 (twins) |
| `unsupported_currency` | 422 | Currency code outside the closed v1 set (D2, §2.1) | §2.1 / §3.4 (twins) |
| `invalid_split_type` | 422 | Split tagged union has an unknown `type` | §3.4 (twins) |
| `split_sum_mismatch` | 422 | EXACT amounts don't sum exactly to `amount` (D3) | §3.2 |
| `split_percent_sum_mismatch` | 422 | PERCENTAGE `basisPoints` don't sum to exactly 10 000 | §3.2 |
| `share_negative` | 422 | EXACT per‑entry violation: a share amount is negative | §3.4 (twins) |
| `invalid_weight` | 422 | SHARES per‑entry violation: a `weight` is not an integer ≥ 1 | §3.4 (twins) |
| `invalid_basis_points` | 422 | PERCENTAGE per‑entry violation: a `basisPoints` is not an integer ≥ 1 | §3.2 / §3.4 (twins) |
| `split_member_invalid` | 422 | Participant‑set violation, **all** split types: member not in group, duplicate `memberId`, or empty set | §3.2 |
| `self_settlement` | 422 | Settlement `from` = `to` | §3.4 (twins) |
| `immutable_field` | 422 | `id`, `groupId`, `clientId`, `createdBy`, `createdAt` on `PUT` (§3.2b); plus reserved image‑URL fields (§3.12) | §3.2b |
| `not_a_ghost` | 422 | Ghost‑claim invite creation targeting a member that isn't an active, unclaimed ghost | §3.8.1 |
| `claim_member_mismatch` | 422 | Accept body's `claimMemberId` ≠ the invite's `member_id` | §3.8.1 |
| `ledger_acceptance_required` | 422 | Ghost claim without explicit `acceptInheritedLedger: true` | §3.8.1 |
| `ghost_cannot_hold_role` | 422 | Role change targeting a ghost with role ≠ `member` (§1.3 CHECK backstop) | §3.8.2 |
| `member_deleted` | 422 | Soft‑deleted member **newly added** to an expense, settlement, or split slot it wasn't already in | §3.8.3 |
| `category_deleted` | 422 | Soft‑deleted category assigned to a new/edited expense | §3.9 |
| `force_update_required` | 426 | Client build below the supported floor (§3.11) — iOS renders the force‑update screen | §3.11 |
| `precondition_required` | 428 | `If-Match` absent on a money‑aggregate `PUT`/`DELETE` (D8) | §3.2b / §3.4 |
| `rate_limited` | 429 | An Appendix A rate limit tripped; `Retry-After` header set | §4.3 / Appendix A |

*Replay semantics for `client_id_conflict` are owned by §3.4's Idempotency bullet
(expense‑semantics cluster): identical canonical body → `200` + the existing record; **any**
divergence → `409 client_id_conflict` — the server never silently applies a divergent body.*
