Feature: Bank Account Event Sourcing

  Scenario: Enroll a client
    Given I enroll a client with name "John Doe" and email "john@example.com"
    Then the response status is 201
    And the client is stored

  Scenario: Update a client
    Given I enroll a client with name "Jane Doe" and email "jane@example.com"
    When I update the client name to "Jane Smith"
    Then the response status is 200

  Scenario: Open a bank account
    Given I enroll a client with name "Alice" and email "alice@example.com"
    When I open a bank account for the client
    Then the response status is 201
    And the account is active

  Scenario: Open account for invalid client returns 400
    When I open a bank account for client id "00000000-0000-0000-0000-000000000000"
    Then the response status is 400

  Scenario: Deposit funds
    Given I enroll a client with name "Bob" and email "bob@example.com"
    And I open a bank account for the client
    When I deposit {int} funds into the account
    Then the response status is 200
    And the balance is {int}

  Scenario: Withdraw funds
    Given I enroll a client with name "Carol" and email "carol@example.com"
    And I open a bank account for the client
    And I deposit 200 funds into the account
    When I withdraw 50 funds from the account
    Then the response status is 200
    And the balance is 150

  Scenario: Withdraw with insufficient funds returns 400
    Given I enroll a client with name "Dave" and email "dave@example.com"
    And I open a bank account for the client
    And I deposit 100 funds into the account
    When I withdraw 200 funds from the account
    Then the response status is 400

  Scenario: Get transactions
    Given I enroll a client with name "Eve" and email "eve@example.com"
    And I open a bank account for the client
    And I deposit 100 funds into the account
    And I withdraw 30 funds from the account
    When I get the account transactions
    Then there are 2 transactions

  Scenario: Get client accounts
    Given I enroll a client with name "Frank" and email "frank@example.com"
    And I open a bank account for the client
    When I get the client accounts
    Then there is 1 account
