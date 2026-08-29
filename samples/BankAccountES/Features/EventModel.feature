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

  # ⚠️ wolverine#4181, asserted deliberately: the HTTP-derived source claims TriggerLabel with the
  # verb+route, so an overlay label on an HTTP slice cannot win and Program.cs withholds them.
  # When #4181 ships, the Derived claim disappears, this scenario trips, and whoever sees it
  # restores the overlay's TriggeredBy labels on the HTTP slices and flips these assertions.
  Scenario: An HTTP slice's trigger label is claimed by the chains until wolverine 4181 lands
    When the event model is assembled from the chains, the overlay and this assembly's specs
    Then the "WithdrawFunds" slice's TriggerLabel role is claimed by Derived
    And the "WithdrawFunds" slice is triggered by "POST /api/accounts/{accountId}/withdrawals"

  # bobcat#175. Until this, the vehicle asserted read models only as a *provenance* claim
  # ("ReadModelTypes role is claimed by Declared") and never as an identity, so a derived read
  # model could be wrong, ugly or missing and every scenario still passed — which is how
  # wolverine#4182 came to be caught by eye on the canvas rather than by a spec.
  Scenario: A query slice reads the document type it returns
    When the event model is assembled from the chains, the overlay and this assembly's specs
    Then the "GET /api/accounts/{id}" slice reads the Account read model
    And the "GET /api/clients/{id}" slice reads the Client read model
    And the "GET /api/accounts/{accountId}/transactions" slice reads the AccountTransactions read model

  # ⚠️ wolverine#4182, asserted deliberately, in the same spirit as the #4181 scenario above:
  # a query returning IReadOnlyList<Account> reports the raw closed generic as its read model, so
  # the collection route mints its own canvas node instead of folding onto the Account node its
  # single-document sibling already produces. Fixed in wolverine#4185, unreleased at 6.30.1.
  # When the pin bumps, this scenario trips; replace it with the fold assertion in bobcat#175:
  #   Then the "GET /api/clients/{clientId}/accounts" slice reads the Account read model
  Scenario: A collection query reports its raw list type until wolverine 4182 lands
    When the event model is assembled from the chains, the overlay and this assembly's specs
    Then the "GET /api/clients/{clientId}/accounts" slice reads the IReadOnlyList`1 read model
