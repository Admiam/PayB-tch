/**
 * Create or amend an expense, ported from `AddExpenseSheet.swift` together with
 * `SplitEditor.swift`.
 *
 * The split editor lives in this file because it is not a screen — it edits
 * this form's state through callbacks, exactly as the Swift binds into its
 * parent. Every number it shows comes out of `domain/money`; nothing here does
 * its own arithmetic on money.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { Avatar } from "@/components/Avatar";
import { Sheet } from "@/components/Sheet";
import {
  DestructiveButton,
  FieldGroup,
  PrimaryButton,
  TextButton,
  glass,
} from "@/components/primitives";
import {
  computeShares,
  decimalsFor,
  formatAmountFull,
  parseAmount,
  roundBankers,
  sumAt,
} from "@/domain/money";
import {
  CURRENCIES,
  CURRENCY_SYMBOLS,
  SPLIT_EQUAL,
  splitKind,
  toCurrency,
  type CurrencyCode,
  type Expense,
  type Group,
  type Member,
  type SplitKind,
  type SplitType,
} from "@/domain/types";
import { Icon, symbolForTitle } from "@/icons";
import { displayName, useStore } from "@/store/useStore";
import { IconPickerSheet } from "./IconPickerSheet";

/** A split counts as balanced below this — the same epsilon the domain uses. */
const BALANCE_EPSILON = 0.01;
/** Scale the exact-split arithmetic runs at. 2dp is the epsilon's own grain, so
 *  the delta stays meaningful even for a zero-decimal currency like CZK. */
const DELTA_SCALE = 2;
const MAX_WEIGHT = 20;

/** The label format every currency picker in the app uses, e.g. "CZK Kč". */
const currencyLabel = (code: CurrencyCode) => `${code} ${CURRENCY_SYMBOLS[code]}`;

interface FormState {
  title: string;
  /** Raw text, not a number — it has to survive a half-typed "12," . */
  amountText: string;
  currency: CurrencyCode;
  paidBy: string;
  splitAmong: string[];
  kind: SplitKind;
  /** Per-member exact amounts, also raw text. */
  exact: Record<string, string>;
  weights: Record<string, number>;
  /** ISO-8601, so the original clock time survives a date change. */
  date: string;
  notes: string;
  iconSymbol: string | null;
}

