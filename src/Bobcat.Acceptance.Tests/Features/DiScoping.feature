Feature: Di Scoping

  Scenario: One scoped instance is shared by every step in a scenario
    Given the scoped session is captured
    When the scoped session is captured again
    Then both captures are the same instance
    And the singleton is the same as the root service

  Scenario: The next scenario gets a fresh scope
    Given the scoped session is captured
    Then the scoped session differs from the previous scenario

  Scenario: A NewScope step gets its own nested instance
    Given the scoped session is captured
    When a nested-scope step captures the session
    Then the nested capture differs from the scenario capture

  Scenario: ScopePerRow isolates each table row
    Given the scoped session is captured
    When each of these rows captures the session
      | label |
      | first |
      | second |
    Then every row captured a different instance
