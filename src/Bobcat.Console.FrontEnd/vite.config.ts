/// <reference types="vitest/config" />
import { fileURLToPath, URL } from "node:url";
import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";

// The /api proxy targets the Bobcat.Console host's fixed dev port
// (src/Bobcat.Console/Properties/launchSettings.json). ws: true carries the
// SignalR websocket upgrade for /api/messages through the same proxy.
const monitorUrl = "http://localhost:5525";

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
        target: monitorUrl,
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
