Feature: Invoicing

  Scenario: Create an invoice with line items
    Given the following line items
      | description    | quantity | unitPrice |
      | Widget         | 5        | 9.99      |
      | Gadget         | 2        | 24.50     |
      | Thingamajig    | 1        | 99.95     |
    When the invoice is totaled
    Then the subtotal should be 198.90
    And the item count should be 3

  Scenario: Empty invoice
    When the invoice is totaled
    Then the subtotal should be 0.00
    And the item count should be 0
