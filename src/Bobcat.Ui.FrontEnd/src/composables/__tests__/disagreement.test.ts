import { describe, expect, it } from 'vitest'
import { disagreementOf } from '@/composables/specification'

/** bobcat#836: a cell states the comparison it made, in the words CellResult.derive() uses. */
describe('a judged cell that disagreed', () => {
  it('reads as an equality when it carries no comparison', () => {
    expect(disagreementOf({ expected: '6', actual: '8', comparison: null })).toBe("expected '6', got '8'")
  })

  it('states a bound rather than an equality it never checked', () => {
    expect(disagreementOf({ expected: '10', actual: '3', comparison: 'greaterThan' })).toBe(
      "should be greater than '10', got '3'",
    )
  })

  it('shows no expected value for a unary comparison', () => {
    expect(disagreementOf({ expected: null, actual: 'x', comparison: 'isNull' })).toBe("should be null, got 'x'")
  })

  it('falls back to equality for a word this page does not know', () => {
    expect(disagreementOf({ expected: 'a', actual: 'b', comparison: 'fromTheFuture' })).toBe("expected 'a', got 'b'")
  })
})
