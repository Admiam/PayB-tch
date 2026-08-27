/**
 * Sign-in — the only way into a session. Two steps behind one full-screen
 * cover, driven entirely by `useAuth().pendingEmail`: no pending email means
 * "enter your address", a pending email means "enter the code that went to
 * it". Same cover, hero heading and footer CTA as `Onboarding` — this is the
 * true front door, so it borrows that screen's treatment rather than
 * inventing a second one.
 */

import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type ChangeEvent,
  type FormEvent,
  type ReactElement,
} from "react";
import { FullScreenCover } from "@/components/Sheet";
import { TextButton, Wordmark, glass } from "@/components/primitives";
import { useAuth } from "@/store/useAuth";

export function SignIn(): ReactElement {
  const pendingEmail = useAuth((s) => s.pendingEmail);
  const busy = useAuth((s) => s.busy);
  const error = useAuth((s) => s.error);
  const requestCode = useAuth((s) => s.requestCode);
  const verifyCode = useAuth((s) => s.verifyCode);
  const cancelCode = useAuth((s) => s.cancelCode);
  const clearError = useAuth((s) => s.clearError);

  const [email, setEmail] = useState("");
  const [code, setCode] = useState("");
  // Which 6-digit value auto-submit has already tried, so a failed code
  // doesn't refire on every render — only a genuinely new value, or an
  // explicit tap on "Verify", tries again.
  const autoSubmitted = useRef<string | null>(null);

  // A fresh code screen always starts blank, even the second time round
  // after "Wrong address?" sent the user back for another go.
  useEffect(() => {
    setCode("");
    autoSubmitted.current = null;
  }, [pendingEmail]);

  const attemptVerify = useCallback(
    (value: string) => {
      if (value.length !== 6 || busy) return;
      autoSubmitted.current = value;
      void verifyCode(value);
    },
    [busy, verifyCode],
  );

  // Submits itself the moment six digits land, so the user only has to type.
  useEffect(() => {
    if (code.length === 6 && autoSubmitted.current !== code) {
      attemptVerify(code);
    }
  }, [code, attemptVerify]);

  const handleEmailChange = (e: ChangeEvent<HTMLInputElement>) => {
    setEmail(e.target.value);
    if (error) clearError();
  };

  const handleCodeChange = (e: ChangeEvent<HTMLInputElement>) => {
    setCode(e.target.value.replace(/\D/g, "").slice(0, 6));
    if (error) clearError();
  };

  const handleEmailSubmit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (busy || !email.trim()) return;
    void requestCode(email);
  };

  const handleCodeSubmit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    attemptVerify(code);
  };

  const canSend = !busy && Boolean(email.trim());
  const canVerify = !busy && code.length === 6;

  return (
    <FullScreenCover open>
      <div className="pb-onboarding__glow" aria-hidden="true" />

      {pendingEmail === null ? (
        <form className="pb-onboarding" onSubmit={handleEmailSubmit}>
          <Wordmark />

          <div className="pb-onboarding__step">
            <div className="pb-onboarding__intro-copy">
              <h1 className="pb-onboarding__heading">
                {"What's your email?"}
              </h1>
              <p className="pb-onboarding__lede">
                {"No password to remember. We'll email you a one-time code."}
              </p>
            </div>

            <input
              className="pb-input pb-input--hero"
              type="email"
              inputMode="email"
              autoComplete="email"
              autoCapitalize="off"
              autoCorrect="off"
              spellCheck={false}
              autoFocus
              name="email"
              value={email}
              onChange={handleEmailChange}
              placeholder="you@example.com"
              aria-label="Email address"
            />

            {error && (
              <p className="pb-auth__error" role="alert">
                {error}
              </p>
            )}
          </div>

          <div className="pb-onboarding__spacer" />

          <div className="pb-onboarding__footer">
            <button
              type="submit"
              disabled={!canSend}
              className={`pb-onboarding__cta pb-onboarding__cta--filled ${glass("accent", { dimmed: !canSend })}`}
            >
              Send me a code
            </button>
          </div>
        </form>
      ) : (
        <form className="pb-onboarding" onSubmit={handleCodeSubmit}>
          <Wordmark />

          <div className="pb-onboarding__step">
            <div className="pb-onboarding__intro-copy">
              <h1 className="pb-onboarding__heading">{"What's the code?"}</h1>
              <p className="pb-onboarding__lede">
                {"We've sent a code to "}
                <span className="pb-auth__email">{pendingEmail}</span>
                {" if it's a Paybitch address."}
              </p>
            </div>

            <TextButton onClick={cancelCode}>Wrong address?</TextButton>

            <div className="pb-onboarding__intro-copy">
              <input
                className="pb-input pb-input--hero pb-auth__code-input pb-tabular"
                type="text"
                inputMode="numeric"
                pattern="[0-9]*"
                autoComplete="one-time-code"
                maxLength={6}
                autoFocus
                name="code"
                value={code}
                onChange={handleCodeChange}
                aria-label="6-digit code"
              />
              <p className="pb-auth__hint">Code expires in 10 minutes.</p>
            </div>

            {error && (
              <p className="pb-auth__error" role="alert">
                {error}
              </p>
            )}
          </div>

          <div className="pb-onboarding__spacer" />

          <div className="pb-onboarding__footer">
            <button
              type="submit"
              disabled={!canVerify}
              className={`pb-onboarding__cta pb-onboarding__cta--filled ${glass("accent", { dimmed: !canVerify })}`}
            >
              {busy ? "Verifying…" : "Verify"}
            </button>
          </div>
        </form>
      )}
    </FullScreenCover>
  );
}