export function AddExpenseSheet({
  open,
  group,
  editing,
  onClose,
}: {
  open: boolean;
  group: Group;
  editing: Expense | null;
  onClose: () => void;
}) {
  const members = useStore((s) => s.members);
  const currentUserId = useStore((s) => s.currentUserId);
  const addExpense = useStore((s) => s.addExpense);
  const updateExpense = useStore((s) => s.updateExpense);
  const deleteExpense = useStore((s) => s.deleteExpense);

  const [form, setForm] = useState<FormState>(() =>
    initialForm(group, editing, currentUserId),
  );
  const [saving, setSaving] = useState(false);
  const [iconPickerOpen, setIconPickerOpen] = useState(false);
  const [confirmingDelete, setConfirmingDelete] = useState(false);

  // Re-seed only on the closed → open transition. Keying off `group` directly
  // would wipe half-typed input whenever anything in the store changed.
  const wasOpen = useRef(false);
  useEffect(() => {
    if (open && !wasOpen.current) {
      setForm(initialForm(group, editing, currentUserId));
      setSaving(false);
      setIconPickerOpen(false);
      setConfirmingDelete(false);
    }
    wasOpen.current = open;
  }, [open, group, editing, currentUserId]);

  const patch = useCallback(
    (next: Partial<FormState>) => setForm((current) => ({ ...current, ...next })),
    [],
  );

  const decimals = decimalsFor(form.currency);
  const amount = parseAmount(form.amountText);
  const total = amount ?? 0;

  // Group order decides who absorbs the rounding remainder, so the selection is
  // always read back in that order. Participants who have since left the group
  // are kept at the end rather than silently dropped from a saved expense.
  const selected = useMemo(
    () => orderSelection(group.memberIds, form.splitAmong),
    [group.memberIds, form.splitAmong],
  );

  const exactSum = useMemo(
    () =>
      sumAt(
        selected.map((id) => parseAmount(form.exact[id] ?? "") ?? 0),
        DELTA_SCALE,
      ),
    [selected, form.exact],
  );
  const delta = roundBankers(total - exactSum, DELTA_SCALE);
  const totalWeight = selected.reduce(
    (sum, id) => sum + (form.weights[id] ?? 0),
    0,
  );

  const splitValid =
    selected.length > 0 &&
    (form.kind === "equal"
      ? true
      : form.kind === "exact"
        ? amount !== null && Math.abs(delta) < BALANCE_EPSILON
        : totalWeight > 0);

  const canSave =
    form.title.trim() !== "" &&
    amount !== null &&
    amount > 0 &&
    form.paidBy !== "" &&
    splitValid &&
    !saving;

  const resolvedSymbol = form.iconSymbol ?? symbolForTitle(form.title);

  // While a nested surface is up it owns Escape and the backdrop; otherwise the
  // sheet's own handlers would fire first and throw away the whole form.
  const closeSheet = useCallback(() => {
    if (iconPickerOpen || confirmingDelete) return;
    onClose();
  }, [iconPickerOpen, confirmingDelete, onClose]);

  const save = () => {
    if (!canSave || amount === null) return;
    setSaving(true);

    const draft = {
      groupId: group.id,
      title: form.title.trim(),
      amount,
      currency: form.currency,
      paidBy: form.paidBy,
      splitAmong: selected,
      splitType: buildSplit(form, selected),
      date: form.date,
      notes: form.notes === "" ? null : form.notes,
      iconSymbol: form.iconSymbol,
    };

    if (editing) {
      updateExpense({
        ...draft,
        id: editing.id,
        groupId: editing.groupId,
        createdAt: editing.createdAt,
      });
    } else {
      addExpense(draft);
    }

    setSaving(false);
    // The phone plays a success haptic here; this is the closest web analogue.
    navigator.vibrate?.(8);
    onClose();
  };

  const toggleMember = useCallback((id: string) => {
    setForm((current) => {
      const isSelected = current.splitAmong.includes(id);
      return {
        ...current,
        splitAmong: isSelected
          ? current.splitAmong.filter((m) => m !== id)
          : [...current.splitAmong, id],
        // Joining the split earns one share; leaving it keeps the weight, so a
        // mis-tap doesn't discard a carefully set number.
        weights:
          isSelected || current.weights[id] !== undefined
            ? current.weights
            : { ...current.weights, [id]: 1 },
      };
    });
  }, []);

  const setExactText = useCallback((id: string, text: string) => {
    setForm((current) => ({
      ...current,
      exact: { ...current.exact, [id]: sanitizeAmount(text) },
    }));
  }, []);

  const setWeight = useCallback((id: string, weight: number) => {
    const clamped = Math.max(0, Math.min(MAX_WEIGHT, Math.floor(weight)));
    setForm((current) => ({
      ...current,
      weights: { ...current.weights, [id]: clamped },
    }));
  }, []);

  return (
    <>
      <Sheet
        open={open}
        onClose={closeSheet}
        title={editing ? "Edit expense" : "New expense"}
        action={
          <PrimaryButton disabled={!canSave} onClick={save}>
            Save
          </PrimaryButton>
        }
      >
        <div className="pb-expense-form">
          <div className="pb-hero">
            <div className="pb-hero__row">
              <button
                type="button"
                className="pb-hero__icon"
                aria-label="Choose icon"
                onClick={() => setIconPickerOpen(true)}
              >
                <Icon name={resolvedSymbol} size={22} />
                <span className="pb-hero__icon-badge" aria-hidden="true">
                  <Icon name="pencil.circle.fill" size={16} />
                </span>
              </button>

              <input
                className="pb-hero__title"
                aria-label="What was it for?"
                placeholder="What was it for?"
                value={form.title}
                onChange={(e) => patch({ title: e.target.value })}
              />
            </div>

            <div className="pb-hero__amount-row">
              <input
                className="pb-hero__amount pb-tabular"
                aria-label="Amount"
                placeholder="0"
                inputMode="decimal"
                value={form.amountText}
                onChange={(e) =>
                  patch({ amountText: sanitizeAmount(e.target.value) })
                }
              />

              {/* A real <select> rather than a bespoke popover: it is the honest
                  counterpart of a SwiftUI Menu on a phone, and it does not fight
                  the sheet over the Escape key. */}
              <span className="pb-hero__currency">
                <span aria-hidden="true" className="pb-hero__currency-symbol">
                  {CURRENCY_SYMBOLS[form.currency]}
                </span>
                <select
                  className="pb-hero__currency-select"
                  aria-label="Currency"
                  value={form.currency}
                  onChange={(e) => patch({ currency: toCurrency(e.target.value) })}
                >
                  {CURRENCIES.map((code) => (
                    <option key={code} value={code}>
                      {currencyLabel(code)}
                    </option>
                  ))}
                </select>
              </span>
            </div>
          </div>

          <FieldGroup label="Paid by">
            <div className="pb-paidby">
              {group.memberIds.map((id) => {
                const name = displayName(members, currentUserId, id);
                const isActive = id === form.paidBy;
                return (
                  <button
                    key={id}
                    type="button"
                    aria-pressed={isActive}
                    aria-label={name}
                    className={`pb-paidby__pick${isActive ? " is-active" : ""}`}
                    onClick={() => patch({ paidBy: id })}
                  >
                    <Avatar
                      member={members.find((m) => m.id === id) ?? null}
                      size={40}
                      isMe={id === currentUserId}
                    />
                    <span className="pb-paidby__name">{name}</span>
                  </button>
                );
              })}
            </div>
          </FieldGroup>

          <FieldGroup label="Split between">
            <SplitEditor
              memberIds={group.memberIds}
              members={members}
              currentUserId={currentUserId}
              currency={form.currency}
              decimals={decimals}
              total={total}
              kind={form.kind}
              selected={selected}
              exact={form.exact}
              weights={form.weights}
              exactSum={exactSum}
              delta={delta}
              totalWeight={totalWeight}
              onKind={(kind) => patch({ kind })}
              onToggle={toggleMember}
              onExact={setExactText}
              onWeight={setWeight}
            />
          </FieldGroup>

          <FieldGroup label="Date · notes">
            <div className="pb-daterow">
              <span className="pb-daterow__icon" aria-hidden="true">
                <Icon name="calendar" size={18} />
              </span>
              <input
                type="date"
                className="pb-daterow__input"
                aria-label="Date"
                value={toDateInput(form.date)}
                onChange={(e) =>
                  patch({ date: withDatePart(form.date, e.target.value) })
                }
              />
            </div>

            <hr className="pb-divider" />

            <NotesField
              value={form.notes}
              onChange={(notes) => patch({ notes })}
            />
          </FieldGroup>

          {editing && (
            <DestructiveButton onClick={() => setConfirmingDelete(true)}>
              Delete expense
            </DestructiveButton>
          )}
        </div>
      </Sheet>

      <IconPickerSheet
        open={iconPickerOpen}
        selected={form.iconSymbol}
        titleHint={form.title}
        onSelect={(symbol) => patch({ iconSymbol: symbol })}
        onClose={() => setIconPickerOpen(false)}
      />

      {confirmingDelete && editing && (
        <ConfirmDialog
          title="Delete this expense?"
          confirmLabel="Delete"
          onConfirm={() => {
            deleteExpense(editing.id);
            setConfirmingDelete(false);
            onClose();
          }}
          onCancel={() => setConfirmingDelete(false)}
        />
      )}
    </>
  );
}

