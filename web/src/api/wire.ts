/**
 * Translation between the server's wire format and the app's domain model.
 *
 * These two disagree on essentially every axis, which is why this lives in one
 * place instead of being scattered through the client:
 *
 *   | axis          | server                        | app                      |
 *   |---------------|-------------------------------|--------------------------|
 *   | money         | minor-unit integer STRING     | major-unit number        |
 *   | split tag     | `type` field                  | the object key           |
 *   | split payload | array of {memberId, ...}      | dictionary by memberId   |
 *   | participants  | implied by the split entries  | separate `splitAmong`    |
 *
 * Money is the dangerous one. `"840"` in CZK is 840 Kč, not 8.40 — the exponent
 * is per-currency and CZK has none. Every conversion goes through the tables in
 * `domain/types.ts` rather than assuming two decimals.
 */

import { MINOR_UNITS, type CurrencyCode, type Expense, type SplitType } from "@/domain/types";
import { splitKind } from "@/domain/types";

/* ─────────────────────────────────────────────────────────────── money ── */

/**
 * Minor-unit string to major-unit number: `"840"` + CZK → `840`,
 * `"10000"` + EUR → `100`.
 */
export function minorToMajor(minor: string, currency: CurrencyCode): number {
  const exponent = MINOR_UNITS[currency];
  const units = Number(minor);
  if (!Number.isFinite(units)) return 0;
  return exponent === 0 ? units : units / 10 ** exponent;
}

/**
 * Major-unit number to the canonical minor-unit string the server accepts.
 *
 * `toPrecision(15)` before rounding scrubs binary representation error, so
 * `12.35 * 100` does not land on `1234.9999999999998` and lose a haléř.
 */
export function majorToMinor(major: number, currency: CurrencyCode): string {
  const exponent = MINOR_UNITS[currency];
  const scaled = exponent === 0 ? major : Number((major * 10 ** exponent).toPrecision(15));
  return String(Math.round(scaled));
}

/* ─────────────────────────────────────────────────────────────── split ── */

export type WireSplit =
  | { type: "equal"; among: string[] }
  | { type: "exact"; amounts: { memberId: string; amount: string }[] }
  | { type: "shares"; weights: { memberId: string; weight: number }[] }
  | { type: "percentage"; percents: { memberId: string; basisPoints: number }[] };

/**
 * Wire split to the domain pair (split type, participant list).
 *
 * The participant list has to be reconstructed: the server has no `splitAmong`
 * field because the split's own entries define who takes part.
 */
export function splitFromWire(
  wire: WireSplit,
  currency: CurrencyCode,
): { splitType: SplitType; splitAmong: string[] } {
  switch (wire.type) {
    case "equal":
      return { splitType: { equal: {} }, splitAmong: [...wire.among] };

    case "exact": {
      const exact: Record<string, number> = {};
      for (const entry of wire.amounts) {
        exact[entry.memberId] = minorToMajor(entry.amount, currency);
      }
      return { splitType: { exact }, splitAmong: wire.amounts.map((a) => a.memberId) };
    }

    case "shares": {
      const shares: Record<string, number> = {};
      for (const entry of wire.weights) shares[entry.memberId] = entry.weight;
      return { splitType: { shares }, splitAmong: wire.weights.map((w) => w.memberId) };
    }

    case "percentage": {
      const percentage: Record<string, number> = {};
      for (const entry of wire.percents) {
        percentage[entry.memberId] = entry.basisPoints;
      }
      return {
        splitType: { percentage },
        splitAmong: wire.percents.map((p) => p.memberId),
      };
    }
  }
}

/**
 * Domain split to the wire shape.
 *
 * `splitAmong` drives membership and ordering so a member the user selected but
 * gave no explicit amount still appears — with a zero — rather than silently
 * dropping out of the expense.
 */
export function splitToWire(
  splitType: SplitType,
  splitAmong: string[],
  currency: CurrencyCode,
): WireSplit {
  switch (splitKind(splitType)) {
    case "exact": {
      const exact = (splitType as { exact: Record<string, number> }).exact;
      return {
        type: "exact",
        amounts: splitAmong.map((memberId) => ({
          memberId,
          amount: majorToMinor(exact[memberId] ?? 0, currency),
        })),
      };
    }

    case "shares": {
      const shares = (splitType as { shares: Record<string, number> }).shares;
      return {
        type: "shares",
        // The server requires weight >= 1, so a member left at zero would be
        // rejected for the whole expense. Treat them as an equal single share.
        weights: splitAmong.map((memberId) => ({
          memberId,
          weight: Math.max(1, Math.round(shares[memberId] ?? 1)),
        })),
      };
    }

    case "percentage": {
      const percentage = (splitType as { percentage: Record<string, number> }).percentage;
      return {
        type: "percentage",
        percents: splitAmong.map((memberId) => ({
          memberId,
          basisPoints: Math.round(percentage[memberId] ?? 0),
        })),
      };
    }

    default:
      return { type: "equal", among: [...splitAmong] };
  }
}

/* ───────────────────────────────────────────────────────────── expense ── */

export interface WireExpense {
  id: string;
  groupId?: string;
  title: string;
  amount: string;
  currency: CurrencyCode;
  paidBy: string;
  date: string;
  split: WireSplit;
  shares?: { memberId: string; amount: string }[];
  categoryId?: string | null;
  iconSymbol?: string | null;
  notes?: string | null;
  version?: number;
  createdAt?: string;
}

export function expenseFromWire(wire: WireExpense, groupId: string): Expense {
  const { splitType, splitAmong } = splitFromWire(wire.split, wire.currency);

  const shares: Record<string, number> = {};
  for (const entry of wire.shares ?? []) {
    shares[entry.memberId] = minorToMajor(entry.amount, wire.currency);
  }

  return {
    id: wire.id,
    groupId: wire.groupId ?? groupId,
    title: wire.title,
    amount: minorToMajor(wire.amount, wire.currency),
    currency: wire.currency,
    paidBy: wire.paidBy,
    splitAmong,
    splitType,
    // The server stores a plain date; the app models an instant. Noon UTC keeps
    // the calendar day stable either side of the date line.
    date: wire.date.length === 10 ? `${wire.date}T12:00:00Z` : wire.date,
    notes: wire.notes ?? null,
    iconSymbol: wire.iconSymbol ?? null,
    createdAt: wire.createdAt ?? new Date().toISOString(),
    shares: wire.shares?.length ? shares : undefined,
    version: wire.version,
  };
}

export interface WriteExpenseBody {
  clientId: string;
  title: string;
  amount: string;
  currency: CurrencyCode;
  paidBy: string;
  date: string;
  split: WireSplit;
  iconSymbol?: string;
  notes?: string;
}

export function expenseToWire(expense: Expense, clientId: string): WriteExpenseBody {
  const body: WriteExpenseBody = {
    clientId,
    title: expense.title,
    amount: majorToMinor(expense.amount, expense.currency),
    currency: expense.currency,
    paidBy: expense.paidBy,
    date: toWireDate(expense.date),
    split: splitToWire(expense.splitType, expense.splitAmong, expense.currency),
  };

  // Omitted rather than null: the idempotency check canonicalizes absent and
  // null identically, but sending nothing keeps the body minimal.
  if (expense.iconSymbol) body.iconSymbol = expense.iconSymbol;
  if (expense.notes) body.notes = expense.notes;

  return body;
}

/** The server wants a strict `yyyy-MM-dd`, not a timestamp. */
export function toWireDate(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso.slice(0, 10);
  return date.toISOString().slice(0, 10);
}
