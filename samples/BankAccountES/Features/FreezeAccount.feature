@domain:Banking
Feature: Freeze Account
  Triggered by The fraud desk

  # Written entirely in the shipped CritterStack grammar (bobcat#104/#106), so this feature both
  # RUNS (the command is dispatched over the bus with a tracked session) and DECLARES the
  # FreezeAccount slice — the type captures below resolve at compile time and stamp the slice's
  # Declared roles on the generated BobcatEventModelSource.
  #
  # ⚠️ bobcat#172: this spec deliberately does NOT mention the AccountFlagged event the handler
  # also emits. The Wolverine-derived claim on the slice's emitted events therefore disagrees
  # with this Declared one, and the merge must surface that as a SourceDisagreement hotspot
  # rather than swallow it. Do not "fix" this spec by adding the missing event — the gap is
  # the point, and EventModel.feature asserts on it.

  @slice:FreezeAccount
  Scenario: Freezing an account records the freeze
    Given no events for Account "77777777-7777-7777-7777-777777777777"
    And events for Account
      | Event         | AccountId                            | ClientId                             | Currency |
      | AccountOpened | 77777777-7777-7777-7777-777777777777 | 88888888-8888-8888-8888-888888888888 | USD      |
    When FreezeAccount is received
      | AccountId                            | Reason          |
      | 77777777-7777-7777-7777-777777777777 | Suspected fraud |
    Then AccountFrozen is emitted
    And the Account read model contains
      | IsFrozen |
      | true     |
