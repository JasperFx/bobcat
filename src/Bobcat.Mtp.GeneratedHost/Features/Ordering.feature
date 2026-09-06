Feature: Ordering

  Scenario: An order is accepted
    Given an empty cart
    When 2 items are added
    Then the cart holds 2 items

  @regression
  Scenario: An order can be emptied
    Given an empty cart
    When 2 items are added
    When the cart is cleared
    Then the cart holds 0 items
