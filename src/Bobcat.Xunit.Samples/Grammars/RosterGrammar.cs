using Bobcat.Runtime;

namespace Bobcat.Xunit.Samples.Grammars;

/// <summary>What Storyteller expressed as [SelectionValues]; an enum says it to the compiler too.</summary>
public enum Position
{
    Pitcher,
    Outfield,
    Catcher
}

/// <summary>
/// Storyteller 5's table grammars, from the C# side: the tabular data a specification sets up,
/// written as a table literal in the test itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>One grammar body, both lanes.</b> This class is an ordinary fixture-style grammar — the same
/// methods a <c>.feature</c> file would bind to, taking <see cref="StepTable"/>. In the Gherkin lane
/// the document's trailing <c>|...|</c> block supplies the table; here the caller supplies it as
/// pipe-delimited text, which <see cref="StepTable"/> reads by an implicit conversion. Nothing in
/// the grammar knows or cares which lane called it.
/// </para>
/// <para>
/// The two alternatives were both worse. Calling a row helper once per row renders as N steps and
/// the grid is gone — which is exactly the report the Gherkin lane just stopped producing. A
/// collection-of-tuples argument (<c>void Sum((int x, int y, int sum)[] rows)</c>) grids up fine but
/// is a second signature written for the C# lane beside the one the document binds to, so a change to
/// the vocabulary has to be made twice.
/// </para>
/// <para>
/// A markdown table pastes in unchanged, alignment row and all, which is the point of the syntax: the
/// table in the specification, the table in the pull request and the table in the test are the same
/// text.
/// </para>
/// </remarks>
public class RosterGrammar
{
    private readonly List<string> _roster = new();

    /// <summary>
    /// The batched-setup shape: one call, the whole table, one save. Storyteller's
    /// <c>AsTable(...).Before(...).After(...)</c> envelope with the envelope written out.
    /// </summary>
    [Given("the roster is")]
    internal void TheRosterIs(StepTable roster)
    {
        _roster.Clear();

        foreach (var row in roster.AsDictionaries())
        {
            var position = row.TryGetValue("position", out var declared) ? declared : "Outfield";
            _roster.Add($"{row["player"]} ({position})");
        }
    }

    /// <summary>
    /// The cell is named for the placeholder it belongs to, so its verdict renders in the sentence
    /// where the value sits rather than on a line underneath — the join between a comparison and its
    /// place in the sentence is that name.
    /// </summary>
    [Then("the roster reads {roster}")]
    internal void TheRosterReads(string roster)
        => SpecAssert.Check("roster", string.Join(", ", _roster), roster);

    /// <summary>
    /// Storyteller's <c>CreateNewObject&lt;T&gt;</c> from the C# side: the rows ARE the input, built
    /// through the same cell conversion the Gherkin lane uses — so <c>TODAY+2</c> is a date and a
    /// field the record defaults need not appear in the table at all.
    /// </summary>
    /// <remarks>
    /// The "before all rows" and "after all rows" hooks Storyteller needed are the lines either side
    /// of the call: <c>_signings.Clear()</c> above, one save below. A declared table needed hooks
    /// because it was declared rather than called.
    /// </remarks>
    [Given("the signings are")]
    internal void TheSigningsAre(StepTable table)
    {
        _signings.Clear();
        _signings.AddRange(TableRunner.BuildRows<Signing>(table));
    }

    [Then("the signings read {signings}")]
    internal void TheSigningsRead(string signings)
        => SpecAssert.Check("signings", string.Join(", ", _signings.Select(s => s.Describe())), signings);

    private readonly List<Signing> _signings = new();

    /// <summary>A record with a defaulted field, so a column can be left out of the table.</summary>
    public record Signing(string Player, Position Position, DateOnly StartsOn, int Years = 1)
    {
        public string Describe() => $"{Player}/{Position}/{Years}y";
    }

    /// <summary>
    /// A decision table from the C# side: the expected value is a column like any other, and the
    /// grammar reports one cell per row so the grid carries a verdict per row.
    /// </summary>
    [Then("adding numbers together")]
    internal void AddingNumbersTogether(StepTable sums)
    {
        var rows = sums.AsDictionaries();

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var x = int.Parse(row["x"]);
            var y = int.Parse(row["y"]);

            SpecAssert.Check("sum", x + y, int.Parse(row["sum"]), rowIndex: i);
        }
    }
}
