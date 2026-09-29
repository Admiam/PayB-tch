/**
 * Money arithmetic and formatting, ported from `Currency.swift` and
 * `SplitType.swift`.
 *
 * Two rules carry over from the Swift and are load-bearing — the app's tests
 * pin both, and getting either wrong loses real money:
 *
 *  1. Rounding is **banker's** (half to even), not half-up. Swift's
 *     `Decimal.rounded(scale:)` defaults to `.bankers`.
 *  2. When a split doesn't divide evenly, the **last member in the list**
 *     absorbs the remainder, so the shares always sum to the total exactly.
 *     This is not the largest-remainder method.
 */

import {
  CURRENCY_SYMBOLS,
  MINOR_UNITS,
  type CurrencyCode,
  type SplitType,
} from "./types";

/** Decimal places a currency renders and rounds to. CZK is 0. */
export function decimalsFor(currency: CurrencyCode): number {
  return MINOR_UNITS[currency];
}

/**
 * Banker's rounding to `scale` decimal places.
 *
 * `toPrecision(15)` first scrubs binary representation error, so a value that
 * is mathematically x.5 but stored as x.4999999999999997 is still recognised as
 * a tie. Without that correction the tie-breaking rule silently never fires.
 */
export function roundBankers(value: number, scale: number): number {
  if (!Number.isFinite(value)) return 0;

  const factor = 10 ** scale;
  const scaled = Number((value * factor).toPrecision(15));
  const floor = Math.floor(scaled);
  const frac = scaled - floor;

  let rounded: number;
  if (Math.abs(frac - 0.5) < 1e-9) {
    // Exact tie — pick the even neighbour.
    rounded = floor % 2 === 0 ? floor : floor + 1;
  } else {
    rounded = Math.round(scaled);
  }

  // Re-normalise through toPrecision so the division cannot reintroduce error.
  return Number((rounded / factor).toPrecision(15));
}

/** Adds a list of amounts at a fixed scale, avoiding float drift. */
export function sumAt(values: number[], scale: number): number {
  const factor = 10 ** scale;
  const total = values.reduce(
    (acc, v) => acc + Math.round(Number((v * factor).toPrecision(15))),
    0,
  );
  return total / factor;
}

// ─────────────────────────────────────────────────────────── splitting ──

/**
 * Per-member share of `total`.
 *
 * The returned values sum to `total` exactly for `equal` and `shares`. For
 * `exact` the caller supplied the numbers, so this returns them untouched —
 * validating that they add up is the editor's job, not this function's.
 *
 * `members` order matters: the final entry receives the rounding remainder.
 */
export function computeShares(
  split: SplitType,
  total: number,
  members: string[],
  scale: number,
): Record<string, number> {
  if (members.length === 0) return {};

  if ("exact" in split) {
    const out: Record<string, number> = {};
    for (const m of members) out[m] = split.exact[m] ?? 0;
    return out;
  }

  if ("shares" in split) {
    const weights = split.shares;
    const totalWeight = Object.values(weights).reduce((a, b) => a + b, 0);
    if (totalWeight <= 0) return {};
    return distributeByWeights(total, members, weights, totalWeight, scale);
  }

  if ("percentage" in split) {
    // Basis points are just weights that happen to sum to 10000, so the same
    // remainder-to-last distribution applies and the shares still total exactly.
    const points = split.percentage;
    const totalPoints = Object.values(points).reduce((a, b) => a + b, 0);
    if (totalPoints <= 0) return {};
    return distributeByWeights(total, members, points, totalPoints, scale);
  }

  return distributeEqually(total, members, scale);
}

function distributeEqually(
  total: number,
  members: string[],
  scale: number,
): Record<string, number> {
  const each = roundBankers(total / members.length, scale);
  const out: Record<string, number> = {};
  let assigned = 0;

  members.forEach((m, i) => {
    if (i === members.length - 1) {
      out[m] = roundBankers(total - assigned, scale);
    } else {
      out[m] = each;
      assigned = roundBankers(assigned + each, scale);
    }
  });

  return out;
}

