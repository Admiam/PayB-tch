/**
 * Ported 1:1 from `PaybitchTests/SplitTypeTests.swift`, plus cases covering the
 * banker's-rounding and formatting rules that the Swift pins elsewhere.
 *
 * These exist to prove the TypeScript agrees with the Swift on money. If one of
 * them fails, the web app and the phone app disagree about who owes what.
 */

import { describe, expect, it } from "vitest";
import {
  amountString,
  computeShares,
  formatAmountFull,
  fxConvert,
  parseAmount,
  roundBankers,
} from "./money";
import { SPLIT_EQUAL, type SplitType } from "./types";

const exact = (m: Record<string, number>): SplitType => ({ exact: m });
const shares = (m: Record<string, number>): SplitType => ({ shares: m });

describe("computeShares — equal", () => {
  it("divides evenly when divisible", () => {
    expect(computeShares(SPLIT_EQUAL, 90, ["a", "b", "c"], 2)).toEqual({
      a: 30,
      b: 30,
      c: 30,
    });
  });

  it("loses no cent — the last member absorbs the remainder", () => {
    const result = computeShares(SPLIT_EQUAL, 100, ["a", "b", "c"], 2);
    expect(result.a).toBe(33.33);
    expect(result.b).toBe(33.33);
    expect(result.c).toBe(33.34);
    expect(result.a + result.b + result.c).toBeCloseTo(100, 10);
  });

  it("returns nothing for an empty member list", () => {
    expect(computeShares(SPLIT_EQUAL, 100, [], 2)).toEqual({});
  });

  it("rounds to whole units for a zero-decimal currency", () => {
    // CZK has scale 0, so 100 across 3 people is 33/33/34 — never 33.33.
    const result = computeShares(SPLIT_EQUAL, 100, ["a", "b", "c"], 0);
    expect(result).toEqual({ a: 33, b: 33, c: 34 });
  });
});

describe("computeShares — exact", () => {
  it("returns the provided amounts untouched", () => {
    const result = computeShares(exact({ a: 70, b: 30 }), 100, ["a", "b"], 2);
    expect(result).toEqual({ a: 70, b: 30 });
  });

  it("returns 0 for a member with no entry", () => {
    const result = computeShares(exact({ a: 100 }), 100, ["a", "b"], 2);
    expect(result).toEqual({ a: 100, b: 0 });
  });
});

describe("computeShares — shares", () => {
  it("divides by weight", () => {
    const result = computeShares(
      shares({ a: 1, b: 1, c: 2 }),
      100,
      ["a", "b", "c"],
      2,
    );
    expect(result).toEqual({ a: 25, b: 25, c: 50 });
  });

  it("returns nothing when every weight is zero", () => {
    expect(computeShares(shares({ a: 0, b: 0 }), 100, ["a", "b"], 2)).toEqual({});
  });

  it("gives a zero-weight member nothing", () => {
    const result = computeShares(
      shares({ a: 1, b: 0, c: 1 }),
      100,
      ["a", "b", "c"],
      2,
    );
    expect(result).toEqual({ a: 50, b: 0, c: 50 });
  });

  it("loses no cent when weights divide unevenly", () => {
    const result = computeShares(
      shares({ a: 1, b: 1, c: 1 }),
      100,
      ["a", "b", "c"],
      2,
    );
    const sum = Object.values(result).reduce((x, y) => x + y, 0);
    expect(sum).toBeCloseTo(100, 10);
  });
});

describe("roundBankers", () => {
  it("rounds a tie to the even neighbour", () => {
    expect(roundBankers(0.125, 2)).toBe(0.12);
    expect(roundBankers(0.135, 2)).toBe(0.14);
    expect(roundBankers(2.5, 0)).toBe(2);
    expect(roundBankers(3.5, 0)).toBe(4);
  });

  it("rounds ties to even for negatives too", () => {
    expect(roundBankers(-2.5, 0)).toBe(-2);
    expect(roundBankers(-3.5, 0)).toBe(-4);
  });

  it("rounds normally when it is not a tie", () => {
    expect(roundBankers(1.234, 2)).toBe(1.23);
    expect(roundBankers(1.236, 2)).toBe(1.24);
  });
});

describe("formatting", () => {
  it("abbreviates millions with one decimal", () => {
    expect(amountString(1_200_000, "CZK")).toBe("1.2M");
  });

  it("abbreviates from ten thousand, not one thousand", () => {
    expect(amountString(10_000, "CZK")).toBe("10k");
    // Four figures stay in full — the threshold really is 10k.
    expect(amountString(9_999, "CZK")).toBe("9,999");
  });

  it("respects the currency's decimal places", () => {
    expect(formatAmountFull(100, "CZK")).toBe("100 Kč");
    expect(formatAmountFull(100, "EUR")).toBe("100.00 €");
  });
});

describe("parseAmount", () => {
  it("accepts both decimal separators", () => {
    expect(parseAmount("12.50")).toBe(12.5);
    expect(parseAmount("12,50")).toBe(12.5);
  });

  it("rejects junk", () => {
    expect(parseAmount("")).toBeNull();
    expect(parseAmount("abc")).toBeNull();
  });
});

describe("fxConvert", () => {
  it("is identity for the same currency", () => {
    expect(fxConvert(100, "CZK", "CZK")).toBe(100);
  });

  it("converts through the CZK cross rate", () => {
    // 1 EUR = 25.20 CZK
    expect(fxConvert(1, "EUR", "CZK")).toBeCloseTo(25.2, 10);
    expect(fxConvert(25.2, "CZK", "EUR")).toBeCloseTo(1, 10);
  });
});
