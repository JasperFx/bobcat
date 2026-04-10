using System.Collections;
using System.Reflection;
using Bobcat.Engine;

namespace Bobcat.Model;

/// <summary>
/// A grammar for set verification — the fixture method returns a collection,
/// Bobcat compares it against expected table data with per-cell diffs.
/// </summary>
public class SetVerificationGrammar : IGrammar
{
    private readonly MethodInfo _method;
    private readonly string _expression;
    private readonly string[] _keyColumns;

    public SetVerificationGrammar(MethodInfo method, ThenAttribute thenAttr, SetVerificationAttribute svAttr)
    {
        _method = method;
        _expression = thenAttr.Expression;
        _keyColumns = string.IsNullOrWhiteSpace(svAttr.KeyColumns)
            ? []
            : svAttr.KeyColumns.Split(',', StringSplitOptions.TrimEntries);
        Name = method.Name;
    }

    public string Name { get; }
    public IReadOnlyList<string> KeyColumns => _keyColumns;

    public void CreatePlan(ExecutionPlan plan, Step step, FixtureInstance fixture)
    {
        plan.Add(new SetVerificationExecutionStep(this, step, fixture));
    }

    private class SetVerificationExecutionStep : IExecutionStep
    {
        private readonly SetVerificationGrammar _grammar;
        private readonly Step _step;
        private readonly FixtureInstance _fixture;

        public SetVerificationExecutionStep(SetVerificationGrammar grammar, Step step, FixtureInstance fixture)
        {
            _grammar = grammar;
            _step = step;
            _fixture = fixture;
        }

        public string StepId => _step.Id;
        public StepKind StepKind => StepKind.Then;

        public async Task Execute(IStepContext context, StepResult result, CancellationToken token)
        {
            var fixtureInstance = _fixture.Instance;
            fixtureInstance.Context = context;

            // Invoke the fixture method to get the actual collection
            object? returnValue;
            try
            {
                returnValue = _grammar._method.Invoke(fixtureInstance, []);
                if (returnValue is Task task)
                {
                    await task;
                    // Extract result from Task<T>
                    var taskType = task.GetType();
                    if (taskType.IsGenericType)
                    {
                        returnValue = taskType.GetProperty("Result")!.GetValue(task);
                    }
                }
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw tie.InnerException;
            }

            if (returnValue is not IEnumerable actualEnumerable)
            {
                throw new InvalidOperationException(
                    $"[SetVerification] method '{_grammar._method.Name}' must return IEnumerable");
            }

            var actualRows = ToRows(actualEnumerable);
            var expectedRows = _step.Rows;

            Compare(expectedRows, actualRows, result);
        }

        private void Compare(
            IReadOnlyList<Dictionary<string, object>> expectedRows,
            List<Dictionary<string, string>> actualRows,
            StepResult result)
        {
            var keyColumns = _grammar._keyColumns;
            var matchedActualIndices = new HashSet<int>();
            var cells = new List<CellResult>();
            var hasFailure = false;
            var rowIndex = 0;

            // Collect column names for rendering
            var columns = expectedRows.Count > 0
                ? expectedRows[0].Keys.ToList()
                : new List<string>();

            foreach (var expected in expectedRows)
            {
                var expectedStr = expected.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value?.ToString() ?? "");

                var matchIndex = FindMatch(expectedStr, actualRows, keyColumns, matchedActualIndices);

                if (matchIndex >= 0)
                {
                    matchedActualIndices.Add(matchIndex);
                    var actual = actualRows[matchIndex];

                    foreach (var col in expectedStr.Keys)
                    {
                        var expectedVal = expectedStr[col];
                        var actualVal = actual.GetValueOrDefault(col, "");

                        if (string.Equals(expectedVal, actualVal, StringComparison.Ordinal))
                        {
                            cells.Add(new CellResult(col, ResultStatus.success, expectedVal)
                                { RowIndex = rowIndex });
                        }
                        else
                        {
                            cells.Add(new CellResult(col, ResultStatus.failed,
                                $"expected '{expectedVal}', got '{actualVal}'")
                                { RowIndex = rowIndex });
                            hasFailure = true;
                        }
                    }
                }
                else
                {
                    var keyDesc = string.Join(", ", expectedStr.Select(kv => $"{kv.Key}={kv.Value}"));
                    cells.Add(new CellResult("missing-row", ResultStatus.missing,
                        $"Expected row not found: {keyDesc}")
                        { RowIndex = rowIndex });
                    hasFailure = true;
                }

                rowIndex++;
            }

            // Extra rows
            for (var i = 0; i < actualRows.Count; i++)
            {
                if (matchedActualIndices.Contains(i)) continue;

                var extra = actualRows[i];
                var desc = string.Join(", ", extra.Select(kv => $"{kv.Key}={kv.Value}"));
                cells.Add(new CellResult("extra-row", ResultStatus.invalid,
                    $"Extra row: {desc}")
                    { RowIndex = rowIndex++ });
            }

            result.IsSetVerification = true;
            result.SetVerificationColumns = columns;
            result.MarkCells(cells.ToArray());

            if (hasFailure)
            {
                result.MarkFailed();
            }
            else
            {
                result.MarkSuccess();
            }
        }

        private static int FindMatch(
            Dictionary<string, string> expected,
            List<Dictionary<string, string>> actuals,
            string[] keyColumns,
            HashSet<int> alreadyMatched)
        {
            for (var i = 0; i < actuals.Count; i++)
            {
                if (alreadyMatched.Contains(i)) continue;

                var actual = actuals[i];

                if (keyColumns.Length > 0)
                {
                    // Match by key columns
                    var allKeysMatch = keyColumns.All(key =>
                        expected.TryGetValue(key, out var ev) &&
                        actual.TryGetValue(key, out var av) &&
                        string.Equals(ev, av, StringComparison.Ordinal));

                    if (allKeysMatch) return i;
                }
                else
                {
                    // No key columns — match by all columns
                    var allMatch = expected.All(kv =>
                        actual.TryGetValue(kv.Key, out var av) &&
                        string.Equals(kv.Value, av, StringComparison.Ordinal));

                    if (allMatch) return i;
                }
            }

            return -1;
        }

        private static List<Dictionary<string, string>> ToRows(IEnumerable actual)
        {
            var rows = new List<Dictionary<string, string>>();
            foreach (var item in actual)
            {
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    row[prop.Name] = prop.GetValue(item)?.ToString() ?? "";
                }
                rows.Add(row);
            }
            return rows;
        }
    }
}
