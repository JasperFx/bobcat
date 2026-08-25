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
