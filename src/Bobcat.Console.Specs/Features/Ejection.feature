Feature: Ejection

  Ejecting a run takes it off the dashboard but never deletes its archive, and what the viewer
  knows is rebuilt from the archives when it restarts — so an eject survives a restart, and so
  does everything that was not ejected.

  Scenario: Ejecting a run removes its card
    Given a run "Orders" has started with 1 scenarios
    And the scenario "Orders/place an order" has started
    And the scenario "Orders/place an order" finished as "CleanPass"
    Then the run's archive is on disk
    When the run is ejected
    Then the eject responds with status 204
    And the run "Orders" is not in the run list
    And asking for the run responds with status 404
    And the run's archive has moved to the ejected folder

  Scenario: Ejecting an unknown run is a 404
    When an unknown run is ejected
    Then the eject responds with status 404

  Scenario: A live run's archive survives a restart
    Given a run "Orders" has started with 2 scenarios
    And the scenario "Orders/place an order" has started
    And the scenario "Orders/place an order" finished as "CleanPass"
    When the viewer restarts
    Then the run "Orders" appears in the run list
    And the run is listed as orphaned
    And the run's summary reports 1 of 2 scenarios finished
    And the outcome of "Orders/place an order" is "CleanPass"

  Scenario: A finished run rehydrates as finished, not orphaned
    Given a run "Orders" has started with 1 scenarios
    And the scenario "Orders/place an order" has started
    And the scenario "Orders/place an order" finished as "CleanPass"
    And the run has finished with exit code 0
    When the viewer restarts
    Then the run is listed as finished
    And the run's summary reports 1 passed, 0 failed and 0 passed on retry

  Scenario: An eject survives a restart
    Given a run "Orders" has started with 1 scenarios
    And the scenario "Orders/place an order" has started
    When the run is ejected
    And the viewer restarts
    Then the run "Orders" is not in the run list
    And the run's archive has moved to the ejected folder

  Scenario: Ejecting the whole board takes every run at once
    Given a run "Orders" has started with 1 scenarios
    And the scenario "Orders/place an order" finished as "CleanPass"
    And the run has finished with exit code 0
    And a run "Payments" has started with 1 scenarios
    And the scenario "Payments/charge a card" finished as "CleanPass"
    And the run has finished with exit code 0
    When every run is ejected
    Then the bulk eject reports 2 runs taken
    And the run "Orders" is not in the run list
    And the run "Payments" is not in the run list

  Scenario: Ejecting all older spares the run it was anchored on
    Given a run "Yesterday" started 600 minutes ago
    And the run has finished with exit code 0
    And a run "Anchor" started 30 minutes ago
    And the run has finished with exit code 0
    And a run "Newest" has started
    And the run has finished with exit code 0
    When every run older than "Anchor" is ejected
    Then the bulk eject reports 1 runs taken
    And the run "Yesterday" is not in the run list
    And the run "Anchor" appears in the run list
    And the run "Newest" appears in the run list

  Scenario: Ejecting all but one keeps exactly that one
    Given a run "Keep" has started
    And the run has finished with exit code 0
    And a run "Drop" has started
    And the run has finished with exit code 0
    When every run but "Keep" is ejected
    Then the bulk eject reports 1 runs taken
    And the run "Keep" appears in the run list
    And the run "Drop" is not in the run list

  # A live publisher recreates the entry with its next event, so ejecting a live run would buy
  # a card that reappears and a count that lied. The bulk verbs leave it where it is.
  Scenario: A bulk eject never takes a run that is still live
    Given a run "Finished" has started
    And the run has finished with exit code 0
    And a run "StillRunning" has started with 10 scenarios
    When every run is ejected
    Then the bulk eject reports 1 runs taken
    And the run "StillRunning" appears in the run list
    And the run "Finished" is not in the run list
