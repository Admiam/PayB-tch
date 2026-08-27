/**
 * The single application store, now backed by the Paybitch API.
 *
 * Screens still talk only to this — nothing reaches for the client directly —
 * but three things changed shape when the data moved off the device:
 *
 *  - **Everything is async.** Mutations resolve when the server has accepted
 *    them, and the object a create returns carries the *server's* id.
 *  - **`members` is one group's roster, not a global address book.** The API
 *    has no such thing as a member outside a group; the same person in two
 *    groups is two rows with two ids. `selectGroup` is what swaps the roster.
 *  - **`currentUserId` is a member id, not a user id.** Each group issues the
 *    caller their own participant row, so "am I this person?" can only be
 *    answered per group. It is resolved whenever a group loads.
 *
 * Nothing here throws. A failed call lands in `lastError` as a sentence, which
 * is what the screens already render.
 */

import { create } from "zustand";
import { api, ApiError } from "@/api/client";
import {
  expenses as expensesApi,
  groups as groupsApi,
  type GroupWithMeta,
  type MemberWithMeta,
} from "@/api/endpoints";
import { expenseFromWire, type WireExpense } from "@/api/wire";
import { messageFor } from "@/lib/errors";
import { newId, setExportSource, storage } from "@/lib/storage";
import {
  toCurrency,
  type Appearance,
  type Expense,
  type Group,
  type Member,
} from "@/domain/types";
import { useAuth } from "./useAuth";

interface State {
  /** Every group the caller belongs to. Carries `version` for the next write. */
  groups: GroupWithMeta[];
  /** The selected group's roster. Empty until a group is selected. */
  members: MemberWithMeta[];
  /** The selected group's expenses. */
  expenses: Expense[];
  /** The caller's *member id in the selected group*. */
  currentUserId: string | null;
  selectedGroupId: string | null;
  hasOnboarded: boolean;
  appearance: Appearance;
  /** True while any store call is in flight. */
  loading: boolean;
  /** Transient, surfaced as a dismissible banner. */
  lastError: string | null;
}

interface Actions {
  load: () => Promise<void>;
  selectGroup: (groupId: string | null) => Promise<void>;
  clearError: () => void;

  setCurrentUser: (id: string | null) => void;
  setAppearance: (a: Appearance) => void;
  completeOnboarding: () => void;

  addGroup: (input: Omit<Group, "id">) => Promise<GroupWithMeta | null>;
  updateGroup: (group: Group) => Promise<void>;
  deleteGroup: (id: string) => Promise<void>;

  addMember: (input: Omit<Member, "id">) => Promise<MemberWithMeta | null>;
  updateMember: (member: Member) => Promise<void>;
  deleteMember: (id: string) => Promise<void>;

  addExpense: (
    input: Omit<Expense, "id" | "createdAt">,
  ) => Promise<Expense | null>;
  updateExpense: (expense: Expense) => Promise<void>;
  deleteExpense: (id: string) => Promise<void>;

  reset: () => Promise<void>;
}

export type Store = State & Actions;

/** What the store hands its own helpers — zustand's `set`, narrowed. */
type SetState = (
  partial: Partial<State> | ((state: State) => Partial<State>),
) => void;

const initialState: State = {
  groups: [],
  members: [],
  expenses: [],
  currentUserId: null,
  selectedGroupId: null,
  hasOnboarded: false,
  // The one piece of state that is still local, so it is right from the first
  // frame rather than after a round trip.
  appearance: storage.loadAppearance(),
  loading: false,
  lastError: null,
};

const CONFLICT_ON_EDIT =
  "Someone changed this expense while you had it open. Your copy has been refreshed — reapply your edit if you still want it.";
const CONFLICT_ON_DELETE =
  "Someone changed this expense before your delete landed, so it was left alone. Check it and try again.";

/* ────────────────────────────────────────────────────── idempotency keys ── */

/**
 * The clientId of a create that has been attempted but not confirmed.
 *
 * Held at module scope, outside both React and the store's state, because it
 * has to outlive the action that minted it: when a save fails and the user
 * presses Save again, the second attempt must carry the *same* key. A fresh
 * one reads as a second, unrelated expense and charges the group twice. Keyed
 * by the content of the intent, so editing the draft correctly starts a new
 * one.
 */
