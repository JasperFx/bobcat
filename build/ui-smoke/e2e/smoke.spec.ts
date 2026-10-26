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
