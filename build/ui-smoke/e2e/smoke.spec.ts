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
