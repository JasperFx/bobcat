Feature: Editor Visible Check

  A [Check] stacked with a [Then] carrying the same expression is still a check, whichever
  attribute is written first. The [Then] exists only so tree-sitter-based editor tooling can
  see the step (docs/editor-integration.md); the generator must never let it demote the check.

  Scenario: Check written after Then
    Given the value is 3
    Then the value is positive with then first
    And the value is negative with then first

  Scenario: Check written before Then
    Given the value is 3
    Then the value is positive with check first
    And the value is negative with check first
