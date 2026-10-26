import { describe, expect, it } from 'vitest'
import {
  allNodes,
  buildRunTree,
  filterTree,
  findNode,
  flattenTree,
  FOLD_PASSED_ABOVE,
  parentAnchorOf,
  parentLabelOf,
  pathToScenario,
  placementOf,
  startsCollapsed,
  uidsUnder,
  zoomTree,
} from '../runTree'
import type { ScenarioState, ScenarioStatus } from '@/stores/runs-store'

/**
 * bobcat#801 — the run tree for suites of thousands: as deep as the suite's namespaces, foldable,
 * zoomable, filterable at every level. A supervised xUnit test (issue #195) arrives with no feature
 * and a dotted `Namespace.Class.method` name, which is where its levels come from.
 */
function forwarded(name: string, status: ScenarioStatus): ScenarioState {
  return {
    uid: `uid-${name}`,
    feature: '',
    scenario: name,
    status,
    attempt: 1,
    attempts: null,
    scheduledAttempt: null,
    outcome: null,
    durationMs: null,
    errorMessage: null,
    retryReason: null,
    steps: [],
    totalSteps: null,
    declaredSteps: [],
    plannedSteps: [],
    touchedTypes: [],
    finishedAt: null,
    state: null,
    workerPublished: false,
    forwarded: true,
    displayName: name,
    reports: [],
  }
}

const suite = () =>
  buildRunTree([
    forwarded('App.Tests.Orders.OrderTests.places', 'passed'),
    forwarded('App.Tests.Orders.OrderTests.cancels', 'failed'),
    forwarded('App.Tests.Billing.InvoiceTests.bills', 'passed'),
    forwarded('App.Tests.Billing.Tax.TaxTests.rounds(amount: 1.5)', 'running'),
  ])

describe('placementOf', () => {
  it('places a dotted test name under its namespace and class, cutting the arguments before it splits', () => {
    expect(placementOf('', 'Ns.Inner.Class.method(path: "a.b")')).toEqual({
      kind: 'namespace',
      path: ['Ns', 'Inner', 'Class'],
      title: 'method(path: "a.b")',
    })
  })

  it('leaves a Bobcat specification under its feature, dots and all', () => {
    expect(placementOf('Orders v1.2', 'places an order')).toEqual({ kind: 'feature', path: ['Orders v1.2'], title: 'places an order' })
  })
})

describe('buildRunTree, nested', () => {
  it('nests by namespace and class, folding a chain with nothing of its own into one node', () => {
    const tree = suite()
    expect(tree.features.map((n) => n.name)).toEqual(['App.Tests'])
    const root = tree.features[0]!
    expect(root.children.map((n) => n.name)).toEqual(['Orders.OrderTests', 'Billing'])
    expect(root.children[1]!.children.map((n) => n.name)).toEqual(['InvoiceTests', 'Tax.TaxTests'])
    expect(root.children[1]!.children[1]!.scenarios.map((s) => s.title)).toEqual(['rounds(amount: 1.5)'])
  })

  it('rolls counts and status up through every level', () => {
    const tree = suite()
    const [orders, billing] = tree.features[0]!.children
    expect(orders!.counts).toEqual({ running: 0, failed: 1, pending: 0, passed: 1 })
    expect(orders!.status).toBe('failed')
    expect(billing!.counts).toEqual({ running: 1, failed: 0, pending: 0, passed: 1 })
    expect(billing!.status).toBe('running')
    expect(tree.features[0]!.counts).toEqual({ running: 1, failed: 1, pending: 0, passed: 2 })
    expect(tree.counts).toEqual(tree.features[0]!.counts)
  })

  it('a scenario’s parent anchor and label are the node it sits in after folding', () => {
    const tree = suite()
    const tax = forwarded('App.Tests.Billing.Tax.TaxTests.rounds(amount: 1.5)', 'running')
    expect(findNode(tree, parentAnchorOf(tax))!.node.name).toBe('Tax.TaxTests')
    expect(parentLabelOf(tax)).toBe('App.Tests.Billing.Tax.TaxTests')
  })
})

describe('filterTree, nested', () => {
  it('keeps only the branches that hold the status, at any depth, with every rollup whole', () => {
    const tree = suite()
    const failed = filterTree(tree, 'failed')
    const root = failed.features[0]!
    expect(root.children.map((n) => n.name)).toEqual(['Orders.OrderTests'])
    expect(root.children[0]!.scenarios.map((s) => s.title)).toEqual(['cancels'])
    expect(root.counts).toEqual(tree.features[0]!.counts)
  })
})

describe('zooming', () => {
  it('finds a node with the nodes above it, and zooming makes it the whole tree', () => {
    const tree = suite()
    const tax = allNodes(tree).find((n) => n.name === 'Tax.TaxTests')!
    const found = findNode(tree, tax.anchor)!
    expect(found.ancestors.map((n) => n.name)).toEqual(['App.Tests', 'Billing'])

    const zoomed = zoomTree(tree, found.ancestors[1]!.anchor)
    expect(zoomed.features.map((n) => n.name)).toEqual(['Billing'])
    expect(zoomed.counts).toEqual({ running: 1, failed: 0, pending: 0, passed: 1 })
    expect([...uidsUnder(zoomed.features[0]!)].sort()).toEqual([
      'uid-App.Tests.Billing.InvoiceTests.bills',
      'uid-App.Tests.Billing.Tax.TaxTests.rounds(amount: 1.5)',
    ])
  })

  it('a stale anchor zooms nowhere, and no anchor is the whole run', () => {
    const tree = suite()
    expect(zoomTree(tree, 'suite:Gone')).toBe(tree)
    expect(zoomTree(tree, null)).toBe(tree)
  })

  it('finds the nodes above a scenario, outermost first', () => {
    const path = pathToScenario(suite(), 'uid-App.Tests.Orders.OrderTests.cancels')!
    expect(path.map((n) => n.name)).toEqual(['App.Tests', 'Orders.OrderTests'])
    expect(pathToScenario(suite(), 'nope')).toBeNull()
  })
})

describe('folding', () => {
  it('a passed node starts folded only on a large page with no filter', () => {
    expect(startsCollapsed({ status: 'passed' }, FOLD_PASSED_ABOVE + 1, false)).toBe(true)
    expect(startsCollapsed({ status: 'passed' }, FOLD_PASSED_ABOVE, false)).toBe(false)
    expect(startsCollapsed({ status: 'failed' }, 5000, false)).toBe(false)
    expect(startsCollapsed({ status: 'passed' }, 5000, true)).toBe(false)
  })

  it('flattens parents before children with their depth, leaving out what a folded node holds', () => {
    const tree = suite()
    const open = flattenTree(tree.features, () => false)
    expect(open.map((r) => [r.node.name, r.depth])).toEqual([
      ['App.Tests', 0],
      ['Orders.OrderTests', 1],
      ['Billing', 1],
      ['InvoiceTests', 2],
      ['Tax.TaxTests', 2],
    ])

    const billingFolded = flattenTree(tree.features, (n) => n.name === 'Billing')
    expect(billingFolded.map((r) => r.node.name)).toEqual(['App.Tests', 'Orders.OrderTests', 'Billing'])
    expect(billingFolded[2]!.collapsed).toBe(true)
  })
})
