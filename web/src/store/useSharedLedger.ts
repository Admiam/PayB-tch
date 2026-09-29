/**
 * The shared ledger's own store: which groups are pooled, who is who across
 * them, and the per-group balance sheets the combined numbers are built from.
 *
 * Separate from `useStore` for the same reason `useAuth` is — a different
 * lifecycle. `useStore` holds one open group; this holds a little bit of every
 * group at once, and it survives switching between them.
 *
 * The configuration is server-held, so a pairing taught to one browser is known
 * to the next. Every edit saves immediately: this is a settings surface where a
 * half-applied state has no meaning, and a Save button people forget to press
 * would silently un-teach it.
 */

import { create } from "zustand";
import { ApiError } from "@/api/client";
import {
  balances as balancesApi,
  sharedLedger as configApi,
  type GroupBalanceSheet,
  type SharedLedgerConfig,
  type SharedPerson,
} from "@/api/endpoints";
import { messageFor } from "@/lib/errors";
import { useAuth } from "./useAuth";

interface State {
  /** False until the server has been asked once — nothing should render before. */
  loaded: boolean;
  /** Content hash of the stored config, sent back as the write precondition. */
  version: number;
  /** Groups the user has pooled into the combined view. */
  groupIds: string[];
  /** Manual pairings only; the ones the server can infer are never stored. */
  people: SharedPerson[];
  /** Authoritative balance sheets, by group id. */
  sheets: Record<string, GroupBalanceSheet>;
  loading: boolean;
  saving: boolean;
  error: string | null;
}

interface Actions {
  /** Reads the config, then the balance sheets for whatever it pooled. */
  load: () => Promise<void>;
  /** Re-reads the pooled groups' balance sheets. Cheap; no config round trip. */
  refreshBalances: () => Promise<void>;
  setIncluded: (groupId: string, included: boolean) => Promise<void>;
  /** Declares two member rows to be the same person. */
  link: (memberId: string, otherMemberId: string) => Promise<void>;
  /** Takes one member row back out of its person. */
  unlink: (memberId: string) => Promise<void>;
  /** Applies a batch of pairings in one save — see `suggestMerges`. */
  linkAll: (pairs: readonly { from: string; to: string }[]) => Promise<void>;
  clearError: () => void;
}

export type SharedLedgerStore = State & Actions;

const initialState: State = {
  loaded: false,
  version: 0,
  groupIds: [],
  people: [],
  sheets: {},
  loading: false,
  saving: false,
  error: null,
};

const CONFLICT =
  "Your shared-debt setup changed on another device, so this change was not applied. What you see now is the newer version — redo it if you still want it.";

export const useSharedLedger = create<SharedLedgerStore>((set, get) => {
  /** Fetches the given groups' balance sheets, tolerating individual failures. */
  const fetchSheets = async (
    groupIds: readonly string[],
  ): Promise<Record<string, GroupBalanceSheet>> => {
    const settled = await Promise.allSettled(
      groupIds.map((groupId) => balancesApi.get(groupId)),
    );

    // One unreachable group should not blank the whole view. It contributes
    // nothing and the total is then visibly short, which beats showing nothing.
    const sheets: Record<string, GroupBalanceSheet> = {};
    for (const result of settled) {
      if (result.status === "fulfilled") sheets[result.value.groupId] = result.value;
    }
    return sheets;
  };

/**
   * Adopts a saved config, fetching only the balance sheets it newly needs.
   *
   * Pairing two people changes who owes whom but not what any group's ledger
   * says, so re-reading every pooled group on every pairing tap would be pure
   * amplification. Sheets for groups that left the pool are dropped, so a group
   * toggled off stops contributing immediately rather than lingering.
   */
  const adopt = async (config: SharedLedgerConfig): Promise<void> => {
    set({
      version: config.version,
      groupIds: config.groupIds,
      people: config.people,
      loaded: true,
    });

    const held = get().sheets;
    const missing = config.groupIds.filter((id) => held[id] === undefined);
    const fetched = missing.length > 0 ? await fetchSheets(missing) : {};

    const sheets: Record<string, GroupBalanceSheet> = {};
    for (const groupId of config.groupIds) {
      const sheet = held[groupId] ?? fetched[groupId];
      if (sheet) sheets[groupId] = sheet;
    }
    set({ sheets });
  };

  /**
   * Writes a changed config, rolling back to what was on screen if it is
   * refused. The optimistic update is what makes a toggle feel like a toggle;
   * the rollback is what stops it from lying when the save fails.
   */
  const commit = async (next: Pick<State, "groupIds" | "people">): Promise<void> => {
    const previous = {
      groupIds: get().groupIds,
      people: get().people,
      sheets: get().sheets,
    };

    set({ ...next, saving: true, error: null });

    try {
      const saved = await configApi.save({ ...next, version: get().version });
      await adopt(saved);
      set({ saving: false });
    } catch (error) {
      const current = conflictingConfig(error);
      if (current) {
        await adopt(current);
        set({ saving: false, error: CONFLICT });
        return;
      }
      set({
        ...previous,
        saving: false,
        error: messageFor(error, "Couldn't save your shared-debt setup."),
      });
    }
  };

  return {
    ...initialState,

    load: async () => {
      set({ loading: true, error: null });
      try {
        await adopt(await configApi.get());
        set({ loading: false });
      } catch (error) {
        set({
          loading: false,
          // Still "loaded": the screen has its answer, which is that there is
          // nothing to show. Leaving it false would spin forever.
          loaded: true,
          error: messageFor(error, "Couldn't load your shared-debt setup."),
        });
      }
    },

    refreshBalances: async () => {
      const groupIds = get().groupIds;
      if (groupIds.length === 0) {
        set({ sheets: {} });
        return;
      }
      set({ sheets: await fetchSheets(groupIds) });
    },

    setIncluded: async (groupId, included) => {
      const groupIds = get().groupIds;
      if (included === groupIds.includes(groupId)) return;

      await commit({
        groupIds: included
          ? [...groupIds, groupId]
          : groupIds.filter((id) => id !== groupId),
        // Pairings survive a group leaving the view. Toggling a trip off for a
        // month should not make you re-teach who everybody was.
        people: get().people,
      });
    },

    link: async (memberId, otherMemberId) => {
      const people = mergePeople(get().people, memberId, otherMemberId);
      if (people === get().people) return;
      await commit({ groupIds: get().groupIds, people });
    },

    unlink: async (memberId) => {
      const people = withoutMember(get().people, memberId);
      if (people === get().people) return;
      await commit({ groupIds: get().groupIds, people });
    },

    linkAll: async (pairs) => {
      if (pairs.length === 0) return;

      const people = pairs.reduce(
        (acc, pair) => mergePeople(acc, pair.from, pair.to),
        get().people,
      );
      if (people === get().people) return;
      await commit({ groupIds: get().groupIds, people });
    },

    clearError: () => set({ error: null }),
  };
});

