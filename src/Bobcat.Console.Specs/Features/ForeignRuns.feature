Feature: Foreign Runs

  Issue #195: a supervised suite whose workers are not Bobcat runners publishes no scenarios of
  its own — a plain xUnit worker has no MonitorPublishingObserver. The supervisor forwards its
  live per-test stream instead, and that is what moves the run card. Before it, a card
  registered with the right total and sat at 0 finished for the whole run: the shape of a
  wedged run, from a suite that was working perfectly.

  Scenario: A forwarded verdict moves the run's progress
    Given a run "ServiceTests" has started with 3 scenarios
    And a foreign test "Acme.OrderTests.pays" has started
    And a foreign test "Acme.OrderTests.pays" finished as "Passed"
    And a foreign test "Acme.OrderTests.refunds" finished as "Failed"
    Then the run's summary reports 2 of 3 scenarios finished
    And the outcome of "Acme.OrderTests.pays" is "CleanPass"
    And the outcome of "Acme.OrderTests.refunds" is "Failed"

  # A test in flight is known, so a lane can say what it is working through — but it is not done.
  Scenario: A test still in flight is not counted as finished
    Given a run "ServiceTests" has started with 2 scenarios
    And a foreign test "Acme.OrderTests.pays" has started
    Then the run's summary reports 0 of 2 scenarios finished

  # Skipped counts as a pass because the supervisor's own WorkerOutcome.Succeeded does — so the
  # progress bar and the terminal counts cannot disagree about the same test.
  Scenario: A skipped test is a pass, on the same terms the run's own counts use
    Given a run "ServiceTests" has started with 1 scenarios
    And a foreign test "Acme.OrderTests.ignored" finished as "Skipped"
    Then the run's summary reports 1 of 1 scenarios finished
    And the outcome of "Acme.OrderTests.ignored" is "CleanPass"

  # A supervised Bobcat suite puts BOTH streams on the wire; the worker's own is richer and
  # owns the uid, whichever arrives first.
  Scenario: A worker publishing its own scenario is not double-reported
    Given a run "Specs" has started with 1 scenarios
    And the scenario "Orders/place an order" has started
    And the scenario "Orders/place an order" finished as "PassOnRetry" after 2 attempts
    And a foreign test "Orders/place an order" finished as "Passed"
    Then the run's summary reports 1 of 1 scenarios finished
    And the outcome of "Orders/place an order" is "PassOnRetry"
    And the scenario "Orders/place an order" shows 2 attempts

  # Free consequence: a supervised xUnit run now has per-test rows, so it ejects as CTRF like
  # any other — from a suite with no Bobcat reference anywhere in it.
  Scenario: A foreign run ejects as CTRF
    Given a run "ServiceTests" has started with 2 scenarios
    And a foreign test "Acme.OrderTests.pays" finished as "Passed"
    And a foreign test "Acme.OrderTests.refunds" finished as "Failed"
    And the run has finished with exit code 1
    When the run is exported as ctrf
    Then the export responds with status 200
    And the CTRF summary counts 1 passed, 1 failed and 0 flaky
    And the CTRF status of "Acme.OrderTests.pays" is passed
