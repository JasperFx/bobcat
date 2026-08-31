Feature: Event Model

  # bobcat#172, the Bobcat-side half of the four-source vehicle. Three producers feed one
  # descriptor here — this assembly's Gherkin specs (Declared), the C# overlay in Program.cs
  # (Declared), and the host's Wolverine + HTTP chains (Derived) — and these scenarios assert
  # the fold: every role names its source, declarations survive where nothing outranks them,
  # and the deliberately planted disagreement (FreezeAccount.cs emits AccountFlagged, which no
  # spec mentions) surfaces as a hotspot instead of vanishing. The fourth source — runtime
  # observation — is CritterWatch's, and joins when its side of the vehicle lands.
  #
  # These scenarios are deliberately untagged and use no type captures, so they contribute
  # nothing to the model they assert on.

  Scenario: Three sources fold into one provenance-stamped model
    When the event model is assembled from the chains, the overlay and this assembly's specs
    Then there is exactly one model, named "BankAccount"
    And every claimed role on every slice names its source
    And the "FreezeAccount" slice's EmittedEvents role is claimed by Derived
    And the "FreezeAccount" slice's HandlerType role is claimed by Derived
    And the "FreezeAccount" slice's Domain role is claimed by Declared
    And the "FreezeAccount" slice's TriggerLabel role is claimed by Declared
    And the "FreezeAccount" slice's ReadModelTypes role is claimed by Declared
    And the "FreezeAccount" slice's Specifications role is claimed by Declared

  Scenario: The planted disagreement surfaces as a hotspot instead of vanishing
    When the event model is assembled from the chains, the overlay and this assembly's specs
    Then the "FreezeAccount" slice reports a source disagreement on EmittedEvents
    And that disagreement kept the Derived claim naming "AccountFlagged"
    And that disagreement dropped the Declared claim "AccountFrozen"
    And the "DepositFunds" slice reports no source disagreement
    And the "WithdrawFunds" slice reports no source disagreement

  Scenario: Declared names, domains and spec bindings survive the merge
    When the event model is assembled from the chains, the overlay and this assembly's specs
    Then the "FreezeAccount" slice is in domain "Banking"
    And the "FreezeAccount" slice is triggered by "The fraud desk"
    And the "FreezeAccount" slice binds the specification "Freeze Account/Freezing an account records the freeze"
    And the "DepositFunds" slice binds the specification "Bank Account Event Sourcing/Deposit funds"
    And the "WithdrawFunds" slice binds the specification "Bank Account Event Sourcing/Withdrawing more than the balance leaves the account untouched"

  # wolverine#4181, FIXED at 6.31.0 — this scenario is the flipped tripwire: the HTTP-derived
  # source no longer claims TriggerLabel, so the overlay's human label wins the role by being
  # its only claimant, exactly as jasperfx#703's contract says naming roles should.
  Scenario: An HTTP slice's trigger label belongs to the overlay again
    When the event model is assembled from the chains, the overlay and this assembly's specs
    Then the "WithdrawFunds" slice's TriggerLabel role is claimed by Declared
    And the "WithdrawFunds" slice is triggered by "Customer at the ATM"

  # bobcat#175. Until this, the vehicle asserted read models only as a *provenance* claim
  # ("ReadModelTypes role is claimed by Declared") and never as an identity, so a derived read
  # model could be wrong, ugly or missing and every scenario still passed — which is how
  # wolverine#4182 came to be caught by eye on the canvas rather than by a spec.
  Scenario: A query slice reads the document type it returns
    When the event model is assembled from the chains, the overlay and this assembly's specs
    Then the "GET /api/accounts/{id}" slice reads the Account read model
    And the "GET /api/clients/{id}" slice reads the Client read model
    And the "GET /api/accounts/{accountId}/transactions" slice reads the AccountTransactions read model

  # wolverine#4182, FIXED at 6.31.0 (via #4185) — the flipped tripwire, now the bobcat#175 fold
  # assertion: the collection query unwraps to its element type and shares the Account node with
  # its single-document sibling instead of minting a raw-generic one.
  Scenario: A collection query reads the element type it returns
    When the event model is assembled from the chains, the overlay and this assembly's specs
    Then the "GET /api/clients/{clientId}/accounts" slice reads the Account read model
