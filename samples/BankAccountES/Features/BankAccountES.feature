Feature: Bank Account Event Sourcing

  Scenario: Enroll a client
    When I enroll a client with name "John Doe" and email "john@example.com"
    Then the response status is 201
    And the stored client is named "John Doe" with email "john@example.com"

  Scenario: Update a client
    Given I enroll a client with name "Jane Doe" and email "jane@example.com"
    When I update the client name to "Jane Smith"
    Then the response status is 204
    And the stored client is named "Jane Smith" with email "jane@example.com"

  Scenario: Open a bank account
    Given I enroll a client with name "Alice" and email "alice@example.com"
    When I open a bank account for the client
    Then the response status is 201
    And the balance is 0

  Scenario: Open an account for a client who was never enrolled
    When I open a bank account for client id "11111111-1111-1111-1111-111111111111"
    Then the response status is 400

  Scenario: Deposit funds
    Given I enroll a client with name "Bob" and email "bob@example.com"
    And I open a bank account for the client
    When I deposit 100 funds into the account
    Then the response status is 204
    And the balance is 100

  Scenario: Withdraw funds
    Given I enroll a client with name "Carol" and email "carol@example.com"
    And I open a bank account for the client
    And I deposit 200 funds into the account
    When I withdraw 50 funds from the account
    Then the response status is 204
    And the balance is 150

  Scenario: Withdrawing more than the balance leaves the account untouched
    Given I enroll a client with name "Dave" and email "dave@example.com"
    And I open a bank account for the client
    And I deposit 100 funds into the account
    When I withdraw 200 funds from the account
    Then the response status is 400
    And the balance is 100

  Scenario: Transaction history records every deposit and withdrawal
    Given I enroll a client with name "Eve" and email "eve@example.com"
    And I open a bank account for the client
    And I deposit 100 funds into the account
    And I withdraw 30 funds from the account
    When I get the account transactions
    Then there are 2 transactions
    And the transaction history balance is 70

  Scenario: Get client accounts
    Given I enroll a client with name "Frank" and email "frank@example.com"
    And I open a bank account for the client
    When I get the client accounts
    Then there is 1 account
