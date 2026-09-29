/**
 * Shared-debt setup: pick the groups to pool, then say who is who across them.
 *
 * Two sections in that order because that is the dependency — there is nobody to
 * pair until at least two groups are in. Everything saves as you touch it: this
 * is a settings surface, and a half-applied pairing has no meaning worth keeping
 * behind a Save button somebody will forget to press.
 */

import { CURRENCY_SYMBOLS, toCurrency } from "@/domain/types";
import { suggestMerges } from "@/domain/people";
import { Icon } from "@/icons";
import { useSharedDebt } from "@/lib/useSharedDebt";
import { useSharedLedger } from "@/store/useSharedLedger";
import { PersonPairing } from "@/components/PersonPairing";
import {
  FieldGroup,
  PrimaryButton,
  Switch,
  glass,
} from "@/components/primitives";
import { Sheet } from "@/components/Sheet";

export function SharedDebtSheet({
  open,
  onClose,
}: {
  open: boolean;
  onClose: () => void;
}) {
  const view = useSharedDebt();
  const saving = useSharedLedger((s) => s.saving);
  const error = useSharedLedger((s) => s.error);
  const clearError = useSharedLedger((s) => s.clearError);
  const setIncluded = useSharedLedger((s) => s.setIncluded);
  const link = useSharedLedger((s) => s.link);
  const unlink = useSharedLedger((s) => s.unlink);
  const linkAll = useSharedLedger((s) => s.linkAll);

  // Pairing needs the pooled rosters, not just the pooled ids — matching people
  // before their link keys have arrived would offer merges the server has
  // already made.
  const pairable = view.ready && view.includedGroups.length >= 2;
  const suggestions = pairable ? suggestMerges(view.people, view.roster) : [];

  return (
    <Sheet
      open={open}
      onClose={onClose}
      title="Shared debt"
      action={<PrimaryButton onClick={onClose}>Done</PrimaryButton>}
    >
      <div className="pb-settings">
        {error && (
          <div className="pb-shared-sheet__error" role="alert">
            <span>{error}</span>
            <button type="button" onClick={clearError} aria-label="Dismiss">
              <Icon name="xmark" size={12} strokeWidth={3} />
            </button>
          </div>
        )}

        <FieldGroup label="Groups in the shared view">
          <div>
            {view.allGroups.map((group, index) => {
              const included = view.includedGroups.some((g) => g.id === group.id);
              const currency = toCurrency(group.defaultCurrency);

              return (
                <div key={group.id}>
                  {index > 0 && <hr className="pb-divider" />}
                  <label className="pb-shared-sheet__group">
                    <span className="pb-shared-sheet__group-text">
                      <span className="pb-shared-sheet__group-name">
                        {group.name}
                      </span>
                      <span className="pb-shared-sheet__group-meta">
                        {group.memberIds.length}{" "}
                        {group.memberIds.length === 1 ? "person" : "people"} ·{" "}
                        {CURRENCY_SYMBOLS[currency]}
                      </span>
                    </span>
                    <Switch
                      checked={included}
                      disabled={saving}
                      label={`Include ${group.name} in the shared view`}
                      onChange={(next) => void setIncluded(group.id, next)}
                    />
                  </label>
                </div>
              );
            })}
          </div>
        </FieldGroup>

        {!pairable ? (
          <p className="pb-shared-sheet__hint">
            {view.includedGroups.length >= 2
              ? "Loading who is in these groups…"
              : "Turn on at least two groups. Then you can say which people in them are the same person, and Paybitch will settle across the lot in one go."}
          </p>
        ) : (
          <FieldGroup label="Who is who">
            <div className="pb-shared-sheet__pairing">
              <p className="pb-shared-sheet__hint pb-shared-sheet__hint--tight">
                Anyone signed in with an account is matched across your groups
                already. Placeholders have no account behind them, so those are
                the ones to pair up by hand.
              </p>

              {suggestions.length > 0 && (
                <button
                  type="button"
                  className={`pb-shared-sheet__suggest ${glass("accent")}`}
                  disabled={saving}
                  onClick={() => void linkAll(suggestions)}
                >
                  <Icon name="sparkles" size={15} />
                  Match {suggestions.length}{" "}
                  {suggestions.length === 1 ? "person" : "people"} by name
                </button>
              )}

              <PersonPairing
                view={view}
                busy={saving}
                onLink={(a, b) => void link(a, b)}
                onUnlink={(memberId) => void unlink(memberId)}
              />
            </div>
          </FieldGroup>
        )}

        <FieldGroup label="How it adds up">
          <p className="pb-settings-about">
            Each group&rsquo;s own numbers come from the server, exactly as its
            card shows them. The shared view adds up the people you paired and
            suggests the shortest set of payments that clears everything at once
            — nothing is settled or changed in any group.
            {view.converted && (
              <>
                {" "}
                Groups kept in another currency are converted at the app&rsquo;s
                fixed rates.
              </>
            )}
          </p>
        </FieldGroup>
      </div>
    </Sheet>
  );
}
