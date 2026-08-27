/**
 * The dashboard sections: stat tiles, the crew strip, the settlement summary
 * and the activity list. Ported from `Tiles.swift`, `Members.swift`,
 * `DebtSummaryList.swift` and `DebtsList.swift`.
 */

import { amountString, formatAmount, formatAmountFull } from "@/domain/money";
import { splitKind, type CurrencyCode, type Expense, type Member } from "@/domain/types";
import type { DebtEdge, MemberBalance } from "@/domain/balances";
import { CURRENCY_SYMBOLS } from "@/domain/types";
import { Icon, symbolForExpense } from "@/icons";
import { Avatar } from "./Avatar";
import { DebtFlowCanvas } from "./DebtFlowCanvas";
import { SectionHeader, Tile } from "./primitives";

/** Below this a balance counts as settled, matching the simplifier's epsilon. */
const EPSILON = 0.01;

/* ────────────────────────────────────────────────────────────── tiles ── */

export function Tiles({
  myNet,
  myCosts,
  totalCosts,
  expenseCount,
  currency,
  owedFromCount,
  owedToCount,
  hasCurrentUser,
}: {
  myNet: number;
  myCosts: number;
  totalCosts: number;
  expenseCount: number;
  currency: CurrencyCode;
  owedFromCount: number;
  owedToCount: number;
  hasCurrentUser: boolean;
}) {
  const symbol = CURRENCY_SYMBOLS[currency];
  const owed = myNet >= 0;

  const iOwedSub = !hasCurrentUser
    ? undefined
    : myNet > EPSILON
      ? `from ${owedFromCount} ${owedFromCount === 1 ? "person" : "people"}`
      : myNet < -EPSILON
        ? `to ${owedToCount} ${owedToCount === 1 ? "person" : "people"}`
        : "all settled";

  const myCostsRatio = totalCosts > 0 ? myCosts / totalCosts : 0;

  return (
    <div className="pb-tiles">
      <Tile
        big
        label={owed ? "I am owed" : "I owe"}
        icon={owed ? "arrow.down" : "arrow.up"}
        amount={amountString(Math.abs(myNet), currency)}
        currencySymbol={symbol}
        amountColor="var(--pb-pink)"
        sub={iOwedSub}
        ratio={totalCosts > 0 ? Math.abs(myNet) / totalCosts : 0}
      />

      <div className="pb-tiles__row">
        <Tile
          label="My costs"
          icon="wallet.bifold.fill"
          amount={amountString(myCosts, currency)}
          currencySymbol={symbol}
          sub={`${Math.round(myCostsRatio * 100)}% of total`}
          ratio={myCostsRatio}
        />
        <Tile
          label="Total"
          icon="doc.text.fill"
          amount={amountString(totalCosts, currency)}
          currencySymbol={symbol}
          sub={`${expenseCount} ${expenseCount === 1 ? "expense" : "expenses"}`}
        />
      </div>
    </div>
  );
}

/* ─────────────────────────────────────────────────────────────── crew ── */

export function Crew({
  balances,
  members,
  currentUserId,
  currency,
}: {
  balances: MemberBalance[];
  members: Member[];
  currentUserId: string | null;
  currency: CurrencyCode;
}) {
  const symbol = CURRENCY_SYMBOLS[currency];
  const resolved = balances
    .map((b) => ({ balance: b, member: members.find((m) => m.id === b.memberId) }))
    .filter((x): x is { balance: MemberBalance; member: Member } =>
      Boolean(x.member),
    );

  if (resolved.length === 0) return null;

  return (
    <section>
      <SectionHeader title="The Crew" badge={resolved.length} />
      <div className="pb-card pb-crew">
        <div className="pb-crew__scroll">
          {resolved.map(({ balance, member }) => {
            const isMe = member.id === currentUserId;
            const net = balance.net;

            // U+2212 minus, not a hyphen — it aligns with digits.
            const { prefix, color } =
              net > EPSILON
                ? { prefix: "+", color: "var(--pb-positive)" }
                : net < -EPSILON
                  ? { prefix: "−", color: "var(--pb-negative)" }
                  : { prefix: "", color: "var(--pb-text-muted)" };

            return (
              <div className="pb-crew__chip" key={member.id}>
                <Avatar member={member} size={48} isMe={isMe} />
                <span className="pb-crew__name">
                  {isMe ? "Me" : member.name}
                </span>
                <span className="pb-crew__balance pb-tabular" style={{ color }}>
                  {prefix}
                  {amountString(Math.abs(net), currency)} {symbol}
                </span>
              </div>
            );
          })}
        </div>
      </div>
    </section>
  );
}

/* ───────────────────────────────────────────────────── who owes whom ── */

