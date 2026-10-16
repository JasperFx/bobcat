import { describe, expect, it } from 'vitest'
import { formatAbsolute, formatDuration, formatRelative } from '../time'

/**
 * Issue #196 — the age of a run card. On a board that accumulates (46 runs across four
 * repositories and worktrees was the observed case) age is the single most useful thing about a
 * card, and it is also the prerequisite for #197's "eject all older": a button whose effect the
 * user cannot predict does not get pressed.
 */
const now = Date.parse('2026-09-01T12:00:00Z')

describe('formatRelative', () => {
  it.each([
    ['2026-09-01T11:59:30Z', 'just now'],
    ['2026-09-01T11:56:00Z', '4m ago'],
    ['2026-09-01T10:00:00Z', '2h ago'],
    ['2026-08-30T12:00:00Z', '2d ago'],
  ])('renders %s as %s', (iso, expected) => {
    expect(formatRelative(iso, now)).toBe(expected)
  })

  it('reads a stamp from the near future as just now rather than a negative age', () => {
    // Clock skew between a publisher and this browser, not a scheduled run.
    expect(formatRelative('2026-09-01T12:00:05Z', now)).toBe('just now')
  })

  it('renders nothing at all for an absent or unparseable stamp', () => {
    expect(formatRelative(null, now)).toBeNull()
    expect(formatRelative(undefined, now)).toBeNull()
    expect(formatRelative('not a date', now)).toBeNull()
  })
})

describe('formatDuration', () => {
  it.each([
    ['2026-09-01T10:00:00Z', '2026-09-01T10:00:00.250Z', '250ms'],
    ['2026-09-01T10:00:00Z', '2026-09-01T10:00:04.500Z', '4.5s'],
    ['2026-09-01T10:00:00Z', '2026-09-01T10:05:03Z', '5m03s'],
    ['2026-09-01T10:00:00Z', '2026-09-01T11:07:00Z', '1h07m'],
  ])('renders %s to %s as %s', (from, to, expected) => {
    expect(formatDuration(from, to)).toBe(expected)
  })

  it('is null while a run is still going, because age is not length', () => {
    expect(formatDuration('2026-09-01T10:00:00Z', null)).toBeNull()
  })

  it('is null rather than negative when the stamps disagree about order', () => {
    expect(formatDuration('2026-09-01T10:00:05Z', '2026-09-01T10:00:00Z')).toBeNull()
  })
})

describe('formatAbsolute', () => {
  it('renders something for a real stamp and nothing for anything else', () => {
    expect(formatAbsolute('2026-09-01T10:00:00Z')).not.toBeNull()
    expect(formatAbsolute(null)).toBeNull()
    expect(formatAbsolute('nope')).toBeNull()
  })
})
