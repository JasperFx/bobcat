Feature: Arithmetic

  # The end-to-end tests assert on these outcomes exactly: one pass, one comparison FAILURE that
  # reports expected and actual, and one exception that reaches the platform as an ERROR.

  Scenario: addition works
    Then 2 + 2 gives 4

  # A comparison that disagrees: MTP state `failed`, with expected and actual on the `result` cell.
  Scenario: subtraction disagrees
    Then 9 - 4 gives 4

  # An exception that escapes: MTP state `error`, with the type and message.
  Scenario: division explodes
    When dividing by zero
