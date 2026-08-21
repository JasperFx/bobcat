import { beforeEach, describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useRunsStore } from '../runs-store'

/**
 * Issue #84 — the supervisor's lane topology, recycles and worker faults folded from a recorded
 * event sequence, the same way the live stream and the hydration replay both feed the store.
 */
const RUN = '9a1f1a1e-0000-0000-0000-000000000084'

type Store = ReturnType<typeof useRunsStore>

function startSupervisedRun(store: Store) {
  store.handleRunStarted({
    runId: RUN,
    suite: 'Wolverine PersistenceTests',
    repository: '/Users/dev/code/wolverine',
    branch: 'main',
    mode: 'supervised',
    startedAt: '2026-08-21T10:00:00Z',
    totalScenarios: 3,
  })
}

function scenarioStarted(store: Store, uid: string, at: string) {
  const slash = uid.indexOf('/')
  store.handleScenarioStarted({
    runId: RUN,
    uid,
    feature: uid.substring(0, slash),
    scenario: uid.substring(slash + 1),
    attempt: 1,
    at,
  })
}

function scenarioFinished(store: Store, uid: string, outcome: string) {
  store.handleScenarioFinished({
    runId: RUN,
    uid,
    outcome,
    attempts: 1,
    durationMs: 10,
    errorMessage: outcome === 'CleanPass' ? null : 'boom',
  })
}

/** The sequence a two-lane supervised run with one same-process retry and one dead worker produces. */
function playRecordedRun(store: Store) {
  startSupervisedRun(store)
  store.handleLaneStarted({ runId: RUN, lane: 0, uids: ['Orders/a', 'Orders/b'], at: '2026-08-21T10:00:01Z' })
  store.handleLaneStarted({ runId: RUN, lane: 1, uids: ['Payments/c'], at: '2026-08-21T10:00:01Z' })

  scenarioStarted(store, 'Orders/a', '2026-08-21T10:00:02Z')
  scenarioStarted(store, 'Payments/c', '2026-08-21T10:00:02Z')
  scenarioFinished(store, 'Orders/a', 'Failed')
  scenarioStarted(store, 'Orders/b', '2026-08-21T10:00:03Z')
  scenarioFinished(store, 'Orders/b', 'CleanPass')
  store.handleLaneFinished({ runId: RUN, lane: 0, outcomes: 2, crashed: false, at: '2026-08-21T10:00:04Z' })

  // Lane 1's worker dies mid-test.
  store.handleLaneFinished({ runId: RUN, lane: 1, outcomes: 0, crashed: true, at: '2026-08-21T10:00:05Z' })
  store.handleWorkerFaulted({
    runId: RUN,
    lane: 1,
    fault: 'the worker exited with code 139. Last standard error:\nSegmentation fault',
    exitCode: 139,
    standardError: 'Segmentation fault',
    at: '2026-08-21T10:00:05Z',
  })

  // Orders/a is retried in place: lane 0 starts again with only that test.
  store.handleRetryScheduled({
    runId: RUN,
    uid: 'Orders/a',
    nextAttempt: 2,
    disposition: 'RetryAfterRecycle',
    reason: 'the broker is slow to warm up',
  })
  store.handleResourceRecycled({ runId: RUN, resource: 'rabbit', at: '2026-08-21T10:00:06Z' })
  store.handleLaneStarted({ runId: RUN, lane: 0, uids: ['Orders/a'], at: '2026-08-21T10:00:07Z' })
  scenarioStarted(store, 'Orders/a', '2026-08-21T10:00:08Z')
}

