Feature: Retries

  A retried scenario keeps its history — the attempts that were watched and the verdict that
  followed — and a pass on retry is never folded into a clean pass.

  Scenario: A retried scenario shows its attempts
    Given a run "Orders" has started with 1 scenarios
    And the scenario "Orders/talk to the broker" has started
    And these steps ran
      | kind | text                | status |
      | When | the broker is asked | error  |
    And a retry of "Orders/talk to the broker" was scheduled as attempt 2 because "the broker is slow to warm up"
    And the scenario "Orders/talk to the broker" has started
    And these steps ran
      | kind | text                | status  |
      | When | the broker is asked | success |
    And the scenario "Orders/talk to the broker" finished as "PassOnRetry" after 2 attempts
    Then the scenario "Orders/talk to the broker" shows 2 attempts
    And the outcome of "Orders/talk to the broker" is "PassOnRetry"
    And the retry reasons for "Orders/talk to the broker" include "the broker is slow to warm up"
    And the final attempt of "Orders/talk to the broker" ran 1 steps

  Scenario: A fresh-process retry that counts from one is still the second attempt
    Given a run "Orders" has started with 1 scenarios
    And the scenario "Orders/talk to the broker" has started
    And a retry of "Orders/talk to the broker" was scheduled as attempt 2 because "flaky broker"
    And the scenario "Orders/talk to the broker" started its attempt 1
    And the scenario "Orders/talk to the broker" finished as "PassOnRetry" after 1 attempts
    Then the scenario "Orders/talk to the broker" shows 2 attempts

  Scenario: A pass on retry is reported apart from a clean pass
    Given a run "Orders" has started with 2 scenarios
    And these scenarios have finished
      | uid                       | outcome     | attempts |
      | Orders/place an order     | CleanPass   | 1        |
      | Orders/talk to the broker | PassOnRetry | 2        |
    When the run finishes with exit code 0
    Then the run's summary reports 1 passed, 0 failed and 1 passed on retry
    And the run's exit code is 0
