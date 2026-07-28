Feature: Lifecycle

  Scenario: Hooks share the scenario's DI scope
    Then before-each saw the same scoped session as the step
    And before-all ran exactly once
    And before-all resolved the root singleton

  Scenario: Hooks run again for the next scenario
    Then before-each saw the same scoped session as the step
    And before-all ran exactly once
    And before-each has run twice
    And each scenario's before-each saw a different session
