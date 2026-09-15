Feature: Event Model

  # bobcat#172, the Bobcat-side half of the multi-source event-model vehicle. Four producers feed
  # one descriptor here — this assembly's Gherkin specs (Declared), the C# overlay in Program.cs
  # (Declared), the host's Wolverine + HTTP chains (Derived), and since bobcat#300 the host's
  # STORE (Derived): one View slice per registered projection, read from the projection registry
  # by jasperfx#825 and registered by AddMarten / AddFisher with no wiring here. These scenarios
  # assert the fold: every role names its source, declarations survive where nothing outranks
  # them, and the deliberately planted disagreement (FreezeAccount.cs emits AccountFlagged, which
  # no spec mentions) surfaces as a hotspot instead of vanishing. The fifth source — runtime
  # observation — is CritterWatch's, and joins when its side of the vehicle lands.
  #
  # These scenarios are deliberately untagged and use no type captures, so they contribute
  # nothing to the model they assert on.

  Scenario: Four sources fold into one provenance-stamped model
    When the event model is assembled from the chains, the overlay, the store and this assembly's specs
    Then there is exactly one model, named "BankAccount"
    And every claimed role on every slice names its source
    And the "FreezeAccount" slice's EmittedEvents role is claimed by Derived
    And the "FreezeAccount" slice's HandlerType role is claimed by Derived
    And the "FreezeAccount" slice's Domain role is claimed by Declared
    And the "FreezeAccount" slice's TriggerLabel role is claimed by Declared
    And the "FreezeAccount" slice's ReadModelTypes role is claimed by Declared
    And the "FreezeAccount" slice's Specifications role is claimed by Declared

  Scenario: The planted disagreement surfaces as a hotspot instead of vanishing
    When the event model is assembled from the chains, the overlay, the store and this assembly's specs
    Then the "FreezeAccount" slice reports a source disagreement on EmittedEvents
    And that disagreement kept the Derived claim naming "AccountFlagged"
    And that disagreement dropped the Declared claim "AccountFrozen"
    And the "DepositFunds" slice reports no source disagreement
    And the "WithdrawFunds" slice reports no source disagreement

  Scenario: Declared names, domains and spec bindings survive the merge
    When the event model is assembled from the chains, the overlay, the store and this assembly's specs
    Then the "FreezeAccount" slice is in domain "Banking"
    And the "FreezeAccount" slice is triggered by "The fraud desk"
    And the "FreezeAccount" slice binds the specification "Freeze Account/Freezing an account records the freeze"
    And the "DepositFunds" slice binds the specification "Bank Account Event Sourcing/Deposit funds"
    And the "WithdrawFunds" slice binds the specification "Bank Account Event Sourcing/Withdrawing more than the balance leaves the account untouched"

  # wolverine#4181, FIXED at 6.31.0 — this scenario is the flipped tripwire: the HTTP-derived
  # source no longer claims TriggerLabel, so the overlay's human label wins the role by being
  # its only claimant, exactly as jasperfx#703's contract says naming roles should.
  Scenario: An HTTP slice's trigger label belongs to the overlay again
    When the event model is assembled from the chains, the overlay, the store and this assembly's specs
    Then the "WithdrawFunds" slice's TriggerLabel role is claimed by Declared
    And the "WithdrawFunds" slice is triggered by "Customer at the ATM"

  # bobcat#175. Until this, the vehicle asserted read models only as a *provenance* claim
  # ("ReadModelTypes role is claimed by Declared") and never as an identity, so a derived read
  # model could be wrong, ugly or missing and every scenario still passed — which is how
  # wolverine#4182 came to be caught by eye on the canvas rather than by a spec.
  Scenario: A query slice reads the document type it returns
    When the event model is assembled from the chains, the overlay, the store and this assembly's specs
    Then the "GET /api/accounts/{id}" slice reads the Account read model
    And the "GET /api/clients/{id}" slice reads the Client read model
    And the "GET /api/accounts/{accountId}/transactions" slice reads the AccountTransactions read model

  # wolverine#4182, FIXED at 6.31.0 (via #4185) — the flipped tripwire, now the bobcat#175 fold
  # assertion: the collection query unwraps to its element type and shares the Account node with
  # its single-document sibling instead of minting a raw-generic one.
  Scenario: A collection query reads the element type it returns
    When the event model is assembled from the chains, the overlay, the store and this assembly's specs
    Then the "GET /api/clients/{clientId}/accounts" slice reads the Account read model

  # bobcat#300 — the store rung. Slices merge BY NAME, and the store names its View slice after
  # the document type, which is how AccountView.feature's `@slice:Account` and `{readmodel}`
  # capture already name it. That agreement is entirely conventional; this scenario is what
  # enforces it. Two stickies for one projection is the failure the design exists to avoid.
  Scenario: The store rung and the spec rung describe one Account slice, not two
    When the event model is assembled from the chains, the overlay, the store and this assembly's specs
    Then there is exactly one slice named "Account"
    And the "Account" slice has pattern View
    And the "Account" slice reads the Account read model
    And the "Account" slice's ReadModelTypes role is claimed by Derived
    And the "Account" slice's ConsumedEvents role is claimed by Derived
    And the "Account" slice consumes the AccountOpened event
    And the "Account" slice consumes the FundsDeposited event
    And the "Account" slice consumes the FundsWithdrawn event
    And the "Account" slice consumes the AccountFrozen event
    And this assembly's specs alone say the "Account" slice consumes the AccountOpened event
    And this assembly's specs alone say the "Account" slice consumes the FundsDeposited event
    And this assembly's specs alone say the "Account" slice consumes the FundsWithdrawn event
    And this assembly's specs alone say the "Account" slice consumes the AccountFrozen event
    And the "Account" slice's Specifications role is claimed by Declared
    And the "Account" slice's Domain role is claimed by Declared
    And the "Account" slice binds the specification "Account View/The account read model folds the stream's history"
    And the "Account" slice reports no source disagreement

  # The payoff of jasperfx#823 + #824 + #825 landing together: the store supplies ConsumedEvents,
  # the roles give them an element, and Links turns the pair into a cross-slice edge nobody drew.
  # The console renders it (bobcat#295); this is where it is proved rather than asserted three
  # times in isolation.
  Scenario: The events the command slices emit reach the Account view as links nobody drew
    When the event model is assembled from the chains, the overlay, the store and this assembly's specs
    Then there is an EventConsumed link from the "DepositFunds" slice to the "Account" slice via FundsDeposited
    And there is an EventConsumed link from the "WithdrawFunds" slice to the "Account" slice via FundsWithdrawn
    And there is an EventConsumed link from the "FreezeAccount" slice to the "Account" slice via AccountFrozen
    And there is an EventConsumed link from the "OpenAccount" slice to the "Account" slice via AccountOpened
