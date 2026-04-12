Feature: Order Processing via Message Bus

  Scenario: Place a simple order
    When I place an order for "Widget" quantity 3 at price 10.00
    Then the order count is 1
    And the order product is "Widget"

  Scenario: Order cascades inventory update
    When I place an order for "Gadget" quantity 5 at price 25.00
    Then the inventory reduction for "Gadget" is 5

  Scenario: Place multiple orders
    When I place an order for "ItemA" quantity 2 at price 5.00
    And I place an order for "ItemB" quantity 4 at price 8.00
    Then the order count is 2

  Scenario: Order total is calculated correctly
    When I place an order for "PriceyItem" quantity 3 at price 100.00
    Then the order total is 300.00
