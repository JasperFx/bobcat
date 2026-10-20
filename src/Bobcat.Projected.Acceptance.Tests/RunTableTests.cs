using Bobcat.Engine;
using Bobcat.Rendering;
using Bobcat.Runtime;

namespace Bobcat.Projected.Acceptance.Tests;

public enum Tier
{
    Free,
    Pro,
    Enterprise
}

/// <summary>Storyteller's <c>CreateNewObject&lt;T&gt;</c> shape: a record a table row builds.</summary>
// #region subscription-record
public record Subscription(string Customer, Tier Tier, DateOnly RenewsOn, string Currency = "USD");
// #endregion subscription-record

/// <summary>
/// <c>RunTable.feature</c> from the projected lane: a plain grammar (no <c>Fixture</c>) hands its
/// table to <see cref="TableRunner"/>, through a private row method or straight into objects.
/// </summary>
/// <remarks>
/// The envelope Storyteller's table grammars and <c>[TableGrammar]</c> declared is the method body
/// here: clear above the rows, flush once below them. That is the whole of #468's argument for
/// dropping the declarative form, so it is worth seeing that nothing else is needed.
/// </remarks>
public class RunTableTests
{
    public class MembersGrammar
    {
        public readonly List<string> Log = new();
        public string Flushed = "";
        public Subscription[] Built = [];

        // #region members-are
        [Given("the members are")]
        internal async Task TheMembersAre(StepTable table)
        {
            Log.Clear();
            await TableRunner.Run(this, nameof(addMember), table);
            Flushed = string.Join("|", Log);
        }

        private void addMember([Header("Member Name")] string member, Tier tier = Tier.Free)
            => Log.Add($"{member}:{tier}");
        // #endregion members-are

        [Given("the members are added slowly")]
        internal async Task TheMembersAreAddedSlowly(StepTable table) => await TableRunner.Run(this, nameof(addMemberAsync), table);

        private async Task addMemberAsync(string member)
        {
            await Task.Yield();
            Log.Add(member);
        }

        // #region doubling-gives
        [Then("doubling gives")]
        internal async Task DoublingGives(StepTable table) => await TableRunner.Run(this, nameof(doubled), table);

        private int doubled(int input) => input * 2;
        // #endregion doubling-gives

        // #region subscriptions-are
        [Given("the subscriptions are")]
        internal void TheSubscriptionsAre(StepTable table) => Built = TableRunner.BuildRows<Subscription>(table);
        // #endregion subscriptions-are
    }

    private readonly MembersGrammar _grammar = new();

    [Fact]
    public async Task runs_the_named_method_once_per_row_and_renders_one_grid()
    {
        var (recording, render) = await Projected.RunAsync("one grid", () => _grammar.TheMembersAre("""
            | Member Name | tier       |
            | Ada         | Pro        |
            | Grace       | Enterprise |
            """));

        recording.Step("the members are").Status.ShouldBe(ResultStatus.success);
        _grammar.Flushed.ShouldBe("Ada:Pro|Grace:Enterprise");

        // One step, one grid.
        var grid = render.Step("the members are").SetVerification.ShouldNotBeNull();
        grid.Columns.ShouldBe(["Member Name", "tier"]);
        grid.Rows.Count.ShouldBe(2);
    }

    [Fact]
    public async Task a_header_renames_the_column_and_an_optional_column_may_be_left_out()
    {
        await Projected.RunAsync("optional column", () => _grammar.TheMembersAre("""
            | Member Name |
            | Ada         |
            """));

        // [Header("Member Name")] bound the column, and `tier = Tier.Free` supplied the rest.
        _grammar.Flushed.ShouldBe("Ada:Free");
    }

    private const string wizardRow = """
        | Member Name | tier   |
        | Ada         | Pro    |
        | Grace       | Wizard |
        | Linus       | Free   |
        """;

