import { describe, expect, it } from 'vitest'
import { buildProblemFor, buildProblems, type BuildStatusReported } from '../builds'
import type { RunnerView } from '../runners'

/** bobcat#651 — what the board's banner says doesn't compile, from both producers. */
function build(suite: string, state: string, detail: string | null = null): BuildStatusReported {
  return { suite, repository: '/code', branch: 'main', state, detail, at: '2026-10-16T12:00:00Z' }
}

function runner(suite: string, status: string | null, statusDetail: string | null = null): RunnerView {
  return {
    runnerId: suite, repository: '/code', repositorySlug: null, branch: 'main', suite, lane: 'projected', modes: ['cold'],
    specs: 1, connected: false, status, statusDetail, registeredAt: '', lastSeenAt: '', commands: [],
  }
}

describe('buildProblems', () => {
  it('lists a failed or running build from bobcat watch, and nothing for one that built', () => {
    const problems = buildProblems(
      [build('Bank.Tests', 'build-failed', 'Specs.cs(3,1): error CS1002'), build('Calc.Tests', 'building'), build('Ok.Tests', 'built')],
      null,
    )

    expect(problems.map((p) => [p.suite, p.failed, p.source])).toEqual([
      ['Bank.Tests', true, 'bobcat watch'],
      ['Calc.Tests', false, 'bobcat watch'],
    ])
    expect(problems[0]!.detail).toContain('CS1002')
  })

  it("lists what stoat runner said of a runner's build, beside the run dialog where it was the only mention", () => {
    const problems = buildProblems([], [runner('Bank.Tests', 'build-failed', 'error CS0103'), runner('Fine.Tests', null)])

    expect(problems).toHaveLength(1)
    expect(problems[0]).toMatchObject({ suite: 'Bank.Tests', failed: true, detail: 'error CS0103', source: 'runner' })
  })

  it('names a project both producers report once, the failure winning', () => {
    const problems = buildProblems([build('Bank.Tests', 'building')], [runner('Bank.Tests', 'build-failed', 'error CS0103')])

    expect(problems).toHaveLength(1)
    expect(problems[0]!.failed).toBe(true)
  })
})

describe('buildProblemFor (bobcat#795)', () => {
  const problems = buildProblems([build('BankAccountES.Tests', 'build-failed', 'WithdrawFunds.cs(25,49): error CS0103')], null)

  it('finds the suite\'s problem when any path the suite is known by matches the checkout it was built in', () => {
    expect(buildProblemFor(problems, 'BankAccountES.Tests', ['JasperFx/wolverine', '/code/'])?.detail).toContain('CS0103')
  })

  it('does not attach a build to another suite, or to the same suite in another checkout', () => {
    expect(buildProblemFor(problems, 'Tests', ['/code'])).toBeUndefined()
    expect(buildProblemFor(problems, 'BankAccountES.Tests', ['/elsewhere', null, undefined])).toBeUndefined()
  })
})
