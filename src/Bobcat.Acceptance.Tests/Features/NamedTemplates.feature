Feature: Named Templates

  Scenario: A named template binds by parameter name
    Given Start with 3
    When Multiply by 2
    Then The value should be 6

  Scenario: Placeholders may be written out of parameter order
    Then 5 added to 3 should be 8

  Scenario: A named template can disagree
    Given Start with 3
    Then The value should be 7

  Scenario: A keywordless step matches under any keyword
    Given Reset the calculator
    When Reset the calculator
    Then The value should be 0

  Scenario: Cucumber and named placeholders mix in one expression
    Then the calculator has 2 operands and the label Hello there World

  Scenario: A named string placeholder may carry spaces
    Then the label should read Hello there World

  Scenario: A tuple return is compared element by element
    Then For 3 and 4, the Sum should be 7 and the Product should be 12

  Scenario: One tuple element may be wrong while the other is right
    Then For 4 and 4, the Sum should be 8 and the Product should be 15

  Scenario: A bool return is the verdict
    Given Start with 0
    Then the calculator is at zero

  Scenario: A bool return can be false
    Given Start with 3
    Then the calculator is at zero

  Scenario: An asynchronous fact
    Then the ledger balances
