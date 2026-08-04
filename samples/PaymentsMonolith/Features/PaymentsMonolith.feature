Feature: Payments Monolith

  The Inflow conversion: four modules in one process that talk to each other only through
  durable local queues. Registering a user makes the Customers module create a customer stub;
  completing that profile makes the Wallets module create a wallet. Neither is a direct call,
  so the specs below are as much about the cascade arriving as about the endpoint returning.

  Scenario: Registering a user creates a customer profile
    When I register "pay.user@example.com" as "Pay User"
    Then the response status is 200
    And a customer profile exists for that user

  Scenario: Registering without an email returns 400
    When I register "" as "No Email"
    Then the response status is 400

  Scenario: Registering an email that is already taken returns 409
    Given I register "dup.pay@example.com" as "First Claimant"
    When I register "dup.pay@example.com" as "Second Claimant"
    Then the response status is 409

  Scenario: Completing a customer profile creates a wallet
    Given I register "complete.user@example.com" as "John Doe"
    When I complete the customer profile as "Johnny" from "Poland"
    Then the response status is 200
    And the customer profile is marked complete
    And the customer has 1 wallet
    And the wallet balance is 0

  Scenario: Completing a customer that does not exist returns 404
    When I complete the profile of a customer that does not exist
    Then the response status is 404

  Scenario: Adding funds raises the wallet balance
    Given I register "addfunds@example.com" as "Funds User"
    And I complete the customer profile as "Funds" from "Poland"
    When I add 100 to the wallet
    Then the response status is 200
    And the wallet balance is 100

  Scenario: Adding a negative amount returns 400
    Given I register "bad.funds@example.com" as "Bad Funds"
    And I complete the customer profile as "Bad Funds" from "Poland"
    When I add -5 to the wallet
    Then the response status is 400

  Scenario: Transferring funds moves the balance between wallets
    Given I register "sender@example.com" as "Sender"
    And I complete the customer profile as "Sender" from "Poland"
    And I add 200 to the wallet
    And a second customer "receiver@example.com" named "Receiver"
    When I transfer 50 to the second customer
    Then the response status is 200
    And the wallet balance is 150
    And the second customer's wallet balance is 50

  Scenario: Transferring more than the balance returns 400
    Given I register "broke@example.com" as "Broke User"
    And I complete the customer profile as "Broke User" from "Poland"
    And a second customer "broke.receiver@example.com" named "Broke Receiver"
    When I transfer 1000 to the second customer
    Then the response status is 400

  Scenario: Creating a deposit records it as completed
    Given I register "depositor@example.com" as "Depositor"
    And I complete the customer profile as "Depositor" from "Poland"
    When I create a deposit of 50 PLN
    Then the response status is 200
    And the deposit is completed
    And the deposit currency is PLN

  Scenario: Depositing a negative amount returns 400
    Given I register "bad.deposit@example.com" as "Bad Depositor"
    And I complete the customer profile as "Bad Depositor" from "Poland"
    When I create a deposit of -1 PLN
    Then the response status is 400
