import { useEffect, type ReactElement } from "react";
import { Dashboard } from "@/screens/Dashboard";
import { InviteLanding } from "@/screens/InviteLanding";
import { SignIn } from "@/screens/SignIn";
import { useAuth } from "@/store/useAuth";
import { useSharedLedger } from "@/store/useSharedLedger";
import { useStore } from "@/store/useStore";

/** Where an invite token waits while the recipient signs in. */
const PENDING_INVITE_KEY = "paybitch.pendingInvite";

export default function App(): ReactElement {
  const status = useAuth((s) => s.status);
  const restore = useAuth((s) => s.restore);
  const appearance = useStore((s) => s.appearance);
  const load = useStore((s) => s.load);
  const loadSharedLedger = useSharedLedger((s) => s.load);

  const pathToken = inviteTokenFromPath(window.location.pathname);

  // Stash the token before anything can navigate away, so signing in and
  // coming back still lands on the right invite.
  useEffect(() => {
    if (pathToken) rememberInvite(pathToken);
  }, [pathToken]);

  useEffect(() => {
    void restore();
  }, [restore]);

  // The theme lives on <html> rather than in a context so portalled sheets,
  // which render outside the app tree, inherit it too.
  useEffect(() => {
    const root = document.documentElement;
    if (appearance === "system") root.removeAttribute("data-theme");
    else root.setAttribute("data-theme", appearance);
  }, [appearance]);

  // Data only exists once there is a session to fetch it with. The shared-debt
  // configuration is fetched alongside, not on demand: the dashboard has to know
  // whether there is a pooled view before it can decide what to render, and
  // asking later would mean a section that pops in after the fact.
  useEffect(() => {
    if (status !== "signed-in") return;
    void load();
    void loadSharedLedger();
  }, [status, load, loadSharedLedger]);

  // Someone who signed in *because* of an invite should be taken to it rather
  // than dropped on an empty dashboard wondering what happened.
  useEffect(() => {
    if (status !== "signed-in" || pathToken) return;
    const pending = takeRememberedInvite();
    if (pending) window.location.assign(`/invite/${pending}`);
  }, [status, pathToken]);

  // Deciding whether the stored session is still good takes a round trip.
  // Rendering nothing beats flashing sign-in at someone already signed in.
  if (status === "restoring") return <div className="pb-app-bg" aria-hidden="true" />;

  // An invite has to be signed in to accept, so send a signed-out visitor
  // through sign-in first — the token is already stashed above.
  if (pathToken) {
    return status === "signed-in" ? <InviteLanding token={pathToken} /> : <SignIn />;
  }

  if (status === "signed-out") return <SignIn />;

  return <Dashboard />;
}

/** `/invite/<token>` → the token, or null for any other path. */
function inviteTokenFromPath(pathname: string): string | null {
  const match = /^\/invite\/([A-Za-z0-9_-]+)\/?$/.exec(pathname);
  return match?.[1] ?? null;
}

function rememberInvite(token: string): void {
  try {
    sessionStorage.setItem(PENDING_INVITE_KEY, token);
  } catch {
    // Blocked storage only costs the post-sign-in redirect; the link still works.
  }
}

function takeRememberedInvite(): string | null {
  try {
    const token = sessionStorage.getItem(PENDING_INVITE_KEY);
    if (token) sessionStorage.removeItem(PENDING_INVITE_KEY);
    return token;
  } catch {
    return null;
  }
}

export { PENDING_INVITE_KEY };
