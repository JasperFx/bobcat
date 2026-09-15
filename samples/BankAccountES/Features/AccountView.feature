@domain:Banking
Feature: Account View

  # bobcat#300. The Account read model is a View slice — event → projection → document — and
  # until JasperFx 2.69 no source could derive one: a View existed on the canvas only when a human
  # declared it. The store now derives one per registered projection (jasperfx#825, registered by
  # AddMarten / AddFisher), named after the DOCUMENT type. This spec declares the same slice by the
  # same name — `@slice:Account` and the `{readmodel}` capture below — so the spec rung and the
  # store rung fold into ONE sticky. Nothing enforces that the two agree on the name; the scenario
  # in EventModel.feature that asserts "exactly one slice named Account" is what keeps it true.
  #
  # There is deliberately no When: a View slice receives no command. Its arranged events are the
  # events the projection folds, which is why the generator stamps them as what the slice
  # CONSUMES (bobcat#297) rather than what it emits — a Command slice's arranged history stays
  # unstamped, because there it is the aggregate's stream.
  #
  # The store says the Account projection applies FOUR events, and lists merge by equality, not
  # union: a spec-declared list that names fewer is a SourceDisagreement hotspot on the slice —
  # the same finding FreezeAccount.feature plants on purpose for emitted events. So between them
  # these two scenarios arrange all four. Drop one and EventModel.feature's "no source
  # disagreement" goes red, which is the right reading: the view is under-specified.

  @slice:Account
  Scenario: The account read model folds the stream's history
    Given no events for Account "99999999-9999-9999-9999-999999999999"
    And AccountOpened occurred
      | AccountId                            | ClientId                             | Currency |
      | 99999999-9999-9999-9999-999999999999 | aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa | EUR      |
    And FundsDeposited occurred
      | AccountId                            | Amount | NewBalance |
      | 99999999-9999-9999-9999-999999999999 | 250    | 250        |
    Then the Account read model contains
      | Balance | Currency | IsFrozen |
      | 250     | EUR      | false    |

  @slice:Account
  Scenario: The account read model folds a withdrawal and a freeze
    Given no events for Account "88888888-8888-8888-8888-888888888888"
    And AccountOpened occurred
      | AccountId                            | ClientId                             | Currency |
      | 88888888-8888-8888-8888-888888888888 | aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa | USD      |
    And FundsDeposited occurred
      | AccountId                            | Amount | NewBalance |
      | 88888888-8888-8888-8888-888888888888 | 100    | 100        |
    And FundsWithdrawn occurred
      | AccountId                            | Amount | NewBalance |
      | 88888888-8888-8888-8888-888888888888 | 40     | 60         |
    And AccountFrozen occurred
      | AccountId                            | Reason          |
      | 88888888-8888-8888-8888-888888888888 | Suspected fraud |
    Then the Account read model contains
      | Balance | IsFrozen |
      | 60      | true     |
