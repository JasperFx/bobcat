/**
 * The run buttons' side of the resident runners (stoat#78; the endpoints are stoat#77, back in
 * Bobcat with the board since bobcat#524). A person
 * picks a runner, presses run, and follows the command to the run it caused. People only: there is
 * no agent-facing equivalent of these buttons.
 */

export interface RunnerCommandView {
  commandId: string
  kind: 'run' | 'restart' | string
  specs: string[]
  mode: string
  /** queued | sent | accepted | rejected */
  state: string
  reason: string | null
  requestedBy: string
  requestedAt: string
  acknowledgedAt: string | null
  /** The run it caused, once that run's run_started names the command. */
  runId: string | null
}

export interface RunnerView {
  runnerId: string
  repository: string
  repositorySlug: string | null
  branch: string | null
  suite: string | null
  lane: string
  modes: string[]
  specs: number
  connected: boolean
  /** What a supervising parent (`stoat runner`) reported while the runner could not: rebuilding | build-failed | exited. */
  status: string | null
  statusDetail: string | null
  registeredAt: string
  lastSeenAt: string
  commands: RunnerCommandView[]
}

/** What a button runs: named specs, or a whole feature. */
export type RunTarget = { specs: string[] } | { feature: string }

export const START_A_RUNNER = 'bobcat resident <test host>'

export async function findRunners(target: RunTarget, fetchImpl: typeof fetch = fetch): Promise<RunnerView[] | null> {
  const query = new URLSearchParams()
  if ('feature' in target) query.set('feature', target.feature)
  else for (const spec of target.specs) query.append('spec', spec)

  try {
    const response = await fetchImpl(`/api/runners/for?${query}`)
    return response.ok ? ((await response.json()) as RunnerView[]) : null
  } catch {
    return null
  }
}

export async function loadRunner(runnerId: string, fetchImpl: typeof fetch = fetch): Promise<RunnerView | null> {
  try {
    const response = await fetchImpl(`/api/runners/${encodeURIComponent(runnerId)}`)
    return response.ok ? ((await response.json()) as RunnerView | null) : null
  } catch {
    return null
  }
}

export type Asked = { ok: true; commandId: string; specs: string[] } | { ok: false; error: string }

/** Send a run (or, with `restart`, a request for a new runner). The server's refusal is shown as it said it. */
export async function askRunner(
  runnerId: string,
  request: RunTarget & { mode?: string } | { restart: true },
  fetchImpl: typeof fetch = fetch,
): Promise<Asked> {
  try {
    const response = await fetchImpl(`/api/runners/${encodeURIComponent(runnerId)}/commands`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ...request, by: 'bobcat-ui' }),
    })
    const body = (await response.json().catch(() => null)) as
      | { commandId?: string; specs?: string[]; errors?: string[]; message?: string }
      | null
    if (response.ok && body?.commandId) return { ok: true, commandId: body.commandId, specs: body.specs ?? [] }
    return { ok: false, error: body?.errors?.join('; ') ?? body?.message ?? `the runner endpoint answered ${response.status}` }
  } catch {
    return { ok: false, error: 'the Bobcat UI could not be reached' }
  }
}

/**
 * How a runner stands, in words, for the picker. "Not there" has reasons (stoat#79): a runner that
 * is rebuilding, or whose build failed, reads differently from one that has stopped, and a command
 * sent to it is queued rather than lost.
 */
export function runnerState(runner: RunnerView): { label: string; tone: 'ok' | 'wait' | 'bad' } {
  if (runner.connected) return { label: 'listening', tone: 'ok' }
  if (runner.status === 'rebuilding') return { label: 'rebuilding', tone: 'wait' }
  if (runner.status === 'build-failed') return { label: 'build failed', tone: 'bad' }
  if (runner.status === 'exited') return { label: 'exited', tone: 'bad' }
  return { label: 'not connected', tone: 'bad' }
}

/** Where a command has got to: queued → sent → accepted (then the run) or rejected. */
export function commandProgress(command: RunnerCommandView | undefined): { label: string; done: boolean; failed: boolean } {
  if (!command) return { label: 'queued', done: false, failed: false }
  if (command.state === 'rejected') return { label: `rejected${command.reason ? ` — ${command.reason}` : ''}`, done: true, failed: true }
  if (command.runId) return { label: 'running', done: true, failed: false }
  if (command.state === 'accepted') return { label: 'accepted — waiting for the run to start', done: false, failed: false }
  if (command.state === 'sent') return { label: 'sent — waiting for the runner to accept', done: false, failed: false }
  return { label: 'queued — the runner gets it when it connects', done: false, failed: false }
}

const MODE_KEY = 'bobcat.runs.mode'

/** Cold or warm, remembered per person in this browser rather than as a setting on the server. */
export function loadMode(): 'cold' | 'warm' {
  try {
    return localStorage.getItem(MODE_KEY) === 'warm' ? 'warm' : 'cold'
  } catch {
    return 'cold'
  }
}

export function saveMode(mode: 'cold' | 'warm'): void {
  try {
    localStorage.setItem(MODE_KEY, mode)
  } catch {
    // Remembering is a convenience; the run still goes.
  }
}

/** The mode to send: the remembered one when this runner offers it, else cold — warm is opt-in. */
export function modeFor(runner: RunnerView, remembered: 'cold' | 'warm'): 'cold' | 'warm' {
  return remembered === 'warm' && runner.modes.includes('warm') ? 'warm' : 'cold'
}
