@domain:Arithmetic
Feature: Derived Calculator
  Triggered by a base-class grammar

  Scenario: Inherited and own steps run together
    Given the running total starts at 10
    When I add 5
    And I subtract 3
    Then the running total is 12

  @slice:Labelling
  Scenario: The derived step hides the base step of the same text
    Given the running total starts at 0
    Then the label is "derived"