function distributeByWeights(
  total: number,
  members: string[],
  weights: Record<string, number>,
  totalWeight: number,
  scale: number,
): Record<string, number> {
  const out: Record<string, number> = {};
  let assigned = 0;

  members.forEach((m, i) => {
    if (i === members.length - 1) {
      out[m] = roundBankers(total - assigned, scale);
    } else {
      const share = roundBankers((total * (weights[m] ?? 0)) / totalWeight, scale);
      out[m] = share;
      assigned = roundBankers(assigned + share, scale);
    }
  });

  return out;
}

// ───────────────────────────────────────────────────────────── formatting ──

function numberString(value: number, decimals: number): string {
  return new Intl.NumberFormat(undefined, {
    minimumFractionDigits: decimals,
    maximumFractionDigits: decimals,
  }).format(value);
}

/**
 * Compact amount, no currency symbol — for tiles and hero figures where the
 * symbol is rendered separately for typographic hierarchy.
 *
 * Note the abbreviation thresholds are asymmetric and deliberately so:
 * millions abbreviate at 1,000,000 but thousands only at **10,000**, so a
 * four-figure amount still renders in full.
 */
export function amountString(value: number, currency: CurrencyCode): string {
  const abs = Math.abs(value);
  if (abs >= 1_000_000) return `${numberString(value / 1_000_000, 1)}M`;
  if (abs >= 10_000) return `${numberString(value / 1_000, 0)}k`;
  return numberString(value, decimalsFor(currency));
}

/** Compact amount with the currency symbol, e.g. `300k Kč`. */
export function formatAmount(value: number, currency: CurrencyCode): string {
  return `${amountString(value, currency)} ${CURRENCY_SYMBOLS[currency]}`;
}

/** Full precision, never abbreviated — editors and detail views. */
export function formatAmountFull(value: number, currency: CurrencyCode): string {
  return `${numberString(value, decimalsFor(currency))} ${CURRENCY_SYMBOLS[currency]}`;
}

/**
 * Minor units as the API writes them — a string, never a number — to major
 * units.
 *
 * The API sends money as integer minor units in a JSON *string* so a large
 * amount cannot lose precision to a double on the way in. Reading it back to
 * the app's major-unit `number` is exact for every amount a person will ever
 * split, and the division is by a power of ten renormalised through
 * `toPrecision` so CZK (scale 0) and EUR (scale 2) both land on the nose.
 *
 * Unparseable input reads as zero rather than NaN: a balance row the server
 * garbled should leave the rest of the sheet legible.
 */
export function fromMinorUnits(minor: string, currency: CurrencyCode): number {
  const units = Number(minor);
  if (!Number.isFinite(units)) return 0;

  const scale = decimalsFor(currency);
  if (scale === 0) return units;
  return Number((units / 10 ** scale).toPrecision(15));
}

/**
 * Parses user input from an amount field. Accepts both decimal separators
 * because a Czech keyboard produces a comma and the numeric keypad a dot.
 */
export function parseAmount(input: string): number | null {
  const cleaned = input.replace(/\s/g, "").replace(",", ".");
  if (cleaned === "" || cleaned === ".") return null;
  const value = Number(cleaned);
  return Number.isFinite(value) ? value : null;
}

// ──────────────────────────────────────────────────────────────────── fx ──

/** CZK per 1 unit. Hardcoded, exactly as `StaticFXProvider` does it. */
const RATES_TO_CZK: Record<CurrencyCode, number> = {
  CZK: 1.0,
  EUR: 25.2,
  USD: 23.1,
  GBP: 29.4,
};

export function fxRate(from: CurrencyCode, to: CurrencyCode): number {
  return RATES_TO_CZK[from] / RATES_TO_CZK[to];
}

export function fxConvert(
  amount: number,
  from: CurrencyCode,
  to: CurrencyCode,
): number {
  if (from === to) return amount;
  return amount * fxRate(from, to);
}