const pendingCreateIds = new Map<string, string>();

/** The clientId each known expense was created with — see `clientIdFor`. */
const expenseClientIds = new Map<string, string>();

function intentKey(input: Omit<Expense, "id" | "createdAt">): string {
  return [
    input.groupId,
    input.title,
    input.amount,
    input.currency,
    input.paidBy,
    input.date,
    input.splitAmong.join(","),
    JSON.stringify(input.splitType),
  ].join("|");
}

/**
 * The clientId an expense was created with.
 *
 * `PUT` treats clientId as an immutable echo field: a body that disagrees with
 * the stored one is rejected outright. The domain `Expense` has no field for
 * it and the mapped read model drops it, so for anything this session did not
 * create it is read once off the raw wire and remembered.
 */
async function clientIdFor(groupId: string, expenseId: string): Promise<string> {
  const known = expenseClientIds.get(expenseId);
  if (known) return known;

  const wire = await api.get<{ clientId?: string | null }>(
    `/groups/${groupId}/expenses/${expenseId}`,
  );
  const clientId = typeof wire.clientId === "string" ? wire.clientId : newId();
  expenseClientIds.set(expenseId, clientId);
  return clientId;
}

/* ──────────────────────────────────────────────────────────────── store ── */

export const useStore = create<Store>((set, get) => {
  // A counter rather than a flag: two overlapping calls should not have the
  // first one to finish declare the app idle.
  let inFlight = 0;

  const begin = () => {
    inFlight += 1;
    set({ loading: true, lastError: null });
  };

  /** Ends one call. Pass the caught error to report it. */
  const finish = (error?: unknown, fallback?: string) => {
    inFlight = Math.max(0, inFlight - 1);
    const patch: Partial<State> = {};
    if (inFlight === 0) patch.loading = false;
    if (error !== undefined) {
      patch.lastError = messageFor(error, fallback ?? "Something went wrong.");
    }
    set(patch);
  };

  return {
    ...initialState,

    load: async () => {
      begin();
      try {
        const listed = await groupsApi.list();

        // Every roster, not just the selected group's: `Group.memberIds` is
        // what the chips count and what the expense form iterates, and the
        // list endpoint carries only a headcount. A handful of parallel reads
        // is cheaper than showing every other group as empty.
        const rosters = await Promise.all(
          listed.map((group) => groupsApi.members(group.id)),
        );
        const groups = listed.map((group, index) => ({
          ...group,
          memberIds: rosters[index].map((member) => member.id),
        }));

        set({
          groups,
          // Once there is a group to look at, onboarding is behind the user.
          hasOnboarded: get().hasOnboarded || groups.length > 0,
        });

        const index = Math.max(
          0,
          groups.findIndex((group) => group.id === get().selectedGroupId),
        );
        const chosen = groups[index] ?? null;

        if (!chosen) {
          set({
            members: [],
            expenses: [],
            currentUserId: null,
            selectedGroupId: null,
          });
          finish();
          return;
        }

        applyGroup(
          set,
          chosen.id,
          rosters[index],
          await expensesApi.list(chosen.id),
        );
        finish();
      } catch (error) {
        finish(error, "Couldn't load your groups.");
      }
    },

    selectGroup: async (groupId) => {
      if (groupId === null) {
        set({
          selectedGroupId: null,
          members: [],
          expenses: [],
          currentUserId: null,
        });
        return;
      }
      if (groupId === get().selectedGroupId) return;

      if (!get().groups.some((group) => group.id === groupId)) {
        set({ lastError: "That group isn't here any more." });
        return;
      }

      begin();
      try {
        const [roster, ledger] = await Promise.all([
          groupsApi.members(groupId),
          expensesApi.list(groupId),
        ]);
        applyGroup(set, groupId, roster, ledger);
        finish();
      } catch (error) {
        finish(error, "Couldn't open that group.");
      }
    },

    clearError: () => set({ lastError: null }),

    setCurrentUser: (id) => {
      // Identity is the server's to decide: each group issues the caller their
      // own participant row, resolved when the group loads. Accepting a switch
      // here would only lie to the screen that asked for it.
      if (id === get().currentUserId) return;
      set({
        lastError:
          "Who you are in a group comes from your account — you can't stand in for someone else.",
      });
    },

    setAppearance: (appearance) => {
      storage.saveAppearance(appearance);
      set({ appearance });
    },

    completeOnboarding: () => set({ hasOnboarded: true }),

    // ── groups ──────────────────────────────────────────────────────────

    addGroup: async (input) => {
      begin();
      try {
        // The server makes the caller the first owner and ignores any roster
        // the form offers — a member only exists inside a group, so there is
        // nobody to enrol yet.
        const created = await groupsApi.create(
          input.name,
          toCurrency(input.defaultCurrency),
        );
        const roster = await groupsApi.members(created.id);
        const group: GroupWithMeta = {
          ...created,
          memberIds: roster.map((member) => member.id),
        };

        set((state) => ({
          groups: [...state.groups, group],
          hasOnboarded: true,
        }));
        // A brand-new group has no ledger yet, so there is nothing to fetch.
        applyGroup(set, group.id, roster, []);
        finish();
        return group;
      } catch (error) {
        finish(error, "Couldn't create that group.");
        return null;
      }
    },

    updateGroup: async (group) => {
      const current = get().groups.find(
        (candidate) => candidate.id === group.id,
      );
      if (!current) {
        set({ lastError: "That group isn't here any more." });
        return;
      }

      begin();
      try {
        let saved = current;
        if (group.name !== current.name) {
          const renamed = await groupsApi.rename(
            group.id,
            group.name,
            current.version,
          );
          saved = { ...renamed, memberIds: current.memberIds };
        }

        // Toggling a row off is the only roster edit the API can honour: a
        // member cannot be moved into a group they were not created in, so an
        // "addition" is always someone this sheet just created here.
        const removed = current.memberIds.filter(
          (id) => !group.memberIds.includes(id),
        );
        for (const id of removed) {
          await groupsApi.removeMember(group.id, id);
        }

        const memberIds = current.memberIds.filter(
          (id) => !removed.includes(id),
        );

        set((state) => ({
          groups: state.groups.map((candidate) =>
            candidate.id === group.id
              ? { ...saved, memberIds, memberCount: memberIds.length }
              : candidate,
          ),
          members:
            state.selectedGroupId === group.id
              ? state.members.filter((member) => !removed.includes(member.id))
              : state.members,
        }));
        finish();

        // Reported once the rest is saved, so the message describes what is
        // left undone rather than implying the whole edit was refused.
        if (
          toCurrency(group.defaultCurrency) !==
          toCurrency(current.defaultCurrency)
        ) {
          set({
            lastError:
              "A group's default currency is fixed once it exists — every other change was saved.",
          });
        }
      } catch (error) {
        finish(error, "Couldn't save that group.");
      }
    },

    deleteGroup: async (id) => {
      begin();
      try {
        await groupsApi.remove(id);
        const groups = get().groups.filter((group) => group.id !== id);
        const wasSelected = get().selectedGroupId === id;
        set({ groups });
        finish();

        if (wasSelected) {
          // `selectGroup` short-circuits on the current id, so drop the
          // selection before asking for the replacement.
          set({ selectedGroupId: null });
          await get().selectGroup(groups.length > 0 ? groups[0].id : null);
        }
      } catch (error) {
        finish(error, "Couldn't delete that group.");
      }
    },

    // ── members ─────────────────────────────────────────────────────────

    addMember: async (input) => {
      const groupId = get().selectedGroupId;
      if (!groupId) {
        set({ lastError: "Open a group before adding someone to it." });
        return null;
      }

      begin();
      try {
        // Everyone added by hand is a ghost: a placeholder that becomes a real
        // account only when someone accepts an invite that claims it.
        const created = await groupsApi.addGhost(
          groupId,
          input.name,
          input.iconSymbol ?? null,
        );

        set((state) => ({
          members: [...state.members, created],
          groups: state.groups.map((group) =>
            group.id === groupId
              ? {
                  ...group,
                  memberIds: [...group.memberIds, created.id],
                  memberCount: group.memberCount + 1,
                }
              : group,
          ),
        }));
        finish();
        return created;
      } catch (error) {
        finish(error, "Couldn't add that person.");
        return null;
      }
    },

    updateMember: async (member) => {
      const groupId = get().selectedGroupId;
      if (!groupId) {
        set({ lastError: "Open a group before editing its members." });
        return;
      }

      begin();
      try {
        // The API only ever lets you edit your own row; anyone else's comes
        // back as a permission failure, which `messageFor` phrases.
        const saved = await groupsApi.updateSelf(groupId, member.id, {
          displayName: member.name,
          iconSymbol: member.iconSymbol ?? null,
        });
        set((state) => ({
          members: state.members.map((candidate) =>
            candidate.id === saved.id ? saved : candidate,
          ),
        }));
        finish();
      } catch (error) {
        finish(error, "Couldn't save that member.");
      }
    },

    deleteMember: async (id) => {
      const groupId = get().selectedGroupId;
      if (!groupId) {
        set({ lastError: "Open a group before removing its members." });
        return;
      }

      begin();
      try {
        await groupsApi.removeMember(groupId, id);
        set((state) => ({
          members: state.members.filter((member) => member.id !== id),
          groups: state.groups.map((group) =>
            group.id === groupId
              ? {
                  ...group,
                  memberIds: group.memberIds.filter(
                    (memberId) => memberId !== id,
                  ),
                  memberCount: Math.max(0, group.memberCount - 1),
                }
              : group,
          ),
        }));
        finish();
      } catch (error) {
        finish(error, "Couldn't remove that person.");
      }
    },

    // ── expenses ────────────────────────────────────────────────────────

    addExpense: async (input) => {
      const groupId = input.groupId || get().selectedGroupId;
      if (!groupId) {
        set({ lastError: "Open a group before adding an expense." });
        return null;
      }

      const key = intentKey(input);
      let clientId = pendingCreateIds.get(key);
      if (!clientId) {
        clientId = newId();
        pendingCreateIds.set(key, clientId);
      }

      // `id` is the server's to assign; `expenseToWire` never reads it.
      const draft: Expense = {
        ...input,
        groupId,
        id: "",
        createdAt: new Date().toISOString(),
      };

      begin();
      try {
        const created = await expensesApi.create(groupId, draft, clientId);
        settleCreate(set, key, clientId, created);
        finish();
        return created;
      } catch (error) {
        const existing = await adoptConflictingCreate(groupId, error);
        if (existing) {
          settleCreate(set, key, clientId, existing);
          finish();
          return existing;
        }
        finish(error, "Couldn't save that expense.");
        return null;
      }
    },

    updateExpense: async (expense) => {
      const current = get().expenses.find(
        (candidate) => candidate.id === expense.id,
      );
      const groupId =
        expense.groupId || current?.groupId || get().selectedGroupId;
      if (!groupId) {
        set({ lastError: "Open a group before editing its expenses." });
        return;
      }

      // The edit form rebuilds an expense out of its own fields and has no
      // concurrency token to offer, so the one the store loaded is used.
      const version = expense.version ?? current?.version;

      begin();
      try {
        const clientId = await clientIdFor(groupId, expense.id);
        const saved = await expensesApi.update(
          groupId,
          { ...expense, groupId, version },
          clientId,
        );
        expenseClientIds.set(saved.id, clientId);
        set((state) => ({
          expenses: state.expenses.map((candidate) =>
            candidate.id === saved.id ? saved : candidate,
          ),
        }));
        finish();
      } catch (error) {
        if (adoptServerVersion(set, groupId, error)) {
          finish();
          set({ lastError: CONFLICT_ON_EDIT });
          return;
        }
        finish(error, "Couldn't save that expense.");
      }
    },

    deleteExpense: async (id) => {
      const current = get().expenses.find((expense) => expense.id === id);
      const groupId = current?.groupId ?? get().selectedGroupId;
      if (!groupId) {
        set({ lastError: "Open a group before deleting its expenses." });
        return;
      }

      begin();
      try {
        await expensesApi.remove(groupId, id, current?.version);
        expenseClientIds.delete(id);
        set((state) => ({
          expenses: state.expenses.filter((expense) => expense.id !== id),
        }));
        finish();
      } catch (error) {
        if (adoptServerVersion(set, groupId, error)) {
          finish();
          set({ lastError: CONFLICT_ON_DELETE });
          return;
        }
        finish(error, "Couldn't delete that expense.");
      }
    },

    reset: async () => {
      pendingCreateIds.clear();
      expenseClientIds.clear();
      // Appearance survives: it is this device's preference, not the account's
      // data, and the server has nothing to restore it from.
      set({ ...initialState, appearance: get().appearance });
      await get().load();
    },
  };
});

