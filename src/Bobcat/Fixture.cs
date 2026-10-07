using System.Text.RegularExpressions;
using Bobcat.Engine;
using Bobcat.Runtime;

namespace Bobcat;

/// <summary>
/// Base class for test fixtures. Subclass this and add methods with
/// [Given], [When], [Then], [Check] attributes to define your test vocabulary.
/// Each fixture maps to one or more Gherkin Features by title.
/// </summary>
public abstract partial class Fixture
{
    // Lifecycle is discovered, not inherited. Declare hooks by convention on your fixture:
    //
    //   void/Task BeforeEach(...) / AfterEach(...)          — per scenario, inside the DI scope
    //   static void/Task BeforeAll(...) / AfterAll(...)     — once per feature, outside any scope
    //
    // The "Async" suffix is recognized too (BeforeEachAsync, ...), and [BeforeEach]/[AfterEach]/
    // [BeforeAll]/[AfterAll] override the naming convention. Parameters are injected by type;
    // the source generator emits the resolution, so there is no runtime reflection.

    /// <summary>
    /// The step context for the currently executing scenario. Available during step execution.
    /// </summary>
    public IStepContext? Context { get; set; }

    /// <summary>
    /// Run a table's rows through one of this fixture's own methods, once per row, and render the
    /// grid.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Storyteller's <c>this["BuildUser"].AsTable("The Users are")</c>, and the same shape: a step
    /// that takes the whole table, and a private method that takes one row.
    /// </para>
    /// <code>
    /// [Given("the users are")]
    /// public Task TheUsersAre(StepTable table) => RunTable(nameof(buildUser), table);
    ///
    /// private void buildUser(string first, string last) => _users.Add(new User(first, last));
    /// </code>
    /// <para>
    /// <b>The envelope is the method body.</b> Storyteller needed <c>.Before(...)</c> and
    /// <c>.After(...)</c> because the table was declared rather than called; here "before all rows"
    /// is the line above and "after all rows" is the line below, which is also where a
    /// <c>DbContext</c> or a document session gets its single save:
    /// </para>
    /// <code>
    /// [Given("the users are")]
    /// public async Task TheUsersAre(StepTable table)
    /// {
    ///     _users.Clear();
    ///     await RunTable(nameof(buildUser), table);
    ///     await _session.SaveChangesAsync();
    /// }
    /// </code>
    /// <para>
    /// Columns bind by name (<see cref="HeaderAttribute"/> renames one), an optional parameter's
    /// column may be left out, cells convert through <see cref="Runtime.CellValues"/> — tokens and
    /// <c>TODAY+2</c> included — and a row that throws is a failed row with the rest still run, all
    /// exactly as a generated <c>[Table]</c> step behaves. A returned value plus one column no
    /// parameter claims makes it a decision table, again as the generator would.
    /// </para>
    /// </remarks>
    protected Task<TableRun> RunTable(string methodName, StepTable table)
        => TableRunner.Run(this, TableRunner.MethodNamed(this, methodName), table, Context);

    /// <summary>
    /// Build one <typeparamref name="T"/> per row of a table and render the grid — Storyteller's
    /// <c>CreateNewObject&lt;T&gt;</c>, for when the rows ARE the test input.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Columns bind to <typeparamref name="T"/>'s constructor parameters first (records-friendly) and
    /// then to its settable properties, by name, through the same cell conversion everything else
    /// uses — so a <c>DateOnly</c> column reads <c>TODAY+2</c> and a parameter with a default does
    /// not need a column at all. That combination is the point: a table of five invoice lines with
    /// two relative dates and three defaulted fields is five rows of text.
    /// </para>
    /// <code>
    /// [Given("the invoice details are")]
    /// public async Task TheInvoiceDetailsAre(StepTable table)
    /// {
    ///     _details.Clear();                             // before all rows
    ///     _details.AddRange(BuildRows&lt;InvoiceDetail&gt;(table));
    ///     await _session.SaveChangesAsync();            // after all rows, once
    /// }
    /// </code>
    /// <para>
    /// A row that cannot be built is a failed row on the grid and is left out of the result, rather
    /// than taking the other rows down with it: the step is already red, and the rows that did build
    /// are what the reader needs to see to know which one did not.
    /// </para>
    /// </remarks>
    protected T[] BuildRows<T>(StepTable table) => TableRunner.BuildRows<T>(table, Context);

    /// <summary>
    /// Start a partial <typeparamref name="T"/>, specified by only the members a spec is about:
    /// <c>Specify&lt;ShipmentConfirmed&gt;().With(x =&gt; x.TrackingNumber, "1Z999")</c> (bobcat#416).
    /// <c>.Build()</c> makes the object, filling what was not specified.
    /// </summary>
    protected static Specified<T> Specify<T>() => Specifications.Specify<T>();