/* ────────────────────────────────────────────────────── stored pairings ── */

/**
 * Folds the people holding these two members into one.
 *
 * Works on the *stored* pairings rather than on the clusters shown on screen,
 * which is what keeps the stored set minimal: a member the server already pairs
 * by account never needs a row here, and re-deriving the stored set from the
 * displayed clusters would write those rows anyway — then keep them merged long
 * after the account that justified it left the group.
 *
 * Returns the same array when there is nothing to do, so callers can skip a
 * pointless save by identity.
 */
function mergePeople(
  people: readonly SharedPerson[],
  memberId: string,
  otherMemberId: string,
): SharedPerson[] {
  if (memberId === otherMemberId) return people as SharedPerson[];

  const holding = (id: string) => people.find((p) => p.memberIds.includes(id));
  const first = holding(memberId);
  const second = holding(otherMemberId);
  if (first && second && first.id === second.id) return people as SharedPerson[];

  const memberIds = [
    ...new Set([
      ...(first?.memberIds ?? [memberId]),
      ...(second?.memberIds ?? [otherMemberId]),
    ]),
  ];

  return [
    ...people.filter((p) => p !== first && p !== second),
    // Reuse an existing id where there is one, so the pairing keeps its
    // identity across the save rather than looking like a brand-new person.
    { id: first?.id ?? second?.id ?? newPersonId(), memberIds },
  ];
}

/**
 * A pairing id the server will accept.
 *
 * Not `newId`: that one is deliberately lenient, because an expense's clientId
 * is an opaque string to the API and any unique value will do. A person id is
 * parsed as a UUID, so it has to be shaped like one even in a context where
 * `crypto.randomUUID` is missing.
 */
function newPersonId(): string {
  if (typeof crypto !== "undefined" && typeof crypto.randomUUID === "function") {
    return crypto.randomUUID();
  }

  const bytes = new Uint8Array(16);
  if (typeof crypto !== "undefined" && typeof crypto.getRandomValues === "function") {
    crypto.getRandomValues(bytes);
  } else {
    for (let i = 0; i < bytes.length; i += 1) bytes[i] = Math.floor(Math.random() * 256);
  }
  bytes[6] = (bytes[6] & 0x0f) | 0x40; // version 4
  bytes[8] = (bytes[8] & 0x3f) | 0x80; // RFC 4122 variant

  const hex = [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");
  return [
    hex.slice(0, 8),
    hex.slice(8, 12),
    hex.slice(12, 16),
    hex.slice(16, 20),
    hex.slice(20),
  ].join("-");
}

/** Drops one member from its pairing, retiring a pairing that is left alone. */
function withoutMember(
  people: readonly SharedPerson[],
  memberId: string,
): SharedPerson[] {
  const holding = people.find((p) => p.memberIds.includes(memberId));
  if (!holding) return people as SharedPerson[];

  const memberIds = holding.memberIds.filter((id) => id !== memberId);
  return [
    ...people.filter((p) => p !== holding),
    ...(memberIds.length >= 2 ? [{ ...holding, memberIds }] : []),
  ];
}

/** The server's copy out of a 412 body, or null for any other failure. */
function conflictingConfig(error: unknown): SharedLedgerConfig | null {
  if (!(error instanceof ApiError) || error.code !== "version_conflict") return null;

  const current = error.extensions.current;
  if (!current || typeof current !== "object") return null;
  return current as SharedLedgerConfig;
}

/* ────────────────────────────────────────────────────────────── session ── */

/**
 * Signing out has to clear this.
 *
 * The pairings are one account's private opinion about who other people are;
 * leaving them in memory would show them to whoever signs in next on this
 * device, before the first fetch could replace them.
 */
useAuth.subscribe((state, previous) => {
  if (previous.status === "signed-in" && state.status !== "signed-in") {
    useSharedLedger.setState(initialState);
  }
});
