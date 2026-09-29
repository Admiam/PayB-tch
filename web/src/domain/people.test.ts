/**
 * The clustering rules that decide whose money gets added to whose.
 *
 * Getting this wrong is worse than a wrong sum: merging two people invents a
 * debt between strangers, and splitting one person hides a debt that is real.
 * So the same-group rule and the "the server's pairings are not yours to undo"
 * rule are both pinned here.
 */

import { describe, expect, it } from "vitest";
import {
  buildPeople,
  canMerge,
  normalizeName,
  suggestMerges,
  type RosterMember,
} from "./people";
import type { SharedPerson } from "@/api/endpoints";

function member(
  id: string,
  groupId: string,
  name: string,
  over: Partial<RosterMember> = {},
): RosterMember {
  return { id, groupId, name, linkKey: null, isMe: false, ...over };
}

/** Sorted member ids per person, so assertions don't depend on cluster order. */
function clustersOf(roster: RosterMember[], stored: SharedPerson[] = []): string[][] {
  return buildPeople(roster, stored)
    .map((person) => [...person.memberIds].sort())
    .sort((a, b) => a[0].localeCompare(b[0]));
}

describe("buildPeople", () => {
  it("leaves everyone alone when there is nothing to pair on", () => {
    const roster = [
      member("a1", "g1", "Adam"),
      member("b1", "g1", "Bob"),
      member("a2", "g2", "Adam"),
    ];

    // Two Adams with no account and no stored pairing are two people. Guessing
    // from the name here is exactly the mistake the feature exists to avoid.
    expect(clustersOf(roster)).toEqual([["a1"], ["a2"], ["b1"]]);
  });

  it("pairs rows the server says are one account", () => {
    const roster = [
      member("a1", "g1", "Adam", { linkKey: "k-adam" }),
      member("a2", "g2", "A. Míka", { linkKey: "k-adam" }),
      member("b1", "g1", "Bob", { linkKey: "k-bob" }),
    ];

    expect(clustersOf(roster)).toEqual([["a1", "a2"], ["b1"]]);
  });

  it("marks account pairings as the server's, not the user's", () => {
    const roster = [
      member("a1", "g1", "Adam", { linkKey: "k" }),
      member("a2", "g2", "Adam", { linkKey: "k" }),
    ];

    const [person] = buildPeople(roster, []);
    expect(person.autoMemberIds.sort()).toEqual(["a1", "a2"]);
  });

  it("applies a stored pairing between two ghosts", () => {
    const roster = [member("p1", "g1", "Petr"), member("p2", "g2", "Péťa")];
    const stored = [{ id: "person-1", memberIds: ["p1", "p2"] }];

    expect(clustersOf(roster, stored)).toEqual([["p1", "p2"]]);
    expect(buildPeople(roster, stored)[0].autoMemberIds).toEqual([]);
  });

  it("identifies a person by their first row, not by the stored pairing", () => {
    const roster = [member("p1", "g1", "Petr"), member("p2", "g2", "Petr")];
    const stored = [{ id: "person-1", memberIds: ["p1", "p2"] }];

    // A member id, so the avatar colour matches the one inside the group.
    expect(buildPeople(roster, stored)[0].id).toBe("p1");
  });

  it("carries a stored pairing through an account pairing", () => {
    // Petr has an account in g1 and g3, and is a bare placeholder in g2. One
    // manual link should pull all three together.
    const roster = [
      member("p1", "g1", "Petr", { linkKey: "k-petr" }),
      member("p2", "g2", "Péťa"),
      member("p3", "g3", "Petr", { linkKey: "k-petr" }),
    ];
    const stored = [{ id: "person-1", memberIds: ["p1", "p2"] }];

    expect(clustersOf(roster, stored)).toEqual([["p1", "p2", "p3"]]);
  });

  it("refuses a stored pairing that would put one group's two rows in one person", () => {
    const roster = [
      member("a1", "g1", "Adam"),
      member("a2", "g1", "Adam junior"),
      member("a3", "g2", "Adam"),
    ];
    // Stale or hand-edited data: the server rejects this on write, but a client
    // that trusted it would invent a debt between two real housemates.
    const stored = [{ id: "person-1", memberIds: ["a1", "a2"] }];

    expect(clustersOf(roster, stored)).toEqual([["a1"], ["a2"], ["a3"]]);
  });

  it("ignores stored members that are not in the pooled rosters", () => {
    const roster = [member("p1", "g1", "Petr")];
    const stored = [{ id: "person-1", memberIds: ["p1", "p-gone"] }];

    expect(clustersOf(roster, stored)).toEqual([["p1"]]);
  });

  it("calls the account holder Me wherever their row is named otherwise", () => {
    const roster = [
      member("me1", "g1", "Adam", { linkKey: "k-me", isMe: true }),
      member("me2", "g2", "AM", { linkKey: "k-me", isMe: true }),
    ];

    const [person] = buildPeople(roster, []);
    expect(person.name).toBe("Me");
    expect(person.isMe).toBe(true);
  });

  it("leads with the commonest name and keeps the rest as aliases", () => {
    const roster = [
      member("p1", "g1", "Petr"),
      member("p2", "g2", "Péťa"),
      member("p3", "g3", "Petr"),
    ];
    const stored = [{ id: "person-1", memberIds: ["p1", "p2", "p3"] }];

    const [person] = buildPeople(roster, stored);
    expect(person.name).toBe("Petr");
    expect(person.aliases).toEqual(["Péťa"]);
  });

  it("orders a person's members by the order the groups were given", () => {
    const roster = [member("p2", "g2", "Petr"), member("p1", "g1", "Petr")];
    const stored = [{ id: "person-1", memberIds: ["p1", "p2"] }];

    expect(buildPeople(roster, stored)[0].memberIds).toEqual(["p2", "p1"]);
  });
});

