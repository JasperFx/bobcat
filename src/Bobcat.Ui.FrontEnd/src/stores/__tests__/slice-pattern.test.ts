import { describe, expect, it } from 'vitest'
import type { EventModelSliceDescriptor, SlicePattern } from '@jasperfx/event-model-vue'
import { PATTERN_LABEL, patternLabel, patternViolationsOf } from '../event-model-store'

/**
 * jasperfx#1003 — `Query`, wire value 4: Wolverine 6.50 derives every read-only GET as a Query slice
 * that names what it reads, where it used to call it a View. The drill-down labels it and lists the
 * roles a Query may not carry.
 */
describe('slice patterns', () => {
  it('labels a Query as a read, not as a View', () => {
    expect(patternLabel('Query')).toBe('Query — a read, no state change')
    expect(patternLabel('Query')).not.toBe(patternLabel('View'))
  })

  it('has a label for every pattern the renderer knows', () => {
    const patterns: SlicePattern[] = ['Command', 'View', 'Automation', 'Translation', 'Query']
    expect(Object.keys(PATTERN_LABEL).sort()).toEqual([...patterns].sort())
    for (const pattern of patterns) expect(patternLabel(pattern)).toMatch(new RegExp(`^${pattern} — `))
  })

  it('never throws or renders blank for an underived or newer pattern', () => {
    expect(patternLabel(null)).toBe('pattern not derived')
    expect(patternLabel(undefined)).toBe('pattern not derived')
    // A producer ahead of this build: shown as given.
    expect(patternLabel('Saga')).toBe('Saga')
  })

  it('reads the pattern violations a Query carries, and none from an older producer', () => {
    const writingQuery: EventModelSliceDescriptor = {
      name: 'GetWallet',
      pattern: 'Query',
      patternViolations: [
        { origin: 'PatternViolation', role: 'EmittedEvents', text: 'Query slice emits WalletRead' },
      ],
    }
    expect(patternViolationsOf(writingQuery).map((v) => v.role)).toEqual(['EmittedEvents'])
    expect(patternViolationsOf({ name: 'Old', pattern: 'Query' })).toEqual([])
    expect(patternViolationsOf(null)).toEqual([])
  })
})
