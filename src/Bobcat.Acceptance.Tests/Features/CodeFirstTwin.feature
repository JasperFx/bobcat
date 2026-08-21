Feature: Code First Twin

  Scenario: Depositing into an account
    Given an account opened with 100
    When 25 is deposited
    Then the balance should be 125
    And the ledger should be
      | Kind    | Amount |
      | Opened  | 100    |
      | Deposit | 25     |
