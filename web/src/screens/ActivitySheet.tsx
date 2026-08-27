/**
 * Searchable per-group expense history, ported from `ActivityFeedView.swift`.
 *
 * The iOS screen is a pushed page with a nav-bar search field; here it is
 * presented as a `Sheet` like every other secondary surface in this port, so
 * it shares the same open/close plumbing as the rest of the app.
 *
 * Row markup, sorting, and date formatting are deliberately NOT redone here —
 * `ExpenseRow`, `sortExpenses`, and `mediumDate` from `components/dashboard`
 * already implement them and stay the single source of truth.
 */

import { useEffect, useMemo, useState, type JSX } from "react";
import { ExpenseRow, mediumDate, sortExpenses } from "@/components/dashboard";
import { Sheet } from "@/components/Sheet";
import { totalCosts } from "@/domain/balances";
import {
  computeShares,
  decimalsFor,
  formatAmount,
  formatAmountFull,
  roundBankers,
} from "@/domain/money";
import { toCurrency, type Expense, type Group } from "@/domain/types";
import { Icon } from "@/icons";
import { displayName, useStore } from "@/store/useStore";

/** Below this a net impact counts as settled, matching the dashboard's own epsilon. */
const EPSILON = 0.01;
/** Keystroke → refilter delay, so a long history doesn't refilter on every key. */
const SEARCH_DEBOUNCE_MS = 120;
const ONE_DAY_MS = 24 * 60 * 60 * 1000;

export function ActivitySheet({
  open,
  group,
  onClose,
  onOpenExpense,
}: {
  open: boolean;
  group: Group;
  onClose: () => void;
  onOpenExpense: (expense: Expense) => void;
}): JSX.Element {
  const allExpenses = useStore((s) => s.expenses);
  const members = useStore((s) => s.members);
  const currentUserId = useStore((s) => s.currentUserId);

  // Lives here (not in ActivitySearchField) because filtering needs it too.
  // `Sheet` unmounts its children while closed, so ActivitySearchField's own
  // `query` state — and thus this, one short debounce tick later — already
  // comes back blank on every reopen without any reset effect of its own.
  const [debouncedQuery, setDebouncedQuery] = useState("");

  const groupExpenses = useMemo(
    () => allExpenses.filter((e) => e.groupId === group.id),
    [allExpenses, group.id],
  );

  const trimmedQuery = debouncedQuery.trim();
  const needle = normalize(trimmedQuery);

  // Search matches title, notes, AND the payer's name — the iOS source only
  // matches title/notes, but "what did Adam pay for" is the obvious thing to
  // want, so payer matching is added here. Folding accents (NFD, strip
  // combining marks) lets a query typed without diacritics still find a name
  // that has them, e.g. "adela" matches "Adéla".
  const filtered = useMemo(() => {
    if (!needle) return groupExpenses;
    return groupExpenses.filter((expense) => {
      const payer = displayName(members, currentUserId, expense.paidBy);
      return (
        normalize(expense.title).includes(needle) ||
        normalize(expense.notes ?? "").includes(needle) ||
        normalize(payer).includes(needle)
      );
    });
  }, [groupExpenses, members, currentUserId, needle]);

  const sections = useMemo(
    () => groupByDay(sortExpenses(filtered)),
    [filtered],
  );

  const currency = toCurrency(group.defaultCurrency);
  const total = useMemo(
    () => totalCosts(filtered, currency),
    [filtered, currency],
  );

  const isGroupEmpty = groupExpenses.length === 0;
  const title = group.name || "Activity";

  return (
    <Sheet open={open} onClose={onClose} title={title}>
      {/* Keyed by group so a (currently unreachable, but cheap to guard)
          group swap while open also starts the field over. */}
      <ActivitySearchField key={group.id} onDebouncedChange={setDebouncedQuery} />

      {filtered.length === 0 ? (
        // Divergence from the iOS source: it shows "No activity yet" for both
        // a genuinely empty group and a fruitless search. Here a search that
        // finds nothing says so and names the query instead of reusing that copy.
        <ActivityEmptyState query={isGroupEmpty ? null : trimmedQuery} />
      ) : (
        <>
          <div className="pb-activity-summary">
            {filtered.length} {filtered.length === 1 ? "expense" : "expenses"}
            <span className="pb-activity-summary__dot" aria-hidden="true">
              ·
            </span>
            {formatAmountFull(total, currency)}
          </div>

          <div className="pb-activity-list">
            {sections.map((section) => (
              <section className="pb-activity-section" key={section.key}>
                <div className="pb-activity-day">
                  <span className="pb-activity-day__label">{section.label}</span>
                  <span className="pb-activity-day__rule" aria-hidden="true" />
                </div>

                <div className="pb-activity-day__rows">
                  {section.expenses.map((expense) => (
                    <div className="pb-activity-row" key={expense.id}>
                      <ExpenseRow
                        expense={expense}
                        members={members}
                        currentUserId={currentUserId}
                        onOpen={() => onOpenExpense(expense)}
                      />
                      <NetImpactLine expense={expense} currentUserId={currentUserId} />
                    </div>
                  ))}
                </div>
              </section>
            ))}
          </div>
        </>
      )}
    </Sheet>
  );
}

