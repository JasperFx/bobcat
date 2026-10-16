using System.Reflection;
using Bobcat.Engine;
using Bobcat.Runtime;

namespace Bobcat;

/// <summary>
/// The rows of a data-driven specification written as one pipe-table literal (bobcat#570): each data
/// row becomes the argument list of one run of the test method, so each row is its own specification
/// with its own identity.
/// </summary>
/// <remarks>
/// <para>
/// <b>Columns bind to the method's parameters by name</b> — the parameter's <see cref="HeaderAttribute"/>
/// title when it has one, else its name, case-insensitive — exactly as <c>TableRunner</c> binds a row
/// method. A parameter with a default value may be left out; every other parameter needs a column.
/// </para>
/// <para>
/// <b>Cells read through <see cref="CellValues"/></b>, so <c>NULL</c>, <c>EMPTY</c>, quoted literals,
/// relative times, enums by name and registered domain types mean here what they mean in every other
/// table.
/// </para>
/// <para>
/// <b>The generator checks a constant table first</b>: a column naming no parameter is BOBCAT032, a
/// missing column, a ragged row or a cell its type cannot read is BOBCAT042. What reaches this class at
/// run time is therefore a table the build already agreed with, except for what only a run can decide
/// (a domain type's conversion, a relative time).
/// </para>
/// </remarks>
public static class SpecExamples
{
    /// <summary>
    /// The argument lists <paramref name="table"/> gives <paramref name="method"/>, one per data row,
    /// in table order.
    /// </summary>
    /// <exception cref="ArgumentException">The table's columns do not fit the method's parameters.</exception>
    /// <exception cref="BadCellException">A cell cannot be read as its parameter's type.</exception>
    public static IReadOnlyList<object?[]> Rows(MethodInfo method, string table)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(table);

        var parsed = StepTable.Parse(table);
        var parameters = method.GetParameters();
        var where = $"[BobcatExamples] on {method.DeclaringType?.Name}.{method.Name}";

        var columnOf = new int[parameters.Length];
        Array.Fill(columnOf, -1);

        for (var column = 0; column < parsed.Headers.Count; column++)
        {
            var header = parsed.Headers[column];
            var index = Array.FindIndex(parameters,
                p => string.Equals(ColumnNames.Of(p), header, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
                throw new ArgumentException(
                    $"{where}: the column '{header}' names no parameter (it takes {describe(parameters)}).");

            if (columnOf[index] >= 0)
                throw new ArgumentException($"{where}: the column '{header}' is given twice.");

            columnOf[index] = column;
        }

        for (var i = 0; i < parameters.Length; i++)
        {
            if (columnOf[i] < 0 && !parameters[i].HasDefaultValue)
                throw new ArgumentException(
                    $"{where}: no column for the parameter '{ColumnNames.Of(parameters[i])}', which has no default value.");
        }

        var rows = new List<object?[]>(parsed.Count);
        for (var r = 0; r < parsed.Rows.Count; r++)
        {
            var cells = parsed.Rows[r];
            if (cells.Count != parsed.Headers.Count)
                throw new ArgumentException(
                    $"{where}: row {r + 1} has {cells.Count} cell(s) where the header has {parsed.Headers.Count}.");

            var values = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                if (columnOf[i] < 0)
                {
                    values[i] = defaultOf(parameters[i]);
                    continue;
                }

                try
                {
                    values[i] = CellValues.Read(cells[columnOf[i]], parameters[i].ParameterType);
                }
                catch (BadCellException e)
                {
                    throw new BadCellException($"{where}, row {r + 1}, column '{parsed.Headers[columnOf[i]]}': {e.Message}", e);
                }
            }

            rows.Add(values);
        }

        return rows;
    }

    /// <summary>A parameter's default, as the enum it is declared as rather than its underlying number.</summary>
    private static object? defaultOf(ParameterInfo parameter)
    {
        var value = parameter.DefaultValue;
        var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
        return value != null && type.IsEnum && value.GetType() != type ? Enum.ToObject(type, value) : value;
    }

    private static string describe(ParameterInfo[] parameters)
        => parameters.Length == 0 ? "none" : string.Join(", ", parameters.Select(ColumnNames.Of));
}
