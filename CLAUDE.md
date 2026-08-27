# Paybitch — Project Context

iOS Swift / SwiftUI expense splitter. Splitwise-style. Local-only now, prepared for `.NET + Postgres` backend later.

## Stack

- **Language:** Swift 6 (strict concurrency)
- **UI:** SwiftUI, iOS 18+ (uses `.glassEffect`, `.glassProminent` button style)
- **Concurrency:** `@MainActor` ModelData, `actor` repos, `Sendable` everywhere
- **Persistence:** JSON files in `Documents/`, seeded from bundle on first run
- **Tests:** Swift Testing (`import Testing`, `@Test`, `#expect`)
- **Build:** Xcode project, no Swift Package, no external deps

## Architecture

```
Paybitch/
├── Common/         # Cross-cutting helpers: PaybitchLogger, AppStorageKey, DateFormatters, paybitchBackground
├── Model/          # Pure value types: Expense (Decimal amount, Date date), Group, Member, Currency, SplitType
├── Services/       # Repository protocols + impls, BalanceCalculator, DebtSimplifier, FX (all Decimal)
│   └── Local/      # JSON-backed actor repos
├── Resources/      # Localizable.xcstrings + seed JSON (members/groups/expenses)
├── Views/
│   ├── AddExpense/ # AddExpenseSheet (create/edit), ExpenseDetailView, SplitEditor
│   ├── Groups/     # GroupEditorSheet
│   ├── Lists/      # DebtsList, DebtSummaryList, ActivityFeedView
│   ├── Members/    # Members tile, MemberList, MemberIcon
│   ├── Settings/   # SettingsSheet (profile picker, member CRUD, JSON export), AddMemberSheet
│   ├── Tiles/      # Top dashboard tiles (Tiles, IOwed, MyCosts, TotalCosts)
│   ├── TopBar/     # Group dropdown, hamburger menu, add-group row
│   ├── ContentView.swift
│   └── OnboardingView.swift
└── PaybitchApp.swift
```

`PaybitchTests/` mirrors `Paybitch/` for unit tests. `PaybitchUITests/` for UI tests.

## Key contracts

- **`ModelData`** (`@Observable`, `@MainActor`) is the single source of truth. Inject via `.environment(modelData)`. Holds `expenses`, `members`, `groups`, `currentUserId`, `lastError`. Wraps repo calls and surfaces errors via `report(_:_:)`.
- **Repository protocols** (`ExpenseRepository`, `MemberRepository`, `GroupRepository`) are `Sendable` actors. Swap `Local*` impl with REST when backend lands.
- **`FXProvider`** abstracts currency conversion. `StaticFXProvider` is hardcoded CZK/EUR/USD/GBP. Swap with ČNB/server feed later.
- **`BalanceCalculator`** + **`DebtSimplifier`** are pure `enum` namespaces with `static` fns — no state, fully testable.
- **No dedicated settle-up flow.** `DebtSummaryList` is read-only ("Who owes whom"). To clear a debt, the user records another expense in the opposite direction. Math handles it naturally — no special expense type or flag.

## Conventions

- `let` by default, `var` only when compiler requires
- `struct` value types; `actor` for shared mutable state; no `class` unless identity needed
- Immutable updates — never mutate, always create new
- **Money is `Decimal` everywhere** (Expense.amount, splits, balances, FX). Never `Double`. Use `Decimal.parseUserInput(_:)` for text input, `Decimal.rounded(scale:)` for currency rounding.
- Errors surfaced through `ModelData.lastError` → `.alert` in `ContentView`
- View files small (<300 lines); split when growing
- `MemberIcon` is the reusable avatar
- `Decimal.formatted(in:)` = compact (10k Kč), `formattedFull(in:)` = exact (in editors). Locale follows `Locale.current` automatically.
- Display name for current user is `"Me"` via `ModelData.displayName(for:)` — never hardcode
- Use `PaybitchLog.make("Category")` for OSLog, `PaybitchDate.{isoDay,mediumDisplay,relative}` for date formatters (cached, thread-safe), `AppStorageKey.*` for `@AppStorage` keys
- Apply brand background with `.paybitchBackground()` modifier

## Active invariants

- Current user ("Me") is auto-included in every group via `ensureCurrentUserInAllGroups()` (runs on `refresh()` and `setCurrentUser`)
- The "Me" toggle in `GroupEditorSheet` is disabled — can't be removed
- `AddExpenseSheet` derives the live group via `model.group(for: group.id)` to avoid stale snapshots

## Build & test

Default simulator: **iPhone 17** (booted in this dev env).

