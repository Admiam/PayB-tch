/**
 * Typed calls against the Paybitch API.
 *
 * Every shape here was transcribed from a live response rather than inferred,
 * including the naming mismatches worth knowing about: a member carries
 * `displayName` where the app's model says `name`, and a group serialises its
 * icon as `icon` while a member uses `iconSymbol`.
 */

import { api, setSession, type Session } from "./client";
import { expenseFromWire, expenseToWire, type WireExpense } from "./wire";
import type { CurrencyCode, Expense, Group, Member } from "@/domain/types";

/** The server paginates list endpoints with this envelope. */
interface Page<T> {
  data: T[];
  page: { nextCursor: string | null; hasMore: boolean; limit: number };
}

/* ─────────────────────────────────────────────────────────────── auth ── */

interface SessionResponse {
  accessToken: string;
  refreshToken: string;
  expiresIn: number;
  user: { id: string; displayName: string | null; email: string | null };
}

export const auth = {
  /**
   * Requests a login code. Always resolves for a well-formed address, whether
   * or not an account exists — the server is deliberately enumeration-safe, so
   * the UI must not claim the address is known.
   */
  async requestCode(email: string): Promise<void> {
    await api.post<{ status: string }>(
      "/auth/email/start",
      { email },
      { anonymous: true },
    );
  },

  async verifyCode(email: string, code: string): Promise<Session> {
    const response = await api.post<SessionResponse>(
      "/auth/email/verify",
      { email, code },
      { anonymous: true },
    );

    const session: Session = {
      accessToken: response.accessToken,
      refreshToken: response.refreshToken,
      expiresAt: Date.now() + response.expiresIn * 1000,
      user: response.user,
    };
    setSession(session);
    return session;
  },

  async signOut(): Promise<void> {
    try {
      await api.post("/auth/logout", {});
    } catch {
      // A dead session cannot be logged out of, and the user asked to leave
      // either way — clearing locally is the part that matters.
    }
    setSession(null);
  },
};

/* ───────────────────────────────────────────────────────────── profile ── */

interface MeResponse {
  id: string;
  displayName: string | null;
  email: string | null;
}

export const me = {
  get: () => api.get<MeResponse>("/me"),
  setName: (displayName: string) => api.patch<MeResponse>("/me", { displayName }),
};

/* ────────────────────────────────────────────────────────────── groups ── */

interface WireGroup {
  id: string;
  name: string;
  icon: string | null;
  defaultCurrency: string;
  memberCount: number;
  archived: boolean;
  version: number;
}

interface WireMember {
  id: string;
  groupId: string;
  displayName: string;
  role: "owner" | "admin" | "member";
  iconSymbol: string | null;
  isGhost: boolean;
  version: number;
  deleted: boolean;
}

/** A group plus the membership facts the UI needs but the domain type omits. */
export interface GroupWithMeta extends Group {
  version: number;
  archived: boolean;
  memberCount: number;
}

/** A member plus their server role, needed to decide who may invite or remove. */
export interface MemberWithMeta extends Member {
  role: "owner" | "admin" | "member";
  /** A placeholder for someone who has not joined yet. */
  isGhost: boolean;
  version: number;
}

function toGroup(wire: WireGroup, memberIds: string[] = []): GroupWithMeta {
  return {
    id: wire.id,
    name: wire.name,
    memberIds,
    defaultCurrency: wire.defaultCurrency,
    version: wire.version,
    archived: wire.archived,
    memberCount: wire.memberCount,
  };
}

function toMember(wire: WireMember): MemberWithMeta {
  return {
    id: wire.id,
    name: wire.displayName,
    iconSymbol: wire.iconSymbol,
    role: wire.role,
    isGhost: wire.isGhost,
    version: wire.version,
  };
}

