import type { RouteLocationRaw } from 'vue-router'
import type { RunState } from '@/stores/runs-store'
import { formatAbsolute } from '@/composables/time'
import { featureAnchor, type SuiteNode } from '@/lib/runTree'

/**
 * The breadcrumb trail on every drill-down (bobcat#787): `Runs › Suite › run › Feature › Scenario`.
 * The run segment always carries the run's identity — when it started, its tag, its branch — because
 * a trail reading `Runs › Wallets › run` says which suite and not which of its ten runs.
 */
export interface Crumb {
  label: string
  /** Where the segment goes; the current page's segment goes nowhere. */
  to?: RouteLocationRaw
  /** The longer form, as a tooltip. */
  title?: string
  testId?: string
}

/** `10:42` today, `26 Oct 10:42` otherwise — enough to tell one run from the next. */
export function formatClock(iso: string | null | undefined, now: number = Date.now()): string | null {
  if (!iso) return null
  const at = new Date(iso)
  if (Number.isNaN(at.getTime())) return null
  const time = at.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
  const today = new Date(now)
  const sameDay = at.getFullYear() === today.getFullYear() && at.getMonth() === today.getMonth() && at.getDate() === today.getDate()
  return sameDay ? time : `${at.toLocaleDateString([], { day: 'numeric', month: 'short' })} ${time}`
}

/** `10:42 · build-512 · main`: the run's identity in one segment, with what the run has of it. */
export function runCrumbLabel(run: Pick<RunState, 'startedAt' | 'tag' | 'branch'>, now: number = Date.now()): string {
  return [formatClock(run.startedAt, now), run.tag, run.branch].filter((part): part is string => !!part).join(' · ')
}

export function runsCrumb(): Crumb {
  return { label: 'Runs', to: '/runs', testId: 'crumb-runs' }
}

/** The suite has no page of its own; the board, filtered by eye, is where its runs are. */
export function suiteCrumb(suite: string): Crumb {
  return { label: suite, to: '/runs', testId: 'crumb-suite' }
}

export function runCrumb(run: RunState, current: boolean, now: number = Date.now()): Crumb {
  const crumb: Crumb = {
    label: runCrumbLabel(run, now),
    title: [`started ${formatAbsolute(run.startedAt) ?? run.startedAt}`, run.tag ? `tag ${run.tag}` : null, run.repository]
      .filter(Boolean)
      .join('\n'),
    testId: 'crumb-run',
  }
  if (!current) crumb.to = `/runs/${run.runId}`
  return crumb
}

/** The feature's section on the run page. */
export function featureCrumb(runId: string, feature: string, current: boolean): Crumb {
  return placeCrumb(runId, featureAnchor(feature), feature || '(no feature)', current)
}

/**
 * The section a scenario sits in, on the run page: its feature, or the namespace and class a
 * forwarded test's name gives (bobcat#801). `anchor` is that node's, from `parentAnchorOf`.
 */
export function placeCrumb(runId: string, anchor: string, label: string, current: boolean): Crumb {
  const crumb: Crumb = { label, testId: 'crumb-feature' }
  if (!current) crumb.to = { path: `/runs/${runId}`, hash: `#${anchor}` }
  return crumb
}

/**
 * One level of a zoomed run page (bobcat#801): `Runs › Suite › run › ServiceTests.Services ›
 * AlertTests`. The run segment then leads back to the whole run, each zoom level above the current
 * one leads to that level, and the status filter rides along on every one of them, so zooming out
 * does not lose what the reader was looking for.
 */
export function zoomCrumb(runId: string, node: Pick<SuiteNode, 'anchor' | 'name'>, current: boolean, status: string | null): Crumb {
  const crumb: Crumb = { label: node.name || '(no feature)', title: node.anchor, testId: 'crumb-zoom' }
  if (!current) crumb.to = { path: `/runs/${runId}`, query: withStatus({ in: node.anchor }, status) }
  return crumb
}

/** The run segment of a zoomed page: back to the whole run, keeping the filter. */
export function unzoomedRunCrumb(run: RunState, status: string | null, now: number = Date.now()): Crumb {
  return { ...runCrumb(run, true, now), to: { path: `/runs/${run.runId}`, query: withStatus({}, status) } }
}

function withStatus(query: Record<string, string>, status: string | null): Record<string, string> {
  return status ? { ...query, status } : query
}

export function scenarioCrumb(title: string): Crumb {
  return { label: title, testId: 'crumb-scenario' }
}
