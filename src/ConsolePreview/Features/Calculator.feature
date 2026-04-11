Feature: Calculator

  Scenario: Add two numbers
    Given the left operand is 25
    And the right operand is 50
    When the operands are added
    Then the result should be 75

  Scenario: Negative result
    Given the left operand is 10
    And the right operand is 30
    When the operands are subtracted
    Then the result should be -20
