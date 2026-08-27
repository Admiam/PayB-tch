/**
 * Ported from `PaybitchTests/DebtSimplifierTests.swift` and
 * `BalanceCalculatorTests.swift`.
 *
 * These are the tests that catch "the web app says Adam owes 300, the phone
 * says 250". Everything here asserts against values the Swift suite pins.
 */

import { describe, expect, it } from "vitest";
import {
  calculateBalances,
  myCosts,
  simplifyDebts,
  totalCosts,
  type MemberBalance,
} from "./balances";
import { SPLIT_EQUAL, type Expense, type SplitType } from "./types";

const bal = (memberId: string, net: number): MemberBalance => ({
  memberId,
  net,
});

let seq = 0;
function expense(
  partial: Partial<Expense> & Pick<Expense, "amount" | "paidBy" | "splitAmong">,
): Expense {
  seq += 1;
  return {
    id: `e${seq}`,
    groupId: "g1",
    title: "Test",
    currency: "CZK",
    splitType: SPLIT_EQUAL,
    date: "2026-01-01T00:00:00Z",
    createdAt: "2026-01-01T00:00:00Z",
    ...partial,
  };
}

describe("simplifyDebts", () => {
  it("produces no transfers for a balanced group", () => {
    expect(simplifyDebts([bal("a", 0), bal("b", 0)])).toEqual([]);
  });

  it("produces one transfer for a simple two-party debt", () => {
    const edges = simplifyDebts([bal("a", -50), bal("b", 50)]);
    expect(edges).toHaveLength(1);
    expect(edges[0].from).toBe("a");
    expect(edges[0].to).toBe("b");
    expect(edges[0].amount).toBe(50);
  });

  it("routes two debtors to a single creditor", () => {
    const edges = simplifyDebts([bal("a", -30), bal("b", -20), bal("c", 50)]);
    expect(edges).toHaveLength(2);
    expect(edges.every((e) => e.to === "c")).toBe(true);
    expect(edges.reduce((s, e) => s + e.amount, 0)).toBe(50);
  });

  it("never needs more than N-1 transfers", () => {
    const edges = simplifyDebts([
      bal("a", -10),
      bal("b", -20),
      bal("c", 5),
      bal("d", 25),
    ]);
    expect(edges.length).toBeLessThanOrEqual(3);
    expect(edges.reduce((s, e) => s + e.amount, 0)).toBe(30);
  });

  it("ignores sub-cent residue", () => {
    expect(simplifyDebts([bal("a", -0.005), bal("b", 0.005)])).toEqual([]);
  });

  it("matches the largest creditor first", () => {
    const edges = simplifyDebts([bal("a", -80), bal("b", 30), bal("c", 50)]);
    expect(edges[0].from).toBe("a");
    expect(edges[0].to).toBe("c");
  });

  it("conserves total credit against total debit", () => {
    const balances = [bal("a", -75), bal("b", -25), bal("c", 60), bal("d", 40)];
    const edges = simplifyDebts(balances);
    const moved = edges.reduce((s, e) => s + e.amount, 0);
    expect(moved).toBe(100);
  });
});

describe("calculateBalances", () => {
  it("credits the payer and debits every participant", () => {
    // One person pays 90, three split it: payer is +90-30 = +60, others -30.
    const balances = calculateBalances(
      [expense({ amount: 90, paidBy: "a", splitAmong: ["a", "b", "c"] })],
      ["a", "b", "c"],
      "CZK",
    );

    expect(balances.find((b) => b.memberId === "a")?.net).toBe(60);
    expect(balances.find((b) => b.memberId === "b")?.net).toBe(-30);
    expect(balances.find((b) => b.memberId === "c")?.net).toBe(-30);
  });

  it("always sums to zero across the group", () => {
    const balances = calculateBalances(
      [
        expense({ amount: 100, paidBy: "a", splitAmong: ["a", "b", "c"] }),
        expense({ amount: 55, paidBy: "b", splitAmong: ["a", "b"] }),
        expense({ amount: 13.5, paidBy: "c", splitAmong: ["a", "b", "c"] }),
      ],
      ["a", "b", "c"],
      "EUR",
    );

    const sum = balances.reduce((s, b) => s + b.net, 0);
    expect(sum).toBeCloseTo(0, 8);
  });

  it("leaves a member out of an expense they are not part of", () => {
    const balances = calculateBalances(
      [expense({ amount: 50, paidBy: "a", splitAmong: ["a", "b"] })],
      ["a", "b", "c"],
      "CZK",
    );

    expect(balances.find((b) => b.memberId === "c")?.net).toBe(0);
  });

  it("honours exact splits", () => {
    const exact: SplitType = { exact: { a: 70, b: 30 } };
    const balances = calculateBalances(
      [expense({ amount: 100, paidBy: "a", splitAmong: ["a", "b"], splitType: exact })],
      ["a", "b"],
      "CZK",
    );

    expect(balances.find((b) => b.memberId === "a")?.net).toBe(30);
    expect(balances.find((b) => b.memberId === "b")?.net).toBe(-30);
  });

  it("honours share weights", () => {
    const shares: SplitType = { shares: { a: 1, b: 3 } };
    const balances = calculateBalances(
      [expense({ amount: 100, paidBy: "a", splitAmong: ["a", "b"], splitType: shares })],
      ["a", "b"],
      "CZK",
    );

    // a owes 25 of their own 100 → net +75; b owes 75.
    expect(balances.find((b) => b.memberId === "a")?.net).toBe(75);
    expect(balances.find((b) => b.memberId === "b")?.net).toBe(-75);
  });

  it("converts foreign-currency expenses into the display currency", () => {
    // 1 EUR = 25.20 CZK, split between two people.
    const balances = calculateBalances(
      [
        expense({
          amount: 10,
          currency: "EUR",
          paidBy: "a",
          splitAmong: ["a", "b"],
        }),
      ],
      ["a", "b"],
      "CZK",
    );

    expect(balances.find((b) => b.memberId === "a")?.net).toBe(126);
    expect(balances.find((b) => b.memberId === "b")?.net).toBe(-126);
  });

  it("returns zero for everyone when there are no expenses", () => {
    const balances = calculateBalances([], ["a", "b"], "CZK");
    expect(balances).toEqual([
      { memberId: "a", net: 0 },
      { memberId: "b", net: 0 },
    ]);
  });
});

describe("tile figures", () => {
  const expenses = [
    expense({ amount: 90, paidBy: "a", splitAmong: ["a", "b", "c"] }),
    expense({ amount: 30, paidBy: "b", splitAmong: ["b", "c"] }),
  ];

  it("myCosts counts only the shares the member is part of", () => {
    // a is in the first expense only: 30.
    expect(myCosts(expenses, "a", "CZK")).toBe(30);
    // b is in both: 30 + 15.
    expect(myCosts(expenses, "b", "CZK")).toBe(45);
  });

  it("myCosts is zero without a current user", () => {
    expect(myCosts(expenses, null, "CZK")).toBe(0);
  });

  it("totalCosts sums every expense", () => {
    expect(totalCosts(expenses, "CZK")).toBe(120);
  });
});
