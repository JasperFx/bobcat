import { fileURLToPath, URL } from 'node:url'
import { defineConfig, type Plugin } from 'vite'
import vue from '@vitejs/plugin-vue'

/**
 * The `<bobcat-spec>` web component (bobcat#553) as ONE file: dist-embed/bobcat-spec.js, which the
 * bobcat tool embeds and `bobcat results-docs` writes beside the Markdown it generates, so a
 * documentation page renders a specification with one script tag and one element.
 *
 * Every .vue file in this build compiles in custom-element mode, which puts each component's styles
 * into a `styles` array that Vue injects into the element's shadow root — the child components'
 * included (Vue 3.5) — instead of into the document. Like the results page, this shares the SPA's
 * components and not its bundle: no Element Plus, no router, no Pinia.
 */
export default defineConfig({
  plugins: [vue({ customElement: true }), oneFile()],
  define: {
    // Vue reads it at run time; a library build does not set it for the consumer's page.
    'process.env.NODE_ENV': JSON.stringify('production'),
  },
  build: {
    outDir: 'dist-embed',
    emptyOutDir: true,
    copyPublicDir: false,
    cssCodeSplit: false,
    assetsInlineLimit: Number.MAX_SAFE_INTEGER,
    lib: {
      entry: fileURLToPath(new URL('./src/embed/main.ts', import.meta.url)),
      name: 'BobcatSpec',
      formats: ['iife'],
      fileName: () => 'bobcat-spec.js',
    },
  },
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
})

/** Fails the build if it produced anything besides the one script: a page including it must need nothing else. */
function oneFile(): Plugin {
  return {
    name: 'bobcat-spec-one-file',
    enforce: 'post',
    generateBundle(_, bundle) {
      const names = Object.keys(bundle)
      if (names.length !== 1 || names[0] !== 'bobcat-spec.js') {
        this.error(`The bobcat-spec build must produce exactly bobcat-spec.js, not: ${names.join(', ')}`)
      }
    },
  }
}
