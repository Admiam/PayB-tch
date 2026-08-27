/**
 * Expense icon grid, ported from `IconPickerSheet.swift`.
 *
 * There is no Save button by design: every tap commits and dismisses, and the
 * first row hands the choice back to the title-derived guess.
 */

import { Sheet } from "@/components/Sheet";
import { Icon, ICON_CATEGORIES, symbolForTitle } from "@/icons";

export function IconPickerSheet({
  open,
  selected,
  titleHint,
  onSelect,
  onClose,
}: {
  open: boolean;
  /** `null` means "auto" — derive the glyph from the title. */
  selected: string | null;
  titleHint: string;
  onSelect: (symbol: string | null) => void;
  onClose: () => void;
}) {
  const choose = (symbol: string | null) => {
    onSelect(symbol);
    onClose();
  };

  const isAuto = selected === null;

  return (
    <Sheet open={open} onClose={onClose} title="Pick icon">
      <div className="pb-iconpicker">
        <section className="pb-iconpicker__section">
          <h3 className="pb-label pb-iconpicker__caption">Auto</h3>

          <button
            type="button"
            className="pb-iconpicker__auto"
            aria-pressed={isAuto}
            onClick={() => choose(null)}
          >
            <span className={tileClass(isAuto)} aria-hidden="true">
              <Icon name={symbolForTitle(titleHint)} size={22} />
            </span>

            <span className="pb-iconpicker__auto-text">
              <span className="pb-iconpicker__auto-title">From title</span>
              <span className="pb-iconpicker__auto-sub">
                Auto-pick based on what you type
              </span>
            </span>

            {isAuto && (
              <span className="pb-iconpicker__tick" aria-hidden="true">
                <Icon name="checkmark.circle.fill" size={20} />
              </span>
            )}
          </button>
        </section>

        {ICON_CATEGORIES.map((category) => (
          <section className="pb-iconpicker__section" key={category.label}>
            <h3 className="pb-label pb-iconpicker__caption">{category.label}</h3>

            <div className="pb-iconpicker__grid">
              {category.symbols.map((symbol) => (
                <button
                  key={symbol}
                  type="button"
                  className={tileClass(selected === symbol)}
                  aria-pressed={selected === symbol}
                  aria-label={symbolLabel(symbol)}
                  onClick={() => choose(symbol)}
                >
                  <Icon name={symbol} size={22} />
                </button>
              ))}
            </div>
          </section>
        ))}
      </div>
    </Sheet>
  );
}

const tileClass = (isSelected: boolean) =>
  `pb-icon-tile${isSelected ? " is-selected" : ""}`;

/** "takeoutbag.and.cup.and.straw.fill" → "takeoutbag and cup and straw". */
function symbolLabel(symbol: string): string {
  return symbol.replace(/\.fill$/, "").replace(/\./g, " ");
}
