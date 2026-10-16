using System.Reflection;
using Bobcat.Engine;
using Bobcat.CritterStack;
using Bobcat.Engine.Verification;

namespace Bobcat.Runtime;

/// <summary>
/// Runs a table a step was handed: a method once per row, or a type constructed once per row. The
/// engine behind a projected grammar's <c>TableRunner.Run(this, nameof(row), table)</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A bounded softening of "no reflection"</b>, beside <c>RecordBuilding</c> and the store
/// conventions: the row method is named by a <c>string</c> at a call site, and naming one is the whole
/// point — <c>TableRunner.Run(this, nameof(BuildUser), table)</c> keeps the row method a private
/// implementation detail of the grammar instead of a step of its own.
/// </para>
/// <para>
/// Columns bind by name through <see cref="Bobcat.HeaderAttribute"/>, an optional parameter's column
/// may be left out, cells convert through <see cref="CellValues"/>, a row that throws is a
/// <c>row-error</c> cell and the remaining rows still run, and the whole thing renders as one grid.
/// </para>
/// <para>
/// <b>Outputs</b> (bobcat#483). Each <c>out</c> parameter is an output column, named the same way an
/// input is (its <see cref="Bobcat.HeaderAttribute"/> title, else its name, case-insensitive): after
/// the call its value is compared with that row's cell exactly as a returned value is. An out
/// parameter the table has no column for is not checked. A returned value is compared against the one
/// column that names no parameter, in or out; with no such column, or more than one, it is not
/// checked. A method may have both, and gets a judged column per output.
/// </para>
/// </remarks>
public static class TableRunner
{
    /// <summary>
    /// Call <paramref name="method"/> on <paramref name="target"/> once per row of
    /// <paramref name="table"/>, and report the grid.
    /// </summary>
    /// <returns>The cells and columns reported, for a caller that wants them.</returns>
    public static async Task<TableRun> Run(object target, MethodInfo method, StepTable table)
    {
        var parameters = method.GetParameters();
        var run = new TableRun(table.Headers.ToList());
        var rows = table.AsDictionaries();

        // An out parameter is always an output (bobcat#483): its column, when the table has one, is
        // the expected value, never an argument. A returned value with exactly one column no
        // parameter claims -- in or out -- is a decision table, the same rule the generator applies:
        // that column is the expected output.
        var bound = new HashSet<string>(
            parameters.Select(columnNameOf), StringComparer.OrdinalIgnoreCase);
        var leftover = table.Headers.Where(h => !bound.Contains(h)).ToList();
        var expectedColumn = method.ReturnType != typeof(void)
                             && !isAwaitableVoid(method.ReturnType)
                             && leftover.Count == 1
            ? leftover[0]
            : null;

        var outputColumns = outputColumnsOf(parameters, table, expectedColumn);

        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];

            var invalid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var arguments = bind(parameters, row, method, invalid);

                // A cell that cannot be read as its parameter's type is a defect in the specification,
                // decidable without running anything (bobcat#544): `invalid` in its own column, the
                // status an unknown column carries, and the row is not run.
                if (invalid.Count > 0)
                {
                    addInvalidCells(run, invalid, r);
                    addInputCells(run, table, row, r, outputColumns, invalid);
                    continue;
                }

                var returned = await invoke(target, method, arguments);

