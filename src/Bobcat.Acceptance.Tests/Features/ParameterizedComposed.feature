Feature: Parameterized Composed

  # Issue #212 phase 2: [IncludeGrammars(typeof(Module), args...)] — the literals construct the
  # module per scenario; parameters the literals do not cover resolve from the scenario scope.

  Scenario: A module is constructed with the attribute literals
    Then the prefixed echo of "x" should be "pre-x!"

  Scenario: A module resolves its remaining constructor parameters from the scenario
    When the bound module records its label
    Then the bound label should be "wallet"
