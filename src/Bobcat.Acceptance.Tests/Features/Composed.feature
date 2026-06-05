Feature: Composed

  Scenario: Module steps work alongside the fixture's own
    Given the counter starts at 5
    When the counter increments
    And the counter increments
    Then the counter should be 7
    And the fixture's own check passes

  Scenario: Module instance is fresh per scenario
    Given the counter starts at 1
    Then the counter should be 1

  Scenario: Fixture-derived module receives context
    Then the module received a context
