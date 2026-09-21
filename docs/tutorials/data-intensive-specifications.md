# Data Intensive Specifications

::: warning OUTLINE — NOT YET WRITTEN
This tutorial has no prose yet. The machinery it describes is **built and shipping**, and is
currently documented nowhere — it appears only in passing in three other pages. That makes this the
largest gap in the documentation, not the smallest.

The outline below records what it needs to cover and where the material lives.
:::

## What this tutorial is for

Specifications whose subject is *a lot of data* — setting up dozens of rows, then verifying a set
rather than a scalar. Bobcat inherited these mechanisms from Storyteller, and they are the reason a
data-heavy spec can stay readable instead of becoming a wall of `Given` lines.

## To cover

### 1. Tables as input — `[Table]`

Bulk setup from a Gherkin data table instead of one step per row. `StepTable`
(`src/Bobcat/StepTable.cs`) is the shape steps receive: `Headers`, `Rows`, `AsDictionaries()`,
`Cell(row, header)`, `HasColumn(header)`.

### 2. Table grammars — building an object per row

A class whose `Row` method turns a table row into an object. Already partly covered in
[Composing Grammar Modules](../composing-grammars.md#building-an-object-from-a-table-row).

Worth stating here: `[TableGrammar]` steps are invisible to the VS Code Cucumber extension — see
[Integrating Bobcat with Your IDE](ide-integration.md).

### 3. Set verification — `[SetVerification]`

Verifying a collection against expected rows without asserting on order. The comparer is
`src/Bobcat/Runtime/SetVerificationComparer.cs`. Needs to cover matching by key, and how
missing/extra/wrong rows are each reported — the reporting is most of the value.

through the same comparer — see [Specifications with Code](specifications-with-code.md).

### 4. Decision tables — `[DecisionTable]`

One row per case, input columns and expected-output columns in the same table. The comparer is
`src/Bobcat/Runtime/DecisionTableComparer.cs`. `preview` names the expected-output cell a parameter
came from, which is worth showing.

### 5. The comparison attributes

`[Expected]`, `[Comparison]`, `[Approx]` — declared in `src/Bobcat/Attributes.cs`. `[Approx]` in
particular needs a worked example; floating-point set verification is where people give up.


## Source material

| | |
|---|---|
| Working demo | `src/ConsolePreview/InventoryFixture.cs` |
| Attributes | `src/Bobcat/Attributes.cs` — `TableAttribute`, `DecisionTableAttribute`, `SetVerificationAttribute`, `ExpectedAttribute`, `ComparisonAttribute`, `ApproxAttribute` |
| Comparers | `src/Bobcat/Runtime/SetVerificationComparer.cs`, `src/Bobcat/Runtime/DecisionTableComparer.cs` |
| Table shapes | `src/Bobcat/StepTable.cs`, `src/Bobcat/CodeFirst/RowTable.cs` |
| Code-first expectations | `src/Bobcat/CodeFirst/Expectations.cs` |

## Open question for the author

How much of the Storyteller lineage to make explicit. Naming it helps anyone arriving from
Storyteller find the equivalent; it means nothing to everyone else.
