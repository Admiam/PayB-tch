/**
 * Cross-group identity: turning many participant rows into the people behind
 * them.
 *
 * A member only exists inside a group, so the same human in three groups is
 * three rows with three ids and three balances that no amount of arithmetic can
 * add together. This module is where they become one person again.
 *
 * Two sources, in this order of authority:
 *
 *  1. **`linkKey`** — the server's own answer. Rows sharing one are the same
 *     account, full stop, so these pairings are not a human's to undo.
 *  2. **Stored links** — the user's own answer, for everyone the server cannot
 *     speak for: ghosts have no account, so nothing but a person can say that
 *     the "Petr" typed into one group is the "Péťa" typed into another.
 *
 * One rule governs both: two members of the *same* group are never the same
 * person. That is not a heuristic — a group holds one row per participant, so
 * two rows in it are two participants by construction.
 */

import type { SharedPerson } from "@/api/endpoints";

/** A participant row, flattened out of its group's roster. */
export interface RosterMember {
  id: string;
  groupId: string;
  name: string;
  iconSymbol?: string | null;
  /** The server's cross-group handle, or null for a ghost. */
  linkKey: string | null;
  /** This row belongs to the signed-in account. */
  isMe: boolean;
}

/** One human, as gathered from the rows that represent them. */
export interface Person {
  /**
   * The first member row, in group order — which doubles as the person's
   * identity everywhere on screen.
   *
   * Deliberately a member id rather than the stored pairing's uuid: the avatar
   * colour is a hash of whatever id it is handed, so using a member id is what
   * makes a face in the combined view the same face as inside the group. The
   * stored uuid is the persistence layer's business and is carried by the store,
   * not by the thing being drawn.
   */
  id: string;
  /** Member rows, in the order their groups are listed. */
  memberIds: string[];
  /** One of these rows is the signed-in account's own. */
  isMe: boolean;
  /**
   * Rows the server paired by account. A human may not unpair these — there is
   * nothing to disagree with, they are literally one login.
   */
  autoMemberIds: string[];
  /** What to call them: "Me" for the account holder, else their commonest name. */
  name: string;
  /** The other names their rows go by, so a merge is legible rather than lossy. */
  aliases: string[];
  /** The first glyph any of their rows chose, so a merge keeps a picked avatar. */
  iconSymbol: string | null;
}

/** The display name reserved for the signed-in account, matching the phone app. */
const ME = "Me";

/* ─────────────────────────────────────────────────────────── clustering ── */

/**
 * Groups `roster` into people.
 *
 * `roster` must already be flattened in the order groups should be read in;
 * that order is what decides a person's primary name and face. Stored links
 * naming members outside `roster` are ignored rather than dropped — a group
 * toggled out of the shared view should not lose its pairings.
 */
export function buildPeople(
  roster: RosterMember[],
  stored: readonly SharedPerson[],
): Person[] {
  const byId = new Map(roster.map((member) => [member.id, member]));
  const parent = new Map<string, string>(roster.map((m) => [m.id, m.id]));

  const find = (id: string): string => {
    let root = id;
    while (parent.get(root) !== root) root = parent.get(root) ?? root;
    // Path compression, so a long merge chain doesn't make every later lookup
    // walk it again.
    let cursor = id;
    while (parent.get(cursor) !== root) {
      const next = parent.get(cursor) ?? root;
      parent.set(cursor, root);
      cursor = next;
    }
    return root;
  };

  /** Members already gathered under each root, so the same-group rule is checkable. */
  const groupsOfRoot = new Map<string, Set<string>>(
    roster.map((m) => [m.id, new Set([m.groupId])]),
  );

  const union = (a: string, b: string): void => {
    const rootA = find(a);
    const rootB = find(b);
    if (rootA === rootB) return;

    const groupsA = groupsOfRoot.get(rootA) ?? new Set<string>();
    const groupsB = groupsOfRoot.get(rootB) ?? new Set<string>();
    // Refuse rather than throw: stored links are validated server-side and
    // link keys cannot collide inside a group, so reaching here means the data
    // is stale in a way that must not take the whole screen down with it.
    for (const groupId of groupsB) if (groupsA.has(groupId)) return;

    parent.set(rootB, rootA);
    for (const groupId of groupsB) groupsA.add(groupId);
    groupsOfRoot.set(rootA, groupsA);
    groupsOfRoot.delete(rootB);
  };

  // 1. The server's pairings.
  const byLinkKey = new Map<string, string[]>();
  for (const member of roster) {
    if (!member.linkKey) continue;
    const existing = byLinkKey.get(member.linkKey);
    if (existing) existing.push(member.id);
    else byLinkKey.set(member.linkKey, [member.id]);
  }
  const auto = new Set<string>();
  for (const ids of byLinkKey.values()) {
    if (ids.length < 2) continue;
    for (const id of ids) auto.add(id);
    for (let i = 1; i < ids.length; i += 1) union(ids[0], ids[i]);
  }

  // 2. The user's own.
  for (const person of stored) {
    const present = person.memberIds.filter((id) => byId.has(id));
    for (let i = 1; i < present.length; i += 1) union(present[0], present[i]);
  }

  // Bucket in roster order, so both the clusters and the members inside them
  // come out in the order the groups were listed.
  const clusters = new Map<string, string[]>();
  for (const member of roster) {
    const root = find(member.id);
    const bucket = clusters.get(root);
    if (bucket) bucket.push(member.id);
    else clusters.set(root, [member.id]);
  }

  return [...clusters.values()].map((memberIds) => {
    const members = memberIds.map((id) => byId.get(id)!);
    const isMe = members.some((member) => member.isMe);
    const { name, aliases } = resolveNames(members, isMe);

    return {
      id: memberIds[0],
      memberIds,
      isMe,
      autoMemberIds: memberIds.filter((id) => auto.has(id)),
      name,
      aliases,
      iconSymbol: members.find((member) => member.iconSymbol)?.iconSymbol ?? null,
    };
  });
}

