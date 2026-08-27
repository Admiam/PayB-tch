/**
 * Core data model, mirroring the Swift `Codable` types field-for-field.
 *
 * None of the Swift models declare custom `CodingKeys`, so the JSON keys are
 * the property names verbatim. Keeping that exact shape means a JSON export
 * from the iOS app can be dropped straight into the web app and vice versa.
 */

export const CURRENCIES = ["CZK", "EUR", "USD", "GBP"] as const;
export type CurrencyCode = (typeof CURRENCIES)[number];
export const DEFAULT_CURRENCY: CurrencyCode = "CZK";

/**
 * Minor units per currency. CZK is a whole-koruna currency in this app — it has
 * **zero** decimal places, unlike the other three. Every rounding decision
 * depends on this table, so it is the single source of truth for it.
 */
export const MINOR_UNITS: Record<CurrencyCode, number> = {
  CZK: 0,
  EUR: 2,
  USD: 2,
  GBP: 2,
};

export const CURRENCY_SYMBOLS: Record<CurrencyCode, string> = {
  CZK: "Kč",
  EUR: "€",
  USD: "$",
  GBP: "£",
};

/**
 * Coerces arbitrary stored text to a known currency, mirroring the Swift
 * decoder: upper-case it, and fall back to CZK rather than throwing. `Group`
 * stores this as a bare string, so unvalidated values really can reach here.
 */
export function toCurrency(raw: string | null | undefined): CurrencyCode {
  const upper = (raw ?? "").toUpperCase();
  return (CURRENCIES as readonly string[]).includes(upper)
    ? (upper as CurrencyCode)
    : DEFAULT_CURRENCY;
}

export interface Group {
  id: string;
  name: string;
  memberIds: string[];
  /**
   * A bare string in Swift too, not the Currency enum — nothing validates it at
   * write time. Always read it through `toCurrency()`.
   */
  defaultCurrency: string;
}

export interface Member {
  id: string;
  name: string;
  /**
   * Photo-URL avatar. No screen in the iOS app ever sets this; it is carried
   * for wire compatibility only.
   */
  imageUrl?: string | null;
  /**
   * The avatar mechanism that is actually used. Holds an SF Symbol name on
   * iOS; the web maps those names to its own icon set. Absent means "render
   * initials instead".
   */
  iconSymbol?: string | null;
}

/**
 * Wire-exact mirror of Swift's enum-with-associated-values encoding:
 *   .equal                       -> {"equal": {}}
 *   .exact(["u1": 70])           -> {"exact": {"u1": 70}}
 *   .shares(["u1": 2])           -> {"shares": {"u1": 2}}
 *
 * Note `equal` encodes as an empty **object**, not a bare string — that shape
 * is pinned by ExpenseCodableTests on the Swift side.
 */
export type SplitType =
  | { equal: Record<string, never> }
  | { exact: Record<string, number> }
  | { shares: Record<string, number> };

export type SplitKind = "equal" | "exact" | "shares";

export const SPLIT_EQUAL: SplitType = { equal: {} };

export function splitKind(split: SplitType): SplitKind {
  if ("exact" in split) return "exact";
  if ("shares" in split) return "shares";
  return "equal";
}

export interface Expense {
  id: string;
  groupId: string;
  title: string;
  /**
   * Decimal major units as written by the iOS app (e.g. `100.5`), NOT minor
   * units. All arithmetic converts to integer minor units first — see
   * `money.ts`. Never add or split these values directly as floats.
   */
  amount: number;
  currency: CurrencyCode;
  paidBy: string;
  splitAmong: string[];
  splitType: SplitType;
  /** ISO-8601 timestamp. */
  date: string;
  notes?: string | null;
  iconSymbol?: string | null;
  /** ISO-8601 timestamp. */
  createdAt: string;
}

/** Theme choice, mirroring the iOS AppearanceMode. */
export type Appearance = "system" | "light" | "dark";
