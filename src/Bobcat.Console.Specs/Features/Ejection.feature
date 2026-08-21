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
