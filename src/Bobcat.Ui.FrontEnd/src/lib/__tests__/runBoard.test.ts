import { afterEach, describe, expect, it } from 'vitest'
import { DEFAULT_RUN_LIMIT, loadRunLimit, saveRunLimit, visibleRuns } from '../runBoard'

type Run = { id: string; live: boolean }
const run = (id: string, live = false): Run => ({ id, live })
const ids = (runs: Run[]) => runs.map((r) => r.id)

describe('visibleRuns', () => {
  it('caps the finished runs at the limit, newest first, and counts what it hid', () => {
    const { shown, hidden } = visibleRuns([run('a'), run('b'), run('c'), run('d')], 2, (r) => r.live)
    expect(ids(shown)).toEqual(['a', 'b'])
    expect(hidden).toBe(2)
  })

  it('never hides a live run, and a live run does not use up the limit', () => {
    const { shown, hidden } = visibleRuns([run('a'), run('b'), run('live', true), run('c')], 2, (r) => r.live)
    expect(ids(shown)).toEqual(['a', 'b', 'live'])
    expect(hidden).toBe(1)
  })

  it('shows everything when the limit is 0', () => {
    expect(visibleRuns([run('a'), run('b'), run('c')], 0, (r) => r.live).hidden).toBe(0)
  })
})

describe('the remembered limit', () => {
  afterEach(() => localStorage.clear())

  it('defaults when the viewer never chose', () => {
    expect(loadRunLimit()).toBe(DEFAULT_RUN_LIMIT)
  })

  it('remembers a choice, including "all"', () => {
    saveRunLimit(25)
    expect(loadRunLimit()).toBe(25)
    saveRunLimit(0)
    expect(loadRunLimit()).toBe(0)
  })

  it('ignores a stored value that is not one of the choices', () => {
    localStorage.setItem('stoat.runs.limit', '7')
    expect(loadRunLimit()).toBe(DEFAULT_RUN_LIMIT)
  })
})
