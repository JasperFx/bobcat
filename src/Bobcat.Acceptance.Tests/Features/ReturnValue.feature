Feature: Return Value

  Scenario: Addition passes
    Then 2 plus 3 should be 5

  Scenario: Addition fails
    Then 2 plus 3 should be 6

  Scenario: Approximate average passes within tolerance
    Then the average of 1 and 2 is 1.55

  Scenario: String return passes
    Then the greeting for "World" should be "Hello World"

  Scenario: String return fails
    Then the greeting for "World" should be "Goodbye World"
