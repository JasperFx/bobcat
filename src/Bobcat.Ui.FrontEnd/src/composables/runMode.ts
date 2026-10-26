import { ref, type Ref } from 'vue'
import { loadMode, saveMode } from '@/lib/runners'

/**
 * The viewer's remembered run mode, cold or warm, as one ref every run button reads (bobcat#787).
 * A button shows the mode it will send; choosing warm in one dialog must change the chip on every
 * other button at once, which a ref per button read off localStorage at mount could not do.
 */
const mode: Ref<'cold' | 'warm'> = ref(loadMode())

export function useRunMode(): { mode: Ref<'cold' | 'warm'>; choose: (next: 'cold' | 'warm') => void } {
  return {
    mode,
    choose(next) {
      mode.value = next
      saveMode(next)
    },
  }
}

/** A test seam: read the remembered mode again after a test cleared storage. */
export function resetRunMode(): void {
  mode.value = loadMode()
}
