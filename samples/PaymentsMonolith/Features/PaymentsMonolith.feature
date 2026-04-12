Feature: Payments Monolith

  Scenario: Register a user
    When I register a user with email "pay.user@example.com" and password "Password1!"
    Then the response status is 201
    And the user id is returned

  Scenario: Register with invalid data returns 400
    When I register a user with email "" and password "x"
    Then the response status is 400

  Scenario: Register duplicate user returns 409
    Given I register a user with email "dup.pay@example.com" and password "Password1!"
    When I register a user with email "dup.pay@example.com" and password "Password1!"
    Then the response status is 409

  Scenario: Complete customer profile
    Given I register a user with email "complete.user@example.com" and password "Password1!"
    When I complete the customer profile with name "John Doe"
    Then the response status is 200

  Scenario: Add funds to wallet
    Given I register a user with email "addfunds@example.com" and password "Password1!"
    And I complete the customer profile with name "Funds User"
    When I add 100.00 to my wallet
    Then the response status is 200

  Scenario: Transfer funds between wallets
    Given I register a user with email "sender@example.com" and password "Password1!"
    And I complete the customer profile with name "Sender"
    And I add 200.00 to my wallet
    And I register a second user with email "receiver@example.com" and password "Password1!"
    When I transfer 50.00 to the second user
    Then the response status is 200

  Scenario: Transfer with insufficient funds returns 400
    Given I register a user with email "broke@example.com" and password "Password1!"
    And I complete the customer profile with name "Broke User"
    And I register a second user with email "broke.receiver@example.com" and password "Password1!"
    When I transfer 1000.00 to the second user
    Then the response status is 400

  Scenario: Get wallets
    Given I register a user with email "wallet.viewer@example.com" and password "Password1!"
    And I complete the customer profile with name "Wallet Viewer"
    When I get my wallets
    Then the response status is 200
    And at least 1 wallet is returned

  Scenario: Create a deposit
    Given I register a user with email "depositor@example.com" and password "Password1!"
    And I complete the customer profile with name "Depositor"
    When I create a deposit for 50.00
    Then the response status is 201

  Scenario: Deposit with invalid amount returns 400
    Given I register a user with email "bad.deposit@example.com" and password "Password1!"
    And I complete the customer profile with name "Bad Depositor"
    When I create a deposit for -1.00
    Then the response status is 400