/* ────────────────────────────────────────────────────────────── helpers ── */

/** Installs one group's roster and ledger, and works out who the caller is. */
function applyGroup(
  set: SetState,
  groupId: string,
  roster: MemberWithMeta[],
  ledger: Expense[],
): void {
  set((state) => ({
    selectedGroupId: groupId,
    members: roster,
    expenses: ledger,
    currentUserId: resolveCurrentMemberId(roster),
    groups: state.groups.map((group) =>
      group.id === groupId
        ? {
            ...group,
            memberIds: roster.map((member) => member.id),
            memberCount: roster.length,
          }
        : group,
    ),
  }));
}

/**
 * Which participant row belongs to the signed-in account.
 *
 * The member list deliberately exposes no user id — `isGhost` is the only
 * link-state it publishes — so this has to be inferred. A ghost is by
 * definition nobody's row; among the rest, the account's display name is the
 * only field the two sides share. When a group has exactly one real user that
 * is unambiguous however they are named, which covers the ordinary case of one
 * account surrounded by placeholders.
 */
function resolveCurrentMemberId(members: MemberWithMeta[]): string | null {
  const user = useAuth.getState().user;
  if (!user) return null;

  const real = members.filter((member) => !member.isGhost);
  if (real.length === 0) return null;
  if (real.length === 1) return real[0].id;

  const name = user.displayName?.trim().toLowerCase();
  if (!name) return null;
  return (
    real.find((member) => member.name.trim().toLowerCase() === name)?.id ?? null
  );
}

