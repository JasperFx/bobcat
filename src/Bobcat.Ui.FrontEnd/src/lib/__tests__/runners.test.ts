import { afterEach, describe, expect, it } from 'vitest'
import { askRunner, commandForRun, commandProgress, describeCommand, findRunners, loadMode, modeFor, rebuiltSince, runnerState, saveMode, type RunnerCommandView, type RunnerView } from '../runners'

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

// bobcat#646: a commanded run's card says what was run — a spec, a feature, the whole project.
describe('describeCommand', () => {
  function runnerWith(specs: number): RunnerView {
    return {
      runnerId: 'r1', repository: '/code/bank', repositorySlug: null, branch: 'main', suite: 'Bank.Tests',
      lane: 'projected', modes: ['cold', 'warm'], specs, connected: true, status: null, statusDetail: null,
      registeredAt: '', lastSeenAt: '', commands: [],
    }
  }

  function asked(specs: string[], mode = 'warm'): RunnerCommandView {
    return {
      commandId: 'c1', kind: 'run', specs, mode, state: 'accepted', reason: null,
      requestedBy: 'bobcat-ui', requestedAt: '', acknowledgedAt: null, runId: 'run-1',
    }
  }

  it('names the whole project, a feature, one spec or a count, with the mode and who asked', () => {
    expect(describeCommand(asked(['A/x', 'A/y', 'B/z']), runnerWith(3))).toBe('Run all · warm · from bobcat-ui')
    expect(describeCommand(asked(['Bank Account/opens', 'Bank Account/closes'], 'cold'), runnerWith(9)))
      .toBe('Run 2 in feature Bank Account · cold · from bobcat-ui')
    expect(describeCommand(asked(['Bank Account/opens']), runnerWith(9))).toBe('Run opens · warm · from bobcat-ui')
    expect(describeCommand(asked(['A/x', 'B/z']), runnerWith(9))).toBe('Run 2 specs · warm · from bobcat-ui')
  })

  it('finds the command that caused a run by the run id it recorded', () => {
    const runner = { ...runnerWith(3), commands: [asked(['A/x'])] }
    expect(commandForRun([runner], 'run-1')?.command.commandId).toBe('c1')
    expect(commandForRun([runner], 'other')).toBeUndefined()
    expect(commandForRun(null, 'run-1')).toBeUndefined()
  })
})

// bobcat#652: a relaunched runner re-registers but runs nothing, so the board marks the verdicts it predates
describe('rebuiltSince', () => {
  const runner = (overrides: Partial<RunnerView>): RunnerView => ({
    runnerId: 'r1', repository: '/code/bank/', repositorySlug: null, branch: 'main', suite: 'Bank.Tests', lane: 'projected',
    modes: ['cold'], specs: 3, connected: true, status: null, statusDetail: null, registeredAt: '', lastSeenAt: '',
    commands: [], builtAt: '2026-10-16T12:00:00Z', ...overrides,
  })
  const run = { suite: 'Bank.Tests', repository: '/code/bank', startedAt: '2026-10-16T11:00:00Z' }

  it("finds the suite's runner when it was built after the run started", () => {
    expect(rebuiltSince(run, [runner({})])?.runnerId).toBe('r1')
  })

  it('says nothing for a run of the current build, another suite, another checkout, or a runner that cannot say', () => {
    expect(rebuiltSince({ ...run, startedAt: '2026-10-16T12:30:00Z' }, [runner({})])).toBeUndefined()
    expect(rebuiltSince(run, [runner({ suite: 'Other.Tests' })])).toBeUndefined()
    expect(rebuiltSince(run, [runner({ repository: '/code/other' })])).toBeUndefined()
    expect(rebuiltSince(run, [runner({ builtAt: null })])).toBeUndefined()
    expect(rebuiltSince(run, null)).toBeUndefined()
  })
})