/**
 * The name to lead with, plus the others.
 *
 * Commonest wins: three groups calling someone "Petr" and one calling them
 * "Péťa" should read as Petr. Ties fall to roster order, which makes the result
 * depend only on how the groups are listed rather than on iteration order.
 */
function resolveNames(
  members: readonly RosterMember[],
  isMe: boolean,
): { name: string; aliases: string[] } {
  const distinct: string[] = [];
  const counts = new Map<string, number>();

  for (const member of members) {
    const name = member.name.trim();
    if (!name) continue;
    if (!counts.has(name)) distinct.push(name);
    counts.set(name, (counts.get(name) ?? 0) + 1);
  }

  if (distinct.length === 0) return { name: isMe ? ME : "—", aliases: [] };

  const primary = distinct.reduce((best, candidate) =>
    (counts.get(candidate) ?? 0) > (counts.get(best) ?? 0) ? candidate : best,
  );

  return {
    // The account holder is "Me" everywhere in the app; their own rows may be
    // named anything, and the group's name for them is not what they call
    // themselves.
    name: isMe ? ME : primary,
    aliases: distinct.filter((candidate) => candidate !== primary),
  };
}

/* ───────────────────────────────────────────────────────────── matching ── */

/**
 * Comparison form for a name: case-folded and stripped of diacritics, so
 * "Péťa" and "Peta" are the same candidate. Used only to *suggest* a pairing —
 * never to make one silently, because two different people sharing a first
 * name is completely ordinary.
 */
export function normalizeName(name: string): string {
  return name
    .trim()
    .toLowerCase()
    .normalize("NFD")
    .replace(/\p{Diacritic}/gu, "");
}

/** Whether these two people could be merged at all — see the same-group rule. */
export function canMerge(a: Person, b: Person, roster: RosterMember[]): boolean {
  if (a.id === b.id) return false;
  const groupOf = new Map(roster.map((m) => [m.id, m.groupId]));
  const groupsOfA = new Set(a.memberIds.map((id) => groupOf.get(id)));
  return !b.memberIds.some((id) => groupsOfA.has(groupOf.get(id)));
}

/**
 * Pairs that share a name and could legally merge, best first.
 *
 * Offered as one bulk action because the alternative — pairing a dozen rows by
 * hand before the feature shows a single number — is where people give up.
 * Exact-name matches only: a suggestion a user has to double-check is worth
 * less than no suggestion.
 */
export function suggestMerges(
  people: Person[],
  roster: RosterMember[],
): { from: string; to: string }[] {
  const byName = new Map<string, Person[]>();
  for (const person of people) {
    // "Me" is already settled by the server; and a person whose rows disagree
    // about their name is not a safe automatic match.
    if (person.isMe || person.aliases.length > 0) continue;
    const key = normalizeName(person.name);
    if (!key) continue;
    const bucket = byName.get(key);
    if (bucket) bucket.push(person);
    else byName.set(key, [person]);
  }

  const merges: { from: string; to: string }[] = [];
  for (const bucket of byName.values()) {
    if (bucket.length < 2) continue;

    // Fold each candidate into the first one it can legally join, tracking the
    // groups taken so a three-way name clash doesn't produce a merge that the
    // same-group rule would then refuse.
    const groupOf = new Map(roster.map((m) => [m.id, m.groupId]));
    const anchor = bucket[0];
    const taken = new Set(anchor.memberIds.map((id) => groupOf.get(id)));

    for (const candidate of bucket.slice(1)) {
      const groups = candidate.memberIds.map((id) => groupOf.get(id));
      if (groups.some((groupId) => taken.has(groupId))) continue;
      for (const groupId of groups) taken.add(groupId);
      merges.push({ from: candidate.memberIds[0], to: anchor.memberIds[0] });
    }
  }

  return merges;
}
