/**
 * The paths where getting it wrong costs money: idempotent creates, and the
 * two concurrency outcomes. Loading and the plain CRUD happy paths are thin
 * wrappers over `endpoints.ts`, which has its own tests.
 */

import { beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "@/api/client";
import type { WireExpense } from "@/api/wire";
import { SPLIT_EQUAL, type Expense } from "@/domain/types";

vi.mock("@/api/endpoints", () => ({
  groups: {
    list: vi.fn(),
    members: vi.fn(),
    create: vi.fn(),
    rename: vi.fn(),
    remove: vi.fn(),
    addGhost: vi.fn(),
    updateSelf: vi.fn(),
    removeMember: vi.fn(),
    leave: vi.fn(),
  },
  expenses: {
    list: vi.fn(),
    create: vi.fn(),
    update: vi.fn(),
    remove: vi.fn(),
    get: vi.fn(),
  },
}));

vi.mock("@/api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/api/client")>();
  return { ...actual, api: { ...actual.api, get: vi.fn() } };
});

const { api } = await import("@/api/client");
const { expenses: expensesApi, groups: groupsApi } = await import(
  "@/api/endpoints"
);
const { useStore } = await import("./useStore");
const { useAuth } = await import("./useAuth");

/* ─────────────────────────────────────────────────────────────── fixtures ── */

let uniqueTitle = 0;

function draft(
  over: Partial<Expense> = {},
): Omit<Expense, "id" | "createdAt"> {
  uniqueTitle += 1;
  return {
    groupId: "g1",
    title: `Dinner ${uniqueTitle}`,
    amount: 840,
    currency: "CZK",
    paidBy: "m1",
    splitAmong: ["m1", "m2"],
    splitType: SPLIT_EQUAL,
    date: "2026-06-27T12:00:00Z",
    notes: null,
    iconSymbol: null,
    ...over,
  };
}

function saved(over: Partial<Expense> = {}): Expense {
  return {
    ...draft(),
    id: "e1",
    createdAt: "2026-06-27T12:00:00Z",
    version: 3,
    ...over,
  };
}

beforeEach(() => {
  vi.clearAllMocks();
  useAuth.setState({
    user: { id: "u1", displayName: "Adam", email: "a@example.com" },
  });
  useStore.setState({
    groups: [],
    members: [],
    expenses: [],
    currentUserId: "m1",
    selectedGroupId: "g1",
    loading: false,
    lastError: null,
  });
});

/* ────────────────────────────────────────────────────────── idempotency ── */

describe("addExpense idempotency", () => {
  it("reuses the same clientId when a failed save is retried", async () => {
    const input = draft();
    vi.mocked(expensesApi.create).mockRejectedValueOnce(new TypeError("offline"));

    await useStore.getState().addExpense(input);
    expect(useStore.getState().lastError).toContain("Couldn't reach the server");

    vi.mocked(expensesApi.create).mockResolvedValueOnce(saved(input));
    await useStore.getState().addExpense(input);

    const calls = vi.mocked(expensesApi.create).mock.calls;
    expect(calls).toHaveLength(2);
    expect(calls[1][2]).toBe(calls[0][2]);
    expect(calls[0][2]).not.toBe("");
  });

  it("mints a fresh clientId once the draft itself changes", async () => {
    vi.mocked(expensesApi.create).mockRejectedValue(new TypeError("offline"));

    await useStore.getState().addExpense(draft({ amount: 100 }));
    await useStore.getState().addExpense(draft({ amount: 200 }));

    const calls = vi.mocked(expensesApi.create).mock.calls;
    expect(calls[1][2]).not.toBe(calls[0][2]);
  });

  it("adopts the existing expense on client_id_conflict instead of re-posting", async () => {
    const existing = saved({ id: "e9", title: "Already booked" });
    vi.mocked(expensesApi.create).mockRejectedValueOnce(
      new ApiError(409, "client_id_conflict", "clientId already used", {
        existingId: "e9",
      }),
    );
    vi.mocked(expensesApi.get).mockResolvedValueOnce(existing);

    const result = await useStore.getState().addExpense(draft());

    expect(result?.id).toBe("e9");
    expect(expensesApi.get).toHaveBeenCalledWith("g1", "e9");
    // The whole point: exactly one create, so the group is charged once.
    expect(expensesApi.create).toHaveBeenCalledTimes(1);
    expect(useStore.getState().expenses.map((e) => e.id)).toEqual(["e9"]);
    expect(useStore.getState().lastError).toBeNull();
  });

  it("reports rather than double-posts when the conflicting expense can't be read", async () => {
    vi.mocked(expensesApi.create).mockRejectedValueOnce(
      new ApiError(409, "client_id_conflict", "clientId already used", {
        existingId: "e9",
      }),
    );
    vi.mocked(expensesApi.get).mockRejectedValueOnce(new Error("nope"));

    const result = await useStore.getState().addExpense(draft());

    expect(result).toBeNull();
    expect(expensesApi.create).toHaveBeenCalledTimes(1);
    expect(useStore.getState().lastError).toBeTruthy();
  });
});

