import { randomUUID } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { expect, test, type Page } from '@playwright/test'

// ../smoke.sh has already ingested a recorded CritterCrush run (BOBCAT_UI_RUN_ID) and published
// the "Wallets" model from ../event-model.json before this runs. BOBCAT_UI_RUN_FIXTURE is the
// NDJSON that run came from, for a second, live, run of the same suite (below).
const runId = process.env.BOBCAT_UI_RUN_ID!
const runFixture = process.env.BOBCAT_UI_RUN_FIXTURE!

// The web fonts come from Google; a box without the network should not fail the smoke over them.
const ignorable = (text: string) => /fonts\.(googleapis|gstatic)\.com/.test(text)

function collectErrors(page: Page): string[] {
  const errors: string[] = []
  page.on('console', (message) => {
    if (message.type() === 'error' && !ignorable(message.text())) errors.push(message.text())
  })
  page.on('pageerror', (error) => errors.push(error.message))
  return errors
}

/**
 * The recorded run's first events — run_started through the first scenario_finished — as a run that is
 * still going: a fresh run id, started now. Ingested by the test itself, moments before it looks, because
 * the console orphans a live run silent for thirty seconds and the browser leg's install takes longer.
 */
function liveHalfOfTheFixture(): { runId: string; events: Record<string, unknown>[] } {
  const liveRunId = randomUUID()
  const events: Record<string, unknown>[] = []
  for (const line of readFileSync(runFixture, 'utf8').split('\n')) {
    if (!line.trim()) continue
    const event = JSON.parse(line) as Record<string, unknown>
    event.runId = liveRunId
    if (event.type === 'run_started') event.startedAt = new Date().toISOString()
    events.push(event)
    if (event.type === 'scenario_finished') break
  }
  return { runId: liveRunId, events }
}

// bobcat#786: the board is grouped by suite — the recorded run and a live run of the same suite are one
// row — with the live run's overall progress bar under the row and in the status strip, and the run's
// own card one expansion away.
test('the run board groups the suite\'s runs under one row and shows the live run\'s progress', async ({ page }) => {
  const errors = collectErrors(page)

  const live = liveHalfOfTheFixture()
  const ingested = await page.request.post('/api/ingest', { data: { events: live.events } })
  expect(ingested.status()).toBe(202)

  await page.goto('/')
  await expect(page).toHaveURL(/\/runs$/)
  await expect(page.getByRole('heading', { name: 'Test suites' })).toBeVisible()

  // One suite, two runs: one row, with a two-segment history strip.
  const row = page.getByTestId('suite-row')
  await expect(row).toHaveCount(1)
  await expect(row.getByTestId('suite-name')).toHaveText('CritterCrush.Specs')
  await expect(row.getByTestId('history-segment')).toHaveCount(2)

  // The live run's bar: done of the declared total, under the row and on the strip.
  const progress = row.getByTestId('live-progress')
  await expect(progress).toBeVisible()
  await expect(progress.getByRole('progressbar')).toHaveAttribute('aria-valuenow', /\d+/)
  await expect(progress.getByTestId('live-counts')).toContainText('1 / 19')
  await expect(progress.getByTestId('go-to-running')).toBeVisible()
  const strip = page.getByTestId('status-strip')
  await expect(strip.getByTestId('strip-live')).toContainText('CritterCrush.Specs')
  await expect(strip.getByTestId('strip-connection').getByRole('img', { name: 'Connection: connected' })).toBeVisible()

  // Expanding shows the runs as cards, newest first; the recorded run's card opens its page.
  await row.getByRole('button', { name: 'Expand CritterCrush.Specs' }).click()
  await expect(row.getByTestId('run-card')).toHaveCount(2)
  await row.getByTestId('run-link').nth(1).click()
  await expect(page).toHaveURL(new RegExp(`/runs/${runId}$`))

  expect(errors).toEqual([])
})

// bobcat#787: a run page a reader can orient in. The recorded run above is all green, so this one is
// published here the way a publisher would — three scenarios, one of them failed — to give the rail a
// failure to jump to and the trail a run to come back to.
const FAILING_RUN = '00000000-0000-4000-8000-000000000787'

async function publishFailingRun(request: APIRequestContext): Promise<void> {
  const at = (s: number) => `2026-10-26T10:42:${String(s).padStart(2, '0')}+00:00`
  const scenario = (uid: string, outcome: string, s: number) => {
    const slash = uid.indexOf('/')
    const feature = uid.slice(0, slash)
    const title = uid.slice(slash + 1)
    const failed = outcome === 'Failed'
    return [
      { type: 'scenario_started', runId: FAILING_RUN, uid, feature, scenario: title, attempt: 1, at: at(s) },
      { type: 'step_started', runId: FAILING_RUN, uid, stepId: 's1', kind: 'Then', text: 'the balance is 5', stepNumber: 1 },
      {
        type: 'step_finished', runId: FAILING_RUN, uid, stepId: 's1', status: failed ? 'Failed' : 'Passed', durationMs: 2,
        errorMessage: failed ? "balance: expected '5', got '0'" : null,
        cells: failed ? [{ name: 'balance', status: 'failed', expected: '5', actual: '0', note: null, rowIndex: -1, value: null, comparison: null }] : [],
      },
      { type: 'scenario_finished', runId: FAILING_RUN, uid, outcome, attempts: 1, durationMs: 4, errorMessage: null, at: at(s + 1) },
    ]
  }
  const events = [
    {
      type: 'run_started', runId: FAILING_RUN, suite: 'Wallets.Specs', repository: '/code/wallets', branch: 'main',
      mode: 'in-process', startedAt: at(0), totalScenarios: 3, tag: 'smoke-787', session: null, command: null,
    },
    ...scenario('Wallet/crediting a wallet', 'CleanPass', 1),
    ...scenario('Wallet/debiting past zero', 'Failed', 3),
    ...scenario('Orders/placing an order', 'CleanPass', 5),
    { type: 'run_finished', runId: FAILING_RUN, exitCode: 1, passed: 2, failed: 1, passedOnRetry: 0, indeterminate: 0, finishedAt: at(7) },
  ]
  const response = await request.post('/api/ingest', { data: { events } })
  expect(response.status()).toBe(202)
  // Ingestion is queued; the run is on the board once the registry has folded it.
  await expect
    .poll(async () => ((await (await request.get('/api/runs')).json()) as { runId: string }[]).some((r) => r.runId === FAILING_RUN))
    .toBe(true)
}

