import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";

export default defineConfig(({ mode }) => ({
  base: "./",
  build: {
    // Keep Rollup from sharing route preload helpers with Mahjong's cold entry.
    emptyOutDir: mode !== "portal",
    modulePreload: { polyfill: false },
    rollupOptions: {
      input: fileURLToPath(new URL(mode === "portal" ? "./portal.html" : "./index.html", import.meta.url)),
    },
  },
  server: mode === "geometry" ? { hmr: false, watch: null } : undefined,
}));
