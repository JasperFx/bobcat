@domain:Wallets
Feature: Wallet over HTTP
  Triggered by the wallet holder

  # The HTTP lane (issue #210): the act is an HTTP POST against a collapsed endpoint — the
  # endpoint IS the handler — carried by HttpGrammars through the tracked session, while every
  # assertion below it is the unchanged store vocabulary. The fixture assembles the two grammars
  # with [IncludeGrammars(typeof(HttpGrammars), "/wallets")]; no fixture-specific steps exist.

  @slice:CreditWallet
  Scenario: Crediting a wallet over HTTP emits the credited event
    Given no events for Wallet "66666666-6666-6666-6666-666666666666"
    And events for Wallet
      | Event        | WalletId                             | Owner |
      | WalletOpened | 66666666-6666-6666-6666-666666666666 | Fay   |
    When CreditWallet is posted to "/credit"
      | WalletId                             | Amount |
      | 66666666-6666-6666-6666-666666666666 | 25     |
    Then the response is 200
    And WalletCredited is emitted
      | WalletId                             | Amount |
      | 66666666-6666-6666-6666-666666666666 | 25     |
    And WalletCreditedNotification is sent
    And the WalletSummary read model contains
      | Credits | Balance |
      | 1       | 25      |

  # An HTTP guard refuses with ProblemDetails/400, not an exception — "Then validation fails
  # with …" (caught-exception semantics) cannot describe it. "Then the response is 400" plus
  # "no events are emitted" is the HTTP lane's sad-path vocabulary.
  @slice:CreditWallet
  Scenario: A refused credit returns 400 and appends nothing
    Given no events for Wallet "77777777-7777-7777-7777-777777777777"
    And events for Wallet
      | Event        | WalletId                             | Owner |
      | WalletOpened | 77777777-7777-7777-7777-777777777777 | Gus   |
    When CreditWallet is posted to "/credit"
      | WalletId                             | Amount |
      | 77777777-7777-7777-7777-777777777777 | 0      |
    Then the response is 400
    And no events are emitted
