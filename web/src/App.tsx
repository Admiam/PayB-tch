import { useEffect } from "react";
import { Dashboard } from "@/screens/Dashboard";
import { useStore } from "@/store/useStore";

export default function App() {
  const load = useStore((s) => s.load);
  const appearance = useStore((s) => s.appearance);

  // Hydrate from localStorage once, before first paint of real content.
  useEffect(() => {
    load();
  }, [load]);

  // The theme lives on <html> rather than a React context so that portalled
  // sheets — which render outside the app tree — inherit it too.
  useEffect(() => {
    const root = document.documentElement;
    if (appearance === "system") root.removeAttribute("data-theme");
    else root.setAttribute("data-theme", appearance);
  }, [appearance]);

  return <Dashboard />;
}
