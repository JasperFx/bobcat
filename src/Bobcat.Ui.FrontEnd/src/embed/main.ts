import { defineCustomElement } from 'vue'

import BobcatSpec from './BobcatSpec.vue'
import variables from '../styles/variables.css?inline'

/**
 * The `<bobcat-spec>` element (bobcat#553): one script a documentation page includes.
 *
 * Built by vite.config.embed.ts into dist-embed/bobcat-spec.js with every component's styles inside
 * it (the whole build compiles in custom-element mode, so a child component's `<style>` lands in the
 * shadow root too — Vue 3.5). The board's colour tokens are declared on `:root` and `html.dark` for
 * the page; a shadow root sees neither, so the same sheet is re-addressed to `:host`.
 */
export const ELEMENT_NAME = 'bobcat-spec'

/** The board's stylesheet re-addressed to a shadow host: what was declared on `:root` and on `html.dark` is declared on `:host`. */
export function hostTheme(stylesheet: string): string {
  return stylesheet.replace(/:root\b/g, ':host').replace(/html\.dark\b/g, ':host')
}

export const BobcatSpecElement = defineCustomElement(BobcatSpec, { styles: [hostTheme(variables)] })

// By shape rather than through the DOM lib: the Vitest project compiles without one, and this
// module is imported by its test. Defined once, so a page that includes the script twice is fine.
interface Registry {
  get(name: string): unknown
  define(name: string, constructor: new () => object): void
}

const registry = (globalThis as { customElements?: Registry }).customElements
if (registry && !registry.get(ELEMENT_NAME)) {
  registry.define(ELEMENT_NAME, BobcatSpecElement as unknown as new () => object)
}
