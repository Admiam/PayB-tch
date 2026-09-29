/**
 * Everything the shared-debt screens read, assembled in one place.
 *
 * Both the dashboard section and the setup sheet need the same derived picture —
 * the pooled rosters, the people behind them, the combined nets — and the one
 * thing worse than computing it twice is computing it twice slightly
 * differently. So it is derived once here, from the two stores, and memoised on
 * exactly what it depends on.
 */

import { useEffect, useMemo, useRef } from "react";
import type { GroupWithMeta } from "@/api/endpoints";
import { buildPeople, type Person, type RosterMember } from "@/domain/people";
import {
  hasConversions,
  sharedCurrency,
  sharedDebts,
  type PersonBalance,
  type SharedDebtEdge,
} from "@/domain/sharedLedger";
import type { CurrencyCode, Member } from "@/domain/types";
import { useSharedLedger } from "@/store/useSharedLedger";
import { useStore } from "@/store/useStore";

export interface SharedDebtView {
  /**
   * The config *and* every pooled roster are in hand.
   *
   * Both matter: without the config there is no pooling to show, and without the
   * rosters there are no link keys — so people who are in fact one account would
   * render for a frame as separate, unpaired strangers with a debt between them.
   */
  ready: boolean;
  /** Every group the user could pool. */
  allGroups: GroupWithMeta[];
  /** The pooled ones, in the user's own group order. */
  includedGroups: GroupWithMeta[];
  /** The pooled groups' rosters, flattened in that same order. */
  roster: RosterMember[];
  people: Person[];
  /** memberId → person id, the key the money is aggregated by. */
  personOfMember: Map<string, string>;
  currency: CurrencyCode;
  nets: PersonBalance[];
  edges: SharedDebtEdge[];
  /** Some pooled balance was converted, so the totals are rate-dependent. */
  converted: boolean;
  /** The signed-in user's combined position, positive when they are owed. */
  myNet: number;
  myPersonId: string | null;
}

export function useSharedDebt(): SharedDebtView {
  const groups = useStore((s) => s.groups);
  const rosters = useStore((s) => s.rosters);

  const ready = useSharedLedger((s) => s.loaded);
  const groupIds = useSharedLedger((s) => s.groupIds);
  const storedPeople = useSharedLedger((s) => s.people);
  const sheets = useSharedLedger((s) => s.sheets);

  return useMemo(() => {
    // The user's own ordering wins over whatever order the ids came back in:
    // it decides which of a person's names leads and which face represents them.
    const includedGroups = groups.filter((group) => groupIds.includes(group.id));

    const roster: RosterMember[] = includedGroups.flatMap((group) =>
      (rosters[group.id] ?? []).map((member) => ({
        id: member.id,
        groupId: group.id,
        name: member.name,
        iconSymbol: member.iconSymbol ?? null,
        linkKey: member.linkKey,
        isMe: member.isMe,
      })),
    );

    const people = buildPeople(roster, storedPeople);
    const personOfMember = new Map<string, string>();
    for (const person of people) {
      for (const memberId of person.memberIds) personOfMember.set(memberId, person.id);
    }

    const currency = sharedCurrency(includedGroups);
    const pooled = includedGroups
      .map((group) => sheets[group.id])
      .filter((sheet) => sheet !== undefined);

    const { nets, edges } = sharedDebts(pooled, personOfMember, currency);
    const mine = people.find((person) => person.isMe) ?? null;

    return {
      ready:
        ready &&
        includedGroups.every((group) => rosters[group.id] !== undefined),
      allGroups: groups,
      includedGroups,
      roster,
      people,
      personOfMember,
      currency,
      nets,
      edges,
      converted: hasConversions(pooled, currency),
      myNet: mine
        ? (nets.find((net) => net.memberId === mine.id)?.net ?? 0)
        : 0,
      myPersonId: mine?.id ?? null,
    };
  }, [groups, rosters, ready, groupIds, storedPeople, sheets]);
}

/**
 * Re-reads the pooled balance sheets whenever this client writes an expense.
 *
 * The sheets come from the server, so nothing local invalidates them — and the
 * shared total is the one number on screen that an expense in *another* group
 * can change. Keyed on the store's write counter rather than on anything the
 * user navigates, so opening a group does not re-fetch every pooled group; and
 * the first run is skipped because loading the config already fetched them.
 */
export function useSharedBalanceSync(): void {
  const revision = useStore((s) => s.ledgerRevision);
  const refreshBalances = useSharedLedger((s) => s.refreshBalances);
  const handled = useRef<number | null>(null);

  useEffect(() => {
    if (handled.current === revision) return;
    const first = handled.current === null;
    handled.current = revision;
    if (!first) void refreshBalances();
  }, [revision, refreshBalances]);
}

/**
 * A person as the avatar and the flow canvas want them: a `Member`.
 *
 * The id is the person's, which is a member id by construction — so the colour
 * a face gets here is the colour that member already has inside their group,
 * and the edges (keyed by person) and the avatars agree without a lookup table.
 */
export function personFace(person: Person): Member {
  return { id: person.id, name: person.name, iconSymbol: person.iconSymbol };
}
