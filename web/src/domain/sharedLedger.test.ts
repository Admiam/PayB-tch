/**
 * The combined ledger's arithmetic.
 *
 * The point of pooling is that two debts running in opposite directions cancel
 * instead of both being paid, so that is the first thing pinned here — followed
 * by the two ways a pooled total can go quietly wrong: a currency converted
 * without being flagged, and a member who silently drops out of the sum.
 */

import { describe, expect, it } from "vitest";
import {
  aggregatePersonNets,
  hasConversions,
  sharedCurrency,
  sharedDebts,
} from "./sharedLedger";
import type { GroupBalanceSheet } from "@/api/endpoints";
import type { CurrencyCode } from "./types";

function sheet(
  groupId: string,
  currency: CurrencyCode,
  nets: Record<string, number>,
): GroupBalanceSheet {
  return {
    groupId,
    buckets: [
      {
        currency,
        nets: Object.entries(nets).map(([memberId, net]) => ({ memberId, net })),
      },
    ],
  };
}

/** memberId → personId, the mapping `buildPeople` produces. */
function people(mapping: Record<string, string>): Map<string, string> {
  return new Map(Object.entries(mapping));
}

describe("aggregatePersonNets", () => {
  it("adds one person's positions across groups", () => {
    const sheets = [
      sheet("g1", "CZK", { "adam@g1": 500, "petr@g1": -500 }),
      sheet("g2", "CZK", { "adam@g2": -300, "petr@g2": 300 }),
    ];
    const mapping = people({
      "adam@g1": "adam",
      "adam@g2": "adam",
      "petr@g1": "petr",
      "petr@g2": "petr",
    });

    const nets = aggregatePersonNets(sheets, mapping, "CZK");

    expect(nets).toEqual([
      { memberId: "adam", net: 200 },
      { memberId: "petr", net: -200 },
    ]);
  });

  it("converts a group held in another currency", () => {
    const sheets = [
      sheet("g1", "CZK", { "adam@g1": 1000, "petr@g1": -1000 }),
      sheet("g2", "EUR", { "adam@g2": -100, "petr@g2": 100 }),
    ];
    const mapping = people({
      "adam@g1": "adam",
      "adam@g2": "adam",
      "petr@g1": "petr",
      "petr@g2": "petr",
    });

    // 100 EUR at the app's fixed 25.2 is 2520 Kč, so Adam swings to owing.
    expect(aggregatePersonNets(sheets, mapping, "CZK")).toEqual([
      { memberId: "adam", net: -1520 },
      { memberId: "petr", net: 1520 },
    ]);
  });

  it("rounds to the target currency's scale, so CZK stays whole", () => {
    const sheets = [sheet("g1", "EUR", { "a@g1": 1.01, "b@g1": -1.01 })];
    const mapping = people({ "a@g1": "a", "b@g1": "b" });

    const nets = aggregatePersonNets(sheets, mapping, "CZK");

    // 1.01 EUR is 25.452 Kč, and CZK carries no decimals.
    expect(nets).toEqual([
      { memberId: "a", net: 25 },
      { memberId: "b", net: -25 },
    ]);
  });

  it("keeps a member the roster does not account for as their own person", () => {
    const sheets = [sheet("g1", "CZK", { known: 400, stranger: -400 })];

    // Dropping the unmapped row would show 400 owed to somebody by nobody.
    expect(aggregatePersonNets(sheets, people({ known: "p-known" }), "CZK")).toEqual([
      { memberId: "p-known", net: 400 },
      { memberId: "stranger", net: -400 },
    ]);
  });

  it("sums several buckets inside one group", () => {
    const sheets: GroupBalanceSheet[] = [
      {
        groupId: "g1",
        buckets: [
          { currency: "CZK", nets: [{ memberId: "a@g1", net: 100 }] },
          { currency: "EUR", nets: [{ memberId: "a@g1", net: 10 }] },
        ],
      },
    ];

    expect(aggregatePersonNets(sheets, people({ "a@g1": "a" }), "CZK")).toEqual([
      { memberId: "a", net: 352 },
    ]);
  });

  it("has nothing to say about no groups", () => {
    expect(aggregatePersonNets([], people({}), "CZK")).toEqual([]);
  });
});

describe("sharedDebts", () => {
  it("collapses debts that run in opposite directions into one transfer", () => {
    const sheets = [
      sheet("g1", "CZK", { "adam@g1": 500, "petr@g1": -500 }),
      sheet("g2", "CZK", { "adam@g2": -300, "petr@g2": 300 }),
    ];
    const mapping = people({
      "adam@g1": "adam",
      "adam@g2": "adam",
      "petr@g1": "petr",
      "petr@g2": "petr",
    });

    const { edges } = sharedDebts(sheets, mapping, "CZK");

    expect(edges).toEqual([
      { id: "petr->adam", from: "petr", to: "adam", amount: 200 },
    ]);
  });

  it("finds two groups already square with each other", () => {
    const sheets = [
      sheet("g1", "CZK", { "adam@g1": 400, "petr@g1": -400 }),
      sheet("g2", "CZK", { "adam@g2": -400, "petr@g2": 400 }),
    ];
    const mapping = people({
      "adam@g1": "adam",
      "adam@g2": "adam",
      "petr@g1": "petr",
      "petr@g2": "petr",
    });

    expect(sharedDebts(sheets, mapping, "CZK").edges).toEqual([]);
  });

  it("keeps unpaired rows apart, so nothing cancels by accident", () => {
    const sheets = [
      sheet("g1", "CZK", { "adam@g1": 500, "petr@g1": -500 }),
      sheet("g2", "CZK", { "adam@g2": -300, "petr@g2": 300 }),
    ];
    // Nobody paired: four separate people, so both debts still stand.
    const mapping = people({});

    expect(sharedDebts(sheets, mapping, "CZK").edges).toHaveLength(2);
  });
});

describe("sharedCurrency", () => {
  const group = (id: string, defaultCurrency: string) => ({ id, defaultCurrency });

  it("takes the currency most of the pooled groups already use", () => {
    expect(
      sharedCurrency([
        group("g1", "EUR"),
        group("g2", "CZK"),
        group("g3", "CZK"),
      ]),
    ).toBe("CZK");
  });

  it("breaks a tie with the first group listed", () => {
    expect(sharedCurrency([group("g1", "EUR"), group("g2", "CZK")])).toBe("EUR");
  });

  it("falls back when nothing is pooled", () => {
    expect(sharedCurrency([])).toBe("CZK");
    expect(sharedCurrency([], "GBP")).toBe("GBP");
  });

  it("treats an unknown code the way the rest of the app does", () => {
    expect(sharedCurrency([group("g1", "XYZ")])).toBe("CZK");
  });
});

describe("hasConversions", () => {
  it("is true when a pooled group holds another currency", () => {
    const sheets = [
      sheet("g1", "CZK", { a: 100 }),
      sheet("g2", "EUR", { a: -4 }),
    ];
    expect(hasConversions(sheets, "CZK")).toBe(true);
  });

  it("is false when every group is settled in the shown currency", () => {
    expect(hasConversions([sheet("g1", "CZK", { a: 100 })], "CZK")).toBe(false);
  });

  it("ignores an empty bucket — a settled currency converts nothing", () => {
    const sheets: GroupBalanceSheet[] = [
      { groupId: "g1", buckets: [{ currency: "EUR", nets: [] }] },
    ];
    expect(hasConversions(sheets, "CZK")).toBe(false);
  });
});
