Feature: Payments Monolith

  Scenario: Register a user
    When I register a user with email "alice@example.com" name "Alice Smith" and password "password123"
    Then the user id should be valid
    And the user email should be "alice@example.com"
    And the user full name should be "Alice Smith"

  Scenario: Register user validates input
    When I try to register a user with email "" name "" and password "short"
    Then the response status should be 400

  Scenario: Register user rejects duplicate email
    Given a user with email "dup@example.com" is registered
    When I try to register another user with email "dup@example.com"
    Then the response status should be 409

  Scenario: Complete customer
    Given a user with email "bob@example.com" name "Bob Jones" is registered and the cascade handler has run
    When I complete the customer with nickname "Bob" and nationality "US"
    Then the customer should be completed
    And the customer name should be "Bob"
    And the customer nationality should be "US"

  Scenario: Add funds to wallet
    Given a user with email "funds@example.com" name "Funds User" has a wallet
    When I add 500 funds to the wallet
    Then the wallet balance should be 500

  Scenario: Transfer funds
    Given two users each have a wallet
    And 1000 funds are added to the first wallet
    When I transfer 300 from the first wallet to the second wallet
    Then the first wallet balance should be 700
    And the second wallet balance should be 300

  Scenario: Transfer funds with insufficient balance returns 400
    Given two users each have a wallet
    When I try to transfer 100 from the first wallet which has 0 balance
    Then the response status should be 400

  Scenario: Get wallets
    When I get all wallets
    Then the wallets list should be returned

  Scenario: Create a deposit
    When I create a deposit for customer "00000000-0000-0000-0000-000000000001" with currency "PLN" and amount 250
    Then the deposit customer id should match
    And the deposit currency should be "PLN"
    And the deposit amount should be 250
    And the deposit status should be "Completed"

  Scenario: Create deposit validates input
    When I try to create a deposit with empty customer id and zero amount
    Then the response status should be 400