    /// <summary>
    /// A cell its parameter's type cannot hold is a defect in the specification, decidable without
    /// running anything, so it is an <c>invalid</c> cell in its own column naming the type and the
    /// text (bobcat#544) -- not a <c>row-error</c>, which says the code threw.
    /// </summary>
    [Fact]
    public async Task a_cell_that_will_not_convert_is_an_invalid_cell_and_the_other_rows_still_run()
    {
        var (recording, render) = await Projected.RunAsync("bad cell", () => _grammar.TheMembersAre(wizardRow));
        var step = recording.Step("the members are");

        step.Cells.ShouldNotContain(c => c.Name == DecisionTableComparer.RowErrorCell);

        var invalid = step.Cells.Single(c => c.RowIndex == 1 && c.Name == "tier");
        invalid.Status.ShouldBe(ResultStatus.invalid);
        invalid.DisplayText.ShouldContain("'Wizard'");
        invalid.DisplayText.ShouldContain("Tier");
        invalid.DisplayText.ShouldContain("Free, Pro, Enterprise");

        // The row's other input is still shown as written.
        step.Cells.Single(c => c.RowIndex == 1 && c.Name == "Member Name").DisplayText.ShouldBe("Grace");

        render.Step("the members are").SetVerification.ShouldNotBeNull()
            .Rows[1].Cells.Single(c => c.Column == "tier").Status.ShouldBe(ResultStatus.invalid);

        // The bad row was not run; the rows either side of it were.
        _grammar.Flushed.ShouldBe("Ada:Pro|Linus:Free");

        // A spec defect fails the step -- it is not an exception the code threw.
        step.Status.ShouldBe(ResultStatus.failed);
        recording.GatheredFailures().ShouldNotBeNull().ShouldContain("the members are");
    }

    [Fact]
    public async Task an_async_row_method_is_awaited_before_the_next_row()
    {
        await Projected.RunAsync("async rows", () => _grammar.TheMembersAreAddedSlowly("""
            | member |
            | Ada    |
            | Grace  |
            """));

        _grammar.Log.ShouldBe(["Ada", "Grace"]);
    }

    [Fact]
    public async Task a_returned_value_and_one_unclaimed_column_is_a_decision_table()
    {
        var (recording, render) = await Projected.RunAsync("doubling", () =>
            // #region doubling-call
            _grammar.DoublingGives("""
                | input | doubled |
                | 2     | 4       |
                | 3     | 7       |
                """)
            // #endregion doubling-call
        );

        recording.Step("doubling gives").Status.ShouldBe(ResultStatus.failed);

        var grid = render.Step("doubling gives").SetVerification.ShouldNotBeNull();
        grid.Rows[0].Cells.Single(c => c.Column == "doubled").Status.ShouldBe(ResultStatus.success);

        var wrong = grid.Rows[1].Cells.Single(c => c.Column == "doubled");
        wrong.Status.ShouldBe(ResultStatus.failed);
        wrong.Expected.ShouldBe("7");
        wrong.Actual.ShouldBe("6");
    }

    [Fact]
    public void builds_one_object_per_row_with_relative_dates_and_declared_defaults()
    {
        var (_, render) = Projected.Run("objects", () =>
            // #region subscriptions-call
            _grammar.TheSubscriptionsAre("""
                | Customer | Tier | RenewsOn |
                | Ada      | Pro  | TODAY+30 |
                | Grace    | Free | TODAY    |
                """)
            // #endregion subscriptions-call
        );

        _grammar.Built.Length.ShouldBe(2);
        _grammar.Built[0].Customer.ShouldBe("Ada");
        _grammar.Built[0].Tier.ShouldBe(Tier.Pro);

        // Relative to each other, because TODAY is a fact about the run.
        _grammar.Built[0].RenewsOn.ShouldBe(_grammar.Built[1].RenewsOn.AddDays(30));

        // The Currency column is not in the table at all; the record's own default supplied it.
        _grammar.Built.ShouldAllBe(s => s.Currency == "USD");

        render.Step("the subscriptions are").SetVerification.ShouldNotBeNull().Columns
            .ShouldBe(["Customer", "Tier", "RenewsOn"]);
    }

    [Fact]
    public void a_method_name_that_matches_nothing_says_so()
    {
        Should.Throw<SpecCriticalException>(() => TableRunner.MethodNamed(_grammar, "noSuchMethod"))
            .Message.ShouldContain("no method named 'noSuchMethod'");
    }
}
