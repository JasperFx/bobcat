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
