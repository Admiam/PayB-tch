/**
 * Persistence layer.
 *
 * The iOS app keeps three independent JSON files in its Documents directory and
 * rewrites a whole array on every mutation. That access pattern — load all,
 * mutate, write all back — maps onto localStorage far more naturally than onto
 * IndexedDB, and the data volume (a few groups, dozens of expenses) is nowhere
 * near the quota. So: localStorage, same three collections, same key namespace.
 *
 * Unlike the Swift side we wrap each collection in a version tag. The Swift
 * `Expense` decoder is full of retrofitted tolerance for old shapes precisely
 * because there was no version to branch on; starting with one costs nothing.
 */

import type { Appearance, Expense, Group, Member } from "@/domain/types";

const KEYS = {
  groups: "paybitch.groups",
  members: "paybitch.members",
  expenses: "paybitch.expenses",
  currentUserId: "paybitch.currentUserId",
  hasOnboarded: "paybitch.hasOnboarded",
  appearance: "paybitch.appearance",
} as const;

const SCHEMA_VERSION = 1;

interface StoredCollection<T> {
  schemaVersion: number;
  items: T[];
}

/**
 * localStorage throws in private-mode Safari and when a site-data policy blocks
 * it, and reads can return junk a previous version wrote. Every access is
 * therefore guarded and degrades to in-memory behaviour rather than taking the
 * app down — losing persistence is bad, refusing to start is worse.
 */
function readCollection<T>(key: string): T[] {
  try {
    const raw = localStorage.getItem(key);
    if (!raw) return [];
    const parsed: unknown = JSON.parse(raw);

    if (
      typeof parsed === "object" &&
      parsed !== null &&
      "items" in parsed &&
      Array.isArray((parsed as StoredCollection<T>).items)
    ) {
      return (parsed as StoredCollection<T>).items;
    }
    // Tolerate a bare array, which is what a raw iOS export looks like.
    if (Array.isArray(parsed)) return parsed as T[];
    return [];
  } catch {
    return [];
  }
}

function writeCollection<T>(key: string, items: T[]): void {
  try {
    const payload: StoredCollection<T> = { schemaVersion: SCHEMA_VERSION, items };
    localStorage.setItem(key, JSON.stringify(payload));
  } catch {
    // Quota exceeded or storage disabled — the in-memory store stays correct
    // for this session.
  }
}

function readString(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function writeString(key: string, value: string | null): void {
  try {
    if (value === null) localStorage.removeItem(key);
    else localStorage.setItem(key, value);
  } catch {
    /* see readCollection */
  }
}

export const storage = {
  loadGroups: () => readCollection<Group>(KEYS.groups),
  saveGroups: (g: Group[]) => writeCollection(KEYS.groups, g),

  loadMembers: () => readCollection<Member>(KEYS.members),
  saveMembers: (m: Member[]) => writeCollection(KEYS.members, m),

  loadExpenses: () => readCollection<Expense>(KEYS.expenses),
  saveExpenses: (e: Expense[]) => writeCollection(KEYS.expenses, e),

  loadCurrentUserId: () => readString(KEYS.currentUserId),
  saveCurrentUserId: (id: string | null) => writeString(KEYS.currentUserId, id),

  loadHasOnboarded: () => readString(KEYS.hasOnboarded) === "true",
  saveHasOnboarded: (v: boolean) => writeString(KEYS.hasOnboarded, String(v)),

  loadAppearance: (): Appearance => {
    const v = readString(KEYS.appearance);
    return v === "light" || v === "dark" || v === "system" ? v : "system";
  },
  saveAppearance: (a: Appearance) => writeString(KEYS.appearance, a),

  /** Everything the app owns, for the export/import feature. */
  exportAll: () => ({
    schemaVersion: SCHEMA_VERSION,
    groups: readCollection<Group>(KEYS.groups),
    members: readCollection<Member>(KEYS.members),
    expenses: readCollection<Expense>(KEYS.expenses),
  }),

  clearAll: () => {
    for (const key of Object.values(KEYS)) writeString(key, null);
  },
};

/**
 * `crypto.randomUUID` needs a secure context. Serving over plain HTTP on a LAN
 * IP is a normal way to test this app on a phone, so fall back rather than
 * crash on the first record the user creates.
 */
export function newId(): string {
  if (typeof crypto !== "undefined" && "randomUUID" in crypto) {
    return crypto.randomUUID();
  }
  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}
