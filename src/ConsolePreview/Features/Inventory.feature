Feature: Inventory

  Scenario: Verify inventory after receiving shipment
    Given the warehouse is empty
    And the following shipment is received
      | sku      | productName   | quantity |
      | SKU-001  | Widget        | 100      |
      | SKU-002  | Gadget        | 50       |
      | SKU-003  | Thingamajig   | 25       |
    When 10 units of "SKU-001" are sold
    And 5 units of "SKU-002" are sold
    Then the inventory should be
      | Sku      | ProductName   | Quantity |
      | SKU-001  | Widget        | 90       |
      | SKU-002  | Gadget        | 45       |
      | SKU-003  | Thingamajig   | 25       |

  @acceptance
  Scenario: Verify inventory with discrepancies
    Given the warehouse is empty
    And the following shipment is received
      | sku      | productName   | quantity |
      | SKU-001  | Widget        | 100      |
      | SKU-002  | Gadget        | 50       |
      | SKU-003  | Doohickey     | 10       |
    When 10 units of "SKU-001" are sold
    Then the inventory should be
      | Sku      | ProductName   | Quantity |
      | SKU-001  | Widget        | 999      |
      | SKU-099  | Phantom       | 1        |
