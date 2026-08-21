import { fileURLToPath, URL } from 'node:url'
// vitest/config re-exports vite's defineConfig with the `test` block typed.
import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'
import dts from 'vite-plugin-dts'

export default defineConfig({
  plugins: [vue(), dts({ rollupTypes: true, tsconfigPath: './tsconfig.json' })],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) }
  },
  build: {
    lib: {
      entry: fileURLToPath(new URL('./src/index.ts', import.meta.url)),
      name: 'EventModelVue',
      fileName: 'event-model-vue',
      formats: ['es']
    },
    // Peers stay external: a consumer supplies its own Vue and Vue Flow, and bundling a
    // second copy of Vue is how you get two reactivity systems that cannot see each other.
    rollupOptions: { external: ['vue', '@vue-flow/core'] }
  },
  test: {
    environment: 'happy-dom',
    globals: true
  }
})
