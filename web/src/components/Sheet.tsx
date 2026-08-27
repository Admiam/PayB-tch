/**
 * Modal sheet — the web stand-in for a SwiftUI `.sheet`.
 *
 * On a phone it slides up from the bottom and can be flicked away by dragging
 * the grabber, which is what makes the app read as native rather than as a web
 * page. On a wide screen it becomes a centred dialog, because a full-height
 * bottom sheet on a desktop monitor looks broken.
 */

import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type PointerEvent as ReactPointerEvent,
  type ReactNode,
} from "react";
import { createPortal } from "react-dom";
import { CloseButton } from "./primitives";

/** Drag distance past which releasing dismisses instead of springing back. */
const DISMISS_THRESHOLD = 120;

/**
 * How long the panel stays mounted after close is requested. Matches the
 * close animation in sheet.css (`--pb-duration-press`); anything longer just
 * leaves a finished, invisible sheet in the DOM holding the body scroll lock.
 */
const CLOSE_ANIMATION_MS = 220;

export interface SheetProps {
  open: boolean;
  onClose: () => void;
  title?: string;
  /** Rendered at the top-right of the sheet's toolbar, typically Save. */
  action?: ReactNode;
  children: ReactNode;
}

export function Sheet({ open, onClose, title, action, children }: SheetProps) {
  const panelRef = useRef<HTMLDivElement>(null);
  const [dragY, setDragY] = useState(0);
  const dragStart = useRef<number | null>(null);
  // Kept mounted through the closing animation so it doesn't vanish abruptly.
  const [visible, setVisible] = useState(open);

  useEffect(() => {
    if (open) {
      setVisible(true);
      setDragY(0);
      return;
    }
    const timer = window.setTimeout(() => setVisible(false), CLOSE_ANIMATION_MS);
    return () => window.clearTimeout(timer);
  }, [open]);

  // Escape closes, and the page behind must not scroll while a sheet is up.
  useEffect(() => {
    if (!open) return;

    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };

    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    document.addEventListener("keydown", onKeyDown);

    return () => {
      document.body.style.overflow = previousOverflow;
      document.removeEventListener("keydown", onKeyDown);
    };
  }, [open, onClose]);

  // Move focus into the sheet so keyboard and screen-reader users land here
  // rather than continuing to traverse the page underneath.
  useEffect(() => {
    if (!open) return;
    const node = panelRef.current;
    if (!node) return;
    const focusable = node.querySelector<HTMLElement>(
      'input, button, [href], select, textarea, [tabindex]:not([tabindex="-1"])',
    );
    (focusable ?? node).focus({ preventScroll: true });
  }, [open]);

  const onPointerDown = useCallback((e: ReactPointerEvent<HTMLDivElement>) => {
    dragStart.current = e.clientY;
    e.currentTarget.setPointerCapture(e.pointerId);
  }, []);

  const onPointerMove = useCallback((e: ReactPointerEvent<HTMLDivElement>) => {
    if (dragStart.current === null) return;
    // Only downward drags move the sheet; pulling up should do nothing.
    setDragY(Math.max(0, e.clientY - dragStart.current));
  }, []);

  const onPointerUp = useCallback(() => {
    if (dragStart.current === null) return;
    dragStart.current = null;
    setDragY((current) => {
      if (current > DISMISS_THRESHOLD) onClose();
      return 0;
    });
  }, [onClose]);

  if (!visible) return null;

  return createPortal(
    <div
      className={`pb-sheet-root ${open ? "is-open" : "is-closing"}`}
      role="dialog"
      aria-modal="true"
      aria-label={title}
    >
      <button
        type="button"
        className="pb-sheet-backdrop"
        aria-label="Close"
        tabIndex={-1}
        onClick={onClose}
      />

      <div
        ref={panelRef}
        className="pb-sheet"
        tabIndex={-1}
        style={
          dragY
            ? { transform: `translateY(${dragY}px)`, transition: "none" }
            : undefined
        }
      >
        <div
          className="pb-sheet__grabber-area"
          onPointerDown={onPointerDown}
          onPointerMove={onPointerMove}
          onPointerUp={onPointerUp}
          onPointerCancel={onPointerUp}
        >
          <span className="pb-sheet__grabber" />
        </div>

        <header className="pb-sheet__bar">
          <CloseButton onClick={onClose} />
          {title && <h2 className="pb-sheet__title">{title}</h2>}
          <div className="pb-sheet__action">{action}</div>
        </header>

        <div className="pb-sheet__content">{children}</div>
      </div>
    </div>,
    document.body,
  );
}

/**
 * Full-screen cover — used for onboarding, which must not be dismissible.
 */
export function FullScreenCover({
  open,
  children,
}: {
  open: boolean;
  children: ReactNode;
}) {
  useEffect(() => {
    if (!open) return;
    const previous = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      document.body.style.overflow = previous;
    };
  }, [open]);

  if (!open) return null;

  return createPortal(
    <div className="pb-cover" role="dialog" aria-modal="true">
      {children}
    </div>,
    document.body,
  );
}
