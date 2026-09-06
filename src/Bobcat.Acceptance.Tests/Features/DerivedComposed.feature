Feature: Derived Composed

  # [IncludeGrammars] is discovered on base classes too, so a shipped abstract fixture can carry
  # its modules; the most-derived declaration of a module type wins, so a derived fixture can
  # re-parameterize what the base declared.

  Scenario: A base-declared module binds through the derived fixture
    Then the inherited flag should be "on"

  Scenario: The most-derived declaration re-parameterizes the module
    Then the prefixed echo of "x" should be "derived-x!"
