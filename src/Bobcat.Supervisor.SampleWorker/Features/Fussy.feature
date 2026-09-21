Feature: Fussy

  @isolated @retry(2)
  Scenario: only works alone
    Then nothing else has run in this process

  @retry(3)
  Scenario: flaky until second attempt
    Then this is at least the second attempt

  Scenario: kills the worker when armed
    Then the worker is killed if BOBCAT_CRASH is set

  Scenario: dies with an unhandled exception when armed
    Then the worker falls over if BOBCAT_UNHANDLED is set