                foreach (var header in table.Headers)
                {
                    if (!outputColumns.TryGetValue(header, out var source)) continue;

                    // The return value is the -1 slot; every other output is its out argument,
                    // which the call wrote back into the argument array.
                    var actual = source < 0 ? returned : arguments[source];
                    run.Cells.Add(CellCheck.ForValue(header, actual,
                        row.TryGetValue(header, out var expected) ? expected : "", null, r));
                }
            }
            catch (Exception e) when (DecisionTableComparer.IsRowFailure(e))
            {
                run.Cells.Add(DecisionTableComparer.RowError(r, e));
            }

            addInputCells(run, table, row, r, outputColumns, null);
        }

        run.Report();
        return run;
    }

    private static void addInvalidCells(TableRun run, IReadOnlyDictionary<string, string> invalid, int rowIndex)
    {
        foreach (var (column, message) in invalid)
            run.Cells.Add(new CellResult(column, ResultStatus.invalid, message) { RowIndex = rowIndex });
    }

    /// <summary>
    /// The table's output columns, each mapped to where its actual value comes from: the index of
    /// the out parameter it names, or -1 for the returned value. An out parameter whose column the
    /// table leaves out is simply not checked.
    /// </summary>
    private static Dictionary<string, int> outputColumnsOf(ParameterInfo[] parameters, StepTable table,
        string? expectedColumn)
    {
        var outputs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (expectedColumn != null) outputs[expectedColumn] = -1;

        for (var i = 0; i < parameters.Length; i++)
        {
            if (!isOut(parameters[i])) continue;

            var column = columnNameOf(parameters[i]);
            if (table.Headers.Contains(column, StringComparer.OrdinalIgnoreCase)) outputs[column] = i;
        }

        return outputs;
    }

    private static bool isOut(ParameterInfo parameter) => parameter.IsOut && parameter.ParameterType.IsByRef;

    /// <summary>
    /// Build one <paramref name="type"/> per row and report the grid. A row that cannot be built is a
    /// <c>row-error</c> cell and is left out of the result; the rest are still built.
    /// </summary>
    public static TableRun Build(Type type, StepTable table, out object?[] built)
    {
        var run = new TableRun(table.Headers.ToList());
        var rows = table.AsDictionaries();
        var results = new List<object?>(rows.Count);

        for (var r = 0; r < rows.Count; r++)
        {
            Dictionary<string, string>? invalid = null;
            try
            {
                results.Add(RecordBuilding.Build(type, rows[r]));
            }
            catch (BadCellException e) when (columnOf(table, e) is { } column)
            {
                // The cell, not the row (bobcat#544): the specification wrote something the member's
                // type cannot hold, so that column is `invalid` and names the type and the text.
                invalid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [column] = e.Reason
                };
                addInvalidCells(run, invalid, r);
            }
            catch (Exception e) when (DecisionTableComparer.IsRowFailure(e))
            {
                run.Cells.Add(DecisionTableComparer.RowError(r, e));
            }

            addInputCells(run, table, rows[r], r, null, invalid);
        }

        built = results.ToArray();
        run.Report();
        return run;
    }

    /// <summary>
    /// <see cref="Run(object,MethodInfo,StepTable)"/> by method name — the form a grammar uses.
    /// </summary>
    public static Task<TableRun> Run(object target, string methodName, StepTable table)
        => Run(target, MethodNamed(target, methodName), table);

    /// <summary>
    /// One <typeparamref name="T"/> per row, and the grid.
    /// </summary>
    public static T[] BuildRows<T>(StepTable table)
    {
        Build(typeof(T), table, out var built);
        return built.OfType<T>().ToArray();
    }

    /// <summary>
    /// The method a <c>nameof</c> names, on this object's type — including a private one, because a
    /// row method is an implementation detail of the grammar rather than part of its vocabulary.
    /// </summary>
    /// <exception cref="SpecCriticalException">No such method, or more than one.</exception>
    public static MethodInfo MethodNamed(object target, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static
                                   | BindingFlags.Public | BindingFlags.NonPublic;

        var candidates = target.GetType().GetMethods(flags)
            .Where(m => m.Name == name)
            .ToList();

        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new SpecCriticalException(
                $"{target.GetType().Name} has no method named '{name}' to run this table's rows through."),
            _ => throw new SpecCriticalException(
                $"{target.GetType().Name} has {candidates.Count} methods named '{name}', so this table " +
                "cannot say which one its rows are for. Give the row method a name of its own.")
        };
    }

    /// <summary>The table's column a bad cell came from, when the exception names one.</summary>
    private static string? columnOf(StepTable table, BadCellException e)
        => e.Path is null
            ? null
            : table.Headers.FirstOrDefault(h => string.Equals(h.Trim(), e.Path, StringComparison.OrdinalIgnoreCase));

    private static void addInputCells(TableRun run, StepTable table,
        IReadOnlyDictionary<string, string> row, int rowIndex, IReadOnlyDictionary<string, int>? outputColumns,
        IReadOnlyDictionary<string, string>? invalid)
    {
        foreach (var header in table.Headers)
        {
            if (outputColumns != null && outputColumns.ContainsKey(header)) continue;
            if (invalid != null && invalid.ContainsKey(header)) continue;

            // The plain-value constructor: an input column was not judged, which is what the
            // report reads to decide it earns no Status. Its text travels as StepCell.Value
            // (issue #396).
            run.Cells.Add(new CellResult(header, ResultStatus.ok,
                row.TryGetValue(header, out var value) ? value : "") { RowIndex = rowIndex });
        }
    }

    private static object?[] bind(ParameterInfo[] parameters,
        IReadOnlyDictionary<string, string> row, MethodInfo method, Dictionary<string, string> invalid)
    {
        var arguments = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];

            // An out argument is the method's to write, never the table's to supply.
            if (isOut(parameter)) continue;

            var column = columnNameOf(parameter);
            var type = parameter.ParameterType.IsByRef
                ? parameter.ParameterType.GetElementType()!
                : parameter.ParameterType;

            if (row.TryGetValue(column, out var cell))
            {
                // Every unreadable cell of the row is reported, not only the first.
                try
                {
                    arguments[i] = CellValues.Read(cell, type);
                }
                catch (BadCellException e)
                {
                    invalid[column] = e.Message;
                }

                continue;
            }

            if (parameter.HasDefaultValue)
            {
                arguments[i] = parameter.DefaultValue;
                continue;
            }

            throw new BadCellException(
                $"The table has no '{column}' column for '{parameter.Name}' of " +
                $"{method.DeclaringType?.Name}.{method.Name}, and it has no default value. " +
                "Add the column, or give the parameter a default.");
        }

        return arguments;
    }

    private static string columnNameOf(ParameterInfo parameter) => ColumnNames.Of(parameter);

    private static async Task<object?> invoke(object target, MethodInfo method, object?[] arguments)
    {
        object? returned;
        try
        {
            returned = method.Invoke(method.IsStatic ? null : target, arguments);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            // The row's own exception, not the reflection wrapper around it — otherwise every
            // failing row reports TargetInvocationException and the reader learns nothing.
            throw e.InnerException;
        }

        switch (returned)
        {
            case Task task:
                await task;
                return taskResult(task);
            case ValueTask valueTask:
                await valueTask;
                return null;
            default:
                return returned;
        }
    }

    private static object? taskResult(Task task)
    {
        var type = task.GetType();
        if (!type.IsGenericType) return null;

        return type.GetProperty("Result")?.GetValue(task);
    }

    private static bool isAwaitableVoid(Type returnType)
        => returnType == typeof(Task) || returnType == typeof(ValueTask);
}