export const groups = {
  async list(): Promise<GroupWithMeta[]> {
    const page = await api.get<Page<WireGroup>>("/groups");
    return page.data.filter((g) => !g.archived).map((g) => toGroup(g));
  },

  async create(name: string, defaultCurrency: CurrencyCode): Promise<GroupWithMeta> {
    const wire = await api.post<WireGroup>("/groups", { name, defaultCurrency });
    return toGroup(wire);
  },

  async rename(groupId: string, name: string, version: number): Promise<GroupWithMeta> {
    const wire = await api.patch<WireGroup>(`/groups/${groupId}`, { name }, { version });
    return toGroup(wire);
  },

  /** Archives rather than destroys, and is idempotent. */
  remove: (groupId: string) => api.delete<void>(`/groups/${groupId}`),

  async members(groupId: string): Promise<MemberWithMeta[]> {
    const wire = await api.get<WireMember[]>(`/groups/${groupId}/members`);
    return wire.filter((m) => !m.deleted).map(toMember);
  },

  /**
   * Adds a placeholder for someone who is not on Paybitch yet. They become a
   * real user by accepting an invite that claims this member.
   */
  async addGhost(groupId: string, displayName: string, iconSymbol?: string | null) {
    const wire = await api.post<WireMember>(`/groups/${groupId}/members`, {
      displayName,
      iconSymbol: iconSymbol ?? null,
    });
    return toMember(wire);
  },

  /** Only ever your own row — the server rejects editing anyone else. */
  async updateSelf(
    groupId: string,
    memberId: string,
    patch: { displayName?: string; iconSymbol?: string | null },
  ) {
    const wire = await api.patch<WireMember>(
      `/groups/${groupId}/members/${memberId}`,
      patch,
    );
    return toMember(wire);
  },

  removeMember: (groupId: string, memberId: string) =>
    api.delete<void>(`/groups/${groupId}/members/${memberId}`),

  leave: (groupId: string) =>
    api.post<void>(`/groups/${groupId}/members/me/leave`, {}),
};

/* ───────────────────────────────────────────────────────────── invites ── */

export interface InviteCreated {
  id: string;
  /** Returned exactly once, at creation — the server only stores its hash. */
  token: string;
  link: string;
  memberId: string | null;
  expiresAt: string;
}

export interface InvitePreview {
  group: { name: string; memberCount: number; defaultCurrency: string };
  invitedBy: string;
  expiresAt: string;
  /** The ghost member this invite would claim, when it targets one. */
  claim: { memberId: string; displayName: string } | null;
}

export interface InviteSummary {
  id: string;
  memberId: string | null;
  expiresAt: string;
  acceptedAt: string | null;
}

export const invites = {
  /** `memberId` ties the invite to an existing placeholder member. */
  create: (groupId: string, memberId?: string | null) =>
    api.post<InviteCreated>(
      `/groups/${groupId}/invites`,
      memberId ? { memberId } : {},
    ),

  list: (groupId: string) => api.get<InviteSummary[]>(`/groups/${groupId}/invites`),

  revoke: (groupId: string, inviteId: string) =>
    api.delete<void>(`/groups/${groupId}/invites/${inviteId}`),

  /** Public — this is what someone sees before deciding to sign in. */
  preview: (token: string) =>
    api.get<InvitePreview>(`/invites/${token}`, { anonymous: true }),

  /** Requires a signed-in caller; single-use. */
  accept: (token: string) =>
    api.post<{ groupId: string; memberId: string; role: string }>(
      `/invites/${token}/accept`,
      {},
    ),
};

/* ──────────────────────────────────────────────────────────── expenses ── */

export const expenses = {
  async list(groupId: string): Promise<Expense[]> {
    const collected: Expense[] = [];
    let cursor: string | null = null;

    // Paginated, so a long-lived group needs every page before balances mean
    // anything — a partial list would quietly understate what people owe.
    do {
      const query: string = cursor ? `?cursor=${encodeURIComponent(cursor)}` : "";
      const page: Page<WireExpense> = await api.get<Page<WireExpense>>(
        `/groups/${groupId}/expenses${query}`,
      );
      collected.push(...page.data.map((e) => expenseFromWire(e, groupId)));
      cursor = page.page.hasMore ? page.page.nextCursor : null;
    } while (cursor);

    return collected;
  },

  /**
   * `clientId` makes the create idempotent. It must be minted once per user
   * intent and reused on retry: a fresh one on retry is how a group ends up
   * charged twice.
   */
  async create(groupId: string, expense: Expense, clientId: string): Promise<Expense> {
    const wire = await api.post<WireExpense>(
      `/groups/${groupId}/expenses`,
      expenseToWire(expense, clientId),
    );
    return expenseFromWire(wire, groupId);
  },

  async update(groupId: string, expense: Expense, clientId: string): Promise<Expense> {
    const wire = await api.put<WireExpense>(
      `/groups/${groupId}/expenses/${expense.id}`,
      expenseToWire(expense, clientId),
      { version: expense.version },
    );
    return expenseFromWire(wire, groupId);
  },

  remove: (groupId: string, expenseId: string, version: number | undefined) =>
    api.delete<void>(`/groups/${groupId}/expenses/${expenseId}`, { version }),

  get: async (groupId: string, expenseId: string): Promise<Expense> =>
    expenseFromWire(
      await api.get<WireExpense>(`/groups/${groupId}/expenses/${expenseId}`),
      groupId,
    ),
};

/* ────────────────────────────────────────────────────────── currencies ── */

export interface WireCurrency {
  code: CurrencyCode;
  minorUnits: number;
  symbol: string;
}

export const currencies = {
  list: () => api.get<WireCurrency[]>("/currencies"),
};
