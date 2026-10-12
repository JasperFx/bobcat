# Bobcat.Acceptance.Tests -> projected-lane coverage map

bobcat#471 phase 2, 2026-10-11. Phase 3 deletes `src/Bobcat.Acceptance.Tests` whole, with its 21
`.feature` files. Most of what that project pins is engine behaviour both lanes share, and deleting it
with the lane would delete that coverage without anyone noticing. This file is the check that nothing
lane-neutral goes with it: every scenario group, the behaviour it pins, and where the projected lane
pins the same thing.

The replacement tests live in **`src/Bobcat.Projected.Acceptance.Tests`** (xUnit v3, in `bobcat.slnx`,
so `./build.sh CI` runs it). It opts into the `Bobcat.Generated` interceptors, which makes it the one
project after phase 3 where a `[Given]`/`[Then]` helper is really intercepted. That's why the
projected-lane tests that used to sit in the Gherkin project are copied there too (section V).

## How to read it

Each row gets one of three classes:

- **a**: lane-neutral, and a projected or engine-level test that survives phase 3 already pinned it
  before this phase. The cited test is the one to keep. Where this phase also added an end-to-end
  projected test, it's named after the `+`.
- **b**: lane-neutral, and nothing that survives phase 3 pinned it. A new test in
  `Bobcat.Projected.Acceptance.Tests` does now. **SKIPPED** means the test is written and skipped,
  because the projected lane doesn't do it yet. Those are listed under "Gaps found".
- **c**: Gherkin-only. It dies with the lane and needs no replacement. The note says why, and what
  the projected lane does instead when there's an obvious counterpart.

Test names are `Class.method`. Unqualified classes are in `Bobcat.Projected.Acceptance.Tests`.

## Counts

| class | rows |
|---|---|
| a | 9 |
| b | 52 (7 of these rows carry a SKIPPED test, and 7 are projected classes copied over) |
| c | 28 |

**93 tests** in the new project: 83 pass and 10 are skipped. 9 of the skipped tests are gaps, and 1 is
a `[BobcatSpec(Pending = true)]` that's skipped by design.

---

## A. Value checks -- `ReturnValue.feature`, `OutParams.feature`, `ReturnAndOutVerificationTests`

In the projected lane a helper takes the expected value as an argument and reports through `SpecAssert`.
It doesn't return the actual value for a generator to compare.

| # | Behaviour pinned | Class | Projected coverage |
|---|---|---|---|
| A1 | A matching value is a structured success cell (name, expected, actual) | a | `Bobcat.Tests` `SpecAssertTests.a_matching_check_records_a_successful_cell` + `ValueCheckTests.a_matching_value_is_a_structured_success_cell` |
| A2 | A mismatch fails the step with expected/actual and `expected '6', got '5'` | a | `SpecAssertTests.a_mismatched_check_records_expected_and_actual` + `ValueCheckTests.a_mismatched_value_fails_with_expected_and_actual` |
| A3 | A numeric tolerance flows into the comparison and the cell's `±0.1` note | b | `ValueCheckTests.a_tolerance_flows_into_the_comparison_and_its_note`. The tolerance is a `CheckOptions` argument. |
| A4 | A string value passes and fails with its own expected/actual | b | `ValueCheckTests.a_string_value_passes`, `a_string_value_fails` |
| A5 | Several outputs of one sentence are one cell each, and one wrong output doesn't hide a right one | a | `SpecAssertTests.several_checks_in_one_step_are_judged_separately` + `ValueCheckTests.several_outputs_of_one_sentence_are_one_cell_each`, `one_wrong_output_fails_the_step_and_names_its_cell` |
| A6 | A return value or `out` parameter is bound by the generator and compared against a document cell; `[Approx]` read off the method | c | A C# call site has no expected cell to compare against. The projected form is A1-A5. `[Approx]` is read by the Gherkin generator only: the projected tolerance is `SpecAssert.Check(..., tolerance:)` (`Bobcat.Tests` `SpecAssertEventuallyTests.a_tolerance_is_an_approximate_comparison_with_its_note`), and BOBCAT035 warns about the attribute in projected code (`Bobcat.Generators.Tests` `GherkinOnlyAttributeTests`). |

