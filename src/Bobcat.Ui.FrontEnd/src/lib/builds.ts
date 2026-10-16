import type { RunnerView } from '@/lib/runners'

/**
 * Whether a watched project builds (bobcat#651): what `bobcat watch` reports on each rebuild, and
 * what `stoat runner` reports for a resident runner it rebuilds. A failed build was visible only in a
 * terminal, or as a tooltip in the Run dialog's picker, while the board went on showing the last run
 * of code that no longer compiled as green.
 */
export interface BuildStatusReported {
  suite: string
  repository: string | null
  branch: string | null
  /** building | build-failed | built */
  state: string
  detail: string | null
  at: string
}

/** One project that does not build right now, or is rebuilding, and who said so. */
export interface BuildProblem {
  suite: string
  repository: string | null
  failed: boolean
  detail: string | null
  source: 'bobcat watch' | 'runner'
  at: string | null
}

export async function loadBuildStatus(fetchImpl: typeof fetch = fetch): Promise<BuildStatusReported[] | null> {
  try {
    const response = await fetchImpl('/api/build-status')
    const body = response.ok ? await response.json() : null
    return Array.isArray(body) ? (body as BuildStatusReported[]) : null
  } catch {
    return null
  }
}

/**
 * The projects that do not build, or are rebuilding: every watch report that is not `built`, and
 * every runner whose supervising parent said `build-failed` or `rebuilding`. A project both sources
 * name is listed once, the failure winning.
 */
export function buildProblems(builds: BuildStatusReported[] | null, runners: RunnerView[] | null): BuildProblem[] {
  const problems = new Map<string, BuildProblem>()
  const add = (problem: BuildProblem) => {
    const existing = problems.get(problem.suite)
    if (!existing || (problem.failed && !existing.failed)) problems.set(problem.suite, problem)
  }

  for (const build of builds ?? []) {
    if (build.state !== 'build-failed' && build.state !== 'building') continue
    add({
      suite: build.suite, repository: build.repository, failed: build.state === 'build-failed',
      detail: build.detail, source: 'bobcat watch', at: build.at,
    })
  }

  for (const runner of runners ?? []) {
    if (runner.status !== 'build-failed' && runner.status !== 'rebuilding') continue
    add({
      suite: runner.suite ?? runner.runnerId, repository: runner.repository, failed: runner.status === 'build-failed',
      detail: runner.statusDetail, source: 'runner', at: null,
    })
  }

  return [...problems.values()].sort((a, b) => Number(b.failed) - Number(a.failed) || a.suite.localeCompare(b.suite))
}