describe('runs-store supervisor topology', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('an in-process run has no topology and the views have nothing to render', () => {
    const store = useRunsStore()
    store.handleRunStarted({
      runId: RUN,
      suite: 'Bobcat Acceptance',
      repository: '/repo',
      branch: null,
      mode: 'in-process',
      startedAt: '2026-08-21T10:00:00Z',
      totalScenarios: 1,
    })

    const run = store.runById(RUN)!
    expect(run.lanes).toEqual([])
    expect(run.recycles).toEqual([])
    expect(run.faults).toEqual([])
    expect(store.hasTopology(run)).toBe(false)
  })

  it('lanes are folded in lane order with what each was handed and what it is running now', () => {
    const store = useRunsStore()
    startSupervisedRun(store)
    // Lane 1 announces first — arrival order is whatever the OS decided, lane order is not.
    store.handleLaneStarted({ runId: RUN, lane: 1, uids: ['Payments/c'], at: '2026-08-21T10:00:01Z' })
    store.handleLaneStarted({ runId: RUN, lane: 0, uids: ['Orders/a', 'Orders/b'], at: '2026-08-21T10:00:01Z' })
    scenarioStarted(store, 'Orders/a', '2026-08-21T10:00:02Z')

    const run = store.runById(RUN)!
    expect(store.hasTopology(run)).toBe(true)
    expect(run.lanes.map((l) => l.lane)).toEqual([0, 1])
    expect(run.lanes[0]!.uids).toEqual(['Orders/a', 'Orders/b'])
    expect(run.lanes[0]!.status).toBe('running')
    expect(run.lanes[0]!.passes).toBe(1)

    // "Running now" is the join of the lane's uids to live scenario state.
    expect(store.runningIn(run, run.lanes[0]!).map((s) => s.uid)).toEqual(['Orders/a'])
    expect(store.runningIn(run, run.lanes[1]!)).toEqual([])

    scenarioFinished(store, 'Orders/a', 'CleanPass')
    expect(store.runningIn(run, run.lanes[0]!)).toEqual([])
  })

  it('a lane finishes, a crashed lane says so, and the worker death carries its exit code and last stderr', () => {
    const store = useRunsStore()
    playRecordedRun(store)

    const run = store.runById(RUN)!
    const lane1 = run.lanes.find((l) => l.lane === 1)!
    expect(lane1.status).toBe('crashed')
    expect(lane1.finishedAt).toBe('2026-08-21T10:00:05Z')
    expect(lane1.outcomes).toBe(0)

    expect(run.faults).toHaveLength(1)
    expect(run.faults[0]).toMatchObject({
      lane: 1,
      exitCode: 139,
      standardError: 'Segmentation fault',
      at: '2026-08-21T10:00:05Z',
    })
    expect(run.faults[0]!.fault).toContain('exited with code 139')
  })

  it('a same-process retry starts the lane again as a second pass with only the retried test', () => {
    const store = useRunsStore()
    playRecordedRun(store)

    const run = store.runById(RUN)!
    const lane0 = run.lanes.find((l) => l.lane === 0)!
    expect(lane0.passes).toBe(2)
    expect(lane0.status).toBe('running')
    expect(lane0.uids).toEqual(['Orders/a'])
    expect(lane0.finishedAt).toBeNull()
    expect(lane0.outcomes).toBeNull()
    // And the retried scenario is what the lane is on, numbered by the scheduled attempt.
    expect(store.runningIn(run, lane0).map((s) => [s.uid, s.attempt])).toEqual([['Orders/a', 2]])
  })

  it('recycles are a timeline in order', () => {
    const store = useRunsStore()
    playRecordedRun(store)
    store.handleResourceRecycled({ runId: RUN, resource: 'kafka', at: '2026-08-21T10:00:06.5Z' })

    expect(store.runById(RUN)!.recycles).toEqual([
      { resource: 'rabbit', at: '2026-08-21T10:00:06Z' },
      { resource: 'kafka', at: '2026-08-21T10:00:06.5Z' },
    ])
  })

  it('replaying the archive over live state (hydration) changes nothing', () => {
    // hydrateFromServer replays the NDJSON archive through the same handlers after the live
    // stream has already delivered it — so every topology handler must be idempotent, and an
    // older lane start must never count as another pass.
    const store = useRunsStore()
    playRecordedRun(store)
    const topology = (r: ReturnType<typeof store.runById>) =>
      JSON.stringify({ lanes: r!.lanes, recycles: r!.recycles, faults: r!.faults })
    const before = topology(store.runById(RUN))

    playRecordedRun(store)

    const run = store.runById(RUN)!
    expect(topology(run)).toBe(before)
    expect(run.lanes).toHaveLength(2)
    expect(run.lanes.find((l) => l.lane === 0)!.passes).toBe(2)
    expect(run.lanes.find((l) => l.lane === 1)!.status).toBe('crashed')
    expect(run.recycles).toHaveLength(1)
    expect(run.faults).toHaveLength(1)
  })

  it('a replayed finish from an earlier pass does not close the pass the lane is on', () => {
    const store = useRunsStore()
    playRecordedRun(store)
    // The archive's first-pass finish for lane 0 arrives after the live second-pass start.
    store.handleLaneFinished({ runId: RUN, lane: 0, outcomes: 2, crashed: false, at: '2026-08-21T10:00:04Z' })

    const lane0 = store.runById(RUN)!.lanes.find((l) => l.lane === 0)!
    expect(lane0.status).toBe('running')
    expect(lane0.passes).toBe(2)
  })

  it('tolerates topology events arriving before run_started, and a finish without its start', () => {
    const store = useRunsStore()
    store.handleLaneFinished({ runId: RUN, lane: 3, outcomes: 4, crashed: false, at: '2026-08-21T10:00:09Z' })
    store.handleWorkerFaulted({
      runId: RUN,
      lane: null,
      fault: 'the worker stopped responding but is still running',
      exitCode: null,
      standardError: null,
      at: '2026-08-21T10:00:10Z',
    })

    const run = store.runById(RUN)!
    expect(run.lanes).toHaveLength(1)
    expect(run.lanes[0]).toMatchObject({ lane: 3, status: 'finished', passes: 1, outcomes: 4 })
    expect(run.faults[0]).toMatchObject({ lane: null, exitCode: null, standardError: null })

    // Metadata backfills when run_started shows up late.
    startSupervisedRun(store)
    expect(store.runById(RUN)!.suite).toBe('Wolverine PersistenceTests')
    expect(store.runById(RUN)!.lanes).toHaveLength(1)
  })

  it('eject drops the topology with the run', () => {
    const store = useRunsStore()
    playRecordedRun(store)
    store.removeRun(RUN)
    expect(store.allRuns).toHaveLength(0)
  })
})
