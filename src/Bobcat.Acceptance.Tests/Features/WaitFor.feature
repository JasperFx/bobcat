Feature: Wait For

  Scenario: Return value converges
    Then the outstanding count becomes 0

  Scenario: Return value times out
    Then the never-ready count becomes 0

  Scenario: Check converges
    Then the system is eventually ready

  Scenario: Action eventually succeeds
    When the queue eventually drains
