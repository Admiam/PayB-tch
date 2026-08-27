/**
 * Read-only expense detail, ported from `ExpenseDetailView.swift`.
 *
 * The expense is re-resolved from the store by id on every render instead of
 * trusting the `expense` prop directly — the prop is a snapshot taken when the
 * sheet was opened, so without this a deletion elsewhere (including the
 * button inside this very sheet) would leave a stale, already-gone expense on
 * screen instead of falling into the "not found" state below.
 */

import { useEffect, useState } from "react";
import { Avatar } from "@/components/Avatar";
import { mediumDate } from "@/components/dashboard";
import {
  Badge,
  DestructiveButton,
  FieldGroup,
  TextButton,
} from "@/components/primitives";
import { Sheet } from "@/components/Sheet";
import {
  amountString,
  computeShares,
  decimalsFor,
  formatAmountFull,
} from "@/domain/money";
import { CURRENCY_SYMBOLS, splitKind, type Expense } from "@/domain/types";
import { Icon, symbolForExpense } from "@/icons";
import { displayName, useStore } from "@/store/useStore";

export function ExpenseDetailSheet({
  expense: expenseProp,
  onClose,
  onEdit,
}: {
  expense: Expense | null;
  onClose: () => void;
  onEdit: (expense: Expense) => void;
}) {
  const expenses = useStore((s) => s.expenses);
  const members = useStore((s) => s.members);
  const currentUserId = useStore((s) => s.currentUserId);
  const deleteExpense = useStore((s) => s.deleteExpense);

  const [confirmingDelete, setConfirmingDelete] = useState(false);

  const open = expenseProp !== null;
  const expense = expenseProp
    ? (expenses.find((e) => e.id === expenseProp.id) ?? null)
    : null;

  // However the sheet closes — Close, Escape, the backdrop, or a completed
  // delete — the confirm step should not still be showing next time it opens.
  useEffect(() => {
    if (!open) setConfirmingDelete(false);
  }, [open]);

  const payer = expense
    ? (members.find((m) => m.id === expense.paidBy) ?? null)
    : null;
  const participantCount = expense?.splitAmong.length ?? 0;
  const shares = expense
    ? computeShares(
        expense.splitType,
        expense.amount,
        expense.splitAmong,
        decimalsFor(expense.currency),
      )
    : {};

  return (
    <Sheet
      open={open}
      onClose={onClose}
      action={
        expense && <TextButton onClick={() => onEdit(expense)}>Edit</TextButton>
      }
    >
      {!expense ? (
        <div className="pb-detail-unavailable">
          <Icon name="questionmark.circle" size={48} />
          <h3 className="pb-detail-unavailable__title">Expense not found</h3>
        </div>
      ) : (
        <div className="pb-detail">
          <div className="pb-detail-hero">
            <div className="pb-detail-hero__icon">
              <Icon name={symbolForExpense(expense)} size={44} strokeWidth={2.2} />
            </div>
            <h1 className="pb-detail-hero__title">{expense.title}</h1>
            <div className="pb-detail-hero__amount">
              <span className="pb-detail-hero__figure">
                {amountString(expense.amount, expense.currency)}
              </span>
              <span className="pb-detail-hero__symbol">
                {CURRENCY_SYMBOLS[expense.currency]}
              </span>
            </div>
            <div className="pb-detail-hero__badges">
              <Badge rotation={-3}>{splitKind(expense.splitType)}</Badge>
              <Badge rotation={3}>{mediumDate(expense.date)}</Badge>
            </div>
          </div>

          {payer && (
            <FieldGroup label="Paid by">
              <div className="pb-detail-payer">
                <Avatar member={payer} size={40} isMe={payer.id === currentUserId} />
                <div className="pb-detail-payer__info">
                  <span className="pb-detail-payer__name">
                    {displayName(members, currentUserId, payer.id)}
                  </span>
                  <span className="pb-detail-payer__caption">
                    put up the whole tab
                  </span>
                </div>
                <span className="pb-detail-payer__amount pb-tabular">
                  +{formatAmountFull(expense.amount, expense.currency)}
                </span>
              </div>
            </FieldGroup>
          )}

          <FieldGroup
            label={`Split between · ${participantCount} ${
              participantCount === 1 ? "person" : "people"
            }`}
          >
            {expense.splitAmong.map((id, index) => {
              const member = members.find((m) => m.id === id) ?? null;
              const isPayer = id === expense.paidBy;

              return (
                <div key={id}>
                  <div className="pb-detail-split-row">
                    <Avatar member={member} size={36} isMe={id === currentUserId} />
                    <span className="pb-detail-split-row__name">
                      {displayName(members, currentUserId, id)}
                    </span>
                    <span
                      className="pb-detail-split-row__amount pb-tabular"
                      style={{
                        color: isPayer
                          ? "var(--pb-text-muted)"
                          : "var(--pb-negative)",
                      }}
                    >
                      {/* The phone app always prints a literal "0" here, even
                          when the payer participates in the split, hiding
                          their real share (ExpenseDetailView.swift:199) — a
                          bug, not a design choice. We show their actual share
                          instead, muted rather than red so it still reads as
                          different from what other members owe. */}
                      {isPayer ? "" : "−"}
                      {formatAmountFull(shares[id] ?? 0, expense.currency)}
                    </span>
                  </div>
                  {index < participantCount - 1 && (
                    <hr className="pb-divider" />
                  )}
                </div>
              );
            })}
          </FieldGroup>

          {expense.notes?.trim() && (
            <FieldGroup label="Notes">
              <p className="pb-detail-notes">{expense.notes}</p>
            </FieldGroup>
          )}

          {confirmingDelete ? (
            <div className="pb-detail-confirm">
              <p className="pb-detail-confirm__title">Delete this expense?</p>
              <DestructiveButton
                onClick={() => {
                  deleteExpense(expense.id);
                  onClose();
                }}
              >
                Delete
              </DestructiveButton>
              <TextButton onClick={() => setConfirmingDelete(false)}>
                Cancel
              </TextButton>
            </div>
          ) : (
            <DestructiveButton onClick={() => setConfirmingDelete(true)}>
              Delete expense
            </DestructiveButton>
          )}
        </div>
      )}
    </Sheet>
  );
}