/* ─────────────────────────────────────────────────────────── split editor ── */

const SPLIT_SEGMENTS: { kind: SplitKind; label: string }[] = [
  { kind: "equal", label: "Equal" },
  { kind: "exact", label: "Exact" },
  { kind: "shares", label: "Shares" },
];

interface SplitEditorProps {
  memberIds: string[];
  members: Member[];
  currentUserId: string | null;
  currency: CurrencyCode;
  decimals: number;
  total: number;
  kind: SplitKind;
  selected: string[];
  exact: Record<string, string>;
  weights: Record<string, number>;
  exactSum: number;
  delta: number;
  totalWeight: number;
  onKind: (kind: SplitKind) => void;
  onToggle: (id: string) => void;
  onExact: (id: string, text: string) => void;
  onWeight: (id: string, weight: number) => void;
}

function SplitEditor({
  memberIds,
  members,
  currentUserId,
  currency,
  decimals,
  total,
  kind,
  selected,
  exact,
  weights,
  exactSum,
  delta,
  totalWeight,
  onKind,
  onToggle,
  onExact,
  onWeight,
}: SplitEditorProps) {
  // The per-head preview is the real split, remainder and all, so what a member
  // sees here is what the ledger will actually charge them.
  const preview = useMemo(() => {
    if (kind === "exact" || total <= 0) return {};
    const split: SplitType =
      kind === "shares"
        ? { shares: Object.fromEntries(selected.map((id) => [id, weights[id] ?? 0])) }
        : SPLIT_EQUAL;
    return computeShares(split, total, selected, decimals);
  }, [kind, total, selected, weights, decimals]);

  const balanced = Math.abs(delta) < BALANCE_EPSILON;
  const deltaClass = balanced ? "is-balanced" : "is-off";
  const deltaLabel = delta === 0 ? "Balanced" : delta > 0 ? "Missing" : "Overshoot";

  return (
    <div className="pb-split">
      <div className="pb-seg" role="group" aria-label="Split type">
        {SPLIT_SEGMENTS.map((segment) => (
          <button
            key={segment.kind}
            type="button"
            aria-pressed={kind === segment.kind}
            className={`pb-seg__btn${kind === segment.kind ? " is-active" : ""}`}
            onClick={() => onKind(segment.kind)}
          >
            {segment.label}
          </button>
        ))}
      </div>

      {kind === "exact" && (
        <p className="pb-split__hint">
          Enter exact amount each person owes. Sum must equal total.
        </p>
      )}
      {kind === "shares" && (
        <p className="pb-split__hint">
          Assign shares (e.g. 1 / 1 / 2 means last person pays double).
        </p>
      )}

      <div className={`pb-split__rows${kind === "equal" ? " is-tight" : ""}`}>
        {memberIds.map((id) => {
          const name = displayName(members, currentUserId, id);
          const isSelected = selected.includes(id);
          const isMe = id === currentUserId;

          return (
            <div className="pb-split__row" key={id}>
              <button
                type="button"
                role="checkbox"
                aria-checked={isSelected}
                aria-label={name}
                className="pb-split__toggle"
                onClick={() => onToggle(id)}
              >
                <span
                  className={`pb-split__check${isSelected ? " is-on" : ""}`}
                  aria-hidden="true"
                >
                  <Icon
                    name={isSelected ? "checkmark.circle.fill" : "circle"}
                    size={22}
                  />
                </span>
                <Avatar
                  member={members.find((m) => m.id === id) ?? null}
                  size={32}
                  isMe={isMe}
                />
                <span className={`pb-split__name${isMe ? " is-me" : ""}`}>
                  {name}
                </span>
              </button>

              {isSelected && kind === "equal" && total > 0 && (
                <span className="pb-split__share pb-tabular">
                  {formatAmountFull(preview[id] ?? 0, currency)}
                </span>
              )}

              {isSelected && kind === "exact" && (
                <input
                  className="pb-split__exact pb-tabular"
                  aria-label={`Exact amount for ${name}`}
                  placeholder="0"
                  inputMode="decimal"
                  value={exact[id] ?? ""}
                  onChange={(e) => onExact(id, e.target.value)}
                />
              )}

              {isSelected && kind === "shares" && (
                <span className="pb-split__shares-cell">
                  {total > 0 && totalWeight > 0 && (
                    <span className="pb-split__share pb-tabular">
                      {formatAmountFull(preview[id] ?? 0, currency)}
                    </span>
                  )}
                  <Stepper
                    label={name}
                    value={weights[id] ?? 0}
                    onChange={(next) => onWeight(id, next)}
                  />
                </span>
              )}
            </div>
          );
        })}
      </div>

      {kind === "exact" && (
        <div className="pb-split__footer">
          <div className={`pb-split__sum ${deltaClass}`}>
            <span>Sum</span>
            <span className="pb-tabular">{formatAmountFull(exactSum, currency)}</span>
          </div>
          <div className={`pb-split__delta ${deltaClass}`}>
            <span>{deltaLabel}</span>
            <span className="pb-tabular">
              {formatAmountFull(Math.abs(delta), currency)}
            </span>
          </div>
        </div>
      )}

      {kind === "shares" && (
        <div className="pb-split__footer">
          <div className="pb-split__sum">
            <span>Total shares</span>
            <span className="pb-tabular">{totalWeight}</span>
          </div>
        </div>
      )}
    </div>
  );
}

