/**
 * Settings — appearance, "who am I", the member roster. Ported from
 * `SettingsSheet.swift`, plus two pieces the phone app has no equivalent for:
 *
 *  - Export: the phone app's data lives in the app sandbox, which the user
 *    never touches directly. A browser tab has no such sandbox the user can
 *    dig into, so exporting is the only way to get a copy out.
 *  - Reset: same asymmetry in reverse — uninstalling clears an iOS app's data
 *    for free; a browser tab needs an explicit "forget everything" action.
 *
 * The About section is rewritten for the same reason: there is no bundle
 * version to report here, so it carries a privacy note instead — worth
 * saying explicitly, since a site that stores money data would otherwise
 * reasonably be assumed to have a server behind it. This build has none.
 */

import { useState } from "react";
import { Avatar } from "@/components/Avatar";
import {
  DestructiveButton,
  FieldGroup,
  InlineRow,
  PrimaryButton,
} from "@/components/primitives";
import { Sheet } from "@/components/Sheet";
import type { Appearance, Member } from "@/domain/types";
import { Icon } from "@/icons";
import { storage } from "@/lib/storage";
import { useStore } from "@/store/useStore";
import { AddMemberSheet } from "./AddMemberSheet";

const APPEARANCE_OPTIONS: { value: Appearance; label: string }[] = [
  { value: "system", label: "System" },
  { value: "light", label: "Light" },
  { value: "dark", label: "Dark" },
];

