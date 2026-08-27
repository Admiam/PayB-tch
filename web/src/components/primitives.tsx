/**
 * The shared building blocks, ported from `PaybitchComponents.swift`.
 *
 * `glass()` is the one place that decides what Liquid Glass looks like — every
 * control routes through it so the whole app shares one material, exactly as
 * the Swift does with its single `PaybitchGlassButton` style.
 */

import type { ButtonHTMLAttributes, ReactNode } from "react";
import { Icon } from "@/icons";

type GlassKind = "neutral" | "accent" | "destructive";

export function glass(
  kind: GlassKind = "neutral",
  { interactive = true, dimmed = false } = {},
): string {
  return [
    "pb-glass",
    kind === "accent" && "pb-glass--accent",
    kind === "destructive" && "pb-glass--destructive",
    interactive && "pb-glass--interactive",
    dimmed && "pb-glass--dimmed",
  ]
    .filter(Boolean)
    .join(" ");
}

/* ───────────────────────────────────────────────────────────── wordmark ── */

export function Wordmark({ size }: { size?: number }) {
  return (
    <span className="pb-wordmark" style={size ? { fontSize: size } : undefined}>
      paybitch
    </span>
  );
}

/* ────────────────────────────────────────────────────────────── buttons ── */

type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement>;

export function PrimaryButton({
  children,
  disabled,
  className,
  ...rest
}: ButtonProps) {
  return (
    <button
      type="button"
      disabled={disabled}
      className={[
        "pb-btn",
        "pb-btn--primary",
        glass("accent", { dimmed: Boolean(disabled) }),
        className,
      ]
        .filter(Boolean)
        .join(" ")}
      {...rest}
    >
      {children}
    </button>
  );
}

export function TextButton({ children, className, ...rest }: ButtonProps) {
  return (
    <button
      type="button"
      className={["pb-btn", "pb-btn--text", className].filter(Boolean).join(" ")}
      {...rest}
    >
      {children}
    </button>
  );
}

export function CloseButton(props: ButtonProps) {
  return (
    <button
      type="button"
      aria-label="Close"
      className={`pb-btn pb-btn--close ${glass()}`}
      {...props}
    >
      <Icon name="xmark" size={13} strokeWidth={3} />
    </button>
  );
}

/**
 * Full-width delete action. Deliberately *neutral* glass with a red label —
 * red glass is a different variant used elsewhere.
 */
export function DestructiveButton({
  children,
  icon = "trash",
  className,
  ...rest
}: ButtonProps & { icon?: string }) {
  return (
    <button
      type="button"
      className={["pb-btn", "pb-btn--destructive", glass(), className]
        .filter(Boolean)
        .join(" ")}
      {...rest}
    >
      <Icon name={icon} size={16} />
      {children}
    </button>
  );
}

export function Fab({ onClick }: { onClick: () => void }) {
  return (
    <button
      type="button"
      aria-label="Add expense"
      onClick={onClick}
      className={`pb-fab ${glass("accent")}`}
    >
      <Icon name="plus" size={26} strokeWidth={2.6} />
    </button>
  );
}

/* ──────────────────────────────────────────────────────────────── chips ── */

export function GroupChip({
  name,
  memberCount,
  active,
  onClick,
}: {
  name: string;
  memberCount: number;
  active: boolean;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-pressed={active}
      className={[
        "pb-chip",
        active && "pb-chip--active",
        glass(active ? "accent" : "neutral"),
      ]
        .filter(Boolean)
        .join(" ")}
    >
      {name}
      <span className="pb-chip__count">· {memberCount}</span>
    </button>
  );
}

export function NewGroupChip({ onClick }: { onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`pb-chip pb-chip--new ${glass()}`}
    >
      + New
    </button>
  );
}

export function Badge({
  children,
  rotation = 0,
}: {
  children: ReactNode;
  rotation?: number;
}) {
  return (
    <span
      className="pb-badge"
      style={rotation ? { transform: `rotate(${rotation}deg)` } : undefined}
    >
      {children}
    </span>
  );
}

/* ─────────────────────────────────────────────────────────────── layout ── */

