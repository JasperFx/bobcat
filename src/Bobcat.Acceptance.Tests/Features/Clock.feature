Feature: Clock

  Scenario: Freeze the date and advance by a duration
    Given the date is "2026-06-05"
    When the clock advances by "2 days"
    Then the clock date should be "2026-06-07"

  Scenario: Advancing duration phrasing
    Given the date is "2026-06-05"
    When "1 day" passes
    Then the clock date should be "2026-06-06"

  Scenario: Advance to an explicit instant
    Given the date is "2026-06-05"
    When the clock advances to "2026-12-25 00:00:00"
    Then the clock date should be "2026-12-25"

  Scenario: Relative tokens resolve against the frozen clock
    Given the current time is "2026-06-05 09:00:00"
    Then the clock date should be "TODAY"
    And the reminder time should be "NOW + 30 minutes"
    And the computed due date should be "TODAY+3"
