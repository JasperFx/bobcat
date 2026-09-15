@domain:Wallets
Feature: Wallet

  A spec assembly's half of an Event Model, for real (issue #294). There is nothing special about
  these scenarios: they exist so that THIS assembly carries a generated BobcatEventModelSource
  with slices and Specifications in it, which is the artefact
  SpecEventModelPublishingTests watches travel over PUT /api/event-model/{source} into a real
  EventModelStore and come back out of GET /api/event-model as half of a merge.

  Triggered by a teller working the counter

  @slice:CreditWallet
  Scenario: Crediting a wallet emits the credited event
    When CreditWallet is received
    Then WalletCredited is emitted

  @slice:DebitWallet
  Scenario: Debiting a wallet emits the debited event
    When DebitWallet is received
    Then WalletDebited is emitted
