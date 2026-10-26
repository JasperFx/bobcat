import type { RunState, ScenarioState, ScenarioStatus } from '@/stores/runs-store'

/**
 * The run page's table of contents (bobcat#787): the suite tree with a status on every node and
 * rollup counts per node, the summary tiles above it, and which scenario "Go to running" goes to.
 * Pure functions over the store's scenarios, so the page's tests drive them without a mount.
 *
 * bobcat#801 made it a tree of any depth, for suites of thousands. A Bobcat specification's node is
 * its feature. A supervised xUnit/TUnit test has no feature (issue #195) and arrives named
 * `Namespace.Class.method`, so its nodes are its namespace and class: without that, a 2,944-test
 * suite was one flat "(no feature)" bucket. A node a reader can zoom into, fold, and filter.
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

/**
 * One node of the suite tree: a Bobcat feature, or a namespace or class a forwarded test's dotted
 * name implies. Counts and status roll up everything beneath it, children included.
 */
export interface SuiteNode {
  /** The node's title: a feature's name, or the dotted segments it stands for (`Services.AlertTests`). */
  name: string
  /**
   * The node's identity on the page and in the URL: the element id its section carries, the target
   * of a jump, and what `?in=` names when the page is zoomed into it.
   */
  anchor: string
  status: TocStatus
  counts: TocCounts
  /** The scenarios directly under this node. */
  scenarios: ScenarioNode[]
  /** The nodes beneath it, in the order the run first reached them. */
  children: SuiteNode[]
}

/** A top-level node: still called a feature, which is what a Bobcat suite's top level is. */
export type FeatureNode = SuiteNode

