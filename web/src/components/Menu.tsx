/**
 * The hamburger menu, ported from `PaybitchMenu.swift`.
 *
 * A native `Menu` on iOS; here a popover that closes on outside click, Escape,
 * or selection. Items that need a selected group are hidden without one, and
 * the separators around them collapse too — the Swift leaves doubled dividers
 * in that case, which looks like an oversight rather than a decision.
 */

import { useEffect, useRef, useState } from "react";
import { Icon } from "@/icons";
import { glass } from "./primitives";

export interface MenuAction {
  label: string;
  icon: string;
  onSelect: () => void;
}

export function PaybitchMenu({
  hasSelectedGroup,
  onEditGroup,
  onNewGroup,
  onSearch,
  onSettings,
}: {
  hasSelectedGroup: boolean;
  onEditGroup: () => void;
  onNewGroup: () => void;
  onSearch: () => void;
  onSettings: () => void;
}) {
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;

    const onPointerDown = (e: MouseEvent) => {
      if (!rootRef.current?.contains(e.target as Node)) setOpen(false);
    };
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };

    document.addEventListener("mousedown", onPointerDown);
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("mousedown", onPointerDown);
      document.removeEventListener("keydown", onKeyDown);
    };
  }, [open]);

  const run = (action: () => void) => () => {
    setOpen(false);
    action();
  };

  const groups: MenuAction[][] = [
    [
      ...(hasSelectedGroup
        ? [{ label: "Edit group", icon: "pencil", onSelect: run(onEditGroup) }]
        : []),
      {
        label: "New group",
        icon: "person.3.sequence",
        onSelect: run(onNewGroup),
      },
    ],
    ...(hasSelectedGroup
      ? [
          [
            {
              label: "Search activity",
              icon: "magnifyingglass",
              onSelect: run(onSearch),
            },
          ],
        ]
      : []),
    [{ label: "Settings", icon: "gearshape", onSelect: run(onSettings) }],
  ];

  return (
    <div className="pb-menu" ref={rootRef}>
      <button
        type="button"
        aria-label="Menu"
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
        className={`pb-btn pb-btn--menu ${glass()}`}
      >
        <Icon name="line.3.horizontal" size={22} strokeWidth={2.4} />
      </button>

      {open && (
        <div className="pb-menu__popover" role="menu">
          {groups.map((group, groupIndex) => (
            <div key={group[0]?.label ?? groupIndex}>
              {groupIndex > 0 && <hr className="pb-divider" />}
              {group.map((item) => (
                <button
                  key={item.label}
                  type="button"
                  role="menuitem"
                  className="pb-menu__item"
                  onClick={item.onSelect}
                >
                  <span>{item.label}</span>
                  <Icon name={item.icon} size={18} />
                </button>
              ))}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
