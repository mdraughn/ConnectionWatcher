/// <reference types="vitest/config" />
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { resolve } from "path";

export default defineConfig({
  plugins: [react()],
  test: {
    environment: "happy-dom",
  },
  build: {
    outDir: "../wwwroot/js",
    emptyOutDir: true,
    rollupOptions: {
      input: {
        dashboard: resolve(__dirname, "src/dashboard.tsx"),
        history: resolve(__dirname, "src/history.tsx"),
      },
      output: {
        // Entries keep stable names: the Razor pages reference them with
        // asp-append-version, which busts the cache per build. Chunks are
        // imported BY the entries with no version stamp, so their names must
        // carry the content hash — a stable chunk name lets a browser pair a
        // fresh entry with a stale cached chunk and the module graph dies
        // ("does not provide an export named ...") on the first deploy.
        entryFileNames: "[name].js",
        chunkFileNames: "chunk-[name]-[hash].js",
        assetFileNames: "[name][extname]",
      },
    },
  },
});
