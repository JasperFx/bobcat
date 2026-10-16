import { afterEach, describe, expect, it } from 'vitest'
import { askRunner, commandProgress, findRunners, loadMode, modeFor, runnerState, saveMode, type RunnerCommandView, type RunnerView } from '../runners'

function runner(overrides: Partial<RunnerView> = {}): RunnerView {
  return {
    runnerId: 'r1', repository: '/code/stoat', repositorySlug: 'JasperFx/stoat', branch: 'main', suite: 'Stoat.Specs',
    lane: 'gherkin', modes: ['cold'], specs: 3, connected: true, status: null, statusDetail: null,
    registeredAt: '', lastSeenAt: '', commands: [], ...overrides,
  }
}

function command(overrides: Partial<RunnerCommandView> = {}): RunnerCommandView {
  return {
    commandId: 'c1', kind: 'run', specs: ['F/a'], mode: 'cold', state: 'queued', reason: null,
    requestedBy: 'stoat-ui', requestedAt: '', acknowledgedAt: null, runId: null, ...overrides,
  }
}

describe('runners', () => {
  afterEach(() => localStorage.clear())

  it('asks for a feature or for every named spec', async () => {
    const urls: string[] = []
    const fetchImpl = ((url: string) => {
      urls.push(url)
      return Promise.resolve({ ok: true, json: () => Promise.resolve([]) } as Response)
    }) as typeof fetch

    await findRunners({ specs: ['F/a', 'F/b c'] }, fetchImpl)
    await findRunners({ feature: 'F' }, fetchImpl)

    expect(urls).toEqual(['/api/runners/for?spec=F%2Fa&spec=F%2Fb+c', '/api/runners/for?feature=F'])
  })

  it("shows the server's refusal as it said it", async () => {
    const fetchImpl = (() =>
      Promise.resolve({ ok: false, status: 400, json: () => Promise.resolve({ errors: ["not this runner's specs: X/y"] }) } as Response)) as typeof fetch
    expect(await askRunner('r1', { specs: ['X/y'] }, fetchImpl)).toEqual({ ok: false, error: "not this runner's specs: X/y" })
  })

  // stoat#79: "not there" has reasons, and a press while rebuilding is queued, not failed.
  it('tells a rebuilding runner from a failed build, an exit and a plain absence', () => {
    expect(runnerState(runner()).label).toBe('listening')
    expect(runnerState(runner({ connected: false, status: 'rebuilding' }))).toEqual({ label: 'rebuilding', tone: 'wait' })
    expect(runnerState(runner({ connected: false, status: 'build-failed' })).label).toBe('build failed')
    expect(runnerState(runner({ connected: false, status: 'exited' })).label).toBe('exited')
    expect(runnerState(runner({ connected: false })).label).toBe('not connected')
  })

  it('follows a command from queued to its run, or to a rejection with the reason', () => {
    expect(commandProgress(undefined).done).toBe(false)
    expect(commandProgress(command({ state: 'sent' })).label).toContain('sent')
    expect(commandProgress(command({ state: 'accepted' })).done).toBe(false)
    expect(commandProgress(command({ state: 'accepted', runId: 'run-1' }))).toEqual({ label: 'running', done: true, failed: false })
    expect(commandProgress(command({ state: 'rejected', reason: 'busy' }))).toEqual({ label: 'rejected — busy', done: true, failed: true })
  })

  it('remembers warm per browser, and sends it only to a runner that offers it', () => {
    expect(loadMode()).toBe('cold')
    saveMode('warm')
    expect(loadMode()).toBe('warm')

    expect(modeFor(runner({ modes: ['cold', 'warm'] }), 'warm')).toBe('warm')
    expect(modeFor(runner({ modes: ['cold'] }), 'warm')).toBe('cold')
  })
})