/* ────────────────────────────────────────────────────────── concurrency ── */

describe("expense concurrency", () => {
  const stored = saved({ id: "e1", title: "Dinner", version: 3 });

  beforeEach(() => {
    useStore.setState({ expenses: [stored] });
    vi.mocked(api.get).mockResolvedValue({ clientId: "c-e1" });
  });

  it("sends the version the store loaded, which the edit form drops", async () => {
    vi.mocked(expensesApi.update).mockResolvedValueOnce({
      ...stored,
      title: "Lunch",
      version: 4,
    });

    // Exactly what AddExpenseSheet hands over: no version, no shares.
    await useStore
      .getState()
      .updateExpense({ ...stored, title: "Lunch", version: undefined });

    expect(vi.mocked(expensesApi.update).mock.calls[0][1].version).toBe(3);
    expect(useStore.getState().expenses[0].version).toBe(4);
  });

  it("takes the server's copy and explains, rather than clobbering, on version_conflict", async () => {
    const current: WireExpense = {
      id: "e1",
      groupId: "g1",
      title: "Dinner, as someone else left it",
      amount: "900",
      currency: "CZK",
      paidBy: "m1",
      date: "2026-06-27",
      split: { type: "equal", among: ["m1", "m2"] },
      version: 7,
    };
    vi.mocked(expensesApi.update).mockRejectedValueOnce(
      new ApiError(412, "version_conflict", "Version conflict", { current }),
    );

    await useStore
      .getState()
      .updateExpense({ ...stored, title: "My unsaved edit" });

    const [after] = useStore.getState().expenses;
    expect(after.title).toBe("Dinner, as someone else left it");
    expect(after.amount).toBe(900);
    expect(after.version).toBe(7);
    expect(useStore.getState().lastError).toMatch(/refreshed/i);
  });

  it("keeps the expense and explains when a delete loses the race", async () => {
    vi.mocked(expensesApi.remove).mockRejectedValueOnce(
      new ApiError(412, "version_conflict", "Version conflict", {
        current: {
          id: "e1",
          groupId: "g1",
          title: "Dinner",
          amount: "840",
          currency: "CZK",
          paidBy: "m1",
          date: "2026-06-27",
          split: { type: "equal", among: ["m1", "m2"] },
          version: 7,
        } satisfies WireExpense,
      }),
    );

    await useStore.getState().deleteExpense("e1");

    expect(useStore.getState().expenses).toHaveLength(1);
    expect(vi.mocked(expensesApi.remove).mock.calls[0][2]).toBe(3);
    expect(useStore.getState().lastError).toBeTruthy();
  });
});

/* ──────────────────────────────────────────────────────────── identity ── */

