/**
 * Session state.
 *
 * Kept separate from the data store because its lifecycle is different: the
 * session outlives any particular group and has to be resolved before the app
 * can load anything at all.
 */

import { create } from "zustand";
import {
  getSession,
  restoreSession,
  setSession,
  setSessionLostHandler,
  storedRefreshToken,
  type Session,
} from "@/api/client";
import { auth, me } from "@/api/endpoints";

export type AuthStatus =
  /** Still deciding whether a stored session is still good. */
  | "restoring"
  | "signed-out"
  | "signed-in";

interface AuthState {
  status: AuthStatus;
  user: Session["user"] | null;
  /** The address a code was sent to, while the code screen is up. */
  pendingEmail: string | null;
  error: string | null;
  busy: boolean;
}

interface AuthActions {
  restore: () => Promise<void>;
  requestCode: (email: string) => Promise<boolean>;
  verifyCode: (code: string) => Promise<boolean>;
  cancelCode: () => void;
  setDisplayName: (name: string) => Promise<void>;
  signOut: () => Promise<void>;
  clearError: () => void;
}

export const useAuth = create<AuthState & AuthActions>((set, get) => ({
  status: "restoring",
  user: null,
  pendingEmail: null,
  error: null,
  busy: false,

  restore: async () => {
    if (!storedRefreshToken()) {
      set({ status: "signed-out" });
      return;
    }

    const ok = await restoreSession();
    if (!ok) {
      set({ status: "signed-out", user: null });
      return;
    }

    // The refresh response carries no profile, so fetch it before declaring the
    // session usable — otherwise the first render has a session but no name.
    try {
      const profile = await me.get();
      const current = getSession();
      if (current) setSession({ ...current, user: profile });
      set({ status: "signed-in", user: profile });
    } catch {
      set({ status: "signed-out", user: null });
    }
  },

  requestCode: async (email) => {
    const trimmed = email.trim();
    if (!trimmed) return false;

    set({ busy: true, error: null });
    try {
      await auth.requestCode(trimmed);
      set({ pendingEmail: trimmed, busy: false });
      return true;
    } catch (err) {
      set({ busy: false, error: messageFor(err, "Couldn't send the code. Try again.") });
      return false;
    }
  },

  verifyCode: async (code) => {
    const email = get().pendingEmail;
    if (!email) return false;

    set({ busy: true, error: null });
    try {
      const session = await auth.verifyCode(email, code.trim());
      set({
        status: "signed-in",
        user: session.user,
        pendingEmail: null,
        busy: false,
      });
      return true;
    } catch (err) {
      set({
        busy: false,
        error: messageFor(err, "That code didn't work. Check it and try again."),
      });
      return false;
    }
  },

  cancelCode: () => set({ pendingEmail: null, error: null }),

  setDisplayName: async (name) => {
    const profile = await me.setName(name.trim());
    set({ user: profile });
  },

  signOut: async () => {
    await auth.signOut();
    set({ status: "signed-out", user: null, pendingEmail: null, error: null });
  },

  clearError: () => set({ error: null }),
}));

/**
 * The client drops the session when a refresh fails, which can happen at any
 * moment. Reflecting that here means the UI falls back to sign-in instead of
 * rendering a shell that can no longer load anything.
 */
setSessionLostHandler(() => {
  useAuth.setState({ status: "signed-out", user: null });
});

function messageFor(err: unknown, fallback: string): string {
  if (err && typeof err === "object" && "message" in err) {
    const message = (err as { message?: unknown }).message;
    if (typeof message === "string" && message) return message;
  }
  return fallback;
}