describe("canMerge", () => {
  const roster = [
    member("a1", "g1", "Adam"),
    member("b1", "g1", "Bob"),
    member("a2", "g2", "Adam"),
  ];
  const people = buildPeople(roster, []);
  const person = (memberId: string) =>
    people.find((p) => p.memberIds.includes(memberId))!;

  it("allows two people from different groups", () => {
    expect(canMerge(person("a1"), person("a2"), roster)).toBe(true);
  });

  it("refuses two people from the same group", () => {
    expect(canMerge(person("a1"), person("b1"), roster)).toBe(false);
  });

  it("refuses merging a person with itself", () => {
    expect(canMerge(person("a1"), person("a1"), roster)).toBe(false);
  });
});

describe("normalizeName", () => {
  it("folds case and diacritics so Péťa matches Peta", () => {
    expect(normalizeName("  Péťa ")).toBe(normalizeName("Peta"));
  });

  it("keeps genuinely different names apart", () => {
    expect(normalizeName("Petr")).not.toBe(normalizeName("Peter"));
  });
});

describe("suggestMerges", () => {
  it("suggests same-name rows across groups", () => {
    const roster = [member("p1", "g1", "Petr"), member("p2", "g2", "Péťa")];
    // Not automatic — "Péťa" and "Petr" normalise differently, so this pair is
    // left for a human. Same spelling is what gets offered.
    expect(suggestMerges(buildPeople(roster, []), roster)).toEqual([]);

    const same = [member("p1", "g1", "Petr"), member("p2", "g2", " petr ")];
    expect(suggestMerges(buildPeople(same, []), same)).toEqual([
      { from: "p2", to: "p1" },
    ]);
  });

  it("never suggests a pair from the same group", () => {
    const roster = [member("a1", "g1", "Adam"), member("a2", "g1", "Adam")];
    expect(suggestMerges(buildPeople(roster, []), roster)).toEqual([]);
  });

  it("stops at the first legal match when three groups share a name", () => {
    const roster = [
      member("a1", "g1", "Adam"),
      member("a2", "g2", "Adam"),
      member("a3", "g3", "Adam"),
    ];
    expect(suggestMerges(buildPeople(roster, []), roster)).toEqual([
      { from: "a2", to: "a1" },
      { from: "a3", to: "a1" },
    ]);
  });

  it("leaves the account holder out — the server already settled that", () => {
    const roster = [
      member("me1", "g1", "Adam", { linkKey: "k-me", isMe: true }),
      member("me2", "g2", "Adam", { linkKey: "k-me", isMe: true }),
      member("x1", "g3", "Adam"),
    ];
    expect(suggestMerges(buildPeople(roster, []), roster)).toEqual([]);
  });

  it("leaves a person whose rows disagree about their name alone", () => {
    const roster = [
      member("p1", "g1", "Petr"),
      member("p2", "g2", "Péťa"),
      member("p3", "g3", "Petr"),
    ];
    const stored = [{ id: "person-1", memberIds: ["p1", "p2"] }];

    // The merged person answers to two names, so "same name" is no longer a
    // safe enough signal to act on without being asked.
    expect(suggestMerges(buildPeople(roster, stored), roster)).toEqual([]);
  });
});
