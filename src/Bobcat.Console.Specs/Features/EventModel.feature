Feature: Event Model

  The design-time descriptor wire (issue #108): a producer pushes the current Event Model over
  PUT /api/event-model — Wolverine's event-model export, or what a spec assembly's generated
  IEventModelDefinitionSource reported — and the viewer's Event Model page reads it back over
  GET /api/event-model, the same public wire an outside consumer uses.

  Scenario: Nothing published yet reads as absent, not empty
    Then asking for the event model responds with status 404

  Scenario: A pushed descriptor is served back normalized
    When the event model "Wallets" is published with slice "CreditWallet" bound to spec "Wallet/Crediting a wallet"
    Then asking for the event model responds with status 200
    And the published event model is named "Wallets"
    And the slice "CreditWallet" of the event model carries the spec identity "Wallet/Crediting a wallet"

  # CritterWatch#1212 — the two halves of a model are compiled into different assemblies: the host's
  # event-model export carries slices with no specs, a spec assembly's generated source carries the
  # specs. Latest-wins erased one of them, so nothing could ever be joined to a run outcome.
  Scenario: Two producers each publish their half and the slices fold together
    When the event model "Wallets" is published with slice "CreditWallet" carrying no spec
    And the source "specs" publishes the event model "Wallets" with slice "CreditWallet" bound to spec "Wallet/Crediting a wallet"
    Then the slice "CreditWallet" of the event model carries the spec identity "Wallet/Crediting a wallet"
    And the slice "CreditWallet" of the event model still carries its command type

  # A re-push must REPLACE that source, not add to it — otherwise a producer that renames or drops
  # a slice leaves the old one behind forever and the model drifts from the code with every push.
  Scenario: Re-publishing a source replaces that source rather than accumulating
    When the source "specs" publishes the event model "Wallets" with slice "CreditWallet" bound to spec "Wallet/First"
    And the source "specs" publishes the event model "Wallets" with slice "CreditWallet" bound to spec "Wallet/Second"
    Then the slice "CreditWallet" of the event model carries the spec identity "Wallet/Second"
    And the slice "CreditWallet" of the event model does not carry the spec identity "Wallet/First"

  Scenario: A successful push is broadcast so an open page redraws without an F5
    When the event model "Wallets" is published with slice "CreditWallet" bound to spec "Wallet/Crediting a wallet"
    Then the event model change is broadcast for "Wallets"

  # Nothing about what the page would load has moved, so announcing a change would make an open
  # diagram flicker for a document nobody stored. (Deliberately asserts only the broadcast: the
  # fixture's data path is shared across a feature's scenarios, so what GET returns here depends on
  # what the scenarios above published.)
  Scenario: A rejected push announces nothing
    When something that is not an event model is published
    Then no event model change is broadcast
