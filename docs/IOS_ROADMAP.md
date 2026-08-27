# Paybitch — iOS Roadmap & Status

> Two branches exist. This worktree (`claude/keen-jemison-b5ef74`) is a **stripped‑down
> subset** — a group dropdown over seed data, 14 files, `amount: Double`. The main checkout
> (`claude/neutral-glass-redesign`) is **much further along**. This roadmap describes the
> **main‑branch product** (the real one) and where it goes next.

---

## 1. What's done (main branch)

| Area | State |
|---|---|
| **Domain model** | `Expense` (`amount: Decimal`, `currency: Currency`, `splitType: SplitType`, `date: Date`, `iconSymbol`, `createdAt`), `Group`, `Member` (+`imageUrl`/`iconSymbol`), tolerant decoders for legacy JSON. ✅ Already past the `Double`/`String` mistakes. |
| **Split logic** | `SplitType` = `equal · exact([id:Decimal]) · shares([id:Int])` with per‑member share computation. ⚠️ remainder dumped on the *last* member (see §3). |
| **Money / currency** | `Currency` enum (CZK=0 decimals, EUR/USD/GBP=2), locale‑aware `Decimal` formatting, compact `300k`/`1.2M` display. ✅ |
| **Services** | `BalanceCalculator`, `DebtSimplifier`, `FXRates` (CZK‑pivot static), `ExpenseRepository`/`GroupRepository`/`MemberRepository` **protocols + Local impls** — already designed to swap for REST. ✅ |
| **UI** | `AddExpenseSheet` + `SplitEditor` + `ExpenseDetailView`, balance tiles (`IOwed`/`MyCosts`/`TotalCosts`), `DebtsList`/`DebtSummaryList`/`DebtFlowCanvas`, `ActivityFeedView`, members management, `GroupEditorSheet`, onboarding, settings, Liquid Glass design system. ✅ a real app surface. |
| **Tests** | Swift Testing — seed load + lookup + group filter. ✅ build + tests **pass on iOS 26.5** (verified). |

**Not done:** real persistence beyond local/in‑memory, networking, auth (Sign in with Apple),
multi‑user/invites, cloud sync, push, settlements in the balance math (see §3), percentage
splits, search, receipts, recurring, export, localization to Czech, widgets.

---

## 2. Prioritization (MVP → v2 → v3)

> Effort: S ≈ 1d · M ≈ 2–4d · L ≈ 1w+. **BE?** = needs the .NET/PG backend.

### MVP — v1 (single device, no login, local persistence)
| Feature | Effort | BE? |
|---|:--:|:--:|
| Real local persistence (SwiftData) behind the existing repository protocols | M | No |
| Settlements in `BalanceCalculator` + record‑payment flow (🔧 §3) | M | No |
| Largest‑remainder split rounding (🔧 §3) | S | No |
| Empty + error states everywhere | S | No |
| Czech + English localization scaffolding | S | No |

### v2 — make it social (backend comes online)
| Feature | Effort | BE? |
|---|:--:|:--:|
| Sign in with Apple + onboarding/account UI | M | **Yes** |
| Cloud sync (swap repo Local→REST; offline outbox + delta pull) | L | **Yes** |
| Invites / real multi‑user groups + ghost‑claim | M | **Yes** |
| Activity feed (multi‑user) + push (APNs) | M | **Yes** |
| Percentage split mode | S | No |
| Multi‑currency display polish; accessibility/Dynamic Type pass | M | No |

### v2.5 — backend depth foundations (bridges v2 → v3)
Platform primitives (async job runner · object storage · transactional email), invite‑email
delivery + web landing page, CSV export + group stats. Specced in
[BACKEND_EXTENSIONS.md](BACKEND_EXTENSIONS.md) (E0, E4a, E5a, E9a).

### v3 — depth
Receipts/photos (blob storage, **Yes**), recurring expenses, live FX (**Yes**), CSV/PDF export,
home‑screen widget/Live Activity, settings polish, dark‑mode + glass polish.
Backend design for each is in [BACKEND_EXTENSIONS.md](BACKEND_EXTENSIONS.md): receipts **E1** ·
live FX **E2** · recurring **E3** · exports **E4** · notifications/push **E5** · email auth **E6** ·
avatars **E7** · comments **E10a**.

---

## 3. Correctness fixes the review found in the real services

These are **actual outstanding bugs** in the main branch, not hypothetical:

1. **`BalanceCalculator` omits settlements** and converts everything to one target currency
   up front — so recorded payments don't move balances, and per‑currency truth + FX rounding
   get folded into the canonical number. **Fix:** compute **per currency**, include settlement
   effects (`from +A`, `to −A`); FX is a separate display concern. (See backend §2.2.)
2. **`SplitType.distributeEqually` / `distributeByWeights` dump the remainder on the last
   member** — deterministic but unfair and order‑dependent. **Fix:** largest‑remainder
   (Hamilton) allocation, so client preview matches the server byte‑for‑byte. (Backend §2.3.)
3. **`SplitType.shares(total:among:roundedTo:)` defaults `scale: 2`** but CZK has 0 decimals —
   a CZK split can produce haléře that don't exist. **Fix:** derive scale from `currency.decimals`.
4. **`DebtSimplifier` epsilon `0.01` is currency‑blind** (wrong for CZK, whose minor unit is 1).
   **Fix:** work in integer minor units (matches the server), drop the float epsilon.
5. **Seed `expenses.json` is empty (`[]`)** — the populated tiles/lists render against nothing;
   add representative seed data (or import on first launch) so the UI is demoable.

---

## 4. The one architectural thing already right

`ExpenseRepository` & friends are **protocols with Local implementations**, explicitly
commented *"swap with REST (.NET + Postgres) later."* That means **v2 cloud sync is a new
repository implementation, not a rewrite** — the views never change. The **v1 backend** in
[BACKEND_DESIGN.md](BACKEND_DESIGN.md) is built to satisfy exactly these protocols; its
**extensions** (v2.5 → v4: receipts, live FX, recurring, exports, notifications, email auth,
scale/exit) are specced in [BACKEND_EXTENSIONS.md](BACKEND_EXTENSIONS.md).
