/**
 * How much of the run board to show (stoat#75). Retention on the server already keeps the latest
 * runs of each repository and suite (five by default, BOBCAT_MONITOR_RETENTION_RUNS); this only
 * chooses how many runs a viewer sees at once, and it is that viewer's choice — remembered in their
 * browser, nowhere else.
 */
export const RUN_LIMIT_CHOICES = [5, 10, 25, 50, 0] as const
export const DEFAULT_RUN_LIMIT = 10
const STORAGE_KEY = 'stoat.runs.limit'

/**
 * The runs to render, newest first. A LIVE run is always shown and never counts against the
 * limit: hiding a suite while it is running is hiding the one card someone is waiting on. The
 * limit caps the finished ones; 0 means all of them.
 */
export function visibleRuns<T>(
  newestFirst: readonly T[],
  limit: number,
  isLive: (run: T) => boolean,
): { shown: T[]; hidden: number } {
  const shown: T[] = []
  let finished = 0
  let hidden = 0
  for (const run of newestFirst) {
    if (isLive(run) || limit === 0 || finished < limit) {
      shown.push(run)
      if (!isLive(run)) finished++
    } else {
      hidden++
    }
  }
  return { shown, hidden }
}

/** The viewer's remembered limit, or the default — storage can be absent or refuse. */
export function loadRunLimit(): number {
  try {
    // Checked before converting: Number(null) is 0, which is the real choice "all".
    const stored = localStorage.getItem(STORAGE_KEY)
    if (stored === null) return DEFAULT_RUN_LIMIT
    const limit = Number(stored)
    return (RUN_LIMIT_CHOICES as readonly number[]).includes(limit) ? limit : DEFAULT_RUN_LIMIT
  } catch {
    return DEFAULT_RUN_LIMIT
  }
}

export function saveRunLimit(limit: number): void {
  try {
    localStorage.setItem(STORAGE_KEY, String(limit))
  } catch {
    // A viewer whose browser will not remember simply gets the default next time.
  }
}