    /// <summary>
    /// Compare a collection against a table of expected rows and render the comparison as a grid —
    /// <c>[SetVerification]</c> as a method call, for a step that is handed its table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one shape of set verification a <b>C# test</b> can make, and therefore the one that puts
    /// a single grammar body in both lanes. <c>[SetVerification]</c> gets its expected rows from the
    /// generator, which is why a projected test cannot call such a step; a <c>StepTable</c> parameter
    /// takes them as an argument, and the document or the call site supplies it:
    /// </para>
    /// <code>
    /// [Then("the inventory should be")]
    /// public void TheInventoryShouldBe(StepTable expected)
    ///     =&gt; VerifySet(_inventory.Values, expected, keyColumns: "Sku");
    /// </code>
    /// <code>
    /// // the feature file                       // and the C# test
    /// Then the inventory should be              TheInventoryShouldBe("""
    ///   | Sku     | Quantity |                      | Sku     | Quantity |
    ///   | SKU-001 | 90       |                      | SKU-001 | 90       |
    ///                                               """);
    /// </code>
    /// <para>
    /// <b><c>[SetVerification]</c> is still the declarative form to prefer</b> where it reaches:
    /// its <c>KeyColumns</c>/<c>Ordered</c>/<c>Column</c> are compile-time facts the preview and the
    /// editor can read, and BOBCAT014 and BOBCAT031 are compile errors because of it. Here they are
    /// arguments, which no tool can see before the step runs.
    /// </para>
    /// <para>
    /// Everything about the comparison itself is identical — rows matched by
    /// <paramref name="keyColumns"/>, every column the table names compared once a row is matched,
    /// a missing row shown with its expected values and an extra one with its actual values, both
    /// failing the step, <paramref name="ordered"/> checked after matching rather than instead of
    /// it, and tokens and relative dates read by <see cref="Runtime.CellValues"/> — because it is
    /// the same comparison.
    /// </para>
    /// </remarks>
    /// <param name="keyColumns">
    /// The columns that identify a row, comma-separated. Empty matches on every column the table
    /// names, which makes a row with one wrong value a missing row beside an extra one rather than a
    /// row with one wrong cell.
    /// </param>
    /// <param name="ordered">
    /// Also require the matched rows to appear in the order the table writes them.
    /// </param>
    /// <param name="column">
    /// For a collection of plain values, the column they are compared under. Inferred from a
    /// one-column table.
    /// </param>
    /// <returns>The grid reported, whose <c>Succeeded</c> is the comparison's verdict.</returns>
    protected TableRun VerifySet<T>(IEnumerable<T> actual, StepTable expected,
        string keyColumns = "", bool ordered = false, string column = "")
        => SetVerificationComparer.Verify(actual, expected, Context, keyColumns, ordered, column);

    /// <summary>
    /// Compare one object against one table row — Storyteller's <c>VerifyObject</c> /
    /// <c>CheckPropertyGrammar</c>, as a method call (issue #395).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The columns the row names are compared against the properties of those names, and
    /// <b>only those</b>: a table naming three of an Address's six fields means nothing by the
    /// other three. That is the same partial rule <c>VerifySet</c> and the event-store grammars
    /// follow (#241), rather than a second convention.
    /// </para>
    /// <code>
    /// [Then("the address should be")]
    /// public void TheAddressShouldBe(StepTable expected) => VerifyObject(_address, expected);
    /// </code>
    /// <code>
    /// // the feature file                        // and the C# test
    /// Then the address should be                 TheAddressShouldBe("""
    ///   | Address1       | City   |                  | Address1       | City   |
    ///   | 3 1st Street   | Dallas |                  | 3 1st Street   | Dallas |
    ///                                                """);
    /// </code>
    /// <para>
    /// <b>The argument form is the only form here, and that is deliberate.</b> Every other grammar
    /// family in Bobcat has a declarative twin that is canonical where it reaches —
    /// <c>[SetVerification]</c> over <c>VerifySet</c>, <c>[Table]</c> over <c>RunTable</c> — because
    /// those twins carry settings (<c>KeyColumns</c>, <c>Ordered</c>, <c>Column</c>) that are
    /// compile-time facts the preview and the editor can read. This one has nothing to configure:
    /// the columns come from the table and the subject from the method, so an attribute would carry
    /// no information and buy nothing.
    /// </para>
    /// <para>
    /// One consequence of that, worth knowing rather than discovering: a column naming no property
    /// is an <c>invalid</c> cell at run time listing what the type does have, and it cannot be a
    /// compile-time diagnostic. The generator would need the subject's type in view to say so, and
    /// in the argument form the subject is a value the step chooses — so there is nothing for a
    /// diagnostic to read. It is the same trade <c>VerifySet</c>'s <c>keyColumns</c> makes.
    /// </para>
    /// </remarks>
    /// <param name="subject">The object whose properties the row describes.</param>
    /// <param name="expected">The expected row. Only its first row is read.</param>
    /// <returns>The grid reported, whose <c>Succeeded</c> is the comparison's verdict.</returns>
    protected TableRun VerifyObject(object subject, StepTable expected)
        => PropertyCells.Verify(subject, expected, Context);

    /// <summary>
    /// Derive a feature title from a fixture type. Uses [FixtureTitle] if present,
    /// otherwise strips "Fixture" suffix and inserts spaces before capitals.
    /// </summary>
    public static string DeriveTitle(Type fixtureType)
    {
        var attr = fixtureType.GetCustomAttributes(typeof(FixtureTitleAttribute), false);
        if (attr.Length > 0)
            return ((FixtureTitleAttribute)attr[0]).Title;

        var name = fixtureType.Name;
        if (name.EndsWith("Fixture"))
            name = name[..^7];

        return PascalCaseToTitle(name);
    }

    internal static string PascalCaseToTitle(string name)
    {
        return pascalCaseSplitter().Replace(name, " $1").Trim();
    }

    [GeneratedRegex(@"(?<=[a-z])([A-Z])|(?<=[A-Z])([A-Z][a-z])")]
    private static partial Regex pascalCaseSplitter();
}
