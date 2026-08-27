/**
 * The page behind `/invite/:token`.
 *
 * This is the only screen a stranger ever sees, so it renders its own
 * background and chrome rather than sitting inside the app shell — the same
 * shape as onboarding. `invites.preview` is public, which is what lets the
 * group name and inviter show *before* anyone is asked to sign in; accepting
 * is the part that needs a session.
 */

import { useEffect, useState, type ReactElement } from "react";
import { ApiError } from "@/api/client";
import { invites, type InvitePreview } from "@/api/endpoints";
import { Wordmark, glass } from "@/components/primitives";
import { Icon } from "@/icons";
import { useAuth } from "@/store/useAuth";
import { expiryLabel, isExpired } from "@/screens/InviteSheet";

/**
 * Where the token waits while the recipient signs in. Mirrors the key the app
 * shell reads back; it is a storage contract, not a shared module, so both
 * sides state the literal.
 */
const PENDING_INVITE_KEY = "paybitch.pendingInvite";

/** Codes that mean "you are in this group already", which is not a failure. */
const ALREADY_MEMBER_CODES = new Set([
  "already_member",
  "member_exists",
  "invite_already_accepted",
]);

interface Failure {
  title: string;
  body: string;
}

const EXPIRED: Failure = {
  title: "This invite has expired",
  body: "Invite links last a week. Ask whoever sent it to create a fresh one — it only takes a second.",
};

const NOT_FOUND: Failure = {
  title: "We can’t find this invite",
  body: "The link may be incomplete or mistyped. Check you copied the whole thing, including the last few characters.",
};

function failureFor(error: unknown): Failure {
  if (!(error instanceof ApiError)) {
    return {
      title: "Something went wrong",
      body: "We couldn’t reach Paybitch. Check your connection and open the link again.",
    };
  }

  switch (error.code) {
    case "invite_consumed":
      return {
        title: "This invite was already used",
        body: "Invite links work exactly once, and someone has already claimed this one. Ask for a new link of your own.",
      };
    case "invite_expired":
      return EXPIRED;
    case "invite_revoked":
      return {
        title: "This invite was cancelled",
        body: "Whoever created the link has since revoked it. Ask them to send a new one.",
      };
    case "invite_not_found":
      return NOT_FOUND;
    default:
      break;
  }

  // The code is the closed set worth branching on, but the status still tells
  // us enough to say something specific when a new code turns up.
  if (error.status === 410) {
    return {
      title: "This invite is no longer valid",
      body: "It has been used, revoked, or has expired. Ask for a fresh link and you’re in.",
    };
  }
  if (error.status === 404) return NOT_FOUND;
  if (error.status === 401) {
    return {
      title: "Your session expired",
      body: "Sign in again, then open this link a second time to finish joining.",
    };
  }
  if (error.status === 403) {
    return {
      title: "This invite isn’t for you",
      body: "It was issued for a different account. Ask for a link addressed to the account you’re signed in with.",
    };
  }

  return { title: "Something went wrong", body: error.message };
}

function rememberToken(token: string): void {
  try {
    sessionStorage.setItem(PENDING_INVITE_KEY, token);
  } catch {
    // Private browsing blocks session storage. The link still works — the
    // recipient just has to open it again once they are signed in.
  }
}

function forgetToken(): void {
  try {
    sessionStorage.removeItem(PENDING_INVITE_KEY);
  } catch {
    // Nothing was stored in the first place.
  }
}

