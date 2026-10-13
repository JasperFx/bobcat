/**
 * Vitest setup — registers Element Plus globally so component-mount tests don't
 * have to stub every `el-*` component they touch, plus minimal polyfills for
 * APIs happy-dom doesn't provide.
 */
import { config } from '@vue/test-utils'
import ElementPlus from 'element-plus'

config.global.plugins = [...(config.global.plugins ?? []), ElementPlus]

// Element Plus measures with ResizeObserver, which happy-dom doesn't provide.
// Untyped access: the vitest tsconfig clears DOM libs, so the global is unknown to it.
const g = globalThis as Record<string, unknown>
if (typeof g.ResizeObserver === 'undefined') {
  g.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  }
}