```bash
# Build only
xcodebuild -project Paybitch.xcodeproj -scheme Paybitch \
  -destination 'platform=iOS Simulator,name=iPhone 17' build \
  2>&1 | grep -E "(error:|warning:|BUILD )" | head -40

# Full test (unit + UI). Takes ~3 min — UI tests dominate.
xcodebuild -project Paybitch.xcodeproj -scheme Paybitch \
  -destination 'platform=iOS Simulator,name=iPhone 17' test 2>&1 | tail -60

# Unit tests only — much faster, prefer this during development
xcodebuild -project Paybitch.xcodeproj -scheme Paybitch \
  -destination 'platform=iOS Simulator,name=iPhone 17' test \
  -only-testing:PaybitchTests 2>&1 | tail -40

# List available simulators if iPhone 17 missing
xcrun simctl list devices available | grep iPhone
```

Watch for `BUILD SUCCEEDED` / `TEST SUCCEEDED`. Unit suites: `SplitTypeTests`, `BalanceCalculatorTests`, `DebtSimplifierTests` (+ stub `PaybitchTests`).

The pbxproj uses `fileSystemSynchronizedGroups` (Xcode 16+) — **new `.swift` files in `Paybitch/` or `PaybitchTests/` are auto-picked up; no project file edits needed.**

## Common pitfalls

- Don't add files to the project file — Xcode 16 auto-syncs from filesystem.
- Don't use `print()` — use `os.Logger` (`subsystem: "com.paybitch", category: "..."`).
- Don't commit `members.json` / `expenses.json` from `Documents/` — those are user data, not seeds.
- `Picker` selection must match a `.tag(...)` exactly or you get the "invalid selection" warning. When in doubt, fall back to a valid id in `setDefaults`.
- Sheets in SwiftUI capture state at presentation — for live data, look up by id from `ModelData`.
- The model type `Group` shadows SwiftUI's `Group` view. Inside `body`, use `SwiftUI.Group { ... }` when you need the view container.
- New localizable strings: just write `Text("English source")` and add an entry to `Localizable.xcstrings`. SwiftUI `Text(_:)` auto-localizes via `LocalizedStringKey`. Use `Text(verbatim:)` only for proper nouns or member-supplied data.
- Swift 6 strict concurrency complains about Foundation classes that are documented thread-safe (NSCache, DateFormatter once configured, RelativeDateTimeFormatter). Mark these `nonisolated(unsafe) static let` — already done in `Common/`.
- `Decimal` parses with `Decimal.parseUserInput("1,234.56")`. `String(decimal)` produces float-like junk — use the cached `NumberFormatter` paths or `formatAmountForEdit` in `AddExpenseSheet`.

## Done so far

- Models: Expense (+ settlement flag), Group, Member, Currency, SplitType
- Local repos with JSON persistence + bundle seeding
- BalanceCalculator (FX-aware) + greedy DebtSimplifier (≤N-1 edges)
- Group create/edit/delete with member roster, "Me" force-included
- Profile switching (current user) via SettingsSheet
- Add / edit / delete expense (single editor sheet)
- Expense detail view — tap row → read-only sheet with full split breakdown, "your impact", edit/delete
- "Who owes whom" read-only summary (`DebtSummaryList`) — settling = recording a new expense the other way
- Activity feed per group — chronological, grouped by day, relative time, **searchable** (title/notes/payer)
- **Decimal money** end-to-end — exact splits with remainder strategy, no cent loss
- **JSON export** via `ShareLink` in SettingsSheet
- Haptic success feedback on expense save
- 36 unit tests green (SplitType, BalanceCalculator, DebtSimplifier, ExpenseCodable, ModelData behaviour)
- **Onboarding flow** — `OnboardingView` shown on fresh install (no groups + `paybitch.hasOnboarded` unset). Two steps: name → first group.
- **Czech localization** via `Resources/Localizable.xcstrings`. `cs` registered in `knownRegions`. Source language `en`. New visible strings should be added to the catalog as `Text("English source")` — Xcode auto-extracts.
- **Launch screen** — auto-generated, background = `LaunchBackground` color asset (light/dark variants). App icon in `AppIcon.appiconset` (light/dark/tinted).
- Error alerts (`ModelData.lastError`)
- 22 unit tests green

## Roadmap (next)

P2 features:
- Categories on expenses
- Photos / receipts
- Recurring expenses
- CSV / PDF export
- Live FX rates (ČNB feed → cache → fallback to static)

P3 backend:
- `RemoteExpenseRepository` + `.NET` API + Postgres
- iCloud / CloudKit as zero-server stepping stone
- Auth, multi-device sync, push notifications

## Working with this repo

When asked to add a feature:
1. Read the relevant `Model/` + `Services/` first — pure logic lives there
2. New view files go in `Views/<area>/` and are auto-included
3. Run unit tests after touching `BalanceCalculator`, `DebtSimplifier`, `SplitType`, or anything money-related
4. Run full build after touching SwiftUI views
5. Surface errors through `ModelData.report(_:_:)`, never silent `try?` swallow
