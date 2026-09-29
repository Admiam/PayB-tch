/**
 * The combined ledger: one "who owes whom" across several groups.
 *
 * Per group, the server is the authority on the numbers — this module never
 * recomputes a balance, it only adds up balance sheets that already exist and
 * re-runs the same greedy simplifier the single-group view uses. That keeps the
 * shared view honest by construction: it can disagree with a group only by the
 * width of a currency conversion, never by a different reading of the ledger.
 *
 * What it does own is the two things that only exist once groups are pooled:
 * choosing the currency to show, and refusing to net two currencies without
 * saying so.
 */

import { simplifyDebts, type DebtEdge, type MemberBalance } from "./balances";
import { decimalsFor, fxConvert, roundBankers } from "./money";
import { toCurrency, type CurrencyCode } from "./types";
import type { GroupBalanceSheet } from "@/api/endpoints";

/**
 * Nets per person, in one currency.
 *
 * Reuses `MemberBalance` deliberately: the shape is identical and so is the
 * meaning, only the id is a person rather than a member. That is what lets
 * `simplifyDebts` and the flow canvas be used unchanged.
 */
export type PersonBalance = MemberBalance;

/** A suggested transfer between two people, spanning every pooled group. */
export type SharedDebtEdge = DebtEdge;

/**
 * The currency the combined view is shown in.
 *
 * The commonest default among the pooled groups, ties going to the first listed.
 * Deriving it beats storing it: there is no answer a user could give that is
 * better than "the one most of my groups already use", and a stored choice would
 * be one more thing to keep in step as groups come and go.
 */
export function sharedCurrency(
  groups: readonly { id: string; defaultCurrency: string }[],
  fallback: CurrencyCode = "CZK",
): CurrencyCode {
  if (groups.length === 0) return fallback;

  const counts = new Map<CurrencyCode, number>();
  for (const group of groups) {
    const code = toCurrency(group.defaultCurrency);
    counts.set(code, (counts.get(code) ?? 0) + 1);
  }

  let best = toCurrency(groups[0].defaultCurrency);
  for (const group of groups) {
    const code = toCurrency(group.defaultCurrency);
    if ((counts.get(code) ?? 0) > (counts.get(best) ?? 0)) best = code;
  }
  return best;
}

/** Whether any pooled balance is held in a currency other than `target`. */
export function hasConversions(
  sheets: readonly GroupBalanceSheet[],
  target: CurrencyCode,
): boolean {
  return sheets.some((sheet) =>
    sheet.buckets.some(
      (bucket) => bucket.currency !== target && bucket.nets.length > 0,
    ),
  );
}

/**
 * Every pooled group's balance sheet, folded into one net per person.
 *
 * Converted and rounded at the target currency's scale on the way in, exactly as
 * `calculateBalances` does — rounding once at the end instead would let a
 * person's total drift from the sum of the rows a user can actually see.
 *
 * A member the roster doesn't account for becomes their own person rather than
 * being skipped. Dropping them would quietly understate somebody's debt, which
 * is the one failure mode worth engineering against here; showing an unnamed
 * person is merely untidy.
 */
export function aggregatePersonNets(
  sheets: readonly GroupBalanceSheet[],
  personOfMember: ReadonlyMap<string, string>,
  target: CurrencyCode,
): PersonBalance[] {
  const scale = decimalsFor(target);
  const nets = new Map<string, number>();

  for (const sheet of sheets) {
    for (const bucket of sheet.buckets) {
      for (const row of bucket.nets) {
        const personId = personOfMember.get(row.memberId) ?? row.memberId;
        const converted = fxConvert(row.net, bucket.currency, target);
        nets.set(
          personId,
          roundBankers((nets.get(personId) ?? 0) + converted, scale),
        );
      }
    }
  }

  return [...nets].map(([memberId, net]) => ({ memberId, net }));
}

/**
 * The combined settlement plan: who should pay whom, across all pooled groups.
 *
 * The same greedy pairing as one group, over the pooled nets — which is the
 * whole point. Three groups where everyone is a little bit in debt to everyone
 * collapse into a couple of transfers that actually clear the board.
 */
export function sharedDebts(
  sheets: readonly GroupBalanceSheet[],
  personOfMember: ReadonlyMap<string, string>,
  target: CurrencyCode,
): { nets: PersonBalance[]; edges: SharedDebtEdge[] } {
  const nets = aggregatePersonNets(sheets, personOfMember, target);
  return { nets, edges: simplifyDebts(nets) };
}
