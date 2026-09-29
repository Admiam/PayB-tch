/**
 * "Shared debt" — one who-owes-whom across every pooled group.
 *
 * The group card below it answers "who owes whom in here". This answers the
 * question that card structurally cannot: you cover dinner on the trip, your
 * flatmate covers the electricity, and the two debts sit in separate groups
 * politely cancelling nothing. Pooled, they cancel.
 *
 * Deliberately not a second copy of the group card: it leads with the one number
 * a person came for — their own combined position — and states plainly which
 * groups went into it, because a total whose inputs are invisible is a total
 * nobody can check.
 */

import { amountString, formatAmount } from "@/domain/money";
import { CURRENCY_SYMBOLS } from "@/domain/types";
import { Icon } from "@/icons";
import { personFace, type SharedDebtView } from "@/lib/useSharedDebt";
import { Avatar } from "./Avatar";
import { SectionHeader, glass } from "./primitives";

/** Below this a combined position counts as settled, matching the simplifier. */
const EPSILON = 0.01;

export function SharedDebt({
  view,
  onSetUp,
}: {
  view: SharedDebtView;
  onSetUp: () => void;
}) {
  // One group is not a pool, and pooling is meaningless before the config has
  // been read — better no section than a section that flickers into existence.
  if (!view.ready || view.allGroups.length < 2) return null;

  const setUp = (
    <button
      type="button"
      className={`pb-shared__setup ${glass()}`}
      onClick={onSetUp}
    >
      <Icon name="person.3.sequence" size={14} />
      {view.includedGroups.length < 2 ? "Set up" : "Edit"}
    </button>
  );

  if (view.includedGroups.length < 2) {
    return (
      <section>
        <SectionHeader title="Shared debt" trailing={setUp} />
        <div className={`pb-shared pb-shared--invite ${glass("neutral", { interactive: false })}`}>
          <p className="pb-shared__pitch">
            Pool two or more groups and see one settlement across all of them —
            debts running opposite ways cancel instead of both being paid.
          </p>
          <p className="pb-shared__pitch pb-shared__pitch--dim">
            You pair up who is who once. People signed in with an account are
            matched for you.
          </p>
          <button type="button" className={`pb-shared__cta ${glass("accent")}`} onClick={onSetUp}>
            Choose groups
          </button>
        </div>
      </section>
    );
  }

  const symbol = CURRENCY_SYMBOLS[view.currency];
  const groupCount = view.includedGroups.length;
  const settled = Math.abs(view.myNet) < EPSILON;
  const owed = view.myNet > EPSILON;

  const name = (personId: string) => {
    const person = view.people.find((candidate) => candidate.id === personId);
    if (!person) return "—";
    return person.name;
  };
  const face = (personId: string) => {
    const person = view.people.find((candidate) => candidate.id === personId);
    return person ? personFace(person) : null;
  };

  return (
    <section>
      <SectionHeader title="Shared debt" badge={view.edges.length} trailing={setUp} />

      <div className={`pb-shared ${glass("neutral", { interactive: false })}`}>
        <div className="pb-shared__hero">
          <span className="pb-label pb-shared__scope">
            Across {groupCount} groups
          </span>

          {view.myPersonId === null ? (
            <p className="pb-shared__hint">
              Your own row isn&rsquo;t in these groups yet, so there is no
              combined position to show for you.
            </p>
          ) : settled ? (
            <div className="pb-shared__amount pb-shared__amount--settled">
              All square
            </div>
          ) : (
            <>
              <div className="pb-shared__amount">
                <span className="pb-shared__figure pb-tabular">
                  {amountString(Math.abs(view.myNet), view.currency)}
                </span>
                <span className="pb-shared__symbol">{symbol}</span>
              </div>
              <span className="pb-shared__caption">
                {owed ? "owed to you, everything counted" : "you owe, everything counted"}
              </span>
            </>
          )}
        </div>

        <div className="pb-shared__groups">
          {view.includedGroups.map((group) => (
            <span className="pb-shared__group" key={group.id}>
              {group.name}
            </span>
          ))}
        </div>

        {view.edges.length > 0 && (
          <>
            <hr className="pb-divider" />
            <div className="pb-shared__rows">
              {view.edges.map((edge) => (
                <div className="pb-shared__row" key={edge.id}>
                  <Avatar
                    member={face(edge.from)}
                    size={32}
                    isMe={edge.from === view.myPersonId}
                  />
                  <span style={{ color: "var(--pb-pink)" }}>
                    <Icon name="arrow.right" size={11} strokeWidth={3} />
                  </span>
                  <Avatar
                    member={face(edge.to)}
                    size={32}
                    isMe={edge.to === view.myPersonId}
                  />

                  <span className="pb-shared__names">
                    {name(edge.from)}
                    <span style={{ opacity: 0.4 }}> → </span>
                    {name(edge.to)}
                  </span>

                  <span className="pb-shared__row-amount pb-tabular">
                    {formatAmount(edge.amount, view.currency)}
                  </span>
                </div>
              ))}
            </div>
          </>
        )}

        {view.converted && (
          <p className="pb-shared__note">
            Some groups keep their money in another currency; those amounts are
            converted at the app&rsquo;s fixed rates to show one total.
          </p>
        )}
      </div>
    </section>
  );
}
