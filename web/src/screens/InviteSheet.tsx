/**
 * Create and share invite links for a group.
 *
 * The whole screen is shaped by one API fact: `invites.create` returns the
 * token exactly once, because the server stores only its hash. There is no
 * "show it again" — so a freshly minted link is pinned to the top of the sheet,
 * loudly labelled, and copyable in one tap. Everything else here (the pending
 * list, revoke, the ghost roster) is secondary to getting that link out.
 */

import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type ReactElement,
  type RefObject,
} from "react";
import { ApiError } from "@/api/client";
import {
  groups,
  invites,
  type InviteCreated,
  type InviteSummary,
  type MemberWithMeta,
} from "@/api/endpoints";
import { Avatar } from "@/components/Avatar";
import { Sheet } from "@/components/Sheet";
import { FieldGroup, PrimaryButton } from "@/components/primitives";
import { Icon } from "@/icons";

/** How long the copy button stays in its confirmed state. */
const COPY_FEEDBACK_MS = 2400;

/**
 * Stands in for "the un-targeted invite" in the which-row-is-busy state.
 * Member ids are server-issued, so this cannot collide with one.
 */
const BLANK = "blank";

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

const relativeTime = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });

/** True once `iso` has passed — an invite past this point cannot be accepted. */
export function isExpired(iso: string, now: number = Date.now()): boolean {
  const at = new Date(iso).getTime();
  return Number.isFinite(at) && at <= now;
}

/** A friendly expiry: "Expires in 6 days", "Expires tomorrow", "Expired". */
export function expiryLabel(iso: string, now: number = Date.now()): string {
  const at = new Date(iso).getTime();
  if (!Number.isFinite(at)) return "Expiry unknown";

  const delta = at - now;
  if (delta <= 0) return "Expired";

  // Each branch is gated on its own threshold rather than on the rounded
  // figure, so half an hour never reads as "in 1 hour" — the closer an invite
  // is to lapsing, the more the precision matters.
  if (delta >= DAY) {
    return `Expires ${relativeTime.format(Math.round(delta / DAY), "day")}`;
  }
  if (delta >= HOUR) {
    return `Expires ${relativeTime.format(Math.round(delta / HOUR), "hour")}`;
  }
  return `Expires ${relativeTime.format(Math.max(1, Math.round(delta / MINUTE)), "minute")}`;
}

function messageFor(error: unknown, fallback: string): string {
  if (error instanceof ApiError && error.message) return error.message;
  return fallback;
}

type CopyState = "idle" | "copied" | "selected";

const COPY_LABELS: Record<CopyState, string> = {
  idle: "Copy link",
  copied: "Copied",
  selected: "Selected — copy it",
};

/**
 * The link to actually share.
 *
 * The server's own `link` points at its API preview endpoint, which serves JSON
 * — fine for the backend's landing page, useless to someone tapping it in a
 * chat. The shareable form is this app's own route, built from the token.
 */
export function shareableInviteLink(token: string): string {
  return `${window.location.origin}/invite/${token}`;
}

