@domain:Wallets
Feature: Wallet
  Triggered by the wallet holder

  # Written ONLY in shipped Critter Stack grammar — no fixture-specific steps exist.
  # {aggregate}/{command}/{event}/{readmodel}/{message} resolve to the domain types by simple name.

  @slice:OpenWallet
  Scenario: Opening a wallet emits the opened event and starts an empty balance
    Given no events for Wallet "11111111-1111-1111-1111-111111111111"
    When OpenWallet is received
      | WalletId                             | Owner |
      | 11111111-1111-1111-1111-111111111111 | Ann   |
    Then WalletOpened is emitted
    And the WalletSummary read model contains
      | Balance |
      | 0       |

  @slice:CreditWallet
  Scenario: Crediting a wallet emits the credited event and sends a notification
    Given no events for Wallet "22222222-2222-2222-2222-222222222222"
    When OpenWallet is received
      | WalletId                             | Owner |
      | 22222222-2222-2222-2222-222222222222 | Bea   |
    When CreditWallet is received
      | WalletId                             | Amount |
      | 22222222-2222-2222-2222-222222222222 | 25     |
    Then WalletCredited is emitted
      | WalletId                             | Amount |
      | 22222222-2222-2222-2222-222222222222 | 25     |
    And WalletCreditedNotification is sent
    And the WalletSummary read model contains
      | Credits | Balance |
      | 1       | 25      |

  @slice:CreditWallet
  Scenario: A wallet with prior events keeps accumulating
    Given no events for Wallet "33333333-3333-3333-3333-333333333333"
    And events for Wallet
      | Event        | WalletId                             | Owner |
      | WalletOpened | 33333333-3333-3333-3333-333333333333 | Dee   |
    And events for Wallet
      | Event          | WalletId                             | Amount |
      | WalletCredited | 33333333-3333-3333-3333-333333333333 | 40     |
    When CreditWallet is received
      | WalletId                             | Amount |
      | 33333333-3333-3333-3333-333333333333 | 10     |
    Then WalletCredited is emitted
    And the WalletSummary read model contains
      | Balance |
      | 50      |

  # The clean-refusal railway (issue #168): the handler's Before returns HandlerContinuation.Stop,
  # so nothing throws — "validation fails with" cannot describe this handler, and the reason-less
  # "the command is refused" is its vocabulary.
  #
  # Its trigger is declared on the scenario (issue #258): a slice is scenario-level, so a
  # scenario's "Triggered by" wins over the feature's for that slice, while OpenWallet and
  # CreditWallet keep the feature-level "the wallet holder".
  @slice:DebitWallet
  Scenario: Debiting more than the balance is refused cleanly
    Triggered by a card payment at the till
    Given no events for Wallet "55555555-5555-5555-5555-555555555555"
    When OpenWallet is received
      | WalletId                             | Owner |
      | 55555555-5555-5555-5555-555555555555 | Edy   |
    When CreditWallet is received
      | WalletId                             | Amount |
      | 55555555-5555-5555-5555-555555555555 | 25     |
    When DebitWallet is received
      | WalletId                             | Amount |
      | 55555555-5555-5555-5555-555555555555 | 100    |
    Then the command is refused
    And no events are emitted

  @slice:CreditWallet
  Scenario: Crediting a non-positive amount fails and emits nothing
    Given no events for Wallet "44444444-4444-4444-4444-444444444444"
    When OpenWallet is received
      | WalletId                             | Owner |
      | 44444444-4444-4444-4444-444444444444 | Cy    |
    When CreditWallet is received
      | WalletId                             | Amount |
      | 44444444-4444-4444-4444-444444444444 | 0      |
    Then validation fails with "must be positive"
    And no events are emitted

  # Issue #233: `When {command} is received` used to declare a non-nullable StepTable, so a step
  # with no table handed the fixture null and it dereferenced it — a bare NRE whose stack was
  # Bobcat's, not the spec's. A field-less command is a perfectly good act, so the table is
  # optional now (and BOBCAT020 catches the steps that genuinely do need one, at build time).
  @slice:SweepWallets
  Scenario: A field-less command needs no table
    Given no events for Wallet "88888888-8888-8888-8888-888888888888"
    When SweepWallets is received
    Then no events are emitted

  # Issue #236: a fan-out read model is keyed by an owner, never by a stream, so the shortcut
  # step — which loads by the scenario's stream id — cannot address it at all. Two wallets, two
  # streams, one document.
  @slice:OwnerWallets
  Scenario: Two wallets for one owner fold into that owner's read model
    Given no events for Wallet "66666666-6666-6666-6666-666666666666"
    When OpenWallet is received
      | WalletId                             | Owner |
      | 66666666-6666-6666-6666-666666666666 | Fay   |
    When OpenWallet is received
      | WalletId                             | Owner |
      | 77777777-7777-7777-7777-777777777777 | Fay   |
    Then the OwnerWallets read model with id "Fay" contains
      | Wallets |
      | 2       |
