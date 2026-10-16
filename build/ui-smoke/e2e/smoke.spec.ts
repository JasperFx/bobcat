import { expect, test, type Page } from '@playwright/test'

// ../smoke.sh has already ingested a recorded CritterCrush run (BOBCAT_UI_RUN_ID) and published
// the "Wallets" model from ../event-model.json before this runs.
const runId = process.env.BOBCAT_UI_RUN_ID!

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

test('the run board renders the ingested run', async ({ page }) => {
  const errors = collectErrors(page)

  await page.goto('/')
  await expect(page).toHaveURL(/\/runs$/)
  await expect(page.getByRole('heading', { name: 'Test suites' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'CritterCrush.Specs' })).toBeVisible()

  await page.getByRole('link', { name: 'CritterCrush.Specs' }).click()
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
