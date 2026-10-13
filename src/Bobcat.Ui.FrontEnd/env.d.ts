/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Bobcat viewer origin for test-run-gate drill-in. Defaults to http://localhost:5525. */
  readonly VITE_BOBCAT_URL?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
