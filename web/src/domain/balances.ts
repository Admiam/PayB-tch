/**
 * Balance and settlement math, ported from `BalanceCalculator.swift` and
 * `DebtSimplifier.swift`.
 *
 * Both are pure functions over the expense list — nothing about "settling up"
 * is ever stored. The iOS app recomputes these on every render and so does
 * this port, which keeps the two provably in agreement.
 */

import { computeShares, decimalsFor, fxConvert, roundBankers } from "./money";
import type { CurrencyCode, Expense } from "./types";

export interface MemberBalance {
  memberId: string;
  /** Positive: others owe them. Negative: they owe others. */
  net: number;
}

export interface DebtEdge {
  id: string;
  from: string;
  to: string;
  amount: number;
}

/**
 * Per-member owed amounts for one expense.
 *
 * Prefers the server's resolved shares when the expense came from the API: the
 * server is the authority on how a remainder was distributed, and recomputing
 * it here risks the web showing a different figure from the phone for the same
 * expense. Falls back to computing locally for expenses that never round-tripped
 * through the server.
 *
 * The server resolves shares in the expense's own currency, so they only apply
 * directly when no conversion is happening.
 */
function sharesFor(
  expense: Expense,
  convertedAmount: number,
  scale: number,
): Record<string, number> {
  // Only usable when the amount was not converted — the server resolved these
  // against the original currency, so after an FX step they no longer sum to
  // the displayed total.
  if (expense.shares && convertedAmount === expense.amount) {
    return expense.shares;
  }
  return computeShares(
    expense.splitType,
    convertedAmount,
    expense.splitAmong,
    scale,
  );
}

/**
 * Net position per member, expressed in `target` currency.
 *
 * Each expense credits its payer the full amount and debits every participant
 * their share. Amounts are converted and rounded to the target currency's scale
 * *before* splitting, exactly as the Swift does — rounding after the split
 * would produce different totals.
 */
export function calculateBalances(
  expenses: Expense[],
  memberIds: string[],
  target: CurrencyCode,
): MemberBalance[] {
  const scale = decimalsFor(target);
  const net = new Map<string, number>(memberIds.map((id) => [id, 0]));

  const add = (id: string, delta: number) => {
    net.set(id, roundBankers((net.get(id) ?? 0) + delta, scale));
  };

  for (const e of expenses) {
    const amount = roundBankers(
      fxConvert(e.amount, e.currency, target),
      scale,
    );

    add(e.paidBy, amount);

    for (const [memberId, share] of Object.entries(sharesFor(e, amount, scale))) {
      add(memberId, -share);
    }
  }

  return memberIds.map((id) => ({ memberId: id, net: net.get(id) ?? 0 }));
}

/**
 * Greedy creditor/debtor pairing into suggested transfers.
 *
 * Repeatedly settles the largest creditor against the largest debtor. Not
 * provably minimal for arbitrary graphs, but always produces at most N-1
 * transfers, which is what makes it useful to a human.
 *
 * `epsilon` swallows sub-cent residue so a balance of 0.004 doesn't generate a
 * transfer nobody can actually make.
 */
export function simplifyDebts(
  balances: MemberBalance[],
  epsilon = 0.01,
): DebtEdge[] {
  const creditors = balances
    .filter((b) => b.net > epsilon)
    .sort((a, b) => b.net - a.net)
    .map((b) => ({ id: b.memberId, amount: b.net }));

  const debtors = balances
    .filter((b) => b.net < -epsilon)
    .sort((a, b) => a.net - b.net)
    .map((b) => ({ id: b.memberId, amount: -b.net }));

  const edges: DebtEdge[] = [];
  let i = 0;
  let j = 0;

  while (i < creditors.length && j < debtors.length) {
    const pay = Math.min(creditors[i].amount, debtors[j].amount);

    edges.push({
      id: `${debtors[j].id}->${creditors[i].id}`,
      from: debtors[j].id,
      to: creditors[i].id,
      amount: pay,
    });

    creditors[i].amount -= pay;
    debtors[j].amount -= pay;

    if (creditors[i].amount < epsilon) i += 1;
    if (debtors[j].amount < epsilon) j += 1;
  }

  return edges;
}

/** What a single member owes or is owed, for the summary tiles. */
export function balanceFor(
  balances: MemberBalance[],
  memberId: string | null,
): number {
  if (!memberId) return 0;
  return balances.find((b) => b.memberId === memberId)?.net ?? 0;
}

/**
 * The current user's own share across the given expenses, in `target`
 * currency — what the "My costs" tile shows.
 */
export function myCosts(
  expenses: Expense[],
  memberId: string | null,
  target: CurrencyCode,
): number {
  if (!memberId) return 0;
  const scale = decimalsFor(target);
  let total = 0;

  for (const e of expenses) {
    if (!e.splitAmong.includes(memberId)) continue;
    const amount = roundBankers(fxConvert(e.amount, e.currency, target), scale);
    const shares = computeShares(e.splitType, amount, e.splitAmong, scale);
    total = roundBankers(total + (shares[memberId] ?? 0), scale);
  }

  return total;
}

/** Total of all expenses in `target` currency — the "Total" tile. */
export function totalCosts(
  expenses: Expense[],
  target: CurrencyCode,
): number {
  const scale = decimalsFor(target);
  return expenses.reduce(
    (acc, e) =>
      roundBankers(acc + fxConvert(e.amount, e.currency, target), scale),
    0,
  );
}
