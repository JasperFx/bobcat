import { beforeEach, describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import type { EventModelDescriptor } from '@jasperfx/event-model-vue'
import { eventModelUrl, outcomesFor, sourceOrigin, undeclaredTouches, useEventModelStore } from '../event-model-store'
import type { ScenarioState } from '../runs-store'

function descriptor(): EventModelDescriptor {
  return {
    name: 'Wallets',
    slices: [
      {
        name: 'CreditWallet',
        pattern: 'Command',
        elements: [
          {
            id: 'CreditWallet/Command/Wallets.CreditWallet',
            kind: 'Command',
            lane: 'Command',
            label: 'CreditWallet',
            type: { name: 'CreditWallet', fullName: 'Wallets.CreditWallet' },
          },
          {
            id: 'CreditWallet/Event/Wallets.WalletCredited',
            kind: 'Event',
            lane: 'EventStream',
            label: 'WalletCredited',
            type: { name: 'WalletCredited', fullName: 'Wallets.WalletCredited' },
          },
        ],
        edges: [],
        specifications: [
          { identity: 'Wallet/Crediting a wallet', resolvedTypes: [] },
          { identity: 'Wallet/Crediting fails', resolvedTypes: [] },
        ],
      },
    ],
  }
}

function scenario(overrides: Partial<ScenarioState> & { uid: string }): ScenarioState {
  return {
    feature: '',
    scenario: '',
    status: 'passed',
    attempt: 1,
    attempts: 1,
    scheduledAttempt: null,
    outcome: 'CleanPass',
    durationMs: 10,
    errorMessage: null,
    retryReason: null,
    steps: [],
    totalSteps: null,
    declaredSteps: [],
    plannedSteps: [],
    touchedTypes: [],
    finishedAt: null,
    state: null,
    workerPublished: true,
    forwarded: false,
    displayName: null,
    reports: [],
    ...overrides,
  }
}

const jsonResponse = (status: number, body?: unknown) =>
  new Response(body === undefined ? null : JSON.stringify(body), { status })

describe('event-model store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('loads the descriptor from the public wire', async () => {
    const store = useEventModelStore()
    await store.load(async () => jsonResponse(200, descriptor()))
    expect(store.status).toBe('loaded')
    expect(store.descriptor?.name).toBe('Wallets')
    expect(store.slices).toHaveLength(1)
  })

  it('a 404 is absent — nothing published, not an error', async () => {
    const store = useEventModelStore()
    await store.load(async () => jsonResponse(404))
    expect(store.status).toBe('absent')
    expect(store.descriptor).toBeNull()
  })

  it('a failed fetch is an error', async () => {
    const store = useEventModelStore()
    await store.load(async () => {
      throw new Error('down')
    })
    expect(store.status).toBe('error')
  })
})

describe('outcomesFor', () => {
  it('maps every declared identity: verdicts to passed/failed, everything else to notRun', () => {
    const outcomes = outcomesFor(descriptor(), {
      'Wallet/Crediting a wallet': scenario({ uid: 'Wallet/Crediting a wallet', outcome: 'PassOnRetry' }),
      'Some/other scenario': scenario({ uid: 'Some/other scenario', outcome: 'Failed' }),
    })

    expect(outcomes).toEqual({
      'Wallet/Crediting a wallet': 'passed',
      // Declared by the model but never reached by the run — the drift colour must be
      // stated, not omitted, for the slice to read as notRun.
      'Wallet/Crediting fails': 'notRun',
    })
  })

  it('a failed scenario fails its identity', () => {
    const outcomes = outcomesFor(descriptor(), {
      'Wallet/Crediting a wallet': scenario({ uid: 'Wallet/Crediting a wallet', outcome: 'Failed' }),
    })
    expect(outcomes['Wallet/Crediting a wallet']).toBe('failed')
  })

  it('tolerates no descriptor', () => {
    expect(outcomesFor(null, {})).toEqual({})
  })
})

describe('undeclaredTouches', () => {
  it('reports touched types neither the elements nor the specs declare', () => {
    const slice = descriptor().slices![0]!
    const undeclared = undeclaredTouches(
      slice,
      scenario({
        uid: 'Wallet/Crediting a wallet',
        touchedTypes: [
          { name: 'CreditWallet', fullName: 'Wallets.CreditWallet', assemblyName: 'W' },
          { name: 'WalletSummary', fullName: 'Wallets.WalletSummary', assemblyName: 'W' },
        ],
      })
    )
    expect(undeclared).toEqual(['Wallets.WalletSummary'])
  })

  it('no scenario means no drift claim', () => {
    expect(undeclaredTouches(descriptor().slices![0]!, undefined)).toEqual([])
  })
})

/** bobcat#799 — reading a model by name, and saying where a source came from. */
describe('event-model store — several models', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('reads the selected model by name, and the current one when none is selected', async () => {
    expect(eventModelUrl(null)).toBe('/api/event-model')
    expect(eventModelUrl('Bank Account & Co')).toBe('/api/event-model?name=Bank%20Account%20%26%20Co')

    const store = useEventModelStore()
    const urls: string[] = []
    await store.select('BankAccount', async (url) => {
      urls.push(String(url))
      return jsonResponse(200, { name: 'BankAccount', slices: [] })
    })

    expect(urls).toEqual(['/api/event-model?name=BankAccount'])
    expect(store.selected).toBe('BankAccount')
    expect(store.descriptor?.name).toBe('BankAccount')
  })

  it('names the repository a source was pushed from, else its checkout folder, with the branch', () => {
    const source = { source: 'default', pushedAt: '', pushedAtIsFileTime: false, root: null, repository: null, branch: null }

    expect(sourceOrigin({ ...source, repository: 'JasperFx/wolverine', root: '/code/wolverine', branch: 'main' }))
      .toBe('JasperFx/wolverine (main)')
    expect(sourceOrigin({ ...source, root: '/private/tmp/scratch/wolverine-799' })).toBe('wolverine-799')
    expect(sourceOrigin(source)).toBeNull()
  })

  it('keeps the last listing when the console cannot list its models', async () => {
    const store = useEventModelStore()
    await store.loadModels(async () => jsonResponse(200, { current: 'A', preferred: 'A', models: [] }))
    await store.loadModels(async () => jsonResponse(500))

    expect(store.listing?.preferred).toBe('A')
  })
})
