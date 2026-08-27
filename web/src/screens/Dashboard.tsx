/**
 * The home screen, ported from `ContentView.swift`.
 *
 * Pick a group, see who owes what, add an expense. Everything below the chips
 * row is derived from the store — no balance is ever stored, exactly as on the
 * phone, which is what keeps the two apps in agreement.
 */

import { useMemo, useState } from "react";
import {
  calculateBalances,
  myCosts as computeMyCosts,
  totalCosts as computeTotalCosts,
  simplifyDebts,
} from "@/domain/balances";
import { toCurrency, type Expense, type Group } from "@/domain/types";
import { useStore } from "@/store/useStore";
import {
  ActivityList,
  Crew,
  DebtSummary,
  Tiles,
} from "@/components/dashboard";
import { PaybitchMenu } from "@/components/Menu";
import { Icon } from "@/icons";
import {
  AllSquaredCard,
  EmptyExpensesCard,
  EmptyGroupsState,
  Fab,
  GroupChip,
  NewGroupChip,
  Wordmark,
} from "@/components/primitives";
import { AddExpenseSheet } from "./AddExpenseSheet";
import { ExpenseDetailSheet } from "./ExpenseDetailSheet";
import { GroupEditorSheet } from "./GroupEditorSheet";
import { SettingsSheet } from "./SettingsSheet";
import { ActivitySheet } from "./ActivitySheet";
import { Onboarding } from "./Onboarding";

/** Below this a balance counts as settled. */
const EPSILON = 0.01;

