import type { EventModelDescriptor } from '../types'

/**
 * A two-slice model shaped like what the Bobcat generator will emit from a `.feature` (issue
 * #106): a command slice with a trigger, command, handler, aggregate and two events, and a view
 * slice folding one of those events into a read model.
 */
export function withdrawFundsModel(): EventModelDescriptor {
  return {
    name: 'Banking',
    slices: [
      {
        name: 'WithdrawFunds',
        domain: 'Accounts',
        pattern: 'Command',
        triggerKind: 'Http',
        triggerLabel: 'Teller screen',
        elements: [
          { id: 'WithdrawFunds/Trigger/Teller screen', kind: 'Trigger', lane: 'Wireframe', label: 'Teller screen' },
          { id: 'WithdrawFunds/Command/Bank.WithdrawFunds', kind: 'Command', lane: 'Command', label: 'WithdrawFunds', type: { name: 'WithdrawFunds', fullName: 'Bank.WithdrawFunds' } },
          { id: 'WithdrawFunds/Handler/Bank.AccountHandler', kind: 'Handler', lane: 'Command', label: 'AccountHandler', type: { name: 'AccountHandler', fullName: 'Bank.AccountHandler' } },
          { id: 'WithdrawFunds/Aggregate/Bank.Account', kind: 'Aggregate', lane: 'Command', label: 'Account', type: { name: 'Account', fullName: 'Bank.Account' } },
          { id: 'WithdrawFunds/Event/Bank.FundsWithdrawn', kind: 'Event', lane: 'EventStream', label: 'FundsWithdrawn', type: { name: 'FundsWithdrawn', fullName: 'Bank.FundsWithdrawn' } },
          { id: 'WithdrawFunds/Event/Bank.AccountOverdrawn', kind: 'Event', lane: 'EventStream', label: 'AccountOverdrawn', type: { name: 'AccountOverdrawn', fullName: 'Bank.AccountOverdrawn' } }
        ],
        edges: [
          { fromId: 'WithdrawFunds/Trigger/Teller screen', toId: 'WithdrawFunds/Command/Bank.WithdrawFunds' },
          { fromId: 'WithdrawFunds/Command/Bank.WithdrawFunds', toId: 'WithdrawFunds/Handler/Bank.AccountHandler' },
          { fromId: 'WithdrawFunds/Handler/Bank.AccountHandler', toId: 'WithdrawFunds/Event/Bank.FundsWithdrawn' }
        ],
        specifications: [
          { identity: 'Withdraw Funds/a withdrawal succeeds', feature: 'Withdraw Funds', scenario: 'a withdrawal succeeds' }
        ]
      },
      {
        name: 'AccountBalance',
        domain: 'Accounts',
        pattern: 'View',
        elements: [
          { id: 'AccountBalance/Event/Bank.FundsWithdrawn', kind: 'Event', lane: 'EventStream', label: 'FundsWithdrawn', type: { name: 'FundsWithdrawn', fullName: 'Bank.FundsWithdrawn' } },
          { id: 'AccountBalance/Projection/Bank.BalanceProjection', kind: 'Projection', lane: 'ReadModel', label: 'BalanceProjection', type: { name: 'BalanceProjection', fullName: 'Bank.BalanceProjection' } },
          { id: 'AccountBalance/ReadModel/Bank.Balance', kind: 'ReadModel', lane: 'ReadModel', label: 'Balance', type: { name: 'Balance', fullName: 'Bank.Balance' } }
        ],
        edges: [
          { fromId: 'AccountBalance/Event/Bank.FundsWithdrawn', toId: 'AccountBalance/Projection/Bank.BalanceProjection' }
        ],
        specifications: [{ identity: 'Account Balance/balance reflects a withdrawal' }],
        hotspots: [
          { origin: 'PendingSpecification', text: 'overdraft not specified', specificationIdentity: 'Account Balance/overdraft' }
        ]
      }
    ]
  }
}

/**
 * jasperfx#703 / #704 — a slice as it comes out of a FOUR-SOURCE merge: a Gherkin spec and the C#
 * overlay declared it, Wolverine's chains derived it, and CritterWatch observed it in production.
 *
 * The interesting bit is the disagreement. The code says this slice emits `FundsWithdrawn`;
 * production says it appends `FundsWithdrawn` **and** `AuditRecorded`. Observed is the higher rung
 * so it wins outright — a higher rung REPLACES a list rather than unioning with it — and the
 * dropped claim survives as a `SourceDisagreement` hotspot instead of vanishing.
 *
 * Element shapes match what `EventModelSliceDescriptor.buildGraph` emits: every element carries the
 * effective rung for its role, and each hotspot is projected into a `Hotspot` element whose LABEL
 * is the hotspot's text — which is the only join a viewer has back to the origin.
 */
export function fourSourceModel(): EventModelDescriptor {
  const disagreement =
    'EmittedEvents: Observed claims FundsWithdrawn, AuditRecorded; Derived claims FundsWithdrawn'

  return {
    name: 'Banking',
    slices: [
      {
        name: 'WithdrawFunds',
        domain: 'Accounts',
        pattern: 'Command',
        elements: [
          // Declared — nothing else claims a trigger label.
          { id: 'WithdrawFunds/Trigger/Teller screen', kind: 'Trigger', lane: 'Wireframe', label: 'Teller screen', provenance: 'Declared' },
          { id: 'WithdrawFunds/Hotspot/pending', kind: 'Hotspot', lane: 'Wireframe', label: 'overdraft not specified', provenance: 'Declared' },
          { id: `WithdrawFunds/Hotspot/${disagreement}`, kind: 'Hotspot', lane: 'Wireframe', label: disagreement, provenance: 'Declared' },
          // Derived — read out of the Wolverine chain.
          { id: 'WithdrawFunds/Command/Bank.WithdrawFunds', kind: 'Command', lane: 'Command', label: 'WithdrawFunds', type: { name: 'WithdrawFunds', fullName: 'Bank.WithdrawFunds' }, provenance: 'Derived' },
          { id: 'WithdrawFunds/Handler/Bank.AccountHandler', kind: 'Handler', lane: 'Command', label: 'AccountHandler', type: { name: 'AccountHandler', fullName: 'Bank.AccountHandler' }, provenance: 'Derived' },
          // Observed — production appended both, and that is the claim that won.
          { id: 'WithdrawFunds/Event/Bank.FundsWithdrawn', kind: 'Event', lane: 'EventStream', label: 'FundsWithdrawn', type: { name: 'FundsWithdrawn', fullName: 'Bank.FundsWithdrawn' }, provenance: 'Observed' },
          { id: 'WithdrawFunds/Event/Bank.AuditRecorded', kind: 'Event', lane: 'EventStream', label: 'AuditRecorded', type: { name: 'AuditRecorded', fullName: 'Bank.AuditRecorded' }, provenance: 'Observed' }
        ],
        edges: [],
        hotspots: [
          { origin: 'PendingSpecification', text: 'overdraft not specified', specificationIdentity: 'Account Balance/overdraft' },
          {
            origin: 'SourceDisagreement',
            text: disagreement,
            role: 'EmittedEvents',
            winningClaim: { provenance: 'Observed', value: 'FundsWithdrawn, AuditRecorded' },
            losingClaim: { provenance: 'Derived', value: 'FundsWithdrawn' }
          }
        ]
      }
    ]
  }
}
