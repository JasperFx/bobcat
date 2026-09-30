using Bobcat;

namespace Bobcat.Gherkin.Samples;

/// <summary>
/// Storyteller's <c>TablesFixture</c> (Samples/Fixtures/TableFixture.cs), one grammar at a time.
/// Storyteller had two ways to build a table — <c>[ExposeAsTable]</c> over a method, and
/// <c>this["Grammar"].AsTable(...)</c> over another grammar. Bobcat has the same two, spelled
/// <see cref="DecisionTableAttribute"/> on a step method and <see cref="TableGrammarAttribute"/>
/// on a class.
/// </summary>
/// <summary>
/// Storyteller wrote this as <c>[SelectionValues("Pitcher", "Outfield", "Catcher")]</c> on a
/// <c>string</c>. An enum says the same thing to the compiler, the reader and the editor.
/// </summary>
public enum Position
{
    Pitcher,
    Outfield,
    Catcher
}

public class TablesFixture : Fixture
{
    /// <summary>
    /// Storyteller: <c>[ExposeAsTable("Adding numbers together", "sum")] [return: AliasAs("sum")]</c>.
    /// The return value is the expected output, and <c>[Expected]</c> names the column — the same
    /// job <c>AliasAs</c> did. Without it the column would be the method name.
    /// </summary>
    [DecisionTable]
    [Then("adding numbers together")]
    [Expected("sum")]
    public int Sum(int x, int y) => x + y;

    /// <summary>
    /// Storyteller's <c>Decisions : DecisionTableGrammar</c> — two settable inputs and TWO computed
    /// outputs, which a return value cannot express. <c>out</c> parameters can, and each one is a
    /// column compared in its own right.
    /// </summary>
    [DecisionTable]
    [Then("what's my name?")]
    public void WhatsMyName(string firstName, string lastName,
        out string fullName, out string lastNameFirst)
    {
        fullName = $"{firstName} {lastName}";
        lastNameFirst = $"{lastName}, {firstName}";
    }

    /// <summary>
    /// Storyteller's <c>Divide</c> paragraph-as-table, minus the paragraph: what the specification
    /// says is a table of divisions, each row checking a quotient. <c>3 / 0</c> is in the sample on
    /// purpose — an exception inside one row.
    /// </summary>
    [DecisionTable]
    [Then("dividing numbers")]
    [Expected("quotient")]
    public double Divide(double x, double y)
    {
        if (y == 0) throw new DivideByZeroException("cannot divide by zero");
        return x / y;
    }

    /// <summary>
    /// Storyteller's <c>IsPositive</c> — a <c>bool</c> return is compared like any other value,
    /// so a table of yes/no answers needs no special grammar.
    /// </summary>
    [DecisionTable]
    [Then("is the number positive?")]
    [Expected("isPositive")]
    public bool IsPositive(int number) => number >= 0;

    /// <summary>
    /// Storyteller's <c>TableWithLotsOfOptions</c>, which decorated its parameters with
    /// <c>[Header("Player Name")]</c>, <c>[DefaultValue("Outfield")]</c> and
    /// <c>[SelectionValues(...)]</c>.
    /// </summary>
    /// <remarks>
    /// Two of the three are here. <c>[Header]</c> is the same attribute doing the same job: the
    /// column is prose in a document and the parameter is code, so they need not be the same word.
    /// The default is a plain C# optional parameter — the column may be left out of the table
    /// entirely and the language says what happens then, in the one place a reader of the fixture
    /// looks. The selection list is not here and is not planned: it existed for Storyteller's
    /// editor, and where it constrained a value an enum parameter now does it better — a cell
    /// outside the list is BOBCAT030 at build time. See <see cref="Position"/> below.
    /// </remarks>
    [Table]
    [Given("the roster is")]
    public void TheRosterIs([Header("Player Name")] string player, Position position = Position.Outfield)
        => Roster.Add($"{player} ({position})");

    [Then("the roster reads {string}")]
    public string TheRosterReads() => string.Join(", ", Roster);

    internal readonly List<string> Roster = new();

    public void BeforeEach()
    {
        Roster.Clear();
        UserTableGrammar.Saved = "";
        BeforeThrowsGrammar.AfterRan = false;
        AfterThrowsGrammar.RowsSeen = 0;
    }

    /// <summary>Reads what the table grammar's After flushed — once, not once per row.</summary>
    [Then("the batch was saved once as {string}")]
    public string TheBatchWasSavedOnceAs() => UserTableGrammar.Saved;

    [Check("the batch was closed anyway")]
    public bool TheBatchWasClosedAnyway() => BeforeThrowsGrammar.AfterRan;

    [Check("every row was seen")]
    public bool EveryRowWasSeen() => AfterThrowsGrammar.RowsSeen == 1;
}

/// <summary>
/// Storyteller's <c>TableWithBeforeAndAfterSteps</c>: <c>this["BuildUser"].AsTable("The Users are")
/// .Before(() =&gt; _users.Clear()).After(() =&gt; saveUsersToTheDatabase(_users))</c>. The whole
/// point is that the save happens ONCE, after every row — batched setup, not a save per row.
/// </summary>
[TableGrammar("the users are")]
public class UserTableGrammar
{
    public record User(string First, string Last);

    /// <summary>What the batch was flushed as, for the assertion step to read.</summary>
    public static string Saved = "";

    private readonly List<User> _users = new();

    public void Before() => _users.Clear();

    public void Row(string first, string last) => _users.Add(new User(first, last));

    /// <summary>Storyteller's <c>saveUsersToTheDatabase</c> — one call, however many rows.</summary>
    public void After() => Saved = string.Join("; ", _users.Select(u => $"{u.Last}, {u.First}"));
}

/// <summary>
/// Storyteller's <c>Tables with Errors</c> specification, first half: the <c>Before</c> throws.
/// Bobcat's contract is that the rows are skipped, the scenario aborts, and <c>After</c> still
/// runs — the half-finished Before is the one that leaves something to clean up.
/// </summary>
[TableGrammar("the batch with a broken open runs")]
public class BeforeThrowsGrammar
{
    public static bool AfterRan;

    public void Before() => throw new InvalidOperationException("the batch could not be opened");

    public void Row(int x) { }

    public void After() => AfterRan = true;
}

/// <summary>
/// The second half: every row succeeds and the <c>After</c> throws. Storyteller reported the
/// error against the table as a whole.
/// </summary>
[TableGrammar("the batch with a broken close runs")]
public class AfterThrowsGrammar
{
    public static int RowsSeen;

    public void Before() => RowsSeen = 0;

    public void Row(int x) => RowsSeen++;

    public void After() => throw new InvalidOperationException("the batch could not be flushed");
}
