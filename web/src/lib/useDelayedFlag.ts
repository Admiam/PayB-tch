import { useEffect, useState } from "react";

/**
 * True only once `active` has held for `delayMs`.
 *
 * Loading indicators have two failure modes: showing nothing during a slow
 * request, and flickering during a fast one. A request that finishes in 60 ms
 * announces itself by simply producing the result; only a request slow enough
 * to feel stalled needs a spinner. This gates on that.
 */
export function useDelayedFlag(active: boolean, delayMs = 150): boolean {
  const [shown, setShown] = useState(false);

  useEffect(() => {
    if (!active) {
      setShown(false);
      return;
    }
    const timer = window.setTimeout(() => setShown(true), delayMs);
    return () => window.clearTimeout(timer);
  }, [active, delayMs]);

  return shown;
}
