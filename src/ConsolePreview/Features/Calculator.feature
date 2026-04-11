Feature: Calculator

  Scenario: Add two numbers
    Given the left operand is 25
    And the right operand is 50
    When the operands are added
    Then the result should be 75

  Scenario: Subtract two numbers
    Given the left operand is 100
    And the right operand is 37
    When the operands are subtracted
    Then the result should be 63

  Scenario: Negative result
    Given the left operand is 10
    And the right operand is 30
    When the operands are subtracted
    Then the result should be -20

  Scenario: Adding to zero
    Given the left operand is 0
    And the right operand is 42
    When the operands are added
    Then the result should be 42
    And the result is not negative
