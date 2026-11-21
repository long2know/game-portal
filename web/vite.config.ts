import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";

export default defineConfig(({ mode }) => ({
  base: "./",
  build: {
    modulePreload: { polyfill: false },
    rollupOptions: {
      input: {
        index: fileURLToPath(new URL("./index.html", import.meta.url)),
        portal: fileURLToPath(new URL("./portal.html", import.meta.url)),
      },
    },
  },
  server: mode === "geometry" ? { hmr: false, watch: null } : undefined,
}));
