/**
 * First-run flow, ported from `OnboardingView.swift`.
 *
 * Three steps behind a cover with no dismiss gesture: the pitch, the user's
 * name, then their first group. Nothing here closes itself — both exits mark
 * onboarding complete, and the parent stops rendering the cover in response.
 */

import { useState, type CSSProperties } from "react";
import { FullScreenCover } from "@/components/Sheet";
import { TextButton, glass } from "@/components/primitives";
import {
  CURRENCIES,
  CURRENCY_SYMBOLS,
  DEFAULT_CURRENCY,
  type CurrencyCode,
} from "@/domain/types";
import { useStore } from "@/store/useStore";

/** The label format every currency picker in the app uses, e.g. "CZK Kč". */
const currencyLabel = (code: CurrencyCode) =>
  `${code} ${CURRENCY_SYMBOLS[code]}`;

type Step = 0 | 1 | 2;

export function Onboarding({ open }: { open: boolean }) {
  const members = useStore((s) => s.members);
  const currentUserId = useStore((s) => s.currentUserId);
  const addMember = useStore((s) => s.addMember);
  const updateMember = useStore((s) => s.updateMember);
  const setCurrentUser = useStore((s) => s.setCurrentUser);
  const addGroup = useStore((s) => s.addGroup);
  const completeOnboarding = useStore((s) => s.completeOnboarding);

  const [step, setStep] = useState<Step>(0);
  const [name, setName] = useState("");
  const [groupName, setGroupName] = useState("");
  const [currency, setCurrency] = useState<CurrencyCode>(DEFAULT_CURRENCY);
  const [saving, setSaving] = useState(false);
  const [wasOpen, setWasOpen] = useState(false);

  // Wiping the data re-opens the cover; that should start over rather than
  // resume whatever was half-typed the first time round. Adjusted during
  // render, not in an effect, so the first frame is already the fresh one.
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setStep(0);
      setName("");
      setGroupName("");
      setCurrency(DEFAULT_CURRENCY);
      setSaving(false);
    }
  }

  const trimmedName = name.trim();
  const trimmedGroup = groupName.trim();

  /** Rename the existing profile rather than accumulating a second "me". */
  const saveProfile = () => {
    if (!trimmedName) return;
    const existing = members.find((m) => m.id === currentUserId);
    if (existing) {
      updateMember({ ...existing, name: trimmedName });
    } else {
      setCurrentUser(addMember({ name: trimmedName, imageUrl: null }).id);
    }
    setStep(2);
  };

  const saveGroupAndFinish = () => {
    if (!trimmedGroup || saving) return;
    setSaving(true);
    try {
      addGroup({
        name: trimmedGroup,
        memberIds: currentUserId ? [currentUserId] : [],
        defaultCurrency: currency,
      });
      completeOnboarding();
    } finally {
      setSaving(false);
    }
  };

  const cta =
    step === 0
      ? {
          label: "Let's go →",
          filled: true,
          disabled: false,
          onClick: () => setStep(1),
        }
      : step === 1
        ? {
            label: "Next →",
            filled: true,
            disabled: !trimmedName,
            onClick: saveProfile,
          }
        : {
            label: saving ? "Saving…" : "Take me in 🎉",
            filled: false,
            disabled: !trimmedGroup || saving,
            onClick: saveGroupAndFinish,
          };

  // The glow intensifies on the last step — the one moment the flow celebrates.
  const glowStyle = {
    "--pb-glow-strength": step === 2 ? 0.22 : 0.14,
  } as CSSProperties;

  return (
    <FullScreenCover open={open}>
      <div className="pb-onboarding__glow" style={glowStyle} aria-hidden="true" />

      <div className="pb-onboarding">
        {step === 0 && (
          <div className="pb-onboarding__step pb-onboarding__step--intro">
            <h1 className="pb-onboarding__wordmark">{"pay\nbitch."}</h1>

            <div className="pb-onboarding__intro-copy">
              <p className="pb-onboarding__tagline">
                {"Split bills.\nLose no friends."}
              </p>
              <p className="pb-onboarding__lede">
                {"Tell us your name and we'll start a group with your crew."}
              </p>
            </div>
          </div>
        )}

        {step === 1 && (
          <div className="pb-onboarding__step">
            <span className="pb-label pb-onboarding__eyebrow">Step 1 of 2</span>
            <h1 className="pb-onboarding__heading">{"What's your name?"}</h1>

            <input
              className="pb-input pb-input--hero"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="Type your name"
              aria-label="Your name"
              autoCapitalize="off"
              autoCorrect="off"
              spellCheck={false}
            />
          </div>
        )}

        {step === 2 && (
          <div className="pb-onboarding__step">
            <span className="pb-label pb-onboarding__eyebrow">Step 2 of 2</span>
            {/* Deliberately the raw typed name, untrimmed, as on the phone. */}
            <h1 className="pb-onboarding__heading">
              {`Hey ${name}.\nName your first group.`}
            </h1>

            <input
              className="pb-input pb-input--hero"
              value={groupName}
              onChange={(e) => setGroupName(e.target.value)}
              placeholder="e.g. Roommates"
              aria-label="Group name"
              autoCapitalize="off"
              autoCorrect="off"
              spellCheck={false}
            />

            <div
              className="pb-onboarding__currencies"
              role="group"
              aria-label="Default currency"
            >
              {CURRENCIES.map((code) => (
                <button
                  key={code}
                  type="button"
                  aria-pressed={code === currency}
                  onClick={() => setCurrency(code)}
                  className={`pb-onboarding__currency ${glass(
                    code === currency ? "accent" : "neutral",
                  )}`}
                >
                  {currencyLabel(code)}
                </button>
              ))}
            </div>
          </div>
        )}

        <div className="pb-onboarding__spacer" />

        <div className="pb-onboarding__footer">
          <button
            type="button"
            disabled={cta.disabled}
            onClick={cta.onClick}
            className={[
              "pb-onboarding__cta",
              cta.filled && "pb-onboarding__cta--filled",
              glass(cta.filled ? "accent" : "neutral", { dimmed: cta.disabled }),
            ]
              .filter(Boolean)
              .join(" ")}
          >
            {cta.label}
          </button>

          {step === 2 && (
            <TextButton
              className="pb-onboarding__skip"
              onClick={completeOnboarding}
            >
              Skip for now
            </TextButton>
          )}
        </div>
      </div>
    </FullScreenCover>
  );
}
