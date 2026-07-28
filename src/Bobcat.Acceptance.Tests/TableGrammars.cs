using Bobcat;
using Bobcat.Engine;

namespace Bobcat.Acceptance.Tests;

/// <summary>
/// The feature's fixture. Every step in "Table Grammar" is served by a [TableGrammar] class,
/// so the fixture itself carries no vocabulary — it just binds the feature to a type.
/// </summary>
public class TableGrammarFixture : Fixture
{
}

/// <summary>
/// Batched data setup — the shape Storyteller users miss most. <c>Before</c> opens the batch,
/// each row adds to it, and <c>After</c> flushes ONCE. State lives in a field, which works
/// because the generator creates a fresh grammar instance per execution.
/// </summary>
[TableGrammar("the following customers exist")]
public class CustomerSetupGrammar
{
    public static readonly List<string> Log = new();

    private List<string>? _batch;

    public static void Reset() => Log.Clear();

    public void Before()
    {
        _batch = new List<string>();
        Log.Add("opened");
    }

    public void Row(string name, int orders) => _batch!.Add($"{name}={orders}");

    public Task AfterAsync()
    {
        Log.Add("saved " + string.Join("|", _batch!));
        return Task.CompletedTask;
    }
}

/// <summary>
/// A decision table: the <c>dividend</c>/<c>divisor</c> columns bind to Row's parameters, and
/// the leftover <c>quotient</c> column is compared against Row's return value per row.
/// </summary>
[TableGrammar("dividing gives")]
public class DivisionGrammar
{
    public int Row(int dividend, int divisor) => dividend / divisor;
}

/// <summary>
/// A throwing <c>Before</c> is critical: the rows are skipped and the scenario aborts, but
/// <c>After</c> still runs as cleanup because the envelope wraps it in a <c>finally</c>.
/// </summary>
[TableGrammar("the failing setup runs")]
public class FailingBeforeGrammar
{
    public static bool AfterRan;
    public static int RowCount;

    public static void Reset()
    {
        AfterRan = false;
        RowCount = 0;
    }

    public void Before() => throw new InvalidOperationException("could not open the batch");

    public void Row(string label) => RowCount++;

    public void After() => AfterRan = true;
}
