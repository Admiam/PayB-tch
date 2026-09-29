/**
 * "Who is who" — the list where a human says that this row and that row are the
 * same person.
 *
 * One rule shapes the whole interaction: two rows in the *same* group can never
 * be one person, so a candidate from a group this person already occupies is
 * never offered. The other constraint is the opposite kind — rows the server
 * matched by account are shown locked, because there is nothing to disagree
 * with: it is one login, and letting someone "unpair" it would only produce a
 * view that quietly disagrees with the groups it is built from.
 */

import { useState } from "react";
import { canMerge } from "@/domain/people";
import { Icon } from "@/icons";
import { personFace, type SharedDebtView } from "@/lib/useSharedDebt";
import { Avatar } from "./Avatar";
import { glass } from "./primitives";

export function PersonPairing({
  view,
  busy,
  onLink,
  onUnlink,
}: {
  view: SharedDebtView;
  /** A save is in flight; every control locks rather than queueing edits. */
  busy: boolean;
  onLink: (memberId: string, otherMemberId: string) => void;
  onUnlink: (memberId: string) => void;
}) {
  const [pickerFor, setPickerFor] = useState<string | null>(null);

  const groupName = (groupId: string) =>
    view.includedGroups.find((group) => group.id === groupId)?.name ?? "—";
  const groupOf = (memberId: string) =>
    view.roster.find((member) => member.id === memberId)?.groupId ?? "";

  // Me first — the server settled that one — then whoever is already matched,
  // then the rows still standing alone, which is the work left to do.
  const ordered = [...view.people].sort((a, b) => {
    if (a.isMe !== b.isMe) return a.isMe ? -1 : 1;
    if (a.memberIds.length !== b.memberIds.length) {
      return b.memberIds.length - a.memberIds.length;
    }
    return a.name.localeCompare(b.name);
  });

  return (
    <div className="pb-pairing">
      {ordered.map((person) => {
        const open = pickerFor === person.id;
        const candidates = open
          ? ordered.filter((other) => canMerge(person, other, view.roster))
          : [];

        return (
          <div className="pb-pairing__person" key={person.id}>
            <div className="pb-pairing__head">
              <Avatar member={personFace(person)} size={36} isMe={person.isMe} />

              <span className="pb-pairing__identity">
                <span className="pb-pairing__name">{person.name}</span>
                {person.aliases.length > 0 && (
                  <span className="pb-pairing__aliases">
                    also {person.aliases.join(", ")}
                  </span>
                )}
              </span>

              <button
                type="button"
                className={`pb-pairing__add ${glass()}`}
                aria-expanded={open}
                disabled={busy}
                onClick={() => setPickerFor(open ? null : person.id)}
              >
                {open ? "Cancel" : "Same as…"}
              </button>
            </div>

            <div className="pb-pairing__rows">
              {person.memberIds.map((memberId) => {
                const locked = person.autoMemberIds.includes(memberId);
                const removable = person.memberIds.length > 1 && !locked;

                return (
                  <span className="pb-pairing__chip" key={memberId}>
                    <span className="pb-pairing__chip-group">
                      {groupName(groupOf(memberId))}
                    </span>
                    {locked && person.memberIds.length > 1 && (
                      <span
                        className="pb-pairing__chip-lock"
                        title="Matched by account"
                      >
                        <Icon name="checkmark.circle.fill" size={11} />
                      </span>
                    )}
                    {removable && (
                      <button
                        type="button"
                        className="pb-pairing__chip-remove"
                        aria-label={`Unpair ${groupName(groupOf(memberId))}`}
                        disabled={busy}
                        onClick={() => onUnlink(memberId)}
                      >
                        <Icon name="xmark" size={9} strokeWidth={3} />
                      </button>
                    )}
                  </span>
                );
              })}
            </div>

            {open && (
              <div className="pb-pairing__picker" role="listbox">
                {candidates.length === 0 ? (
                  <p className="pb-pairing__empty">
                    Nobody left to match — every other person is already in one
                    of {person.name}&rsquo;s groups.
                  </p>
                ) : (
                  candidates.map((candidate) => (
                    <button
                      key={candidate.id}
                      type="button"
                      role="option"
                      aria-selected={false}
                      className="pb-pairing__candidate"
                      disabled={busy}
                      onClick={() => {
                        setPickerFor(null);
                        onLink(person.memberIds[0], candidate.memberIds[0]);
                      }}
                    >
                      <Avatar
                        member={personFace(candidate)}
                        size={28}
                        isMe={candidate.isMe}
                      />
                      <span className="pb-pairing__candidate-name">
                        {candidate.name}
                      </span>
                      <span className="pb-pairing__candidate-groups">
                        {candidate.memberIds
                          .map((memberId) => groupName(groupOf(memberId)))
                          .join(", ")}
                      </span>
                    </button>
                  ))
                )}
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}