export function Dashboard() {
  const groups = useStore((s) => s.groups);
  const members = useStore((s) => s.members);
  const expenses = useStore((s) => s.expenses);
  const currentUserId = useStore((s) => s.currentUserId);
  const hasOnboarded = useStore((s) => s.hasOnboarded);
  const deleteExpense = useStore((s) => s.deleteExpense);
  const selectGroup = useStore((s) => s.selectGroup);
  const lastError = useStore((s) => s.lastError);
  const clearError = useStore((s) => s.clearError);

  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [editingGroup, setEditingGroup] = useState<Group | null>(null);
  const [groupSheetOpen, setGroupSheetOpen] = useState(false);
  const [expenseSheetOpen, setExpenseSheetOpen] = useState(false);
  const [editingExpense, setEditingExpense] = useState<Expense | null>(null);
  const [detailExpense, setDetailExpense] = useState<Expense | null>(null);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [activityOpen, setActivityOpen] = useState(false);

  // Derived rather than synced by an effect: if the stored choice no longer
  // exists — the group was deleted, or nothing has been picked yet — fall back
  // to the first group. Resolving this during render means there is never a
  // frame showing a stale or empty selection.
  const group = useMemo(() => {
    const chosen = groups.find((g) => g.id === selectedId);
    return chosen ?? groups[0] ?? null;
  }, [groups, selectedId]);

  const currency = toCurrency(group?.defaultCurrency);

  const groupExpenses = useMemo(
    () => (group ? expenses.filter((e) => e.groupId === group.id) : []),
    [expenses, group],
  );

  const balances = useMemo(
    () =>
      group ? calculateBalances(groupExpenses, group.memberIds, currency) : [],
    [group, groupExpenses, currency],
  );

  const edges = useMemo(() => simplifyDebts(balances), [balances]);

  const myNet = currentUserId
    ? (balances.find((b) => b.memberId === currentUserId)?.net ?? 0)
    : 0;
  const myCosts = computeMyCosts(groupExpenses, currentUserId, currency);
  const totalCosts = computeTotalCosts(groupExpenses, currency);

  const isEmpty = groupExpenses.length === 0;
  const isSettled = !isEmpty && Math.abs(myNet) < EPSILON && edges.length === 0;

  const openNewExpense = () => {
    setEditingExpense(null);
    setExpenseSheetOpen(true);
  };

  const openNewGroup = () => {
    setEditingGroup(null);
    setGroupSheetOpen(true);
  };

  const openEditGroup = () => {
    if (!group) return;
    setEditingGroup(group);
    setGroupSheetOpen(true);
  };

  return (
    <>
      <div className="pb-app-bg" aria-hidden="true" />

      <div className="pb-app">
        {lastError && (
          <div className="pb-error" role="alert">
            <span>{lastError}</span>
            <button type="button" onClick={clearError} aria-label="Dismiss">
              <Icon name="xmark" size={14} strokeWidth={3} />
            </button>
          </div>
        )}

        <header className="pb-topbar">
          <Wordmark />
          <span className="pb-topbar__spacer" />
          <PaybitchMenu
            hasSelectedGroup={Boolean(group)}
            onEditGroup={openEditGroup}
            onNewGroup={openNewGroup}
            onSearch={() => setActivityOpen(true)}
            onSettings={() => setSettingsOpen(true)}
          />
        </header>

        <nav className="pb-chips" aria-label="Groups">
          {groups.map((g) => (
            <GroupChip
              key={g.id}
              name={g.name}
              memberCount={g.memberIds.length}
              active={g.id === group?.id}
              onClick={() => {
                setSelectedId(g.id);
                void selectGroup(g.id);
              }}
            />
          ))}
          <NewGroupChip onClick={openNewGroup} />
        </nav>

        <main className="pb-scroll">
          {!group ? (
            <EmptyGroupsState />
          ) : (
            <div className="pb-dashboard">
              <div>
                <Tiles
                  myNet={myNet}
                  myCosts={myCosts}
                  totalCosts={totalCosts}
                  expenseCount={groupExpenses.length}
                  currency={currency}
                  owedFromCount={
                    edges.filter((e) => e.to === currentUserId).length
                  }
                  owedToCount={
                    edges.filter((e) => e.from === currentUserId).length
                  }
                  hasCurrentUser={Boolean(currentUserId)}
                />

                {isSettled && (
                  <div style={{ padding: "14px var(--pb-gutter) 0" }}>
                    <AllSquaredCard />
                  </div>
                )}

                {!isEmpty && (
                  <Crew
                    balances={balances}
                    members={members}
                    currentUserId={currentUserId}
                    currency={currency}
                  />
                )}
              </div>

              <div>
                {isEmpty ? (
                  <div style={{ padding: "20px var(--pb-gutter) 0" }}>
                    <EmptyExpensesCard onAdd={openNewExpense} />
                  </div>
                ) : (
                  <>
                    <DebtSummary
                      edges={edges}
                      members={members}
                      currentUserId={currentUserId}
                      currency={currency}
                    />
                    <ActivityList
                      expenses={groupExpenses}
                      members={members}
                      currentUserId={currentUserId}
                      onOpen={setDetailExpense}
                      onDelete={(e) => deleteExpense(e.id)}
                    />
                  </>
                )}
              </div>
            </div>
          )}
        </main>

        {group && <Fab onClick={openNewExpense} />}
      </div>

      {/* Onboarding owns the screen until the user has been through it. */}
      <Onboarding open={!hasOnboarded && groups.length === 0} />

      <GroupEditorSheet
        open={groupSheetOpen}
        editing={editingGroup}
        onClose={() => setGroupSheetOpen(false)}
        onCreated={(created) => {
          setSelectedId(created.id);
          void selectGroup(created.id);
        }}
      />

      {group && (
        <AddExpenseSheet
          open={expenseSheetOpen}
          group={group}
          editing={editingExpense}
          onClose={() => {
            setExpenseSheetOpen(false);
            setEditingExpense(null);
          }}
        />
      )}

      <ExpenseDetailSheet
        expense={detailExpense}
        onClose={() => setDetailExpense(null)}
        onEdit={(expense) => {
          setDetailExpense(null);
          setEditingExpense(expense);
          setExpenseSheetOpen(true);
        }}
      />

      <SettingsSheet
        open={settingsOpen}
        onClose={() => setSettingsOpen(false)}
      />

      {group && (
        <ActivitySheet
          open={activityOpen}
          group={group}
          onClose={() => setActivityOpen(false)}
          onOpenExpense={(expense) => {
            setActivityOpen(false);
            setDetailExpense(expense);
          }}
        />
      )}
    </>
  );
}
