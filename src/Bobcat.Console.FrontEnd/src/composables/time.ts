import { onScopeDispose, readonly, ref } from 'vue'

/**
 * Age formatting for run cards (issue #196). A board that accumulates — and today's does, 46
 * runs across four repositories and worktrees — makes age the single most useful thing about a
 * card: it is what answers "is this mine, from just now?" without opening anything.
 */

const minute = 60_000
const hour = 60 * minute
const day = 24 * hour

/**
 * "just now" / "4m ago" / "2h ago" / "3d ago". Coarse on purpose: the question a card answers
 * is which run this is, not how long ago to the second. Returns null for an unparseable or
 * absent stamp, so a caller renders nothing rather than "NaN ago".
 */
export function formatRelative(iso: string | null | undefined, now: number = Date.now()): string | null {
  if (!iso) return null
  const at = Date.parse(iso)
  if (Number.isNaN(at)) return null

  // A stamp from the near future is clock skew between the publisher and this browser, not a
  // scheduled run. "just now" is the honest reading of it; a negative age is not.
  const elapsed = Math.max(0, now - at)

  if (elapsed < minute) return 'just now'
  if (elapsed < hour) return `${Math.floor(elapsed / minute)}m ago`
  if (elapsed < day) return `${Math.floor(elapsed / hour)}h ago`
  return `${Math.floor(elapsed / day)}d ago`
}

/** The full timestamp, for the tooltip behind the relative one. */
export function formatAbsolute(iso: string | null | undefined): string | null {
  if (!iso) return null
  const at = new Date(iso)
  return Number.isNaN(at.getTime()) ? null : at.toLocaleString()
}

/**
 * How long a run took, from its own two stamps — the other question people ask of a run card.
 * Null unless both stamps are present and ordered, because a duration derived from one of them
 * plus the current clock would be the run's age, not its length.
 */
export function formatDuration(
  startedAt: string | null | undefined,
  finishedAt: string | null | undefined,
): string | null {
  if (!startedAt || !finishedAt) return null
  const from = Date.parse(startedAt)
  const to = Date.parse(finishedAt)
  if (Number.isNaN(from) || Number.isNaN(to) || to < from) return null

  const ms = to - from
  if (ms < 1000) return `${ms}ms`
  if (ms < minute) return `${(ms / 1000).toFixed(1)}s`

  const minutes = Math.floor(ms / minute)
  const seconds = Math.round((ms % minute) / 1000)
  if (minutes < 60) return `${minutes}m${seconds.toString().padStart(2, '0')}s`

  return `${Math.floor(ms / hour)}h${Math.floor((ms % hour) / minute).toString().padStart(2, '0')}m`
}

/**
 * A clock that ticks slowly enough to keep relative labels honest without re-rendering the
 * board constantly. 15s is under the resolution of every label above ("4m ago" changes at
 * most once a minute), so nothing is ever visibly stale.
 */
export function useNow(intervalMs = 15_000) {
  const now = ref(Date.now())
  const handle = setInterval(() => (now.value = Date.now()), intervalMs)
  onScopeDispose(() => clearInterval(handle))
  return readonly(now)
}
