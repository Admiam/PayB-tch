/**
 * The wire adapter is where money silently breaks if it breaks at all — a
 * misplaced exponent turns 840 Kč into 8.40 Kč, and nothing else in the app
 * would notice. These tests pin both directions and the CZK zero-decimal case.
 */

import { describe, expect, it } from "vitest";
import {
  expenseFromWire,
  expenseToWire,
  majorToMinor,
  minorToMajor,
  splitFromWire,
  splitToWire,
  toWireDate,
  type WireExpense,
} from "./wire";
import type { Expense } from "@/domain/types";

describe("money conversion", () => {
  it("treats CZK as a zero-decimal currency", () => {
    // The trap: "840" is 840 Kč, not 8.40.
    expect(minorToMajor("840", "CZK")).toBe(840);
    expect(majorToMinor(840, "CZK")).toBe("840");
  });

  it("treats EUR, USD and GBP as two-decimal", () => {
    expect(minorToMajor("10000", "EUR")).toBe(100);
    expect(minorToMajor("1234", "USD")).toBe(12.34);
    expect(majorToMinor(100, "EUR")).toBe("10000");
    expect(majorToMinor(12.34, "GBP")).toBe("1234");
  });

  it("does not lose a unit to float error", () => {
    // 12.35 * 100 is 1234.9999999999998 in binary floating point.
    expect(majorToMinor(12.35, "EUR")).toBe("1235");
    expect(majorToMinor(0.29, "USD")).toBe("29");
    expect(majorToMinor(1.005, "EUR")).toBe("101");
  });

  it("round-trips every currency", () => {
    for (const [currency, value] of [
      ["CZK", 1240],
      ["EUR", 99.99],
      ["USD", 0.01],
      ["GBP", 1234.56],
    ] as const) {
      expect(minorToMajor(majorToMinor(value, currency), currency)).toBeCloseTo(
        value,
        10,
      );
    }
  });
});

describe("split conversion", () => {
  it("reconstructs participants for an equal split", () => {
    const { splitType, splitAmong } = splitFromWire(
      { type: "equal", among: ["a", "b", "c"] },
      "CZK",
    );
    expect(splitType).toEqual({ equal: {} });
    expect(splitAmong).toEqual(["a", "b", "c"]);
  });

  it("converts exact amounts out of minor units", () => {
    const { splitType, splitAmong } = splitFromWire(
      {
        type: "exact",
        amounts: [
          { memberId: "a", amount: "7000" },
          { memberId: "b", amount: "3000" },
        ],
      },
      "EUR",
    );
    expect(splitType).toEqual({ exact: { a: 70, b: 30 } });
    // The participant list has no wire field — it comes from the entries.
    expect(splitAmong).toEqual(["a", "b"]);
  });

  it("carries share weights through unchanged", () => {
    const { splitType } = splitFromWire(
      { type: "shares", weights: [{ memberId: "a", weight: 2 }, { memberId: "b", weight: 1 }] },
      "CZK",
    );
    expect(splitType).toEqual({ shares: { a: 2, b: 1 } });
  });

  it("reads a percentage split the phone app cannot represent", () => {
    const { splitType, splitAmong } = splitFromWire(
      {
        type: "percentage",
        percents: [
          { memberId: "a", basisPoints: 5000 },
          { memberId: "b", basisPoints: 5000 },
        ],
      },
      "CZK",
    );
    expect(splitType).toEqual({ percentage: { a: 5000, b: 5000 } });
    expect(splitAmong).toEqual(["a", "b"]);
  });

  it("emits exact amounts in minor units", () => {
    expect(splitToWire({ exact: { a: 70, b: 30 } }, ["a", "b"], "EUR")).toEqual({
      type: "exact",
      amounts: [
        { memberId: "a", amount: "7000" },
        { memberId: "b", amount: "3000" },
      ],
    });
  });

  it("includes a selected member who was given no explicit amount", () => {
    const wire = splitToWire({ exact: { a: 100 } }, ["a", "b"], "CZK");
    expect(wire).toEqual({
      type: "exact",
      amounts: [
        { memberId: "a", amount: "100" },
        { memberId: "b", amount: "0" },
      ],
    });
  });

  it("never emits a zero weight, which the server rejects", () => {
    const wire = splitToWire({ shares: { a: 2, b: 0 } }, ["a", "b"], "CZK");
    expect(wire).toEqual({
      type: "shares",
      weights: [
        { memberId: "a", weight: 2 },
        { memberId: "b", weight: 1 },
      ],
    });
  });

  it("round-trips an exact split", () => {
    const original = { exact: { a: 12.34, b: 87.66 } };
    const wire = splitToWire(original, ["a", "b"], "EUR");
    expect(splitFromWire(wire, "EUR").splitType).toEqual(original);
  });
});

describe("expense conversion", () => {
  const wire: WireExpense = {
    id: "exp-1",
    title: "Dinner",
    amount: "840",
    currency: "CZK",
    paidBy: "mem-a",
    date: "2026-06-27",
    split: { type: "equal", among: ["mem-a", "mem-b"] },
    shares: [
      { memberId: "mem-a", amount: "420" },
      { memberId: "mem-b", amount: "420" },
    ],
    iconSymbol: "fork.knife",
    version: 3,
  };

  it("decodes a server expense into the domain model", () => {
    const expense = expenseFromWire(wire, "grp-1");
    expect(expense.amount).toBe(840);
    expect(expense.groupId).toBe("grp-1");
    expect(expense.splitAmong).toEqual(["mem-a", "mem-b"]);
    expect(expense.version).toBe(3);
  });

  it("keeps the server's resolved shares", () => {
    // These are what stop the web and the phone disagreeing over a remainder.
    expect(expenseFromWire(wire, "grp-1").shares).toEqual({
      "mem-a": 420,
      "mem-b": 420,
    });
  });

  it("anchors a date-only value at midday so the calendar day is stable", () => {
    expect(expenseFromWire(wire, "grp-1").date).toBe("2026-06-27T12:00:00Z");
  });

  it("encodes back to a write body", () => {
    const expense: Expense = {
      id: "exp-1",
      groupId: "grp-1",
      title: "Dinner",
      amount: 840,
      currency: "CZK",
      paidBy: "mem-a",
      splitAmong: ["mem-a", "mem-b"],
      splitType: { equal: {} },
      date: "2026-06-27T12:00:00Z",
      createdAt: "2026-06-27T12:00:00Z",
      iconSymbol: "fork.knife",
    };

    expect(expenseToWire(expense, "client-1")).toEqual({
      clientId: "client-1",
      title: "Dinner",
      amount: "840",
      currency: "CZK",
      paidBy: "mem-a",
      date: "2026-06-27",
      split: { type: "equal", among: ["mem-a", "mem-b"] },
      iconSymbol: "fork.knife",
    });
  });
});

describe("toWireDate", () => {
  it("reduces a timestamp to a calendar day", () => {
    expect(toWireDate("2026-06-27T12:00:00Z")).toBe("2026-06-27");
  });

  it("passes through a value that is already date-only", () => {
    expect(toWireDate("2026-06-27")).toBe("2026-06-27");
  });
});
