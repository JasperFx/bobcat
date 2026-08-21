Feature: Exports

  The eject formats are rendered from the same projection the dashboard reads, so what CI sees
  in a report is what the viewer showed: CTRF first-class, JUnit as the compatibility floor,
  NDJSON as the raw archive.

  Background:
    Given a run "Orders" has started with 3 scenarios
    And these scenarios have finished
      | uid                       | outcome     | attempts |
      | Orders/place an order     | CleanPass   | 1        |
      | Orders/cancel an order    | Failed      | 1        |
      | Orders/talk to the broker | PassOnRetry | 2        |
    And the run has finished with exit code 1

  Scenario: The CTRF report reflects the ingested outcomes
    When the run is exported as ctrf
    Then the export responds with status 200
    And the export is served as "application/json"
    And the CTRF summary counts 2 passed, 1 failed and 1 flaky
    And the CTRF test "Orders: talk to the broker" has 1 retries
    And the CTRF test "Orders: talk to the broker" is flaky
    And the CTRF status of "Orders: cancel an order" is failed
    And the CTRF status of "Orders: place an order" is passed

  Scenario: The JUnit report is the compatibility floor
    When the run is exported as junit
    Then the export responds with status 200
    And the export is served as "application/xml"
    And the JUnit report counts 3 tests and 1 failures

  Scenario: The NDJSON archive is the raw event stream
    When the run is exported as ndjson
    Then the export responds with status 200
    And the export is served as "application/x-ndjson"
    And the NDJSON export has 8 events

  Scenario: An unknown format is refused
    When the run is exported as yaml
    Then the export responds with status 400