export function DebtSummary({
  edges,
  members,
  currentUserId,
  currency,
}: {
  edges: DebtEdge[];
  members: Member[];
  currentUserId: string | null;
  currency: CurrencyCode;
}) {
  if (edges.length === 0) return null;

  const name = (id: string) =>
    id === currentUserId ? "Me" : (members.find((m) => m.id === id)?.name ?? "—");
  const member = (id: string) => members.find((m) => m.id === id) ?? null;

  return (
    <section>
      <SectionHeader title="Who owes whom" badge={edges.length} />
      <div className="pb-card pb-debts">
        <DebtFlowCanvas
          edges={edges}
          members={members}
          currentUserId={currentUserId}
        />
        <hr className="pb-divider" />

        <div className="pb-debts__rows">
          {edges.map((edge) => (
            <div className="pb-debts__row" key={edge.id}>
              <Avatar
                member={member(edge.from)}
                size={32}
                isMe={edge.from === currentUserId}
              />
              <span style={{ color: "var(--pb-pink)" }}>
                <Icon name="arrow.right" size={11} strokeWidth={3} />
              </span>
              <Avatar
                member={member(edge.to)}
                size={32}
                isMe={edge.to === currentUserId}
              />

              <span className="pb-debts__names">
                {name(edge.from)}
                <span style={{ opacity: 0.4 }}> → </span>
                {name(edge.to)}
              </span>

              <span className="pb-debts__amount pb-tabular">
                {formatAmount(edge.amount, currency)}
              </span>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}

/* ─────────────────────────────────────────────────────────── activity ── */

/** Newest first, falling back to creation time when two share a date. */
export function sortExpenses(expenses: Expense[]): Expense[] {
  return [...expenses].sort((a, b) => {
    const byDate = b.date.localeCompare(a.date);
    return byDate !== 0 ? byDate : b.createdAt.localeCompare(a.createdAt);
  });
}

export function ActivityList({
  expenses,
  members,
  currentUserId,
  onOpen,
  onDelete,
  title = "Activity",
}: {
  expenses: Expense[];
  members: Member[];
  currentUserId: string | null;
  onOpen: (expense: Expense) => void;
  onDelete?: (expense: Expense) => void;
  title?: string;
}) {
  if (expenses.length === 0) return null;
  const sorted = sortExpenses(expenses);

  return (
    <section>
      <SectionHeader title={title} badge={sorted.length} />
      <div className="pb-activity">
        {sorted.map((expense) => (
          <ExpenseRow
            key={expense.id}
            expense={expense}
            members={members}
            currentUserId={currentUserId}
            onOpen={() => onOpen(expense)}
            onDelete={onDelete ? () => onDelete(expense) : undefined}
          />
        ))}
      </div>
    </section>
  );
}

export function ExpenseRow({
  expense,
  members,
  currentUserId,
  onOpen,
  onDelete,
}: {
  expense: Expense;
  members: Member[];
  currentUserId: string | null;
  onOpen: () => void;
  onDelete?: () => void;
}) {
  const paidByMe = expense.paidBy === currentUserId;
  const payer = paidByMe
    ? "you"
    : (members.find((m) => m.id === expense.paidBy)?.name ?? "—");

  return (
    <div className="pb-expense">
      <button type="button" className="pb-expense__main" onClick={onOpen}>
        <span
          className="pb-expense__icon"
          style={{
            background: paidByMe ? "var(--pb-pink)" : "var(--pb-chip-bg)",
            color: paidByMe ? "#fff" : "var(--pb-pink)",
          }}
        >
          <Icon name={symbolForExpense(expense)} size={22} />
        </span>

        <span className="pb-expense__body">
          <span className="pb-expense__title">{expense.title}</span>
          <span className="pb-expense__meta">
            <span className="pb-expense__meta-dim">Paid by</span>{" "}
            <span className="pb-expense__meta-strong">{payer}</span>
            <span className="pb-expense__meta-dim"> · {mediumDate(expense.date)}</span>
          </span>
        </span>

        <span className="pb-expense__right">
          <span className="pb-expense__amount pb-tabular">
            {formatAmount(expense.amount, expense.currency)}
          </span>
          <span className="pb-expense__split">÷ {expense.splitAmong.length}</span>
        </span>
      </button>

      {onDelete && (
        <button
          type="button"
          className="pb-expense__delete"
          aria-label={`Delete ${expense.title}`}
          onClick={onDelete}
        >
          <Icon name="trash" size={16} />
        </button>
      )}
    </div>
  );
}

/* ─────────────────────────────────────────────────────────────── util ── */

/**
 * Medium locale date, e.g. "Sep 15, 2025".
 *
 * The year is dropped for the current year. Almost every expense is recent, and
 * the full form does not fit the activity row on a narrow phone — it truncated
 * mid-date, which is worse than omitting a year the reader can assume.
 */
export function mediumDate(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return "";

  const sameYear = date.getFullYear() === new Date().getFullYear();
  return new Intl.DateTimeFormat(undefined, {
    year: sameYear ? undefined : "numeric",
    month: "short",
    day: "numeric",
  }).format(date);
}

export { formatAmountFull, splitKind };
