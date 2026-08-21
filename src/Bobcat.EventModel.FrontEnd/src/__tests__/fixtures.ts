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
