using System.Reflection;
using Bobcat.Engine;
using Bobcat.CritterStack;
using Bobcat.Engine.Verification;

namespace Bobcat.Runtime;

/// <summary>
/// Runs a table a step was handed: a method once per row, or a type constructed once per row. The
/// engine behind <c>Fixture.RunTable</c> and <c>Fixture.BuildRows</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The reflective twin of the generated envelope</b>, and the fourth bounded softening of "no
/// reflection" beside <c>GrammarBehaviors.Resolve</c>, <c>RecordBuilding</c> and the store
/// conventions. A <c>[Table]</c> step's rows are bound at compile time because the generator can see
/// the method the step text matched; a method named by a <c>string</c> at a call site cannot be, and
/// naming one is the whole point — <c>RunTable(nameof(BuildUser), table)</c> keeps the row method a
/// private implementation detail of the fixture instead of a step in the document.
/// </para>
/// <para>
/// Everything else is the same as the generated envelope, deliberately: columns bind by name through
/// <see cref="Bobcat.HeaderAttribute"/>, an optional parameter's column may be left out, cells convert
/// through <see cref="CellValues"/>, a row that throws is a <c>row-error</c> cell and the remaining
/// rows still run, and the whole thing renders as one grid. A table run this way and a table bound by
/// the generator report the same way over the same document.
/// </para>
/// </remarks>
public static class TableRunner
{
    /// <summary>
    /// Call <paramref name="method"/> on <paramref name="target"/> once per row of
    /// <paramref name="table"/>, and report the grid.
    /// </summary>
    /// <returns>The cells and columns reported, for a caller that wants them.</returns>
    public static async Task<TableRun> Run(object target, MethodInfo method, StepTable table,
        IStepContext? context)
    {
        var parameters = method.GetParameters();
        var run = new TableRun(table.Headers.ToList());
        var rows = table.AsDictionaries();

        // A returned value with exactly one column no parameter claims is a decision table, the same
        // rule the generator applies: that column is the expected output.
        var bound = new HashSet<string>(
            parameters.Select(columnNameOf), StringComparer.OrdinalIgnoreCase);
        var leftover = table.Headers.Where(h => !bound.Contains(h)).ToList();
        var expectedColumn = method.ReturnType != typeof(void)
                             && !isAwaitableVoid(method.ReturnType)
                             && leftover.Count == 1
            ? leftover[0]
            : null;

        for (var r = 0; r < rows.Count; r++)
        {
            context?.ReportProgress(StepUpdate.ForRow(r + 1, rows.Count));

            var row = rows[r];

            try
            {
                var arguments = bind(parameters, row, method);
                var returned = await invoke(target, method, arguments);

                if (expectedColumn != null)
                {
                    run.Cells.Add(CellCheck.ForValue(expectedColumn, returned,
                        row.TryGetValue(expectedColumn, out var expected) ? expected : "", null, r));
                }
            }
            catch (Exception e) when (DecisionTableComparer.IsRowFailure(e))
            {
                run.Cells.Add(DecisionTableComparer.RowError(r, e));
            }

            addInputCells(run, table, row, r, expectedColumn);
        }

        run.Report(context);
        return run;
    }

    /// <summary>
    /// Build one <paramref name="type"/> per row and report the grid. A row that cannot be built is a
    /// <c>row-error</c> cell and is left out of the result; the rest are still built.
    /// </summary>
    public static TableRun Build(Type type, StepTable table, IStepContext? context, out object?[] built)
    {
        var run = new TableRun(table.Headers.ToList());
        var rows = table.AsDictionaries();
        var results = new List<object?>(rows.Count);

        for (var r = 0; r < rows.Count; r++)
        {
            try
            {
                results.Add(RecordBuilding.Build(type, rows[r]));
            }
            catch (Exception e) when (DecisionTableComparer.IsRowFailure(e))
            {
                run.Cells.Add(DecisionTableComparer.RowError(r, e));
            }

            addInputCells(run, table, rows[r], r, null);
        }

        built = results.ToArray();
        run.Report(context);
        return run;
    }

    /// <summary>
    /// <see cref="Run(object,MethodInfo,StepTable,IStepContext?)"/> by method name — what
    /// <c>Fixture.RunTable</c> calls, and the form a grammar that is not a <c>Fixture</c> uses.
    /// </summary>
    public static Task<TableRun> Run(object target, string methodName, StepTable table,
        IStepContext? context = null)
        => Run(target, MethodNamed(target, methodName), table, context);

    /// <summary>
    /// One <typeparamref name="T"/> per row, and the grid — what <c>Fixture.BuildRows</c> calls.
    /// </summary>
    public static T[] BuildRows<T>(StepTable table, IStepContext? context = null)
    {
        Build(typeof(T), table, context, out var built);
        return built.OfType<T>().ToArray();
    }

    /// <summary>
    /// The method a <c>nameof</c> names, on this object's type — including a private one, because a
    /// row method is an implementation detail of the fixture rather than part of its vocabulary.
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

    private static void addInputCells(TableRun run, StepTable table,
        IReadOnlyDictionary<string, string> row, int rowIndex, string? expectedColumn)
    {
        foreach (var header in table.Headers)
        {
            if (expectedColumn != null
                && string.Equals(header, expectedColumn, StringComparison.OrdinalIgnoreCase)) continue;

            run.Cells.Add(new CellResult(header, ResultStatus.ok,
                row.TryGetValue(header, out var value) ? value : "") { RowIndex = rowIndex });
        }
    }

    private static object?[] bind(ParameterInfo[] parameters,
        IReadOnlyDictionary<string, string> row, MethodInfo method)
    {
        var arguments = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            var column = columnNameOf(parameter);

            if (row.TryGetValue(column, out var cell))
            {
                arguments[i] = CellValues.Read(cell, parameter.ParameterType);
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
    /// Report this grid on whichever step is open — the Gherkin lane's through
    /// <paramref name="context"/>, the projected lane's through <c>ScenarioRecorder</c>.
    /// </summary>
    /// <remarks>
    /// <b>Both, not either</b>, and that is what makes one grammar body serve both lanes: a step
    /// executing under <c>BobcatRunner</c> has a context and no recorder, one called from a C# test
    /// has a recorder and no context, and neither has to know which it is.
    /// </remarks>
    public void Report(IStepContext? context)
    {
        context?.RecordCells(Cells, Columns);

        // The projected lane's step, when one is open. A grammar called from a C# test has no step
        // context — the recorder is what holds its step.
        var step = ScenarioRecorder.CurrentStep;
        if (step is null) return;

        step.TableColumns = Columns;
        foreach (var cell in Cells) step.Cells.Add(cell);

        // The grid reaches a watcher while the step is still running (issue #387). Coalesced there,
        // so one call is one post at most.
        ScenarioRecorder.PublishCellsSoFar();
    }
}
