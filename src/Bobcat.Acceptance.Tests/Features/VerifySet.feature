Feature: Verify Set

  Scenario: A set the step verifies itself
    Given the inventory is
      | Sku     | ProductName | Quantity |
      | SKU-001 | Widget      | 90       |
      | SKU-002 | Gadget      | 12       |
    Then the inventory should be
      | Sku     | ProductName | Quantity |
      | SKU-002 | Gadget      | 12       |
      | SKU-001 | Widget      | 90       |

  Scenario: A wrong value is one cell, because the key column identified the row
    Given the inventory is
      | Sku     | ProductName | Quantity |
      | SKU-001 | Widget      | 90       |
    Then the inventory should be
      | Sku     | ProductName | Quantity |
      | SKU-001 | Widget      | 85       |

  Scenario: A missing row and an extra row
    Given the inventory is
      | Sku     | ProductName | Quantity |
      | SKU-002 | Gadget      | 12       |
    Then the inventory should be
      | Sku     | ProductName | Quantity |
      | SKU-001 | Widget      | 90       |

  Scenario: An ordered set reports the row that turned up early
    Given the inventory is
      | Sku     | ProductName | Quantity |
      | SKU-002 | Gadget      | 12       |
      | SKU-001 | Widget      | 90       |
    Then the inventory in order should be
      | Sku     | ProductName | Quantity |
      | SKU-001 | Widget      | 90       |
      | SKU-002 | Gadget      | 12       |

  Scenario: A set of plain values under the column the table names
    Given the tags are
      | tag     |
      | urgent  |
      | shipped |
    Then the tags should be
      | tag     |
      | urgent  |
      | shipped |

  Scenario: A table with a header and no rows says the set is empty
    Given the inventory is
      | Sku     | ProductName | Quantity |
      | SKU-001 | Widget      | 90       |
    Then the inventory should be
      | Sku     | ProductName | Quantity |