/* ── search field ─────────────────────────────────────────────────────── */

/**
 * Owns the raw keystroke state and debounces it before reporting upward, so
 * the parent's filtering only reruns `SEARCH_DEBOUNCE_MS` after typing stops.
 */
function ActivitySearchField({
  onDebouncedChange,
}: {
  onDebouncedChange: (value: string) => void;
}) {
  const [query, setQuery] = useState("");

  useEffect(() => {
    const handle = window.setTimeout(
      () => onDebouncedChange(query),
      SEARCH_DEBOUNCE_MS,
    );
    return () => window.clearTimeout(handle);
  }, [query, onDebouncedChange]);

  return (
    <div className="pb-activity-search">
      <div className="pb-activity-search__field">
        <Icon name="magnifyingglass" size={16} strokeWidth={2.4} />
        <input
          className="pb-input pb-activity-search__input"
          type="search"
          inputMode="search"
          placeholder="Search title, notes, payer"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          aria-label="Search activity"
        />
      </div>
    </div>
  );
}

/* ── empty state ──────────────────────────────────────────────────────── */

function ActivityEmptyState({ query }: { query: string | null }) {
  const title = query ? "No matches" : "No activity yet";
  const body = query
    ? `Nothing matches "${query}". Try a different search.`
    : "Add an expense to see history here.";

  return (
    <div className="pb-activity-empty">
      <Icon name="clock" size={40} strokeWidth={1.75} />
      <h3 className="pb-activity-empty__title">{title}</h3>
      <p className="pb-activity-empty__body">{body}</p>
    </div>
  );
}

/* ── per-row net impact ───────────────────────────────────────────────── */

/** "you owe" / "you're owed" caption, mirroring `ActivityRow`'s `meNet`. */
function NetImpactLine({
  expense,
  currentUserId,
}: {
  expense: Expense;
  currentUserId: string | null;
}) {
  if (!currentUserId) return null;

  const scale = decimalsFor(expense.currency);
  const shares = computeShares(
    expense.splitType,
    expense.amount,
    expense.splitAmong,
    scale,
  );
  const myShare = shares[currentUserId] ?? 0;
  const paidByMe = expense.paidBy === currentUserId;
  const net = roundBankers((paidByMe ? expense.amount : 0) - myShare, scale);

  if (Math.abs(net) < EPSILON) return null;

  const owed = net > 0;
  return (
    <div
      className="pb-activity-net"
      style={{ color: owed ? "var(--pb-positive)" : "var(--pb-negative)" }}
    >
      {owed ? "you're owed " : "you owe "}
      {formatAmount(Math.abs(net), expense.currency)}
    </div>
  );
}

/* ── day sectioning ───────────────────────────────────────────────────── */

interface DaySection {
  key: number;
  label: string;
  expenses: Expense[];
}

function startOfLocalDay(date: Date): Date {
  return new Date(date.getFullYear(), date.getMonth(), date.getDate());
}

/** Buckets already-sorted expenses into day sections, newest day first. */
function groupByDay(expenses: Expense[]): DaySection[] {
  const todayKey = startOfLocalDay(new Date()).getTime();
  const buckets = new Map<number, Expense[]>();

  for (const expense of expenses) {
    const parsed = new Date(expense.date);
    const key = Number.isNaN(parsed.getTime())
      ? todayKey
      : startOfLocalDay(parsed).getTime();
    const bucket = buckets.get(key);
    if (bucket) bucket.push(expense);
    else buckets.set(key, [expense]);
  }

  return [...buckets.entries()]
    .sort(([a], [b]) => b - a)
    .map(([key, bucketExpenses]) => ({
      key,
      label: dayLabel(key, todayKey),
      expenses: bucketExpenses,
    }));
}

function dayLabel(key: number, todayKey: number): string {
  if (key === todayKey) return "Today";
  if (key === todayKey - ONE_DAY_MS) return "Yesterday";
  return mediumDate(new Date(key).toISOString());
}

/* ── search normalisation ─────────────────────────────────────────────── */

/** First and last code point of the "Combining Diacritical Marks" block. */
const COMBINING_MARK_START = 0x0300;
const COMBINING_MARK_END = 0x036f;

/**
 * Case- and accent-insensitive fold. NFD splits an accented letter into a
 * base letter plus a combining mark; stripping marks in the U+0300–U+036F
 * range lets a query typed without diacritics still match a name that has
 * them, e.g. Czech "Adéla" matches a search for "adela".
 */
function normalize(value: string): string {
  let result = "";
  for (const ch of value.normalize("NFD")) {
    const code = ch.codePointAt(0) ?? 0;
    if (code < COMBINING_MARK_START || code > COMBINING_MARK_END) result += ch;
  }
  return result.toLowerCase();
}
