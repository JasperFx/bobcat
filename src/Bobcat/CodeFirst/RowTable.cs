using System.Collections;
using System.Globalization;
using System.Reflection;
using Bobcat.Engine;

namespace Bobcat.CodeFirst;

/// <summary>
/// Turns plain objects — records, anonymous types, DTOs — into the tabular cell shape the renderer
/// already understands, so a code-first step can show its input rows (the events it appended, the
/// command it sent) as a table instead of a prose string. Records are self-describing: their
/// public readable properties are the columns.
/// </summary>
/// <remarks>
/// The same describer feeds set verification: <see cref="ExpectedRow"/> flattens an expected row
/// into the <c>column → text</c> dictionary <see cref="Runtime.SetVerificationComparer"/> consumes,
/// formatting every value the invariant way the Gherkin cell parser expects to read it back.
/// </remarks>
public sealed class RowTable
{
    /// <summary>The column name used for a row's runtime type when the rows do not all share one.</summary>
    public const string TypeColumn = "type";

    private RowTable(IReadOnlyList<string> columns, IReadOnlyList<CellResult> cells)
    {
        Columns = columns;
        Cells = cells;
    }

    public IReadOnlyList<string> Columns { get; }

    /// <summary>One cell per column per row, all with status <c>ok</c> — inputs, not comparisons.</summary>
    public IReadOnlyList<CellResult> Cells { get; }

    /// <summary>
    /// Describe <paramref name="rows"/> as an input table. A <see cref="TypeColumn"/> is prepended
    /// when the rows are of more than one runtime type, or when any row has no readable properties
    /// (a marker event like <c>record AEvent;</c>), so that an event stream reads as a list of
    /// event names rather than a table with nothing in it.
    /// </summary>
    public static RowTable Describe(IEnumerable<object?> rows)
    {
        var list = rows.ToList();
        var columns = new List<string>();
        var includeType = false;
        Type? firstType = null;

        foreach (var row in list)
        {
            if (row is null)
            {
                includeType = true;
                continue;
            }

            var type = row.GetType();
            firstType ??= type;
            if (type != firstType) includeType = true;

            var properties = readableProperties(type);
            if (properties.Count == 0) includeType = true;

            foreach (var property in properties)
            {
                if (!columns.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    columns.Add(property.Name);
            }
        }

        if (includeType) columns.Insert(0, TypeColumn);

        var cells = new List<CellResult>();
        for (var rowIndex = 0; rowIndex < list.Count; rowIndex++)
        {
            var row = list[rowIndex];
            var values = row is null
                ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                : valuesOf(row);

            foreach (var column in columns)
            {
                string text;
                if (column == TypeColumn && includeType)
                    text = row?.GetType().Name ?? Format(null);
                else
                    text = values.TryGetValue(column, out var value) ? Format(value) : "";

                cells.Add(new CellResult(column, ResultStatus.ok) { Actual = text, RowIndex = rowIndex });
            }
        }

        return new RowTable(columns, cells);
    }

    /// <summary>
    /// Attach this table to a step result as an input table: rendered as a grid, counted as nothing.
    /// </summary>
    public void ApplyTo(StepResult result)
    {
        result.IsSetVerification = true;
        result.SetVerificationColumns = Columns;
        result.MarkCells(Cells.ToArray());
    }

    /// <summary>
    /// Flatten an expected row (anonymous object, record, dictionary) into the
    /// <c>column → expected text</c> shape set verification compares against.
    /// </summary>
    public static Dictionary<string, string> ExpectedRow(object row)
    {
        var expected = new Dictionary<string, string>();

        switch (row)
        {
            case IDictionary<string, string> strings:
                foreach (var (key, value) in strings) expected[key] = value;
                return expected;

            case IDictionary<string, object?> objects:
                foreach (var (key, value) in objects) expected[key] = Format(value);
                return expected;
        }

        foreach (var (name, value) in valuesOf(row))
            expected[name] = Format(value);

        return expected;
    }

    /// <summary>
    /// The display form of a value — invariant culture, <c>NULL</c> for null, enumerables joined
    /// with commas, and a type name for an element that carries no state of its own.
    /// </summary>
    public static string Format(object? value) => value switch
    {
        null => "NULL",
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable items => string.Join(", ", items.Cast<object?>().Select(Format)),
        _ => readableProperties(value.GetType()).Count == 0 ? value.GetType().Name : value.ToString() ?? ""
    };

    private static Dictionary<string, object?> valuesOf(object row)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in readableProperties(row.GetType()))
        {
            values[property.Name] = property.GetValue(row);
        }
        return values;
    }

    private static List<PropertyInfo> readableProperties(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract")
            .ToList();
}
