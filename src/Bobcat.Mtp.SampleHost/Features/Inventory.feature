Feature: Inventory

  @regression
  Scenario: stock is counted
    Then the stock is counted

  @isolated @recycle(rabbit)
  Scenario: restock is flaky
    Then the restock completes
