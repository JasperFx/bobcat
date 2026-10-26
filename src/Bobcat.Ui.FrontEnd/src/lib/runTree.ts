import type { RunState, ScenarioState, ScenarioStatus } from '@/stores/runs-store'

/**
 * The run page's table of contents (bobcat#787): the suite tree — suite › feature › scenario — with
 * a status on every node and rollup counts per feature, the summary tiles above it, and which
 * scenario "Go to running" goes to. Pure functions over the store's scenarios, so the page's tests
 * drive them without a mount.
 */

/**
 * A node's status, as the rail colours it. Four words rather than the store's seven: a reader
 * scanning a thousand scenarios wants to know what is going, what went wrong, what has not been
 * reached, and what is fine — the finer store status stays on the scenario itself.
 */
export type TocStatus = 'running' | 'failed' | 'pending' | 'passed'

export const TOC_STATUSES: readonly TocStatus[] = ['running', 'failed', 'pending', 'passed']

/**
 * The store's status folded into the rail's. A scheduled retry is still going. An acceptance spec
 * not yet passing (bobcat#545) reads as failed here — it is not a failure of the run, and the
 * tiles count it apart, but on the rail it is the thing a reader wants to find.
 */
export function tocStatusOf(status: ScenarioStatus): TocStatus {
  switch (status) {
    case 'running':
    case 'retry-scheduled':
      return 'running'
    case 'failed':
    case 'aborted':
    case 'acceptance-failing':
      return 'failed'
    default:
      return 'passed'
  }
}

export type TocCounts = Record<TocStatus, number>

export interface ScenarioNode {
  uid: string
  /** The scenario's own title — the feature is the parent node. */
  title: string
  status: TocStatus
  /** The store's finer status, for the badge beside the title. */
  scenarioStatus: ScenarioStatus | null
}

export interface FeatureNode {
  /** The feature's title; empty for a forwarded foreign test, which has none (issue #195). */
  name: string
  /** The element id the feature's section carries on the page, for a jump. */
  anchor: string
  status: TocStatus
  counts: TocCounts
  scenarios: ScenarioNode[]
}

export interface RunTree {
  status: TocStatus
  counts: TocCounts
  features: FeatureNode[]
}

export function emptyCounts(): TocCounts {
  return { running: 0, failed: 0, pending: 0, passed: 0 }
}

/**
 * A parent's status from its children's: anything running makes it running — the run is still
 * deciding it — else anything failed, else anything unreached, else passed. An empty parent is
 * pending: nothing has said anything about it.
 */
export function rollup(counts: TocCounts): TocStatus {
  if (counts.running > 0) return 'running'
  if (counts.failed > 0) return 'failed'
  if (counts.pending > 0) return 'pending'
  return counts.passed > 0 ? 'passed' : 'pending'
}

/** The id a feature's section and a scenario's block carry, so the rail's jump has a target. */
export function featureAnchor(feature: string): string {
  return `feature:${feature || '(none)'}`
}

export function scenarioAnchor(uid: string): string {
  return `spec:${uid}`
}

/** `Feature/Scenario` split the way the store splits a uid; a bare name has no feature. */
function splitIdentity(identity: string): { feature: string; title: string } {
  const slash = identity.indexOf('/')
  return slash > 0
    ? { feature: identity.substring(0, slash), title: identity.substring(slash + 1) }
    : { feature: '', title: identity }
}

/**
 * The tree. Features in the order the run first reached them and scenarios likewise: the page
 * lists them that way, and a rail that re-sorted would send a jump somewhere else than the
 * reader's eye expects.
 *
 * `expected` names the specifications the run was asked for — a commanded run's selection — so
 * one the run has not reached yet is on the rail already, as "no result yet", rather than
 * appearing from nowhere when it starts. A scenario the run published stands in for its entry.
 */
