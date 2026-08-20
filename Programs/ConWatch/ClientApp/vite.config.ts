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
        entryFileNames: "[name].js",
        chunkFileNames: "chunk-[name].js",
        assetFileNames: "[name][extname]",
      },
    },
  },
});