## B. Clock -- `Clock.feature`, `ClockTests`

| # | Behaviour pinned | Class | Projected coverage |
|---|---|---|---|
| B1 | Freeze the date, advance by a duration, "passes", advance to an instant | a | `Bobcat.CritterStackScenario.Tests` `critter_stack_scenario<T>.clock_steps_render_and_move_the_ambient_clock`, `the_clock_steps_render_what_they_did_in_words_the_grammar_reads_back` (run on Fisher always, on Marten with Postgres) |
| B2 | Relative tokens (`TODAY`, `NOW + 30 minutes`, `TODAY+3`) on the expected side resolve against the frozen clock; the cell shows the date and the note shows the token | b | `RelativeTimeTests` (3 tests). The read side is already pinned by `Bobcat.Tests` `CellValuesTests.a_relative_date_resolves_against_the_clock_the_run_is_using`. |
| B3 | `ClockGrammars`' Gherkin step texts | c | Replaced by B1's `CritterStackScenario` steps |

## C. Decision tables -- `DecisionTable.feature`, `DecisionTableTests`

Projected form: `TableRunner.Run(this, nameof(row), table)`. A returned value plus exactly one column
no parameter names makes a decision table, which is the same rule the generator applies.

| # | Behaviour pinned | Class | Projected coverage |
|---|---|---|---|
| C1 | All rows pass; input cells are plain and the expected column is judged; the grid keeps the document's column order | b | `DecisionTableTests.return_value_columns_all_pass` |
| C2 | A wrong row reports its cell with expected/actual | b | `DecisionTableTests.a_return_value_column_reports_its_failing_cell` |
| C3 | A throwing row is a `row-error` cell on that row; the other rows are still judged and the scenario carries on | b | `DecisionTableTests.a_throwing_row_is_a_row_error_and_the_rest_of_the_table_still_runs` |
| C4 | A throwing row renders as an `Errored` row with its reason and inputs | b | `DecisionTableTests.a_throwing_row_renders_as_its_own_row_with_the_reason` |
| C5 | ...and the step's verdict for it is `failed` (an assertion-level wrong) | b | **SKIPPED** `DecisionTableTests.a_throwing_row_fails_the_step_rather_than_erroring_it` (gap 2) |
| C6 | ...and the column it never produced renders `error`, not as an input | b | **SKIPPED** `DecisionTableTests.the_column_a_throwing_row_never_produced_renders_as_an_error` (gap 3) |
| C7 | `out`-parameter output columns are compared per row | b | **SKIPPED** `DecisionTableTests.out_param_columns_are_compared_per_row` (gap 4) |
| C8 | `[DecisionTable]` binding by the generator | c | The projected form is C1 |

## D. Tables a step runs itself -- `RunTable.feature`, `RunTableTests`

Nothing that survives phase 3 called `TableRunner` before this. The only callers were here and in
`Bobcat.Xunit.Samples`, which CI never runs.