export function buildRunTree(scenarios: readonly ScenarioState[], expected: readonly string[] = []): RunTree {
  const features = new Map<string, FeatureNode>()
  const seen = new Set<string>()

  const featureOf = (name: string): FeatureNode => {
    let node = features.get(name)
    if (!node) {
      node = { name, anchor: featureAnchor(name), status: 'pending', counts: emptyCounts(), scenarios: [] }
      features.set(name, node)
    }
    return node
  }

  for (const scenario of scenarios) {
    seen.add(scenario.uid)
    const status = tocStatusOf(scenario.status)
    const node = featureOf(scenario.feature)
    node.scenarios.push({ uid: scenario.uid, title: scenario.scenario, status, scenarioStatus: scenario.status })
    node.counts[status]++
  }

  for (const identity of expected) {
    if (seen.has(identity)) continue
    seen.add(identity)
    const { feature, title } = splitIdentity(identity)
    const node = featureOf(feature)
    node.scenarios.push({ uid: identity, title, status: 'pending', scenarioStatus: null })
    node.counts.pending++
  }

  const counts = emptyCounts()
  for (const node of features.values()) {
    node.status = rollup(node.counts)
    for (const status of TOC_STATUSES) counts[status] += node.counts[status]
  }

  return { status: rollup(counts), counts, features: [...features.values()] }
}

/** The tree narrowed to one status; a feature with nothing left is dropped, its rollup kept. */
export function filterTree(tree: RunTree, status: TocStatus | null): RunTree {
  if (status === null) return tree
  return {
    ...tree,
    features: tree.features
      .map((f) => ({ ...f, scenarios: f.scenarios.filter((s) => s.status === status) }))
      .filter((f) => f.scenarios.length > 0),
  }
}

/**
 * What the summary tiles say. The run's own terminal counts win once it has finished — the
 * publisher's tally is the one the exit code was decided on — and until then the scenarios on
 * hand are counted. "No result yet" is the declared total less everything heard of, or, with no
 * declared total, the expected specifications the run has not reached.
 */
export interface RunSummary {
  passed: number
  passedOnRetry: number
  failed: number
  running: number
  pending: number
  /** Acceptance specs not yet passing (bobcat#545): shown only when there are any. */
  acceptanceFailing: number
}

export function summarize(
  run: Pick<RunState, 'counts' | 'finished' | 'totalScenarios'>,
  scenarios: readonly ScenarioState[],
  expected: readonly string[] = [],
): RunSummary {
  const summary: RunSummary = { passed: 0, passedOnRetry: 0, failed: 0, running: 0, pending: 0, acceptanceFailing: 0 }
  const seen = new Set<string>()

  for (const scenario of scenarios) {
    seen.add(scenario.uid)
    switch (scenario.status) {
      case 'passed':
        summary.passed++
        break
      case 'passed-on-retry':
        summary.passedOnRetry++
        break
      case 'failed':
      case 'aborted':
        summary.failed++
        break
      case 'acceptance-failing':
        summary.acceptanceFailing++
        break
      default:
        summary.running++
    }
  }

  if (run.finished && run.counts) {
    summary.passed = run.counts.passed
    summary.passedOnRetry = run.counts.passedOnRetry
    summary.failed = run.counts.failed + run.counts.indeterminate
  }

  const unreached = expected.filter((identity) => !seen.has(identity)).length
  const declared = run.totalScenarios != null && run.totalScenarios > 0 ? Math.max(0, run.totalScenarios - seen.size) : 0
  summary.pending = run.finished ? 0 : Math.max(unreached, declared)

  return summary
}

/**
 * The scenario "Go to running" scrolls to: the one in flight, or the first of several (parallel
 * lanes run more than one). Null when nothing is running.
 */
export function runningScenario(scenarios: readonly ScenarioState[]): ScenarioState | null {
  return scenarios.find((s) => s.status === 'running') ?? null
}

/**
 * Whether a scenario starts expanded on the page (bobcat#787): a failed or running one does, a
 * passed one is a line until clicked. A scenario still to be decided — scheduled for a retry, an
 * acceptance spec not yet passing — is read the way a failure is.
 */
export function startsExpanded(status: ScenarioStatus): boolean {
  return status !== 'passed' && status !== 'passed-on-retry'
}
