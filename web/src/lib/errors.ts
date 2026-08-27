/**
 * Failed calls, turned into something a screen can put in front of a person.
 *
 * The server's problem `code` is a closed set the API commits to, so the cases
 * worth phrasing ourselves are matched on it. Anything else falls back to the
 * server's own `detail`, which is already human prose — repeating it beats
 * flattening every unknown failure into one generic apology.
 */

import { ApiError } from "@/api/client";

const SENTENCES: Record<string, string | undefined> = {
  unauthenticated: "Your session has expired. Sign in again to continue.",
  insufficient_role: "You don't have permission to do that in this group.",
  not_found: "That's already gone — someone else may have removed it.",
  version_conflict:
    "Someone else changed this first, so nothing of yours was overwritten.",
  precondition_required:
    "Your copy of this is out of date. Reload the group and try again.",
  group_archived: "This group is archived, so it can't be changed.",
  balance_not_zero:
    "They still have an unsettled balance, so they can't be removed yet.",
  last_owner: "A group needs at least one owner, so this one has to stay.",
  limit_exceeded: "This group has hit its limit.",
  immutable_field: "This expense can't be edited from here any more.",
  rate_limited: "Too many requests just now — wait a moment and try again.",
};

export function messageFor(error: unknown, fallback: string): string {
  if (error instanceof ApiError) {
    const known =
      SENTENCES[error.code] ??
      (error.isUnauthenticated ? SENTENCES.unauthenticated : undefined);
    if (known) return known;
    if (error.message) return asSentence(error.message);
    return fallback;
  }

  // `fetch` rejects with a TypeError when the request never reached the server
  // at all — offline, DNS, CORS. That is worth saying plainly.
  if (error instanceof TypeError) {
    return "Couldn't reach the server. Check your connection and try again.";
  }

  return fallback;
}

function asSentence(text: string): string {
  const trimmed = text.trim();
  if (!trimmed) return "";
  return /[.!?]$/.test(trimmed) ? trimmed : `${trimmed}.`;
}
