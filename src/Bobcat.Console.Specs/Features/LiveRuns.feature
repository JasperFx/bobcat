Feature: Live Runs

  The dashboard is fed by POST /api/ingest and read back over GET /api/runs — the same public
  wire an outside consumer uses, so nothing here needs a browser.

  Scenario: A run appears when it starts
    Given a run "Orders" has started with 3 scenarios
    Then the run "Orders" appears in the run list
    And the run is listed as running
    And the run's summary reports 0 of 3 scenarios finished

  Scenario: Progress is visible while the run is still going
    Given a run "Orders" has started with 3 scenarios
    And the scenario "Orders/place an order" has started
    And the scenario "Orders/place an order" finished as "CleanPass"
    Then the run's summary reports 1 of 3 scenarios finished
    And the run is listed as running

  Scenario: A finished run carries its verdict
    Given a run "Orders" has started with 2 scenarios
    And these scenarios have finished
      | uid                    | outcome   | attempts |
      | Orders/place an order  | CleanPass | 1        |
      | Orders/cancel an order | Failed    | 1        |
    When the run finishes with exit code 1
    Then the run is listed as finished
    And the run's summary reports 1 passed, 1 failed and 0 passed on retry
    And the run's exit code is 1

  Scenario: A scenario's run evidence is readable per scenario
    Given a run "Wallets" has started with 1 scenarios
    And the scenario "Credit Wallet/happy path" has started
    And the scenario "Credit Wallet/happy path" finished touching "CreditWallet, WalletCredited, WalletSummary"
    Then the touched types of "Credit Wallet/happy path" are "CreditWallet, WalletCredited, WalletSummary"
    And the evidence for "Credit Wallet/happy path" is stamped with a finish time

  Scenario: Runs are found by their correlation tag
    Given a run "Orders" tagged "build-42" has started
    And a run "Payments" tagged "build-43" has started
    Then the run list filtered by tag "build-42" shows only "Orders"
    And the run "Payments" appears in the run list
