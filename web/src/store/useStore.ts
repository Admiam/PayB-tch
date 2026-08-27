/**
 * The single application store, mirroring `ModelData.swift`.
 *
 * Like the Swift original this is the only thing screens talk to — nothing
 * reaches into storage directly. Two differences, both deliberate:
 *
 *  - Mutations are synchronous. The Swift versions are `async` only because
 *    the repositories are actors; localStorage is synchronous, and making the
 *    backfill awaited removes a race the Swift tests work around with a sleep.
 *  - `currentUserId` is only settable through `setCurrentUser`, so it can't be
 *    changed in a way that skips persistence and the group backfill.
 */

import { create } from "zustand";
import { newId, storage } from "@/lib/storage";
import {
  DEFAULT_CURRENCY,
  type Appearance,
  type Expense,
  type Group,
  type Member,
} from "@/domain/types";

interface State {
  groups: Group[];
  members: Member[];
  expenses: Expense[];
  currentUserId: string | null;
  hasOnboarded: boolean;
  appearance: Appearance;
  /** Transient, surfaced as a dismissible banner. */
  lastError: string | null;
}

interface Actions {
  load: () => void;
  clearError: () => void;

  setCurrentUser: (id: string | null) => void;
  setAppearance: (a: Appearance) => void;
  completeOnboarding: () => void;

  addGroup: (input: Omit<Group, "id">) => Group;
  updateGroup: (group: Group) => void;
  deleteGroup: (id: string) => void;

  addMember: (input: Omit<Member, "id">) => Member;
  updateMember: (member: Member) => void;
  deleteMember: (id: string) => void;

  addExpense: (input: Omit<Expense, "id" | "createdAt">) => Expense;
  updateExpense: (expense: Expense) => void;
  deleteExpense: (id: string) => void;

  reset: () => void;
}

export type Store = State & Actions;

const initialState: State = {
  groups: [],
  members: [],
  expenses: [],
  currentUserId: null,
  hasOnboarded: false,
  appearance: "system",
  lastError: null,
};

export const useStore = create<Store>((set, get) => ({
  ...initialState,

  load: () => {
    const groups = storage.loadGroups();
    const members = storage.loadMembers();
    const expenses = storage.loadExpenses();
    let currentUserId = storage.loadCurrentUserId();

    // Invariant 1: there is always a "me" member. Reuse one literally named
    // "Me" before fabricating another, so a reinstall over existing data
    // doesn't accumulate duplicates.
    let nextMembers = members;
    if (!currentUserId || !members.some((m) => m.id === currentUserId)) {
      const existing = members.find((m) => m.name.toLowerCase() === "me");
      if (existing) {
        currentUserId = existing.id;
      } else {
        const me: Member = { id: newId(), name: "Me" };
        nextMembers = [...members, me];
        currentUserId = me.id;
        storage.saveMembers(nextMembers);
      }
      storage.saveCurrentUserId(currentUserId);
    }

    // Invariant 2: the current user belongs to every group.
    const nextGroups = backfillCurrentUser(groups, currentUserId);
    if (nextGroups !== groups) storage.saveGroups(nextGroups);

    set({
      groups: nextGroups,
      members: nextMembers,
      expenses,
      currentUserId,
      hasOnboarded: storage.loadHasOnboarded(),
      appearance: storage.loadAppearance(),
    });
  },

  clearError: () => set({ lastError: null }),

  setCurrentUser: (id) => {
    storage.saveCurrentUserId(id);
    const groups = backfillCurrentUser(get().groups, id);
    storage.saveGroups(groups);
    set({ currentUserId: id, groups });
  },

  setAppearance: (appearance) => {
    storage.saveAppearance(appearance);
    set({ appearance });
  },

  completeOnboarding: () => {
    storage.saveHasOnboarded(true);
    set({ hasOnboarded: true });
  },

  // ── groups ──────────────────────────────────────────────────────────

  addGroup: (input) => {
    const group: Group = { ...input, id: newId() };
    const groups = [...get().groups, group];
    storage.saveGroups(groups);
    set({ groups });
    return group;
  },

  updateGroup: (group) => {
    const groups = get().groups.map((g) => (g.id === group.id ? group : g));
    storage.saveGroups(groups);
    set({ groups });
  },

  deleteGroup: (id) => {
    // Cascade: a group's expenses have nowhere to belong once it is gone.
    const groups = get().groups.filter((g) => g.id !== id);
    const expenses = get().expenses.filter((e) => e.groupId !== id);
    storage.saveGroups(groups);
    storage.saveExpenses(expenses);
    set({ groups, expenses });
  },

  // ── members ─────────────────────────────────────────────────────────

  addMember: (input) => {
    const member: Member = { ...input, id: newId() };
    const members = [...get().members, member];
    storage.saveMembers(members);
    set({ members });
    return member;
  },

  updateMember: (member) => {
    const members = get().members.map((m) => (m.id === member.id ? member : m));
    storage.saveMembers(members);
    set({ members });
  },

  deleteMember: (id) => {
    const { members, groups, currentUserId } = get();

    const nextMembers = members.filter((m) => m.id !== id);
    // Cascade out of every roster. Expenses deliberately keep their historical
    // references — rewriting who paid for a past dinner would falsify the
    // ledger, so the UI resolves unknown ids to a placeholder instead.
    const nextGroups = groups.map((g) =>
      g.memberIds.includes(id)
        ? { ...g, memberIds: g.memberIds.filter((m) => m !== id) }
        : g,
    );

    storage.saveMembers(nextMembers);
    storage.saveGroups(nextGroups);
    set({ members: nextMembers, groups: nextGroups });

    if (currentUserId === id) {
      get().setCurrentUser(nextMembers[0]?.id ?? null);
    }
  },

  // ── expenses ────────────────────────────────────────────────────────

  addExpense: (input) => {
    const expense: Expense = {
      ...input,
      id: newId(),
      createdAt: new Date().toISOString(),
    };
    const expenses = [...get().expenses, expense];
    storage.saveExpenses(expenses);
    set({ expenses });
    return expense;
  },

  updateExpense: (expense) => {
    const expenses = get().expenses.map((e) =>
      e.id === expense.id ? expense : e,
    );
    storage.saveExpenses(expenses);
    set({ expenses });
  },

  deleteExpense: (id) => {
    const expenses = get().expenses.filter((e) => e.id !== id);
    storage.saveExpenses(expenses);
    set({ expenses });
  },

  reset: () => {
    storage.clearAll();
    set({ ...initialState });
    get().load();
  },
}));

/** Adds `userId` to any group whose roster is missing it. */
function backfillCurrentUser(groups: Group[], userId: string | null): Group[] {
  if (!userId) return groups;
  let changed = false;

  const next = groups.map((g) => {
    if (g.memberIds.includes(userId)) return g;
    changed = true;
    return { ...g, memberIds: [...g.memberIds, userId] };
  });

  return changed ? next : groups;
}

// ── selectors ─────────────────────────────────────────────────────────

export const selectGroup = (id: string | null) => (s: Store) =>
  s.groups.find((g) => g.id === id) ?? null;

export const selectExpensesForGroup = (groupId: string | null) => (s: Store) =>
  groupId ? s.expenses.filter((e) => e.groupId === groupId) : [];

export const selectMember = (id: string) => (s: Store) =>
  s.members.find((m) => m.id === id) ?? null;

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

export const defaultCurrencyFor = (group: Group | null) =>
  group?.defaultCurrency ?? DEFAULT_CURRENCY;
