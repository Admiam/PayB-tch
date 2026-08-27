/**
 * Device-local persistence.
 *
 * Groups, members and expenses used to live here; they live on the server now,
 * where they can be shared between people and devices. What is left is the one
 * thing the server has no field for: which theme *this browser* should render
 * in. That is a per-device preference, not user data, so it stays local.
 *
 * Export is still offered from Settings, but it is no longer a storage
 * concern — the store owns the data and registers a source for it below.
 */

import type { Appearance, Expense, Group, Member } from "@/domain/types";

const KEYS = {
  appearance: "paybitch.appearance",
} as const;

const SCHEMA_VERSION = 1;

/** The collections an export carries, as the store currently holds them. */
export interface ExportData {
  groups: Group[];
  members: Member[];
  expenses: Expense[];
}

const EMPTY_EXPORT: ExportData = { groups: [], members: [], expenses: [] };

let exportSource: (() => ExportData) | null = null;

/**
 * Lets the store supply what an export contains without storage having to
 * import the store — which would make the two modules circular.
 */
export function setExportSource(source: () => ExportData): void {
  exportSource = source;
}

/**
 * localStorage throws in private-mode Safari and when a site-data policy blocks
 * it. Every access is guarded and degrades to in-memory behaviour rather than
 * taking the app down — losing a theme preference is bad, refusing to start is
 * worse.
 */
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
    /* see readString */
  }
}

export const storage = {
  loadAppearance: (): Appearance => {
    const value = readString(KEYS.appearance);
    return value === "light" || value === "dark" || value === "system"
      ? value
      : "system";
  },
  saveAppearance: (appearance: Appearance) =>
    writeString(KEYS.appearance, appearance),

  exportAll: () => ({
    schemaVersion: SCHEMA_VERSION,
    ...(exportSource?.() ?? EMPTY_EXPORT),
  }),
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