describe("currentUserId", () => {
  const group = {
    id: "g1",
    name: "Roommates",
    memberIds: [],
    defaultCurrency: "CZK",
    version: 1,
    archived: false,
    memberCount: 3,
  };

  const ghost = (id: string, name: string) => ({
    id,
    name,
    iconSymbol: null,
    role: "member" as const,
    isGhost: true,
    isMe: false,
    // A ghost has no account, so the server has no cross-group handle to offer.
    linkKey: null,
    version: 1,
  });
  const real = (id: string, name: string) => ({
    ...ghost(id, name),
    isGhost: false,
    linkKey: `lk-${id}`,
  });
  /** The row the server flagged as the caller's. */
  const mine = (id: string, name: string) => ({
    ...real(id, name),
    isMe: true,
  });

  beforeEach(() => {
    useStore.setState({ groups: [group], selectedGroupId: null });
    vi.mocked(expensesApi.list).mockResolvedValue([]);
  });

  it("is the caller's member id in the group, not their user id", async () => {
    vi.mocked(groupsApi.members).mockResolvedValueOnce([
      ghost("m-bob", "Bob"),
      real("m-me", "Adam"),
    ]);

    await useStore.getState().selectGroup("g1");

    expect(useStore.getState().currentUserId).toBe("m-me");
    expect(useStore.getState().currentUserId).not.toBe("u1");
  });

  it("picks the sole real member however they are named", async () => {
    vi.mocked(groupsApi.members).mockResolvedValueOnce([
      ghost("m-bob", "Bob"),
      real("m-me", "A. Míka"),
    ]);

    await useStore.getState().selectGroup("g1");

    expect(useStore.getState().currentUserId).toBe("m-me");
  });

  it("trusts the server's own flag over any name", async () => {
    vi.mocked(groupsApi.members).mockResolvedValueOnce([
      real("m-bob", "Adam"),
      mine("m-me", "Renamed Me"),
    ]);

    await useStore.getState().selectGroup("g1");

    // Name matching would have picked m-bob here, and been wrong.
    expect(useStore.getState().currentUserId).toBe("m-me");
  });

  it("matches on the account's name once several people have joined", async () => {
    vi.mocked(groupsApi.members).mockResolvedValueOnce([
      real("m-bob", "Bob"),
      real("m-me", "Adam"),
    ]);

    await useStore.getState().selectGroup("g1");

    expect(useStore.getState().currentUserId).toBe("m-me");
  });

  it("refuses to guess when nothing identifies the caller", async () => {
    vi.mocked(groupsApi.members).mockResolvedValueOnce([
      real("m-bob", "Bob"),
      real("m-cara", "Cara"),
    ]);

    await useStore.getState().selectGroup("g1");

    expect(useStore.getState().currentUserId).toBeNull();
  });

  it("fills the group's roster so the screens can count it", async () => {
    vi.mocked(groupsApi.members).mockResolvedValueOnce([
      real("m-me", "Adam"),
      ghost("m-bob", "Bob"),
    ]);

    await useStore.getState().selectGroup("g1");

    expect(useStore.getState().groups[0].memberIds).toEqual(["m-me", "m-bob"]);
    expect(useStore.getState().members).toHaveLength(2);
  });
});

/* ─────────────────────────────────────────────────────────────── errors ── */

describe("error reporting", () => {
  it("never throws out of an action, and phrases the failure", async () => {
    vi.mocked(groupsApi.list).mockRejectedValueOnce(
      new ApiError(403, "insufficient_role", "Forbidden"),
    );

    await expect(useStore.getState().load()).resolves.toBeUndefined();

    expect(useStore.getState().lastError).toBe(
      "You don't have permission to do that in this group.",
    );
    expect(useStore.getState().loading).toBe(false);
  });

  it("clears on request", () => {
    useStore.setState({ lastError: "boom" });
    useStore.getState().clearError();
    expect(useStore.getState().lastError).toBeNull();
  });
});
