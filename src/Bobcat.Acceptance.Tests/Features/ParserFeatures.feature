Feature: Parser Features

  Background:
    Given the base value is 10

  Scenario: Background applies and docstring is captured
    When the request body is
      """
      {
        "name": "widget"
      }
      """
    Then the body should contain "widget"
    And adding 5 gives 15

  Scenario Outline: Arithmetic over examples
    Then adding <addend> gives <sum>

    Examples:
      | addend | sum |
      | 1      | 11  |
      | 2      | 12  |
      | 5      | 15  |
