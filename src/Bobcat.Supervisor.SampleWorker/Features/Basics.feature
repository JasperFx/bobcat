Feature: Basics

  # The scenario identities here are load-bearing: Bobcat.Supervisor.Tests asserts on
  # "Basics/passes", "Basics/always fails" and friends by exact string. Renaming a scenario
  # renames the identity the supervisor reports, so change one only with those tests in hand.

  Scenario: passes
    Then it passes

  Scenario: also passes
    Then it also passes

  Scenario: always fails
    Then it never works

  Scenario: hangs when armed
    Then the worker hangs if BOBCAT_HANG is set
