/// <reference types="vitest/config" />
import { fileURLToPath, URL } from "node:url";
import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";

// The /api proxy targets `bobcat ui` on its fixed port. ws: true carries the SignalR websocket
// upgrade for /api/messages through the same proxy. BOBCAT_UI_URL points a dev server at a
// `bobcat ui --port <n>` elsewhere.
const bobcatUiUrl = process.env.BOBCAT_UI_URL ?? "http://localhost:5525";

// https://vite.dev/config/
export default defineConfig({
  plugins: [vue()],
  test: {
    globals: true,
    // happy-dom environment for component-mount tests (Element Plus components
    // touch window/document at mount time). Logic-only store tests don't depend
    // on it but are unaffected.
    environment: "happy-dom",
    exclude: ["**/node_modules/**"],
    setupFiles: ["./src/__tests__/setup.ts"],
  },
  server: {
    proxy: {
      "/api": {
        target: bobcatUiUrl,
        ws: true,
        changeOrigin: true,
      },
    },
  },
  resolve: {
    alias: {
      "@": fileURLToPath(new URL("./src", import.meta.url)),
    },
  },
});
