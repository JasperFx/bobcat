import { fileURLToPath, URL } from 'node:url'
import { defineConfig, type Plugin } from 'vite'
import vue from '@vitejs/plugin-vue'

/**
 * The HTML results page (bobcat#541) as ONE file: dist-results/results.html, with its script and
 * styles inlined, which the bobcat tool embeds and fills with a run's results. It is an archived
 * artifact opened from disk or a CI download, so it may reference nothing: no chunk, no stylesheet,
 * no font, no favicon.
 *
 * The SPA's own build (vite.config.ts) is unchanged; this shares its components, not its bundle,
 * so the page carries neither Element Plus nor the router.
 */
export default defineConfig({
  plugins: [vue(), singleFile()],
  build: {
    outDir: 'dist-results',
    emptyOutDir: true,
    copyPublicDir: false,
    // Everything into one script and one stylesheet, both inlined below.
    cssCodeSplit: false,
    assetsInlineLimit: Number.MAX_SAFE_INTEGER,
    modulePreload: false,
    rollupOptions: {
      input: fileURLToPath(new URL('./results.html', import.meta.url)),
    },
  },
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
})

/**
 * Replaces the built page's script and stylesheet references with their contents and drops the
 * files, failing the build if anything external is left behind — a page that silently needs a
 * sibling file is exactly what an archived artifact must not be.
 */
function singleFile(): Plugin {
  return {
    name: 'bobcat-results-single-file',
    enforce: 'post',
    generateBundle(_, bundle) {
      const html = Object.values(bundle).find((f) => f.type === 'asset' && f.fileName.endsWith('.html'))
      if (!html || html.type !== 'asset') return

      let page = String(html.source)
      for (const [name, file] of Object.entries(bundle)) {
        if (file === html) continue
        const reference = new RegExp(`<script[^>]*src="[^"]*${escape(name)}"[^>]*></script>|<link[^>]*href="[^"]*${escape(name)}"[^>]*>`)
        if (!reference.test(page)) continue

        if (file.type === 'chunk') {
          // `</script` inside the code would end the inline element early.
          const code = file.code.replace(/<\/script/gi, '<\\/script')
          page = page.replace(reference, () => `<script type="module">${code}</script>`)
        } else {
          page = page.replace(reference, () => `<style>${String(file.source)}</style>`)
        }
        delete bundle[name]
      }

      const leftover = page.match(/<(?:script|link)[^>]*(?:src|href)="[^"]*"[^>]*>/)
      if (leftover) this.error(`The results page still references a file: ${leftover[0]}`)

      html.source = page
    },
  }
}

function escape(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}
