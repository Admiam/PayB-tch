/**
 * Member avatar, ported from `PaybitchAvatar.swift`.
 *
 * Render priority matches the phone app: chosen glyph, then photo, then
 * initials.
 */

import { Icon } from "@/icons";
import type { Member } from "@/domain/types";

/** Same seven colours as `Members.swift`, in the same order. */
const AVATAR_COLORS = [
  "var(--pb-avatar-0)",
  "var(--pb-avatar-1)",
  "var(--pb-avatar-2)",
  "var(--pb-avatar-3)",
  "var(--pb-avatar-4)",
  "var(--pb-avatar-5)",
  "var(--pb-avatar-6)",
];

/**
 * FNV-1a over the member id.
 *
 * The phone app uses Swift's `hashValue`, which is reseeded every process
 * launch — so a member's colour there actually changes between restarts. A
 * fixed hash is deliberately *more* stable than the original rather than a
 * compromise.
 */
function hashString(value: string): number {
  let hash = 0x811c9dc5;
  for (let i = 0; i < value.length; i += 1) {
    hash ^= value.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return hash;
}

export function avatarColor(id: string): string {
  return AVATAR_COLORS[hashString(id) % AVATAR_COLORS.length];
}

/** First letter of up to two name parts, e.g. "Adam Mika" → "AM". */
export function initialsFor(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean).slice(0, 2);
  if (parts.length === 0) return "?";
  return parts.map((p) => p[0]?.toUpperCase() ?? "").join("");
}

export interface AvatarProps {
  member: Member | null;
  size?: number;
  isMe?: boolean;
  showRing?: boolean;
}

export function Avatar({
  member,
  size = 48,
  isMe = false,
  showRing = false,
}: AvatarProps) {
  const id = member?.id ?? "unknown";
  const name = member?.name ?? "";

  return (
    <div
      className="pb-avatar"
      style={{
        width: size,
        height: size,
        background: avatarColor(id),
        fontSize: size * 0.36,
      }}
    >
      {member?.iconSymbol ? (
        <Icon name={member.iconSymbol} size={size * 0.46} strokeWidth={2.2} />
      ) : member?.imageUrl ? (
        <img
          src={member.imageUrl}
          alt=""
          style={{
            width: "100%",
            height: "100%",
            borderRadius: "50%",
            objectFit: "cover",
          }}
        />
      ) : (
        <span aria-hidden="true">{initialsFor(name)}</span>
      )}

      {showRing && <span className="pb-avatar__ring" />}
      {isMe && <span className="pb-avatar__me">ME</span>}

      {/* The visual is decorative; this is what a screen reader announces. */}
      <span className="pb-visually-hidden">{isMe ? "Me" : name}</span>
    </div>
  );
}