export function SettingsSheet({
  open,
  onClose,
}: {
  open: boolean;
  onClose: () => void;
}) {
  const members = useStore((s) => s.members);
  const currentUserId = useStore((s) => s.currentUserId);
  const appearance = useStore((s) => s.appearance);
  const setAppearance = useStore((s) => s.setAppearance);
  const setCurrentUser = useStore((s) => s.setCurrentUser);
  const reset = useStore((s) => s.reset);

  const [pickerOpen, setPickerOpen] = useState(false);
  const [memberSheetOpen, setMemberSheetOpen] = useState(false);
  const [editingMember, setEditingMember] = useState<Member | null>(null);

  const currentUser = members.find((m) => m.id === currentUserId) ?? null;
  const activeIndex = Math.max(
    0,
    APPEARANCE_OPTIONS.findIndex((o) => o.value === appearance),
  );

  const openNewMember = () => {
    setEditingMember(null);
    setMemberSheetOpen(true);
  };

  const openEditMember = (member: Member) => {
    setEditingMember(member);
    setMemberSheetOpen(true);
  };

  const closeMemberSheet = () => {
    setMemberSheetOpen(false);
    setEditingMember(null);
  };

  const exportData = () => {
    const payload = storage.exportAll();
    const blob = new Blob([JSON.stringify(payload, null, 2)], {
      type: "application/json",
    });
    const url = URL.createObjectURL(blob);
    const isoDate = new Date().toISOString().slice(0, 10);

    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = `paybitch-export-${isoDate}.json`;
    document.body.appendChild(anchor);
    anchor.click();
    document.body.removeChild(anchor);
    URL.revokeObjectURL(url);
  };

  const resetApp = () => {
    const confirmed = window.confirm(
      "Reset the app? This permanently erases every group, member, and expense stored in this browser.",
    );
    if (confirmed) reset();
  };

  return (
    <>
      <Sheet
        open={open}
        onClose={onClose}
        title="Settings"
        action={<PrimaryButton onClick={onClose}>Done</PrimaryButton>}
      >
        <div className="pb-settings">
          <FieldGroup label="Appearance">
            <div className="pb-settings-seg-pad">
              <div
                className="pb-settings-seg"
                role="radiogroup"
                aria-label="Appearance"
              >
                <span
                  className="pb-settings-seg__thumb"
                  style={{ transform: `translateX(${activeIndex * 100}%)` }}
                  aria-hidden="true"
                />
                {APPEARANCE_OPTIONS.map((option) => {
                  const active = option.value === appearance;
                  return (
                    <button
                      key={option.value}
                      type="button"
                      role="radio"
                      aria-checked={active}
                      className={`pb-settings-seg__btn${active ? " is-active" : ""}`}
                      onClick={() => setAppearance(option.value)}
                    >
                      {option.label}
                    </button>
                  );
                })}
              </div>
            </div>
          </FieldGroup>

          <FieldGroup label="Profile">
            {members.length === 0 ? (
              <p className="pb-settings-empty">
                No members yet — add someone first.
              </p>
            ) : (
              <div>
                <button
                  type="button"
                  className="pb-row"
                  aria-expanded={pickerOpen}
                  onClick={() => setPickerOpen((v) => !v)}
                >
                  <span>I am</span>
                  <span className="pb-row__value">
                    {currentUser?.name ?? "—"}
                    <Chevron />
                  </span>
                </button>

                {pickerOpen && (
                  <>
                    <hr className="pb-divider" />
                    <div className="pb-settings-picker">
                      {members.map((m) => {
                        const isCurrent = m.id === currentUserId;
                        return (
                          <button
                            key={m.id}
                            type="button"
                            className={`pb-settings-picker__item${isCurrent ? " is-current" : ""}`}
                            onClick={() => {
                              setCurrentUser(m.id);
                              setPickerOpen(false);
                            }}
                          >
                            <span>{m.name}</span>
                            {isCurrent && (
                              <Icon name="checkmark.circle.fill" size={16} />
                            )}
                          </button>
                        );
                      })}
                    </div>
                  </>
                )}
              </div>
            )}
          </FieldGroup>

          <FieldGroup label="Members">
            <div>
              {members.map((m) => (
                <div key={m.id}>
                  <button
                    type="button"
                    className="pb-settings-member"
                    onClick={() => openEditMember(m)}
                  >
                    <Avatar member={m} size={36} isMe={m.id === currentUserId} />
                    <span className="pb-settings-member__info">
                      <span className="pb-settings-member__name">{m.name}</span>
                      {m.id === currentUserId && (
                        <span className="pb-settings-member__caption">
                          This is me
                        </span>
                      )}
                    </span>
                    <Chevron />
                  </button>
                  <hr className="pb-divider" />
                </div>
              ))}

              <button
                type="button"
                className="pb-settings-new-member"
                onClick={openNewMember}
              >
                <span className="pb-settings-plus-badge" aria-hidden="true">
                  <Icon name="plus" size={18} strokeWidth={3} />
                </span>
                <span>New member</span>
              </button>
            </div>
          </FieldGroup>

          <FieldGroup label="Data">
            <InlineRow
              label="Export data"
              value={<Icon name="arrow.down" size={16} strokeWidth={2.6} />}
              onClick={exportData}
              divider={false}
            />
          </FieldGroup>

          <div className="pb-settings-danger">
            <DestructiveButton onClick={resetApp}>Reset app</DestructiveButton>
            <p className="pb-settings-danger__hint">
              Permanently erases every group, member, and expense stored in
              this browser.
            </p>
          </div>

          <FieldGroup label="About">
            <p className="pb-settings-about">
              Your data lives only in this browser&rsquo;s local storage.
              Nothing is ever uploaded to a server.
            </p>
          </FieldGroup>

          <div className="pb-settings-wordmark">
            <span className="pb-settings-wordmark__text">paybitch</span>
            <span className="pb-settings-wordmark__tagline">
              sweet &amp; sour math
            </span>
          </div>
        </div>
      </Sheet>

      <AddMemberSheet
        open={memberSheetOpen}
        editing={editingMember}
        onClose={closeMemberSheet}
      />
    </>
  );
}

function Chevron() {
  return (
    <span className="pb-settings-chevron" aria-hidden="true">
      <Icon name="chevron.right" size={12} strokeWidth={3} />
    </span>
  );
}