export interface RunTree {
  status: TocStatus
  counts: TocCounts
  /** The top-level nodes. */
  features: SuiteNode[]
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

/** The anchor of a node a dotted test name implies: `suite:Ns.Class`. */
export function namespaceAnchor(segments: readonly string[]): string {
  return `suite:${segments.join('.')}`
}

/**
 * Where a scenario sits in the tree: the path of nodes above it, and its title under them.
 *
 * - A Bobcat specification sits under its feature.
 * - A test with no feature (a supervised xUnit/TUnit test, issue #195) sits under the namespace and
 *   class its dotted name gives: `ServiceTests.Services.AlertTests.raises_once(x: 1)` is
 *   `raises_once(x: 1)` under ServiceTests › Services › AlertTests. The argument list is cut before
 *   splitting, because a theory's arguments may hold dots of their own (as `splitDisplayName` does).
 * - A bare name with neither sits under the nameless feature, as before.
 */
export interface Placement {
  kind: 'feature' | 'namespace'
  /** The node path from the top: one feature, or the namespace and class segments. */
  path: string[]
  title: string
}

export function placementOf(feature: string, title: string): Placement {
  if (feature) return { kind: 'feature', path: [feature], title }
  const paren = title.indexOf('(')
  const head = paren >= 0 ? title.slice(0, paren) : title
  const dot = head.lastIndexOf('.')
  if (dot <= 0) return { kind: 'feature', path: [''], title }
  return { kind: 'namespace', path: head.slice(0, dot).split('.'), title: title.slice(dot + 1) }
}

/** `Feature/Scenario` split the way the store splits a uid; a bare name has no feature. */
function splitIdentity(identity: string): { feature: string; title: string } {
  const slash = identity.indexOf('/')
  return slash > 0
    ? { feature: identity.substring(0, slash), title: identity.substring(slash + 1) }
    : { feature: '', title: identity }
}

/**
 * The anchor of the node a scenario sits directly under: the target of the spec page's crumb back
 * into the run. Compaction (below) never moves a scenario, so this is the same whatever else the
 * run holds.
 */
export function parentAnchorOf(scenario: Pick<ScenarioState, 'feature' | 'scenario'>): string {
  const placement = placementOf(scenario.feature, scenario.scenario)
  return placement.kind === 'feature' ? featureAnchor(placement.path[0]!) : namespaceAnchor(placement.path)
}

/** The label of that node, for the same crumb: the feature, or the dotted namespace and class. */
export function parentLabelOf(scenario: Pick<ScenarioState, 'feature' | 'scenario'>): string {
  const placement = placementOf(scenario.feature, scenario.scenario)
  return placement.kind === 'feature' ? placement.path[0] || '(no feature)' : placement.path.join('.')
}

/**
 * The tree. Nodes in the order the run first reached them and scenarios likewise: the page lists
 * them that way, and a rail that re-sorted would send a jump somewhere else than the reader's eye
 * expects.
 *
 * `expected` names the specifications the run was asked for — a commanded run's selection — so
 * one the run has not reached yet is on the rail already, as "no result yet", rather than
 * appearing from nowhere when it starts. A scenario the run published stands in for its entry.
 *
 * A chain of namespace nodes with nothing of their own folds into one (`ServiceTests.Services`),
 * the way an editor's compact folders do: a level that only leads to one other level is a click
 * that tells the reader nothing.
 */
export function buildRunTree(scenarios: readonly ScenarioState[], expected: readonly string[] = []): RunTree {
  const roots: SuiteNode[] = []
  const byAnchor = new Map<string, SuiteNode>()
  const seen = new Set<string>()

  const nodeAt = (placement: Placement): SuiteNode => {
    let siblings = roots
    let node: SuiteNode | undefined
    for (let depth = 0; depth < placement.path.length; depth++) {
      const segments = placement.path.slice(0, depth + 1)
      const anchor = placement.kind === 'feature' ? featureAnchor(segments[0]!) : namespaceAnchor(segments)
      node = byAnchor.get(anchor)
      if (!node) {
        node = { name: segments[depth]!, anchor, status: 'pending', counts: emptyCounts(), scenarios: [], children: [] }
        byAnchor.set(anchor, node)
        siblings.push(node)
      }
      siblings = node.children
    }
    return node!
  }

  for (const scenario of scenarios) {
    seen.add(scenario.uid)
    const placement = placementOf(scenario.feature, scenario.scenario)
    const status = tocStatusOf(scenario.status)
    nodeAt(placement).scenarios.push({ uid: scenario.uid, title: placement.title, status, scenarioStatus: scenario.status })
  }

  for (const identity of expected) {
    if (seen.has(identity)) continue
    seen.add(identity)
    const { feature, title } = splitIdentity(identity)
    const placement = placementOf(feature, title)
    nodeAt(placement).scenarios.push({ uid: identity, title: placement.title, status: 'pending', scenarioStatus: null })
  }

  const features = roots.map(compact)
  const counts = emptyCounts()
  for (const node of features) {
    tally(node)
    for (const status of TOC_STATUSES) counts[status] += node.counts[status]
  }

  return { status: rollup(counts), counts, features }
}

/** Fold a namespace node with no scenarios and one child into that child, all the way down. */
function compact(node: SuiteNode): SuiteNode {
  let current = node
  while (current.anchor.startsWith('suite:') && current.scenarios.length === 0 && current.children.length === 1) {
    const only = current.children[0]!
    current = { ...only, name: `${current.name}.${only.name}` }
  }
  return { ...current, children: current.children.map(compact) }
}

/** A node's counts and status from its own scenarios and everything beneath it. */
function tally(node: SuiteNode): void {
  const counts = emptyCounts()
  for (const scenario of node.scenarios) counts[scenario.status]++
  for (const child of node.children) {
    tally(child)
    for (const status of TOC_STATUSES) counts[status] += child.counts[status]
  }
  node.counts = counts
  node.status = rollup(counts)
}

/**
 * The tree narrowed to one status: a node keeps the scenarios of that status and the children with
 * any left, and is dropped when nothing is left beneath it. Its rollup is kept — a node's counts
 * are its whole story, not the filtered view's.
 */
export function filterTree(tree: RunTree, status: TocStatus | null): RunTree {
  if (status === null) return tree
  const narrow = (node: SuiteNode): SuiteNode | null => {
    const scenarios = node.scenarios.filter((s) => s.status === status)
    const children = node.children.map(narrow).filter((child): child is SuiteNode => child !== null)
    return scenarios.length > 0 || children.length > 0 ? { ...node, scenarios, children } : null
  }
  return { ...tree, features: tree.features.map(narrow).filter((node): node is SuiteNode => node !== null) }
}

/** Every node of the tree, parents before their children. */
export function allNodes(tree: Pick<RunTree, 'features'>): SuiteNode[] {
  const nodes: SuiteNode[] = []
  const walk = (node: SuiteNode) => {
    nodes.push(node)
    node.children.forEach(walk)
  }
  tree.features.forEach(walk)
  return nodes
}

/** A node and the nodes above it, outermost first; null when the tree has no such node. */
export function findNode(tree: Pick<RunTree, 'features'>, anchor: string): { node: SuiteNode; ancestors: SuiteNode[] } | null {
  const search = (nodes: SuiteNode[], above: SuiteNode[]): { node: SuiteNode; ancestors: SuiteNode[] } | null => {
    for (const node of nodes) {
      if (node.anchor === anchor) return { node, ancestors: above }
      const found = search(node.children, [...above, node])
      if (found) return found
    }
    return null
  }
  return search(tree.features, [])
}

/** The nodes above the scenario with this uid, outermost first, and the one it sits in; null when absent. */
export function pathToScenario(tree: Pick<RunTree, 'features'>, uid: string): SuiteNode[] | null {
  const search = (nodes: SuiteNode[], above: SuiteNode[]): SuiteNode[] | null => {
    for (const node of nodes) {
      const path = [...above, node]
      if (node.scenarios.some((s) => s.uid === uid)) return path
      const found = search(node.children, path)
      if (found) return found
    }
    return null
  }
  return search(tree.features, [])
}

/** Every scenario uid at or beneath a node. */
export function uidsUnder(node: SuiteNode): Set<string> {
  const uids = new Set<string>()
  const walk = (n: SuiteNode) => {
    for (const s of n.scenarios) uids.add(s.uid)
    n.children.forEach(walk)
  }
  walk(node)
  return uids
}

/** The page zoomed into one node (bobcat#801): that node is the whole tree, its counts the tree's. */
export function zoomTree(tree: RunTree, anchor: string | null): RunTree {
  if (!anchor) return tree
  const found = findNode(tree, anchor)
  if (!found) return tree
  return { status: found.node.status, counts: found.node.counts, features: [found.node] }
}

/**
 * The run size above which a passed node starts folded (bobcat#801). Below it the page is short
 * enough to read whole, as it always was; above it, what passed is a line until asked for, the
 * same rule a passed scenario already follows.
 */
export const FOLD_PASSED_ABOVE = 100

export function startsCollapsed(node: Pick<SuiteNode, 'status'>, size: number, filtered: boolean): boolean {
  // A filter is the reader asking to see those scenarios: nothing it shows starts hidden.
  return !filtered && size > FOLD_PASSED_ABOVE && node.status === 'passed'
}

/** One line of the flattened tree, as the rail and the page draw it: a node, how deep, whether folded. */
export interface TreeRow {
  node: SuiteNode
  depth: number
  collapsed: boolean
}

/** The tree as rows, parents before children, a folded node's descendants left out. */
export function flattenTree(features: readonly SuiteNode[], isCollapsed: (node: SuiteNode) => boolean): TreeRow[] {
  const rows: TreeRow[] = []
  const walk = (node: SuiteNode, depth: number) => {
    const collapsed = isCollapsed(node)
    rows.push({ node, depth, collapsed })
    if (!collapsed) node.children.forEach((child) => walk(child, depth + 1))
  }
  features.forEach((node) => walk(node, 0))
  return rows
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
