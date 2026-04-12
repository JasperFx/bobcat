Feature: Bank Account Event Sourcing

  Scenario: Open an account
    When I open an account for "Alice" with initial deposit of 1000
    Then the response is 201 Created
    And the account owner is "Alice"
    And the account balance is 1000

  Scenario: Get account details
    Given an account exists for "Bob" with initial deposit of 500
    When I get the account details
    Then the response is 200 OK
    And the account owner is "Bob"
    And the account balance is 500

  Scenario: Deposit money
    Given an account exists for "Carol" with initial deposit of 200
    When I deposit 300 into the account
    Then the response is 200 OK
    And the account balance is 500

  Scenario: Check balance after deposit
    Given an account exists for "Dave" with initial deposit of 100
    When I deposit 50 into the account
    When I deposit 75 into the account
    When I get the account details
    Then the account balance is 225

  Scenario: Withdraw money
    Given an account exists for "Eve" with initial deposit of 1000
    When I withdraw 250 from the account
    Then the response is 200 OK
    And the account balance is 750

  Scenario: Cannot withdraw more than balance
    Given an account exists for "Frank" with initial deposit of 100
    When I attempt to withdraw 500 from the account
    Then the response is 400 Bad Request

  Scenario: Get transaction history
    Given an account exists for "Grace" with initial deposit of 500
    When I deposit 100 into the account
    When I get the transaction history
    Then the response is 200 OK
    And the transaction history has 2 entries
    And the transaction history contains a deposit of 100

  Scenario: Multiple deposits
    Given an account exists for "Heidi" with initial deposit of 0
    When I deposit 100 into the account
    When I deposit 200 into the account
    When I deposit 300 into the account
    When I get the account details
    Then the account balance is 600

  Scenario: Deposit and then withdraw
    Given an account exists for "Ivan" with initial deposit of 500
    When I deposit 200 into the account
    When I withdraw 100 from the account
    Then the response is 200 OK
    And the account balance is 600