export function SectionHeader({
  title,
  badge,
  trailing,
}: {
  title: string;
  badge?: string | number;
  trailing?: ReactNode;
}) {
  return (
    <div className="pb-section-header">
      <h2 className="pb-section-header__title">{title}</h2>
      {badge !== undefined && (
        <span className="pb-section-header__badge">{badge}</span>
      )}
      <span className="pb-section-header__spacer" />
      {trailing}
    </div>
  );
}

export function FieldGroup({
  label,
  children,
}: {
  label?: string;
  children: ReactNode;
}) {
  return (
    <div className="pb-field-group">
      {label && <span className="pb-label pb-field-group__label">{label}</span>}
      <div className="pb-field-group__body">{children}</div>
    </div>
  );
}

/**
 * A row inside a field group. Renders as a button when it has an action and as
 * a plain div otherwise — an inert row should not be focusable.
 */
export function InlineRow({
  label,
  value,
  onClick,
  divider = true,
}: {
  label: ReactNode;
  value?: ReactNode;
  onClick?: () => void;
  divider?: boolean;
}) {
  const content = (
    <>
      <span>{label}</span>
      {value !== undefined && <span className="pb-row__value">{value}</span>}
    </>
  );

  return (
    <>
      {onClick ? (
        <button type="button" className="pb-row" onClick={onClick}>
          {content}
        </button>
      ) : (
        <div className="pb-row">{content}</div>
      )}
      {divider && <hr className="pb-divider" />}
    </>
  );
}

/* ───────────────────────────────────────────────────────────────── tile ── */

export function Tile({
  label,
  amount,
  currencySymbol,
  sub,
  ratio = 0,
  amountColor,
  big = false,
  icon,
}: {
  label: string;
  amount: string;
  currencySymbol: string;
  sub?: string;
  ratio?: number;
  amountColor?: string;
  big?: boolean;
  icon?: string;
}) {
  const color = amountColor ?? "var(--pb-text-primary)";

  return (
    // Tiles use plain glass, not interactive glass — they don't press.
    <div
      className={["pb-tile", big && "pb-tile--big", glass("neutral", { interactive: false })]
        .filter(Boolean)
        .join(" ")}
    >
      <div className="pb-tile__head">
        <span className="pb-label pb-tile__label">{label}</span>
        {icon && (
          <span style={{ color, opacity: 0.85 }}>
            <Icon name={icon} size={big ? 22 : 18} />
          </span>
        )}
      </div>

      <div className="pb-tile__body">
        <div className="pb-tile__amount" style={{ color }}>
          <span className="pb-tile__figure">{amount}</span>
          <span className="pb-tile__symbol">{currencySymbol}</span>
        </div>

        {ratio > 0 && (
          <div className="pb-tile__bar">
            <div
              className="pb-tile__bar-fill"
              style={{
                width: `${Math.max(0, Math.min(1, ratio)) * 100}%`,
                background: color,
              }}
            />
          </div>
        )}

        {sub && <div className="pb-tile__sub">{sub}</div>}
      </div>
    </div>
  );
}

/* ────────────────────────────────────────────────────────────── notices ── */

export function AllSquaredCard() {
  return (
    <div className={`pb-squared ${glass("neutral", { interactive: false })}`}>
      <div className="pb-squared__title">All squared up</div>
      <div className="pb-squared__body">Nobody owes anybody. Nice.</div>
    </div>
  );
}

export function EmptyExpensesCard({ onAdd }: { onAdd: () => void }) {
  return (
    <div className="pb-empty">
      <div className="pb-empty__icon">
        <Icon name="wallet.bifold.fill" size={44} />
      </div>
      <h3 className="pb-empty__title">Nothing on the tab yet</h3>
      <p className="pb-empty__body">
        Drop the first expense and we&rsquo;ll do the math.
      </p>
      <PrimaryButton onClick={onAdd} style={{ marginTop: 18 }}>
        + Add first expense
      </PrimaryButton>
    </div>
  );
}

export function EmptyGroupsState() {
  return (
    <div
      style={{
        display: "flex",
        flexDirection: "column",
        alignItems: "center",
        textAlign: "center",
        paddingTop: 80,
        color: "var(--pb-text-primary)",
      }}
    >
      <Icon name="person.3" size={44} />
      <h3 className="pb-empty__title">No groups yet</h3>
      <p className="pb-empty__body">Tap + New to create your first group.</p>
    </div>
  );
}