function Stepper({
  label,
  value,
  onChange,
}: {
  label: string;
  value: number;
  onChange: (value: number) => void;
}) {
  return (
    <span className="pb-stepper">
      <button
        type="button"
        className="pb-stepper__btn"
        aria-label={`Fewer shares for ${label}`}
        disabled={value <= 0}
        onClick={() => onChange(value - 1)}
      >
        −
      </button>
      <span className="pb-stepper__value pb-tabular">{value}</span>
      <button
        type="button"
        className="pb-stepper__btn"
        aria-label={`More shares for ${label}`}
        disabled={value >= MAX_WEIGHT}
        onClick={() => onChange(value + 1)}
      >
        +
      </button>
    </span>
  );
}

/* ──────────────────────────────────────────────────────────────── pieces ── */

/** Grows with its content between one and four lines; CSS owns the ceiling. */
function NotesField({
  value,
  onChange,
}: {
  value: string;
  onChange: (value: string) => void;
}) {
  const ref = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = `${el.scrollHeight}px`;
  }, [value]);

  return (
    <textarea
      ref={ref}
      rows={1}
      className="pb-notes"
      aria-label="Notes"
      placeholder="Notes (optional)"
      value={value}
      onChange={(e) => onChange(e.target.value)}
    />
  );
}