export function InviteLanding({ token }: { token: string }): ReactElement {
  const status = useAuth((s) => s.status);

  const [preview, setPreview] = useState<InvitePreview | null>(null);
  const [loading, setLoading] = useState(true);
  const [failure, setFailure] = useState<Failure | null>(null);
  const [accepting, setAccepting] = useState(false);
  const [alreadyMember, setAlreadyMember] = useState(false);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setFailure(null);

    void (async () => {
      try {
        const result = await invites.preview(token);
        if (!cancelled) setPreview(result);
      } catch (error) {
        if (!cancelled) setFailure(failureFor(error));
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [token]);

  // Held so the join can be finished after signing in — by then the token is
  // no longer in the address bar.
  useEffect(() => {
    if (status === "signed-out") rememberToken(token);
  }, [status, token]);

  const accept = async () => {
    setAccepting(true);
    setFailure(null);

    try {
      await invites.accept(token);
      forgetToken();
      window.location.assign("/");
    } catch (error) {
      if (error instanceof ApiError && ALREADY_MEMBER_CODES.has(error.code)) {
        forgetToken();
        setAlreadyMember(true);
      } else {
        setFailure(failureFor(error));
      }
      setAccepting(false);
    }
  };

  const expired = preview !== null && isExpired(preview.expiresAt);

  return (
    <>
      <div className="pb-app-bg" aria-hidden="true" />
      <div className="pb-invite-landing__glow" aria-hidden="true" />

      <main className="pb-invite-landing">
        <Wordmark />

        {loading ? (
          <p className="pb-invite-landing__checking">Checking this invite…</p>
        ) : failure ? (
          <FailureCard failure={failure} />
        ) : alreadyMember ? (
          <FailureCard
            failure={{
              title: "You’re already in",
              body: "This group is on your list — nothing left to accept.",
            }}
            action={
              <button
                type="button"
                className={`pb-invite-landing__cta ${glass("accent")}`}
                onClick={() => window.location.assign("/")}
              >
                Open Paybitch
              </button>
            }
          />
        ) : preview ? (
          <section className="pb-invite-landing__card">
            <span className="pb-label pb-invite-landing__eyebrow">
              You’re invited
            </span>

            <h1 className="pb-invite-landing__heading">
              <span className="pb-invite-landing__who">{preview.invitedBy}</span>
              {" wants you in "}
              <span className="pb-invite-landing__group">
                {preview.group.name}
              </span>
            </h1>

            <dl className="pb-invite-landing__stats">
              <div className="pb-invite-landing__stat">
                <dt className="pb-label">People</dt>
                <dd>{memberCountLabel(preview.group.memberCount)}</dd>
              </div>
              <div className="pb-invite-landing__stat">
                <dt className="pb-label">Splits in</dt>
                <dd>{preview.group.defaultCurrency}</dd>
              </div>
            </dl>

            {preview.claim && (
              <p className="pb-invite-landing__claim">
                <span className="pb-invite-landing__claim-icon" aria-hidden="true">
                  <Icon name="checkmark.circle.fill" size={18} />
                </span>
                You’ll join as <strong>{preview.claim.displayName}</strong>, so
                everything already split with them stays yours.
              </p>
            )}

            {expired ? (
              <p className="pb-invite-landing__note" role="alert">
                <strong>{EXPIRED.title}.</strong> {EXPIRED.body}
              </p>
            ) : status === "signed-in" ? (
              <>
                <button
                  type="button"
                  className={`pb-invite-landing__cta ${glass("accent", {
                    dimmed: accepting,
                  })}`}
                  disabled={accepting}
                  onClick={() => void accept()}
                >
                  {accepting ? "Joining…" : "Accept invitation"}
                </button>
                <p className="pb-invite-landing__meta">
                  {expiryLabel(preview.expiresAt)}
                </p>
              </>
            ) : status === "restoring" ? (
              <p className="pb-invite-landing__meta">Checking your session…</p>
            ) : (
              <p className="pb-invite-landing__note">
                <strong>Sign in first.</strong> Paybitch needs to know who you
                are before it can add you to a group. We’ve kept this invite —
                sign in and it will be waiting for you.
              </p>
            )}
          </section>
        ) : null}
      </main>
    </>
  );
}

function memberCountLabel(count: number): string {
  return `${count} ${count === 1 ? "person" : "people"}`;
}

function FailureCard({
  failure,
  action,
}: {
  failure: Failure;
  action?: ReactElement;
}): ReactElement {
  return (
    <section className="pb-invite-landing__card" role="alert">
      <span className="pb-invite-landing__fail-icon" aria-hidden="true">
        <Icon name="questionmark.circle" size={26} />
      </span>
      <h1 className="pb-invite-landing__heading pb-invite-landing__heading--fail">
        {failure.title}
      </h1>
      <p className="pb-invite-landing__note">{failure.body}</p>
      {action}
    </section>
  );
}