/// <summary>What one grid reported: its cells, each carrying its row, and its column order.</summary>
/// <remarks>
/// A table a step ran (<see cref="TableRunner"/>) and a set a step verified
/// (<see cref="SetVerificationComparer"/>) both produce one of these, because both render as one
/// grid under one sentence. <see cref="Report"/> is the sink they share.
/// </remarks>
public sealed class TableRun(List<string> columns)
{
    public List<string> Columns { get; } = columns;

    public List<CellResult> Cells { get; } = new();

    /// <summary>True when no row failed and no comparison disagreed.</summary>
    public bool Succeeded => Cells.All(c => c.Status is ResultStatus.ok or ResultStatus.success);

    /// <summary>
    /// Report this grid on the step <c>ScenarioRecorder</c> has open, if any. Outside a recorded
    /// scenario it is a no-op, and the returned <see cref="TableRun"/> is the whole answer.
    /// </summary>
    public void Report()
    {
        var step = ScenarioRecorder.CurrentStep;
        if (step is null) return;

        // REPLACE the cells the interceptor echoed from the table the step was handed, rather than
        // adding beside them (bobcat#471). This grid carries every input cell again, judged where
        // it was judged, so keeping both put each input on the wire twice and, worse, let the
        // document's own text stand in as `ok` for a column a throwing row never produced.
        step.ReplaceTableInputCells();
        step.TableColumns = Columns;
        foreach (var cell in Cells) step.Cells.Add(cell);

        // The grid reaches a watcher while the step is still running (issue #387). Coalesced there,
        // so one call is one post at most.
        ScenarioRecorder.PublishCellsSoFar();
    }
}