| # | Behaviour pinned | Class | Projected coverage |
|---|---|---|---|
| D1 | A named (private) row method runs once per row and renders one grid | b | `RunTableTests.runs_the_named_method_once_per_row_and_renders_one_grid` |
| D2 | `[Header]` renames a column, and an optional parameter's column may be left out | b | `RunTableTests.a_header_renames_the_column_and_an_optional_column_may_be_left_out` |
| D3 | A cell that won't convert is that row's `row-error` (naming the enum's members), and the other rows still run | b | `RunTableTests.a_row_whose_cell_will_not_convert_is_that_rows_error_alone` |
| D4 | An async row method is awaited before the next row | b | `RunTableTests.an_async_row_method_is_awaited_before_the_next_row` |
| D5 | A returned value plus one unclaimed column is a decision table | b | `RunTableTests.a_returned_value_and_one_unclaimed_column_is_a_decision_table` |
| D6 | `BuildRows<T>` builds one object per row, with relative dates and record defaults, and renders the grid | b | `RunTableTests.builds_one_object_per_row_with_relative_dates_and_declared_defaults` |
| D7 | A method name that matches nothing says so | b | `RunTableTests.a_method_name_that_matches_nothing_says_so` |

## E. Set verification -- `SetVerification.feature`, `SetVerificationTests`

Projected form: `SetVerificationComparer.Verify(actual, table, keyColumns:, ordered:, column:)`. The
`[SetVerification]` settings become arguments, and the expected rows arrive as a table literal.

| # | Behaviour pinned | Class | Projected coverage |
|---|---|---|---|
| E1 | An unordered set ignores row order | b | `SetVerificationTests.an_unordered_set_ignores_the_order_the_rows_arrive_in` (the `SetVerificationComparerTests` neighbour drives a Gherkin `StepResult` and goes in phase 3) |
| E2 | An ordered set passes when the order agrees | b | `SetVerificationTests.an_ordered_set_passes_when_the_order_agrees` (the `SetVerificationComparerTests` neighbour drives a Gherkin `StepResult` and goes in phase 3) |
| E3 | An ordered set reports the row that turned up early (`out-of-order` marker, `OutOfOrder` row, values still right) | b | `SetVerificationTests.an_ordered_set_reports_the_row_that_turned_up_early` (the `SetVerificationComparerTests` neighbour drives a Gherkin `StepResult` and goes in phase 3) |
| E4 | A set of plain values compared under an explicitly named column, ordered, never against `string`'s properties | b | `SetVerificationTests.a_set_of_plain_values_is_compared_under_the_column_the_grammar_names` |
| E5 | A table column carries a `[Header]` of its own, and the grid shows it | b | `SetVerificationTests.a_column_can_carry_a_header_of_its_own` |
| E6 | An optional column left out gets the parameter's default, not `default(T)` | b | `SetVerificationTests.an_optional_column_may_be_left_out_of_the_table` |
| E7 | A header with no rows is "the set is empty", and still names the columns | a | `SetVerificationComparerTests.a_header_row_with_nothing_under_it_still_names_the_columns` + `SetVerificationTests.a_header_with_no_rows_under_it_still_names_the_columns` |
| E8 | A `[Header]` on a record property titles the column it's compared under, and key columns use the title | a | `SetVerificationComparerTests.a_header_on_a_property_titles_the_column_it_is_compared_under` + `SetVerificationTests.a_property_titled_for_the_document_is_compared_under_its_title` |
| E9 | A title replaces the property name rather than aliasing it | a | `SetVerificationComparerTests.the_property_name_is_not_a_second_spelling_of_a_titled_column` + `SetVerificationTests.a_title_replaces_the_property_name_rather_than_aliasing_it` |
| E10 | `[Table]` / `[SetVerification]` bound by the generator | c | The projected form is E1-E9 |

## F. Sets a step verifies itself -- `VerifySet.feature`, `VerifySetTests`, `ProjectedVerifySetTests`

`ProjectedVerifySetTests` was already projected, but it drove a `Fixture` subclass through
`Fixture.VerifySet`/`BuildRows`, and `Fixture` dies in phase 3. The conversion uses a plain grammar
calling the static forms those methods always delegated to.

| # | Behaviour pinned | Class | Projected coverage |
|---|---|---|---|
| F1 | A set the step verifies itself renders one grid, same shape as a declared one | b | `VerifySetTests.a_set_the_step_verifies_itself_renders_one_grid` |
| F2 | A key column turns a wrong value into one failed cell, with no marker cells | b | `VerifySetTests.a_key_column_turns_a_wrong_value_into_one_failed_cell` |
| F3 | A missing and an extra row each render their own values in place | b | `VerifySetTests.a_missing_row_and_an_extra_row_each_show_their_own_values` (the `SetVerificationComparerTests` neighbour drives a Gherkin `StepResult` and goes in phase 3) |
| F4 | Order is checked after matching: a reordering is not a missing row plus an extra one | b | `VerifySetTests.ordered_is_checked_after_matching` (the `SetVerificationComparerTests` neighbour drives a Gherkin `StepResult` and goes in phase 3) |
| F5 | A one-column table over plain values infers its column | a | `SetVerificationComparerTests.verify_infers_the_column_a_set_of_plain_values_compares_under` + `VerifySetTests.a_set_of_plain_values_infers_its_column_from_a_one_column_table` |
| F6 | A header with no rows still names the columns | a | as E7 + `VerifySetTests.a_header_with_no_rows_under_it_still_names_the_columns` |
| F7 | A disagreeing set costs the projected spec its verdict without throwing | b | `VerifySetTests.a_disagreeing_set_costs_the_spec_its_verdict_without_throwing` |
| F8 | A missing row alone still costs the scenario its verdict | b | `VerifySetTests.a_missing_row_alone_still_costs_the_scenario_its_verdict` |
| F9 | An extra row alone fails the step (`failed`) | b | **SKIPPED** `VerifySetTests.an_extra_row_alone_fails_the_step` (gap 2) |

## G. `[TableGrammar]` -- `TableGrammar.feature`, `TableGrammarScoped.feature`, `TableGrammarTests`

#468 decided to drop `[TableGrammar]`, `[ScopePerRow]` and the recipes. The lane-neutral parts of this
feature (a decision table's leftover column, a row-error that keeps the grid) are pinned again in C
and D through `TableRunner`, which is the runtime twin of the generated envelope.

| # | Behaviour pinned | Class | Projected coverage |
|---|---|---|---|
| G1 | Batched setup: `Before` once, a `Row` per row, `After` once | c | The envelope is the method body: `RunTableTests.runs_the_named_method_once_per_row_and_renders_one_grid` (clear above, flush below) |
| G2 | Batched setup renders every input cell | c | Covered for table literals by `ProjectedTableTests.the_table_renders_as_a_grid_and_not_as_words_in_the_sentence` |
| G3 | A `[TableGrammar]` decision table compares the leftover column | c | Behaviour re-pinned at D5 |
| G4 | A throwing `Before` skips the rows and still runs `After` | c | A `try/finally` in the step method |
| G5 | A throwing row aborts the envelope but keeps the grid so far | c | The projected lane doesn't abort on a row: C3 |
| G6 | `Before`, rows and `After` share one scenario-scoped service | c | The Bobcat DI scope dies with the lane |

## H. Step progress -- `StepProgressTests`

| # | Behaviour pinned | Class | Projected coverage |
|---|---|---|---|
| H1 | A `[TableGrammar]` / decision table ticks "row n of N", and a throwing `Before` ticks nothing | c | Row ticks come from the generated envelope (#468). The projected form of "a long table shows progress" is H3. |
| H2 | A `[WaitFor]` step narrates its poll loop | c | `SpecAssert.EventuallyAsync` records one cell when the poll is over and doesn't narrate the attempts. What a reader needs from the loop (how long, how many polls, the last value) is on that cell's note: I1. |
| H3 | (projected form) A table step's cells reach a watcher before it finishes, and `StepFinished` carries the judged cells and column order | b | `TableProgressTests.a_table_step_shows_its_cells_before_it_finishes`, `the_finished_step_carries_the_judged_cells_and_the_column_order`; **SKIPPED** `the_finished_step_carries_one_cell_per_row_and_column` (gap 3) |

## I. WaitFor -- `WaitFor.feature`, `WaitForTests`

| # | Behaviour pinned | Class | Projected coverage |
|---|---|---|---|
| I1 | A value check converges and says so; a timeout reports the last actual value; a fact converges; an action is retried until it stops throwing | b | `WaitForTests` (5 tests, through `SpecAssert.EventuallyAsync`, the 5th a tolerance riding along with the poll). The engine half is `Bobcat.Tests` `SpecAssertEventuallyTests`; BOBCAT035 is `Bobcat.Generators.Tests` `GherkinOnlyAttributeTests`. |

## J. Named templates -- `NamedTemplates.feature`, `NamedTemplateTests`

| # | Behaviour pinned | Class | Projected coverage |
|---|---|---|---|
| J1 | A named template binds and renders by parameter name | b | `FactAndTemplateTests.a_named_template_binds_and_renders_by_parameter_name` (`StepValueBindingTests` covers typed values) |
| J2 | Placeholders written out of parameter order bind by name, not position | b | `FactAndTemplateTests.placeholders_render_by_name_whatever_order_the_sentence_puts_them_in` |
| J3 | A named template can disagree | b | `FactAndTemplateTests.a_named_template_can_disagree` |
| J4 | A keywordless `[Step]` reads as prose | b | `FactAndTemplateTests.a_keywordless_step_reads_as_prose` |
| J5 | A named `string` placeholder takes the rest of the sentence, spaces and all | b | `FactAndTemplateTests.a_named_string_placeholder_may_carry_spaces` |
| J6 | A `bool` return is the verdict, true or false, and still reaches the caller | b | `FactAndTemplateTests.a_bool_return_is_the_verdict`, `a_bool_return_can_be_false` |
| J7 | A `Task<bool>` is an asynchronous fact | b | `FactAndTemplateTests.an_asynchronous_fact_is_a_fact` |
| J8 | A keywordless step matches under any keyword in a document | c | Step matching. A C# call site doesn't match anything. |
| J9 | Cucumber `{int}` and named placeholders mixed in one expression | c | Cucumber words are the Gherkin matching vocabulary |
| J10 | A tuple return compared element by element | c | Its expected cells come from the document (BOBCAT027 in C#). The lane-neutral claim is A5. |

`Bobcat.Xunit.Samples` shows J1-J7 too, but CI never runs it, because its samples fail on purpose.

## K. `[Check]` stacked with `[Then]` -- `EditorVisibleCheck.feature`, `EditorVisibleCheckTests`

| # | Behaviour pinned | Class | Projected coverage |
|---|---|---|---|
| K1 | `[Check]` wins over a `[Then]` of the same text whichever is written first | c | A Gherkin generator rule for an editor workaround that goes with editor integration. In the projected lane a `bool` return is the verdict whatever the attribute, which `FactAndTemplateTests.a_check_is_a_fact_that_reads_as_then` pins. |

## L. JSON output -- `JsonOutputTests`

| # | Behaviour pinned | Class | Projected coverage |
|---|---|---|---|
| L1 | A scalar value failure is structured (column/status/expected/actual, no `value`) | b | `JsonOutputTests.a_scalar_value_failure_is_structured` |
| L2 | A decision-table failure carries its row and column | b | `JsonOutputTests.a_decision_table_failure_carries_its_row_and_column` |
| L3 | A decision table's input cells keep a plain value | b | `JsonOutputTests.a_decision_tables_input_cells_keep_a_plain_value` |

## M. Event Model identity -- `SliceIdentity.feature`, `SpecIdentityAuditTests`

| # | Behaviour pinned | Class | Projected coverage |
|---|---|---|---|
| M1 | The generated descriptor is reachable out of a compiled spec assembly; specified scenarios bind to their slice and a step-less one is a pending claim | b | `SpecIdentityAuditTests.the_generated_descriptor_is_reachable_out_of_a_compiled_spec_assembly`, against `SliceIdentitySpecs` (`[BobcatSlice]`, `[BobcatSpec(Pending = true)]`), which declares the same identities the feature did |
| M2 | No drift / orphan / hole, and the assembly form with excusals | b | `SpecIdentityAuditTests` (the other 4 tests). The pure comparison is already in `Bobcat.Tests` `EventModel/SpecIdentityAuditTests`. |

## N. Preview -- `PreviewBindingTests`

| # | Behaviour pinned | Class | Projected coverage |
|---|---|---|---|
| N1 | The generator records which method, expression and argument sources each step bound to, and `PreviewRender.FromScenario` reads it off the plan without executing | c | A `FeatureDefinition` plan preview. The projected preview is `ProjectedSpecPreview`/`PlannedSteps`, already covered by `Bobcat.Tests` `PlannedStepRenderTests`. |

## O. Gherkin-only machinery

| # | Source | Behaviour pinned | Class | Note |
|---|---|---|---|---|
| O1 | `ParserFeatures.feature`, `GherkinParserTests` | `Background`, doc strings, `Scenario Outline` expansion | c | Parser features. Projected: a shared setup method, a `string` argument, `[Theory]`. |
| O2 | `TableCapture.feature`, `TableCaptureTests` | Captures in a `[Table]` step's text bind by role | c | Cucumber capture binding |
| O3 | `Composed.feature`, `IncludeGrammarsTests` | Module steps alongside the fixture's own | c | `[IncludeGrammars]` composition. Projected: a field holding the other grammar. |
| O4 | same | A module instance is fresh per scenario | c | xUnit builds a class instance per test |
| O5 | same | Two modules cooperate through the scenario-state blackboard, and the state dies with the scenario | c | `IStepContext.SetState`. #107 and #408 kept the projected lane off the blackboard; grammars share ordinary state. |
| O6 | same | A `Fixture`-derived module receives the step context | c | `Fixture` and `IStepContext` die |
| O7 | `ParameterizedComposed.feature`, `DerivedComposed.feature`, `ParameterizedModulesTests` | Parameterized `[IncludeGrammars]`, scope-resolved constructor arguments, base-class discovery, most-derived re-parameterizes | c | Composition plus DI scope |
| O8 | `DerivedCalculator.feature`, `BaseFixtureStepsTests` | Inherited steps run with the fixture's own; the most-derived step of the same text wins | c | Compile-time step matching |
| O9 | same | Feature-level `@slice`/`@domain` tags are exposed on the definition | c | Projected slice tags are `[BobcatSlice]`, pinned by M1 and `Bobcat.Generators.Tests` |
| O10 | `DiScoping.feature`, `DiScopingTests` | One scoped instance per scenario, fresh next scenario, `NewScope`, `ScopePerRow`, `CurrentServices`/`RootServices` | c | The Bobcat scope dies. xUnit fixtures and the test's own scope do this job. |
| O11 | `Lifecycle.feature`, `LifecycleTests` | Discovered hooks run at the right level and in the scenario's scope | c | The four hooks die. Projected: constructor, `IAsyncLifetime`, class and assembly fixtures. |
| O12 | same | Hosted services and resources share one ordering; start in order, stop in reverse; a service that won't start is catastrophic | c | `TestResources` dies (#467 keeps preflight at the supervisor) |

## V. Projected tests that lived in the Gherkin project

These were never Gherkin. They sat in `Bobcat.Acceptance.Tests` because it was the only project with
the interceptor opt-in, and deleting the project would delete them. They're copied verbatim (namespace
aside) into `Bobcat.Projected.Acceptance.Tests`. The originals stay put until phase 3.

| # | Class | Tests | Class | Pins |
|---|---|---|---|---|
| V1 | `MarkerCommentStepsTests` | 3 | b | Marker comments reach run time as declared steps (#110) |
| V2 | `MarkerCommentTighteningTests` | 5 | b | Which comments are steps: `And`/`But` only continue an open narrative |
| V3 | `MarkerStepAttributionTests` | 3 | b | A `[BobcatStep]` attributes itself to the comment it ran under (#304) |
| V4 | `StepValueBindingTests` | 4 | b | Typed vocabularies render as sentences (#339) |
| V5 | `SynchronousStepFailureTests` | 4 | b | A synchronous helper that throws is recorded as an error |
| V6 | `DerivedIdentitySpecs` | 1 | b | The derived `{Feature}/{Scenario}` identity agrees between generator and runtime |
| V7 | `ProjectedTableTests` | 4 | b | Table literals reach the grammar, render as a grid and report per-row verdicts |

---

## Gaps found

The projected lane doesn't do these yet. Each has a skipped test that states what it should do.

1. **No projected WaitFor.** FIXED. `[WaitFor]` is read only by the Gherkin generator, so a projected
   helper carrying it compiled, ran once, and reported its first answer, with no diagnostic. Decided
   2026-10-11: a `SpecAssert` poll helper plus a generator diagnostic. `SpecAssert.EventuallyAsync`
   polls a value, a condition or an action and records one cell for the whole poll, and **BOBCAT035**
   (warning) reports `[WaitFor]` or `[Approx]` on a helper a C# test calls or on a projected spec
   method. `WaitForTests` (5) run against the helper now.
2. **Bad cells are a different verdict.** `RecordedStep.Status` reads an `error` or `invalid` cell as
   `error`. The Gherkin lane's `IStepContext.RecordCells` marks the step `failed` for any bad cell. So a
   throwing table row (`row-error`, `error`) and an extra row in a set (`extra-row`, `invalid`) are
   exceptions in the projected lane and wrongs in the Gherkin lane. The step is red either way, but
   counts and the wrong/exception distinction differ. Skipped:
   `DecisionTableTests.a_throwing_row_fails_the_step_rather_than_erroring_it`,
   `VerifySetTests.an_extra_row_alone_fails_the_step`. The passing tests assert "not success" so they
   hold under either answer. **This needs a maintainer decision.**
3. **Every table step is recorded twice.** The interceptor records any `StepTable` argument as plain
   cells (`Recording.RecordTable`), and `TableRun.Report` then adds its own cells beside them. The
   render merges them, but two symptoms get through. `StepFinished` carries each input cell twice
   (12 cells for a 2x3 table). And the column a throwing row never produced renders the document's
   text as `ok`, where the Gherkin lane renders it `error`. Skipped:
   `TableProgressTests.the_finished_step_carries_one_cell_per_row_and_column`,
   `DecisionTableTests.the_column_a_throwing_row_never_produced_renders_as_an_error`. The likely fix is
   for `TableRun.Report` to replace the step's input cells rather than add to them.
4. **`TableRunner.Run` can't bind an `out` parameter.** A row method shaped like a Gherkin
   `[DecisionTable]` with `out` outputs gets a `BadCellException` (`Int32&`) on every row. The projected
   idiom is one `SpecAssert.Check(..., rowIndex: i)` per output, so this might be a documentation
   answer rather than a code one. Skipped: `DecisionTableTests.out_param_columns_are_compared_per_row`.

## Smaller notes

- `[Approx]` on a projected helper is ignored in the same way `[WaitFor]` is: only the Gherkin
  generator reads it. The projected tolerance is a `CheckOptions` argument (A3) or the
  `SpecAssert.Check(..., tolerance:)` shorthand, and BOBCAT035 covers the attribute too (gap 1).
- Watch for engine tests that only look lane-neutral. About half of `Bobcat.Tests`
  `SetVerificationComparerTests` drives the comparison through a Gherkin `StepResult` and
  `SetVerificationRender.FromStepResult`, both on the phase 3 deletion list. Only the half after its
  "the split" comment (`Cells`, `Verify`) is safe to cite, so the map cites only those and rows E1-E3,
  F3 and F4 count as **b**.
- The old `ProjectedVerifySetTests` depended on `Fixture`, so it wasn't safe for phase 3 even though it
  was a projected test. Anything else that's "projected but drives a `Fixture`" has the same problem.