test('the run page’s rail jumps to a failed scenario, and the breadcrumbs lead back', async ({ page, request }) => {
  const errors = collectErrors(page)
  await publishFailingRun(request)

  await page.goto(`/runs/${FAILING_RUN}`)
  // The run's identity, its tiles, and the rail with a status on every node.
  await expect(page.getByTestId('crumb-run')).toContainText('smoke-787')
  await expect(page.getByTestId('tile-failed')).toContainText('1')
  await expect(page.getByTestId('toc-scenario')).toHaveCount(3)
  await expect(page.getByTestId('toc-feature').first()).toHaveAttribute('data-status', 'failed')

  // A passed scenario is folded to its line; the failed one is open on its specification.
  const credited = page.locator('[data-uid="Wallet/crediting a wallet"]')
  const debited = page.locator('[data-uid="Wallet/debiting past zero"]')
  await expect(credited.getByTestId('specification')).toHaveCount(0)
  await expect(debited.getByTestId('cell')).toContainText("expected '5', got '0'")

  // The rail's failed filter, then the node: the failed scenario scrolls into view.
  await page.getByTestId('filter-failed').click()
  await expect(page.getByTestId('toc-scenario')).toHaveCount(1)
  await page.getByTestId('toc-scenario').getByRole('button').click()
  await expect(debited).toBeInViewport()

  // Into the spec page, and back up the trail: run, then the board.
  await debited.getByTestId('spec-link').click()
  await expect(page).toHaveURL(new RegExp(`/runs/${FAILING_RUN}/specs/`))
  await expect(page.getByTestId('crumb-scenario')).toHaveText('debiting past zero')
  await page.getByTestId('crumb-run').click()
  await expect(page).toHaveURL(new RegExp(`/runs/${FAILING_RUN}$`))
  await page.getByTestId('crumb-runs').click()
  await expect(page).toHaveURL(/\/runs$/)
  await expect(page.getByRole('heading', { name: 'Test suites' })).toBeVisible()

  expect(errors).toEqual([])
})

test('the Event Model canvas renders the published model', async ({ page }) => {
  const errors = collectErrors(page)

  await page.goto('/event-model')
  await expect(page.getByRole('heading', { name: /Event Model — Wallets/ })).toBeVisible()
  await expect(page.getByTestId('event-model-absent')).toHaveCount(0)
  // The renderer (@jasperfx/event-model-vue) drew the slices: their header and their lane cards,
  // not just a label somewhere on the page.
  await expect(page.getByTestId('event-model')).toBeVisible()
  await expect(page.locator('.em-slice-name', { hasText: 'CreditWallet' })).toBeVisible()
  await expect(page.locator('.em-card-label', { hasText: 'WalletCredited' }).first()).toBeVisible()

  expect(errors).toEqual([])
})

// The HTML results page (bobcat#541), written by the packed tool from a results file: opened from
// disk the way a downloaded CI artifact is, with every network request refused, because an archived
// page that needs anything beside itself is not one.
test('the archived results page renders from disk with nothing else', async ({ page }) => {
  const resultsPage = process.env.BOBCAT_RESULTS_PAGE
  test.skip(!resultsPage, 'BOBCAT_RESULTS_PAGE names no page')

  const errors = collectErrors(page)
  const requested: string[] = []
  await page.route('**/*', (route) => {
    const url = route.request().url()
    if (url.startsWith('file:')) return route.continue()
    requested.push(url)
    return route.abort()
  })

  await page.goto(`file://${resultsPage}`)
  await expect(page.getByTestId('run-verdict')).toHaveText('Failed')
  await expect(page.getByTestId('feature')).toHaveCount(5)

  // A failing scenario starts open, rendered by the board's own specification view.
  const sets = page.locator('[id="Sets/a set of names missing one and expecting one that is not there"]')
  await expect(sets.getByTestId('specification')).toBeVisible()
  await expect(sets.getByTestId('row-marker').filter({ hasText: 'missing' })).toBeVisible()

  await page.getByTestId('failing-only').check()
  await expect(page.locator('[id="Actions/custom logging from a step"]')).toHaveCount(0)

  // A step's screenshot is a file beside the results file (smoke.sh puts one there), linked from
  // the page and drawn inline under the step that took it.
  const thrown = page.locator('[id="Calculator/an exception in an action stops the scenario"]')
  const screenshot = thrown.locator('img[alt="screen.png"]')
  await expect(screenshot).toBeVisible()
  expect(await screenshot.evaluate((img: HTMLImageElement) => img.naturalWidth)).toBeGreaterThan(0)

  expect(requested).toEqual([])
  expect(errors).toEqual([])
})
