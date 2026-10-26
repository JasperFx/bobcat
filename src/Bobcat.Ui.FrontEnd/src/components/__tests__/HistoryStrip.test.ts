import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import HistoryStrip from '../HistoryStrip.vue'
import { useRunsStore } from '@/stores/runs-store'

/**
 * bobcat#786 — a suite's history as five segments, newest at the right, each a link with a label
 * that says the verdict, the age and the branch: a screen reader gets the same five facts a sighted
 * reader hovers for.
 */
const routerLinkStub = {
  template: '<a :href="`/runs/${to.params.runId}`" :aria-label="$attrs[\'aria-label\']"><slot /></a>',
  props: ['to'],
}

describe('HistoryStrip', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.useFakeTimers()
    vi.setSystemTime(Date.parse('2026-10-26T12:00:00Z'))
  })

  afterEach(() => vi.useRealTimers())

  function run(runId: string, minute: number, branch: string | null, passed: number, failed: number, passedOnRetry = 0) {
    const store = useRunsStore()
    const startedAt = `2026-10-26T11:${String(minute).padStart(2, '0')}:00Z`
    store.handleRunStarted({ runId, suite: 'Bank.Tests', repository: '/repo', branch, mode: 'in-process', startedAt, totalScenarios: passed + failed })
    store.handleRunFinished({ runId, exitCode: failed > 0 ? 1 : 0, passed, failed, passedOnRetry, indeterminate: 0, finishedAt: `2026-10-26T11:${String(minute).padStart(2, '0')}:30Z` })
    return store.runById(runId)!
  }

  it('draws the latest five, oldest at the left, each a labelled link to its run', () => {
    const runs = [6, 5, 4, 3, 2, 1].map((minute) => run(`r${minute}`, minute * 5, minute % 2 ? 'main' : 'feature/x', 10, minute === 4 ? 1 : 0, minute === 2 ? 2 : 0))
    const strip = mount(HistoryStrip, { props: { suite: 'Bank.Tests', runs }, global: { stubs: { RouterLink: routerLinkStub } } })

    const segments = strip.findAll('[data-testid="history-segment"]')
    expect(segments.map((s) => s.attributes('href'))).toEqual(['/runs/r2', '/runs/r3', '/runs/r4', '/runs/r5', '/runs/r6'])
    expect(segments.map((s) => s.attributes('data-tone'))).toEqual(['retried', 'passed', 'failed', 'passed', 'passed'])
    expect(segments[2]!.attributes('aria-label')).toBe('Bank.Tests: failed (1 failed), 39m ago, branch feature/x')
    expect(segments[4]!.attributes('aria-label')).toBe('Bank.Tests: passed, 29m ago, branch feature/x')
    expect(strip.find('[role="list"]').attributes('aria-label')).toBe('Recent runs of Bank.Tests')
  })

  it('renders nothing for a suite with no runs', () => {
    const strip = mount(HistoryStrip, { props: { suite: 'Bank.Tests', runs: [] }, global: { stubs: { RouterLink: routerLinkStub } } })
    expect(strip.find('[data-testid="history-strip"]').exists()).toBe(false)
  })
})
