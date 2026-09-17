@domain:Acceptance @chapter:Identity
Feature: Slice Identity

  @slice:CreditWallet
  Scenario: A credited wallet shows the new balance
    Given a wallet with 100
    When 25 is credited
    Then the balance should be 125

  @slice:CreditWallet
  Scenario: Crediting nothing is refused
    Given a wallet with 100
    When 0 is credited
    Then the credit is refused

  @slice:WalletBalance
  Scenario: The balance view is not specified yet
