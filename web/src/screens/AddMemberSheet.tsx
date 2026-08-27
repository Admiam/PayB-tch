/**
 * Create / edit a member — name, avatar glyph, live preview. Ported from
 * `AddMemberSheet.swift`.
 *
 * One addition beyond the phone app: a delete action. The Swift source has
 * no delete affordance anywhere in the UI even though the store supports it
 * (see spec appendix, genuine ambiguity #8) — on the web, with no App Store
 * uninstall to fall back on for "get rid of a person I added by mistake",
 * leaving that unreachable would be a real gap. It never applies to the
 * current user: removing "me" would leave the app with no signed-in member.
 */

import { useState } from "react";
import { Avatar } from "@/components/Avatar";
import { DestructiveButton, FieldGroup, PrimaryButton } from "@/components/primitives";
import { Sheet } from "@/components/Sheet";
import type { Member } from "@/domain/types";
import { AVATAR_SYMBOLS, Icon } from "@/icons";
import { useStore } from "@/store/useStore";

/** "cat.fill" -> "cat", "sun.max.fill" -> "sun max" — a readable a11y label. */
function labelForSymbol(symbol: string): string {
  return symbol.replace(/\.fill$/, "").replace(/\./g, " ");
}

export function AddMemberSheet({
  open,
  editing,
  onClose,
  onSaved,
}: {
  open: boolean;
  editing: Member | null;
  onClose: () => void;
  onSaved?: (member: Member) => void;
}) {
  const currentUserId = useStore((s) => s.currentUserId);
  const addMember = useStore((s) => s.addMember);
  const updateMember = useStore((s) => s.updateMember);
  const deleteMember = useStore((s) => s.deleteMember);

  const [name, setName] = useState(editing?.name ?? "");
  const [iconSymbol, setIconSymbol] = useState<string | null>(editing?.iconSymbol ?? null);

  // Re-seed the form from the edited member (or blank, for a new one) every
  // time the sheet opens, rather than leaking the previous session's draft.
  // Adjusted during render (React's documented pattern for this) instead of
  // in an effect, so the reset lands in the same commit as the open — no
  // one-frame flash of the previous member's data.
  const [prevOpen, setPrevOpen] = useState(open);
  if (open !== prevOpen) {
    setPrevOpen(open);
    if (open) {
      setName(editing?.name ?? "");
      setIconSymbol(editing?.iconSymbol ?? null);
    }
  }

  const trimmedName = name.trim();
  const canSave = trimmedName.length > 0;
  const isCurrentUser = editing !== null && editing.id === currentUserId;

  // Synthetic member for the live preview — mirrors the Swift `previewMember`,
  // right down to the palette color coming from the literal id "preview"
  // while creating rather than the eventual real id.
  const previewMember: Member = {
    id: editing?.id ?? "preview",
    name: trimmedName || "?",
    imageUrl: editing?.imageUrl ?? null,
    iconSymbol,
  };

  const handleSave = async () => {
    if (!canSave) return;

    if (editing) {
      const updated: Member = { ...editing, name: trimmedName, iconSymbol };
      await updateMember(updated);
      onSaved?.(updated);
    } else {
      const created = await addMember({
        name: trimmedName,
        imageUrl: null,
        iconSymbol,
      });
      // A null means the server refused; the store has already put a message in
      // lastError, so closing silently would hide it.
      if (!created) return;
      onSaved?.(created);
    }
    onClose();
  };

  const handleDelete = async () => {
    if (!editing || isCurrentUser) return;
    const confirmed = window.confirm(
      `Remove ${editing.name}? They’ll be taken out of every group. This can’t be undone.`,
    );
    if (!confirmed) return;
    await deleteMember(editing.id);
    onClose();
  };

  return (
    <Sheet
      open={open}
      onClose={onClose}
      title={editing ? "Edit member" : "New member"}
      action={
        <PrimaryButton onClick={handleSave} disabled={!canSave}>
          Save
        </PrimaryButton>
      }
    >
      <div className="pb-member-form">
        <div className="pb-member-preview">
          <Avatar member={previewMember} size={88} />
        </div>

        <FieldGroup label="Name">
          <input
            className="pb-input pb-input--row"
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="Type a name"
            autoComplete="off"
          />
        </FieldGroup>

        <FieldGroup label="Profile icon">
          <div className="pb-member-icon-grid">
            <button
              type="button"
              className={`pb-member-icon-tile${iconSymbol === null ? " is-selected" : ""}`}
              aria-label="Use initials"
              aria-pressed={iconSymbol === null}
              onClick={() => setIconSymbol(null)}
            >
              Aa
            </button>
            {AVATAR_SYMBOLS.map((symbol) => (
              <button
                key={symbol}
                type="button"
                className={`pb-member-icon-tile${iconSymbol === symbol ? " is-selected" : ""}`}
                aria-label={labelForSymbol(symbol)}
                aria-pressed={iconSymbol === symbol}
                onClick={() => setIconSymbol(symbol)}
              >
                <Icon name={symbol} size={20} strokeWidth={2.2} />
              </button>
            ))}
          </div>
        </FieldGroup>

        {editing &&
          (isCurrentUser ? (
            <p className="pb-member-delete-hint">
              You can’t delete yourself — switch “I am” to someone else in
              Settings first.
            </p>
          ) : (
            <DestructiveButton onClick={handleDelete}>
              Delete member
            </DestructiveButton>
          ))}
      </div>
    </Sheet>
  );
}