/** Records a confirmed create and retires its idempotency key. */
function settleCreate(
  set: SetState,
  key: string,
  clientId: string,
  expense: Expense,
): void {
  pendingCreateIds.delete(key);
  expenseClientIds.set(expense.id, clientId);

  set((state) =>
    state.selectedGroupId === expense.groupId
      ? {
          expenses: [
            ...state.expenses.filter(
              (candidate) => candidate.id !== expense.id,
            ),
            expense,
          ],
        }
      : {},
  );
}

/**
 * Recovers the expense a rejected create had already made.
 *
 * `client_id_conflict` means this key is spoken for: the intent landed, and
 * only the body has since drifted. Re-posting under a fresh key is exactly how
 * a group gets charged twice, so the existing row is fetched and adopted.
 */
async function adoptConflictingCreate(
  groupId: string,
  error: unknown,
): Promise<Expense | null> {
  if (!(error instanceof ApiError) || error.code !== "client_id_conflict") {
    return null;
  }

  const existingId = error.extensions.existingId;
  if (typeof existingId !== "string" || !existingId) return null;

  try {
    return await expensesApi.get(groupId, existingId);
  } catch {
    return null;
  }
}

/**
 * Takes the server's copy of an expense out of a 412 body.
 *
 * The point is to leave the caller's edit unapplied: the local row is replaced
 * with what the server actually holds, so the screen stops showing a version
 * that no longer exists and the caller is told rather than overruled.
 */
function adoptServerVersion(
  set: SetState,
  groupId: string,
  error: unknown,
): boolean {
  if (!(error instanceof ApiError) || error.code !== "version_conflict") {
    return false;
  }

  const current = error.extensions.current;
  if (!current || typeof current !== "object") return false;

  const fresh = expenseFromWire(current as WireExpense, groupId);
  set((state) => ({
    expenses: state.expenses.map((expense) =>
      expense.id === fresh.id ? fresh : expense,
    ),
  }));
  return true;
}

// Export used to read straight from localStorage. The data is on the server
// now, so what Settings hands the user is what the store currently holds.
setExportSource(() => {
  const { groups, members, expenses } = useStore.getState();
  return { groups, members, expenses };
});

/* ──────────────────────────────────────────────────────────── selectors ── */

/**
 * The name to show for a member. The current user always reads as "Me",
 * matching the phone app, and an id with no member left resolves to a dash
 * rather than blank so a deleted participant is visible rather than invisible.
 */
export function displayName(
  members: Member[],
  currentUserId: string | null,
  memberId: string,
): string {
  if (memberId === currentUserId) return "Me";
  return members.find((m) => m.id === memberId)?.name ?? "—";
}
