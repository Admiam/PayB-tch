/**
 * Create or edit a group, ported from `GroupEditorSheet.swift`.
 *
 * The roster lists *every* member the app knows about, not just this group's —
 * toggling is how someone joins or leaves. The current user is pinned on: the
 * store back-fills them into every group anyway, so offering the switch would
 * promise something the model cannot honour.
 */

import { useState } from "react";
import { Avatar } from "@/components/Avatar";
import { Sheet } from "@/components/Sheet";
import {
  DestructiveButton,
  FieldGroup,
  PrimaryButton,
  TextButton,
} from "@/components/primitives";
import { Icon } from "@/icons";
import {
  CURRENCIES,
  CURRENCY_SYMBOLS,
  toCurrency,
  type CurrencyCode,
  type Group,
} from "@/domain/types";
import { displayName, useStore } from "@/store/useStore";

const currencyLabel = (code: CurrencyCode) =>
  `${code} ${CURRENCY_SYMBOLS[code]}`;

/** The current user belongs to every group, so they are never absent. */
function withCurrentUser(ids: string[], currentUserId: string | null): string[] {
  if (!currentUserId || ids.includes(currentUserId)) return ids;
  return [...ids, currentUserId];
}

export function GroupEditorSheet({
  open,
  editing,
  onClose,
  onCreated,
}: {
  open: boolean;
  editing: Group | null;
  onClose: () => void;
  onCreated: (group: Group) => void;
}) {
  const members = useStore((s) => s.members);
  const currentUserId = useStore((s) => s.currentUserId);
  const addGroup = useStore((s) => s.addGroup);
  const updateGroup = useStore((s) => s.updateGroup);
  const deleteGroup = useStore((s) => s.deleteGroup);
  const addMember = useStore((s) => s.addMember);

  const [name, setName] = useState("");
  const [currency, setCurrency] = useState<CurrencyCode>(toCurrency(null));
  const [selected, setSelected] = useState<string[]>([]);
  const [addingMember, setAddingMember] = useState(false);
  const [newMemberName, setNewMemberName] = useState("");
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const [saving, setSaving] = useState(false);
  const [wasOpen, setWasOpen] = useState(false);

  // One instance serves both "new group" and "edit group", so the form is
  // re-seeded on every open rather than on mount. Adjusted during render, not
  // in an effect, so the sheet never paints a frame of the previous group.
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setName(editing?.name ?? "");
      setCurrency(toCurrency(editing?.defaultCurrency));
      setSelected(withCurrentUser(editing?.memberIds ?? [], currentUserId));
      setAddingMember(false);
      setNewMemberName("");
      setConfirmingDelete(false);
      setSaving(false);
    }
  }

  const trimmedName = name.trim();
  const canSave = Boolean(trimmedName) && selected.length > 0 && !saving;

  const toggle = (id: string) =>
    setSelected((current) =>
      current.includes(id)
        ? current.filter((m) => m !== id)
        : [...current, id],
    );

  const confirmNewMember = () => {
    const trimmed = newMemberName.trim();
    if (!trimmed) return;
    const created = addMember({ name: trimmed, imageUrl: null });
    setSelected((current) => [...current, created.id]);
    setNewMemberName("");
    setAddingMember(false);
  };

  const save = () => {
    if (!canSave) return;
    setSaving(true);
    try {
      const input = {
        name: trimmedName,
        memberIds: withCurrentUser(selected, currentUserId),
        defaultCurrency: currency,
      };
      if (editing) {
        updateGroup({ ...editing, ...input });
      } else {
        onCreated(addGroup(input));
      }
      onClose();
    } finally {
      setSaving(false);
    }
  };

  const remove = () => {
    if (!editing) return;
    deleteGroup(editing.id);
    onClose();
  };

  return (
    <Sheet
      open={open}
      onClose={onClose}
      title={editing ? "Edit group" : "New group"}
      action={
        <PrimaryButton onClick={save} disabled={!canSave}>
          Save
        </PrimaryButton>
      }
    >
      <div className="pb-group-editor">
        <FieldGroup label="Group">
          <div className="pb-group-editor__name">
            <span className="pb-label pb-group-editor__name-label">Name</span>
            <input
              className="pb-input pb-group-editor__name-input"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="e.g. Roommates"
              aria-label="Group name"
              autoCapitalize="off"
              autoCorrect="off"
              spellCheck={false}
            />
          </div>

          <hr className="pb-divider" />

          <div className="pb-row pb-currency-row">
            <span aria-hidden="true">Default currency</span>
            <span className="pb-currency-row__value" aria-hidden="true">
              {currencyLabel(currency)}
              <span className="pb-currency-row__chevron">
                <Icon name="chevron.up.chevron.down" size={11} strokeWidth={3} />
              </span>
            </span>

            {/* A native select keeps the keyboard and screen-reader behaviour of
                the iOS Menu; the row above is what it actually looks like. */}
            <select
              className="pb-currency-row__select"
              aria-label="Default currency"
              value={currency}
              onChange={(e) => setCurrency(toCurrency(e.target.value))}
            >
              {CURRENCIES.map((code) => (
                <option key={code} value={code}>
                  {currencyLabel(code)}
                </option>
              ))}
            </select>
          </div>
        </FieldGroup>

        <FieldGroup label={`Members (${selected.length})`}>
          {members.length === 0 ? (
            <p className="pb-group-editor__empty">
              No members yet — add one first.
            </p>
          ) : (
            members.map((member) => {
              const isMe = member.id === currentUserId;
              const on = selected.includes(member.id);

              return (
                <div key={member.id}>
                  <div className="pb-member-row">
                    <Avatar member={member} size={36} isMe={isMe} />
                    <span className="pb-member-row__name">
                      {displayName(members, currentUserId, member.id)}
                    </span>

                    <button
                      type="button"
                      role="switch"
                      aria-checked={on}
                      aria-label={`${member.name} in this group`}
                      disabled={isMe}
                      onClick={() => toggle(member.id)}
                      className="pb-toggle"
                    >
                      <span className="pb-toggle__knob" />
                    </button>
                  </div>
                  <hr className="pb-divider" />
                </div>
              );
            })
          )}

          {addingMember ? (
            <div className="pb-member-add">
              <input
                className="pb-input pb-member-add__input"
                value={newMemberName}
                onChange={(e) => setNewMemberName(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === "Enter") confirmNewMember();
                  if (e.key === "Escape") setAddingMember(false);
                }}
                placeholder="Type a name"
                aria-label="New member name"
                autoCapitalize="off"
                autoCorrect="off"
                spellCheck={false}
                autoFocus
              />
              <PrimaryButton
                onClick={confirmNewMember}
                disabled={!newMemberName.trim()}
              >
                Add
              </PrimaryButton>
            </div>
          ) : (
            <button
              type="button"
              className="pb-member-new"
              onClick={() => setAddingMember(true)}
            >
              <span className="pb-member-new__plus" aria-hidden="true">
                +
              </span>
              New member
            </button>
          )}
        </FieldGroup>

        <p className="pb-group-editor__hint">
          {"Toggle a member to add or remove them. You're always in."}
        </p>

        {editing &&
          (confirmingDelete ? (
            <div className="pb-confirm">
              <p className="pb-confirm__title">Delete this group?</p>
              <DestructiveButton onClick={remove}>
                Delete group and expenses
              </DestructiveButton>
              <TextButton onClick={() => setConfirmingDelete(false)}>
                Cancel
              </TextButton>
            </div>
          ) : (
            <DestructiveButton onClick={() => setConfirmingDelete(true)}>
              Delete group
            </DestructiveButton>
          ))}
      </div>
    </Sheet>
  );
}
