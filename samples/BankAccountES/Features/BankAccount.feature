Feature: Bank Account ES

  Scenario: Enroll a new client
    When I enroll a client named "Alice Smith" with email "alice@test.com"
    Then the client id should be valid
    And the client name should be "Alice Smith"
    And the client email should be "alice@test.com"

  Scenario: Update a client
    Given a client named "Jane Doe" with email "jane@test.com" is enrolled
    When I update the client name to "Updated Name" and email to "updated@test.com"
    Then the response status should be 204
    When I retrieve the client
    Then the client name should be "Updated Name"
    And the client email should be "updated@test.com"

  Scenario: Open an account
    Given a client named "Jane Doe" with email "jane@test.com" is enrolled
    When I open a "USD" account for the client
    Then the account id should be valid
    And the account client id should match the client
    And the account currency should be "USD"
    And the account balance should be 0

  Scenario: Open account with invalid client returns 400
    When I try to open an account for a non-existent client in "USD"
    Then the response status should be 400

  Scenario: Deposit funds
    Given a client named "Jane Doe" with email "jane@test.com" is enrolled
    And a "USD" account is opened for the client
    When I deposit 500 into the account
    Then the account balance should be 500

  Scenario: Withdraw funds
    Given a client named "Jane Doe" with email "jane@test.com" is enrolled
    And a "USD" account is opened for the client
    And 1000 has been deposited into the account
    When I withdraw 300 from the account
    Then the account balance should be 700

  Scenario: Withdraw with insufficient funds returns 400
    Given a client named "Jane Doe" with email "jane@test.com" is enrolled
    And a "USD" account is opened for the client
    And 100 has been deposited into the account
    When I try to withdraw 500 from the account
    Then the response status should be 400

  Scenario: Get transaction history
    Given a client named "Jane Doe" with email "jane@test.com" is enrolled
    And a "USD" account is opened for the client
    And 1000 has been deposited into the account
    And 200 has been withdrawn from the account
    When I get the transaction history for the account
    Then there should be 2 transactions
    And transaction 1 should be a "Deposit" of 1000
    And transaction 2 should be a "Withdrawal" of 200
    And the transaction history balance should be 800

  Scenario: Get a client by id
    When I enroll a client named "Bob" with email "bob@test.com"
    And I retrieve the client
    Then the client name should be "Bob"

  Scenario: Get client accounts
    Given a client named "Jane Doe" with email "jane@test.com" is enrolled
    And a "USD" account is opened for the client
    And a "EUR" account is opened for the client
    When I get the accounts for the client
    Then there should be 2 client accounts