export function InviteSheet({
  open,
  groupId,
  groupName,
  onClose,
}: {
  open: boolean;
  groupId: string;
  groupName: string;
  onClose: () => void;
}): ReactElement {
  const [roster, setRoster] = useState<MemberWithMeta[]>([]);
  const [pending, setPending] = useState<InviteSummary[]>([]);
  const [loading, setLoading] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [created, setCreated] = useState<InviteCreated | null>(null);
  const [creatingFor, setCreatingFor] = useState<string | null>(null);
  const [revoking, setRevoking] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [copyState, setCopyState] = useState<CopyState>("idle");
  const [wasOpen, setWasOpen] = useState(false);

  const linkRef = useRef<HTMLElement>(null);
  const copyTimer = useRef<number | null>(null);

  // One instance is reused for every group, so the transient state is cleared
  // on open rather than on mount. Adjusted during render, as the other sheets
  // do, so the first painted frame is already the fresh one — a stale token
  // flashing up under a new group's name would be genuinely alarming.
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setCreated(null);
      setActionError(null);
      setCopyState("idle");
      setCreatingFor(null);
      setRevoking(null);
    }
  }

  useEffect(() => {
    if (!open) return;

    let cancelled = false;
    setLoading(true);
    setLoadError(null);

    void (async () => {
      try {
        const [summaries, members] = await Promise.all([
          invites.list(groupId),
          groups.members(groupId),
        ]);
        if (cancelled) return;
        setPending(summaries.filter((invite) => invite.acceptedAt === null));
        setRoster(members);
      } catch (error) {
        if (!cancelled) {
          setLoadError(messageFor(error, "Couldn’t load the invites for this group."));
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [open, groupId]);

  useEffect(
    () => () => {
      if (copyTimer.current !== null) window.clearTimeout(copyTimer.current);
    },
    [],
  );

  const selectLink = useCallback((): boolean => {
    const node = linkRef.current;
    const selection = window.getSelection();
    if (!node || !selection) return false;

    const range = document.createRange();
    range.selectNodeContents(node);
    selection.removeAllRanges();
    selection.addRange(range);
    return true;
  }, []);

  const copyLink = useCallback(async () => {
    const link = created ? shareableInviteLink(created.token) : null;
    if (!link) return;

    if (copyTimer.current !== null) window.clearTimeout(copyTimer.current);

    let next: CopyState;
    try {
      // The Clipboard API needs a secure context, which this app is not when it
      // is opened over plain HTTP on a LAN — a very normal way to use it.
      await navigator.clipboard.writeText(link);
      next = "copied";
    } catch {
      next = selectLink() ? "selected" : "idle";
    }

    setCopyState(next);
    copyTimer.current = window.setTimeout(
      () => setCopyState("idle"),
      COPY_FEEDBACK_MS,
    );
  }, [created, selectLink]);

  const create = async (memberId: string | null) => {
    if (creatingFor !== null) return;

    setCreatingFor(memberId ?? BLANK);
    setActionError(null);
    setCopyState("idle");

    try {
      const invite = await invites.create(groupId, memberId);
      setCreated(invite);
      setPending((current) => [
        ...current,
        {
          id: invite.id,
          memberId: invite.memberId,
          expiresAt: invite.expiresAt,
          acceptedAt: null,
        },
      ]);
    } catch (error) {
      setActionError(messageFor(error, "Couldn’t create an invite link."));
    } finally {
      setCreatingFor(null);
    }
  };

  const revoke = async (inviteId: string) => {
    setRevoking(inviteId);
    setActionError(null);

    try {
      await invites.revoke(groupId, inviteId);
      setPending((current) => current.filter((invite) => invite.id !== inviteId));
      // If the link on screen is the one just revoked, stop offering it.
      setCreated((current) => (current?.id === inviteId ? null : current));
    } catch (error) {
      setActionError(messageFor(error, "Couldn’t revoke that invite."));
    } finally {
      setRevoking(null);
    }
  };

  const ghosts = roster.filter((member) => member.isGhost);
  const nameFor = (memberId: string | null): string | null =>
    memberId ? (roster.find((m) => m.id === memberId)?.name ?? null) : null;

  return (
    <Sheet open={open} onClose={onClose} title="Invite people">
      <div className="pb-invite">
        <p className="pb-invite__lede">
          Anyone who opens the link joins <strong>{groupName}</strong>. Each link
          works once, then it’s spent.
        </p>

        {created && (
          <FreshLink
            invite={created}
            claimName={nameFor(created.memberId)}
            copyState={copyState}
            linkRef={linkRef}
            onCopy={() => void copyLink()}
          />
        )}

        {actionError && (
          <p className="pb-invite__error" role="alert">
            {actionError}
          </p>
        )}

        <FieldGroup label="New link">
          <button
            type="button"
            className="pb-invite__row pb-invite__row--action"
            disabled={creatingFor !== null}
            onClick={() => void create(null)}
          >
            <span className="pb-invite__row-icon" aria-hidden="true">
              <Icon name="plus" size={16} strokeWidth={2.6} />
            </span>
            <span className="pb-invite__row-text">
              <span className="pb-invite__row-name">Anyone with the link</span>
              <span className="pb-invite__row-sub">
                They join as a brand-new person
              </span>
            </span>
            <span className="pb-invite__row-cta">
              {creatingFor === BLANK ? "Creating…" : "Create"}
            </span>
          </button>

          {ghosts.length > 0 && <hr className="pb-divider" />}

          {ghosts.map((ghost, index) => (
            <div key={ghost.id}>
              {index > 0 && <hr className="pb-divider" />}
              <div className="pb-invite__row">
                <Avatar member={ghost} size={34} />
                <span className="pb-invite__row-text">
                  <span className="pb-invite__row-name">{ghost.name}</span>
                  <span className="pb-invite__row-sub">
                    Claims their history
                  </span>
                </span>
                <PrimaryButton
                  disabled={creatingFor !== null}
                  onClick={() => void create(ghost.id)}
                >
                  {creatingFor === ghost.id ? "Creating…" : "Create"}
                </PrimaryButton>
              </div>
            </div>
          ))}
        </FieldGroup>

        {ghosts.length > 0 && (
          <p className="pb-invite__hint">
            Inviting someone by name hands over the expenses already logged
            against them, instead of adding a second copy of that person.
          </p>
        )}

        <FieldGroup label={`Pending${pending.length ? ` (${pending.length})` : ""}`}>
          {loading ? (
            <p className="pb-invite__empty">Loading invites…</p>
          ) : loadError ? (
            <p className="pb-invite__empty" role="alert">
              {loadError}
            </p>
          ) : pending.length === 0 ? (
            <p className="pb-invite__empty">
              No links waiting to be used.
            </p>
          ) : (
            pending.map((invite, index) => (
              <div key={invite.id}>
                {index > 0 && <hr className="pb-divider" />}
                <div className="pb-invite__row">
                  <span className="pb-invite__row-icon" aria-hidden="true">
                    <Icon name="key.fill" size={16} />
                  </span>
                  <span className="pb-invite__row-text">
                    <span className="pb-invite__row-name">
                      {nameFor(invite.memberId) ?? "Anyone with the link"}
                    </span>
                    <span
                      className={`pb-invite__row-sub${
                        isExpired(invite.expiresAt) ? " is-expired" : ""
                      }`}
                    >
                      {expiryLabel(invite.expiresAt)}
                    </span>
                  </span>
                  <button
                    type="button"
                    className="pb-invite__revoke"
                    aria-label={`Revoke invite for ${
                      nameFor(invite.memberId) ?? "anyone with the link"
                    }`}
                    disabled={revoking === invite.id}
                    onClick={() => void revoke(invite.id)}
                  >
                    {revoking === invite.id ? (
                      "Revoking…"
                    ) : (
                      <Icon name="trash" size={16} />
                    )}
                  </button>
                </div>
              </div>
            ))
          )}
        </FieldGroup>
      </div>
    </Sheet>
  );
}

/**
 * The one-time link. Deliberately the loudest thing in the sheet: once this
 * unmounts the token is unrecoverable and the user has to mint a new invite.
 */
function FreshLink({
  invite,
  claimName,
  copyState,
  linkRef,
  onCopy,
}: {
  invite: InviteCreated;
  claimName: string | null;
  copyState: CopyState;
  linkRef: RefObject<HTMLElement | null>;
  onCopy: () => void;
}): ReactElement {
  return (
    <section className="pb-invite__fresh" aria-label="New invite link">
      <header className="pb-invite__fresh-head">
        <span className="pb-label">Shown once</span>
        <h3 className="pb-invite__fresh-title">
          {claimName ? `Link for ${claimName}` : "Your invite link"}
        </h3>
      </header>

      <code className="pb-invite__link" ref={linkRef}>
        {shareableInviteLink(invite.token)}
      </code>

      <div className="pb-invite__copy-row">
        <PrimaryButton onClick={onCopy}>{COPY_LABELS[copyState]}</PrimaryButton>
        <span className="pb-invite__meta" aria-live="polite">
          {copyState === "idle" ? expiryLabel(invite.expiresAt) : COPY_LABELS[copyState]}
        </span>
      </div>

      <p className="pb-invite__warn">
        Copy it now — we can’t show this link again. If it gets lost, create
        another one.
      </p>
    </section>
  );
}
