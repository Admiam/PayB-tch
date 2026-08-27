import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";
import { fileURLToPath, URL } from "node:url";

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      "@": fileURLToPath(new URL("./src", import.meta.url)),
    },
  },
  build: {
    // The app is local-first and tiny; a single chunk beats waterfall requests
    // on mobile. Only the icon set is worth splitting out since it is the one
    // dependency that grows independently of our own code.
    rollupOptions: {
      output: {
        manualChunks(id) {
          if (id.includes("lucide-react")) return "icons";
          return undefined;
        },
      },
    },
    // Fail loudly if we ever regress the budget rather than silently shipping
    // a heavy bundle to phones.
    chunkSizeWarningLimit: 250,
  },
  test: {
    environment: "jsdom",
    setupFiles: ["./src/test-setup.ts"],
    globals: true,
  },
});