function ConfirmDialog({
  title,
  confirmLabel,
  onConfirm,
  onCancel,
}: {
  title: string;
  confirmLabel: string;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape") onCancel();
    };
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [onCancel]);

  return createPortal(
    <div
      className="pb-expense-confirm"
      role="alertdialog"
      aria-modal="true"
      aria-label={title}
    >
      <button
        type="button"
        className="pb-expense-confirm__backdrop"
        aria-label="Cancel"
        tabIndex={-1}
        onClick={onCancel}
      />
      <div
        className={`pb-expense-confirm__panel ${glass("neutral", {
          interactive: false,
        })}`}
      >
        <h3 className="pb-expense-confirm__title">{title}</h3>
        <div className="pb-expense-confirm__actions">
          <TextButton onClick={onCancel}>Cancel</TextButton>
          <button
            type="button"
            className="pb-expense-confirm__delete"
            onClick={onConfirm}
          >
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>,
    document.body,
  );
}

/* ────────────────────────────────────────────────────────────────── util ── */

function initialForm(
  group: Group,
  editing: Expense | null,
  currentUserId: string | null,
): FormState {
  if (editing) {
    const currency = toCurrency(editing.currency);
    const decimals = decimalsFor(currency);
    const split = editing.splitType;

    const exact: Record<string, string> = {};
    if ("exact" in split) {
      for (const [id, value] of Object.entries(split.exact)) {
        exact[id] = plainDecimal(value, decimals);
      }
    }

    // Stored weights win; every other group member starts at one share so the
    // Shares mode is usable the moment it is opened.
    const weights: Record<string, number> = {
      ...Object.fromEntries(group.memberIds.map((id) => [id, 1])),
      ...("shares" in split ? split.shares : {}),
    };

    return {
      title: editing.title,
      amountText: plainDecimal(editing.amount, decimals),
      currency,
      paidBy: editing.paidBy,
      splitAmong: [...editing.splitAmong],
      kind: splitKind(split),
      exact,
      weights,
      date: editing.date,
      notes: editing.notes ?? "",
      iconSymbol: editing.iconSymbol ?? null,
    };
  }

  const paidBy =
    currentUserId && group.memberIds.includes(currentUserId)
      ? currentUserId
      : (group.memberIds[0] ?? "");

  return {
    title: "",
    amountText: "",
    currency: toCurrency(group.defaultCurrency),
    paidBy,
    splitAmong: [...group.memberIds],
    kind: "equal",
    exact: {},
    weights: Object.fromEntries(group.memberIds.map((id) => [id, 1])),
    date: new Date().toISOString(),
    notes: "",
    iconSymbol: null,
  };
}

function buildSplit(form: FormState, selected: string[]): SplitType {
  if (form.kind === "exact") {
    return {
      exact: Object.fromEntries(
        selected.map((id) => [id, parseAmount(form.exact[id] ?? "") ?? 0]),
      ),
    };
  }
  if (form.kind === "shares") {
    return {
      shares: Object.fromEntries(
        selected.map((id) => [id, form.weights[id] ?? 0]),
      ),
    };
  }
  return SPLIT_EQUAL;
}

function orderSelection(memberIds: string[], chosen: string[]): string[] {
  return [
    ...memberIds.filter((id) => chosen.includes(id)),
    ...chosen.filter((id) => !memberIds.includes(id)),
  ];
}

/** Keeps only what an amount can be made of — both decimal separators included. */
function sanitizeAmount(input: string): string {
  return input.replace(/[^\d.,]/g, "");
}

/** POSIX-ish text for an editable amount: no grouping, dot separator. */
function plainDecimal(value: number, decimals: number): string {
  return new Intl.NumberFormat("en-US", {
    useGrouping: false,
    minimumFractionDigits: 0,
    maximumFractionDigits: decimals,
  }).format(value);
}

function toDateInput(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return "";
  const month = `${date.getMonth() + 1}`.padStart(2, "0");
  const day = `${date.getDate()}`.padStart(2, "0");
  return `${date.getFullYear()}-${month}-${day}`;
}

/** Moves the calendar day, leaving the original time of day alone. */
function withDatePart(iso: string, ymd: string): string {
  const [year, month, day] = ymd.split("-").map(Number);
  if (!year || !month || !day) return iso;

  const parsed = new Date(iso);
  const base = Number.isNaN(parsed.getTime()) ? new Date() : parsed;

  return new Date(
    year,
    month - 1,
    day,
    base.getHours(),
    base.getMinutes(),
    base.getSeconds(),
    base.getMilliseconds(),
  ).toISOString();
}
