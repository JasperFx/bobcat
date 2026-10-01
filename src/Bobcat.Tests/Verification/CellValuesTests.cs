using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Verification;

/// <summary>
/// The one runtime authority on what a written cell means — the input side of what the checkers
/// already read on the expected side.
/// </summary>
public class CellValuesTests
{
    [Fact]
    public void reads_the_ordinary_types()
    {
        CellValues.Read<int>("42").ShouldBe(42);
        CellValues.Read<decimal>("10.50").ShouldBe(10.50m);
        CellValues.Read<bool>("true").ShouldBeTrue();
        CellValues.Read<string>("Widget").ShouldBe("Widget");
        CellValues.Read<DayOfWeek>("tuesday").ShouldBe(DayOfWeek.Tuesday);
        CellValues.Read<Guid>("11111111-1111-1111-1111-111111111111")
            .ShouldBe(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        CellValues.Read<TimeSpan>("00:05:00").ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void a_relative_date_resolves_against_the_clock_the_run_is_using()
    {
        BobcatClock.Set(new ControllableTimeProvider(
            new DateTimeOffset(2026, 6, 5, 9, 0, 0, TimeSpan.Zero)));
        try
        {
            CellValues.Read<DateOnly>("TODAY").ShouldBe(new DateOnly(2026, 6, 5));
            CellValues.Read<DateOnly>("TODAY+2").ShouldBe(new DateOnly(2026, 6, 7));
            CellValues.Read<DateOnly>("TODAY - 1 week").ShouldBe(new DateOnly(2026, 5, 29));
            CellValues.Read<DateTime>("NOW + 30 minutes")
                .ShouldBe(new DateTime(2026, 6, 5, 9, 30, 0, DateTimeKind.Utc));
            CellValues.Read<TimeOnly>("NOW").ShouldBe(new TimeOnly(9, 0));
            CellValues.Read<DateTimeOffset>("TODAY").ShouldBe(
                new DateTimeOffset(new DateTime(2026, 6, 5), TimeSpan.Zero));
        }
        finally
        {
            BobcatClock.Reset();
        }
    }

    [Fact]
    public void a_plain_date_still_reads_as_itself()
    {
        CellValues.Read<DateOnly>("2026-01-31").ShouldBe(new DateOnly(2026, 1, 31));
        CellValues.Read<DateTime>("2026-01-31T10:15:00").ShouldBe(new DateTime(2026, 1, 31, 10, 15, 0));
    }

    [Fact]
    public void the_reserved_tokens_mean_what_they_mean_on_the_expected_side()
    {
        CellValues.Read("NULL", typeof(int?)).ShouldBeNull();
        CellValues.Read("null", typeof(string)).ShouldBeNull();
        CellValues.Read("EMPTY", typeof(string)).ShouldBe("");
        CellValues.Read("EMPTY", typeof(int?)).ShouldBeNull();
    }

    [Fact]
    public void a_quoted_token_is_its_own_literal_text()
    {
        CellValues.Read<string>("\"NULL\"").ShouldBe("NULL");
        CellValues.Read<string>("\"TODAY\"").ShouldBe("TODAY");
    }

    [Fact]
    public void an_empty_cell_is_null_for_a_nullable_and_the_value_for_a_string()
    {
        CellValues.Read("", typeof(int?)).ShouldBeNull();
        CellValues.Read("", typeof(string)).ShouldBe("");
    }

    [Fact]
    public void NULL_against_a_type_that_cannot_be_null_says_which_type()
    {
        Should.Throw<BadCellException>(() => CellValues.Read<int>("NULL"))
            .Message.ShouldContain("Int32 cannot be null");
    }

    [Fact]
    public void a_cell_it_cannot_read_names_the_cell_and_the_type()
    {
        var ex = Should.Throw<BadCellException>(() => CellValues.Read<int>("oops"));

        ex.Message.ShouldContain("'oops'");
        ex.Message.ShouldContain("Int32");
    }

    [Fact]
    public void a_relative_time_against_something_that_is_not_a_date_is_read_as_that_type()
    {
        // A string cell is text, so TODAY is the word TODAY. Anything else is refused as itself.
        CellValues.Read<string>("TODAY").ShouldBe("TODAY");
        Should.Throw<BadCellException>(() => CellValues.Read<int>("TODAY"));
    }
}
