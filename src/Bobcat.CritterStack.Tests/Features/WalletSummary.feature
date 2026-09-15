@domain:Wallets
Feature: Wallet Summary

  # Issue #297: the View slice. Written only in shipped grammar and named after the DOCUMENT type
  # — the same name the store-derived rung (jasperfx#825) gives the slice, so the two fold into one.
  # There is no When: a View receives no command. What its scenarios ARRANGE is what the projection
  # applies, so the generator stamps the arranged `{event}`s as ConsumedEvents here — and, by the
  # #259 demotion that still stands for Command slices, stamps nothing for the same Givens on
  # CreditWallet, where they are the aggregate's own stream.

  @slice:WalletSummary
  Scenario: The summary folds an opened and credited wallet
    Given no events for Wallet "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
    And WalletOpened occurred
      | WalletId                             | Owner |
      | bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb | Kim   |
    And WalletCredited occurred
      | WalletId                             | Amount |
      | bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb | 15     |
    Then the WalletSummary read model contains
      | Credits | Balance |
      | 1       | 15      |

  # A named arrangement is inlined before matching, so its events are consumed exactly as if
  # written longhand. Wallet.feature declares "Hal's wallet with 40 credited"; arrangements are
  # feature-scoped, so this feature carries its own.
  @arrangement
  Scenario: a wallet Lou opened and credited twice
    Given WalletOpened occurred
      | Owner |
      | Lou   |
    And WalletCredited occurred
      | Amount |
      | 20     |
    And WalletCredited occurred
      | Amount |
      | 30     |

  @slice:WalletSummary
  Scenario: The summary folds history arranged by name
    Given no events for Wallet "cccccccc-cccc-cccc-cccc-cccccccccccc"
    And the arrangement "a wallet Lou opened and credited twice"
    Then the WalletSummary read model contains
      | Credits | Balance |
      | 2       | 50      |
